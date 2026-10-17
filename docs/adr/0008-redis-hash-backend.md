# ADR 0008: New backend: one Redis hash per (counter, tier), written by one Lua script per event

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §6.2 (decision Q2)

## Context

Grab moved from a row-per-bucket wide-column table to Aerospike with **one record per counter holding a map of buckets**, pruned in the write path. They credit the *schema* change for most of the gains: about 3 TB → 1 TB and p99 roughly halved. We mirror that model on Redis 8, which runs on a home server and offers both a map type (hash) and native per-field expiry.

## Decision

**Layout:**

```
key   = cs:{<counter key>}:<tier>        e.g. cs:{payment_failed_by_card:c_42}:15m
field = bucket start, unix seconds       e.g. 1760702400
value = count
```

- `{…}` is a Redis Cluster *hash tag*, so all tiers of one counter share a slot. That's harmless on one node and keeps cluster options open.

**Write: one Lua script per event** (`EVAL`, one round trip). For each bucket it runs:
1. `HINCRBY key field delta`: atomic increment, no read-modify-write (ADR 0006's requirement).
2. `HPEXPIREAT key <bucket end + tier retention> FIELDS 1 field`: native per-field expiry (Redis 7.4+). This is the same rule the Postgres cleanup uses (ADR 0002), so both stores forget the same buckets at the same moment.
3. `EXPIRE key <width + retention + 1 h>`: a key-level safety net for counters that stop receiving writes. Grab's record-level TTL plays the same role.

**Read:** one `HMGET` per hash. StackExchange.Redis pipelines concurrent commands over one connection.

**Durability:** `appendonly yes`, `appendfsync everysec`. A machine crash can lose up to about 1 s of increments.

**Connection:** `AbortOnConnectFail=false` with 2 s timeouts. The API starts even if Redis is down, and calls fail fast with `StorageUnavailableException`. During most of a migration the API doesn't depend on Redis yet, so Redis must not be able to take it down.

## Alternatives considered

| Option | Why not |
|---|---|
| PostgreSQL row per counter with a `jsonb` map | Same engine, so it isolates the schema change, which is attractive. But every increment rewrites the whole jsonb value as a new tuple version (MVCC bloat), and all of a hot counter's increments serialise on one row lock. Kept as a stretch goal for comparison. |
| Redis key per bucket (`INCR cs:key:15m:1760702400`) | Mirrors the *old* row-per-bucket model (many tiny keys, about 50+ bytes of overhead each). It's the design Grab moved away from. |
| Redis sorted set per counter (score = bucket start) | Supports range queries, but the count must be encoded in the member, and increments become read-modify-write. |
| Pipeline/`MULTI` instead of Lua | `MULTI/EXEC` would also be atomic, but it's more round-trip-sensitive and no simpler. The Lua script also documents the per-bucket write in one readable place. |
| Prune old fields in the script (like Grab's `MapRemoveByKeyRangeOp`) | `HPEXPIREAT` gives the same result without scanning fields on every write. |

## Consequences

- Redis's memory **is** the storage cost, so "bytes per million events" will be compared against PostgreSQL's on-disk size (`MEMORY USAGE` vs `pg_total_relation_size`).
- A Lua script that errors midway does **not** roll back the commands it already ran. Redis scripts are isolated, not transactional. Only a bug can make this script error (bad arguments), and parity checks in M6 would surface it.
- In Redis Cluster, one script touching several counters (several slots) would be rejected. The fix would be one script per slot. That's not needed on a single node; it's noted for the record.
- **Durability differs from PostgreSQL** (≈1 s of loss vs none). Grab accepted a similar tradeoff by lowering replication: counters "self-heal" as new events arrive. The migration report should say so explicitly.
