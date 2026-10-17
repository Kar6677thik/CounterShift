# 05: The new backend: Redis hashes, Lua and native expiry

**Batch B · Milestone M5** · Field Guide: §2.3 (LSP), §12, §13, §17, §23

## What we built

| Piece | Where |
|---|---|
| Redis backend | `src/CounterShift.Storage.Redis/RedisCounterBackend.cs` |
| Registration (lazy, non-blocking connect) | `src/CounterShift.Storage.Redis/RedisStorage.cs` |
| Contract run + Redis-specific tests | `tests/CounterShift.Storage.Tests/RedisBackendTests.cs` |
| Local Redis (AOF, fsync every second) | `deploy/compose/docker-compose.yml` |
| One-setting backend switch | `CounterShift:Storage:Backend` → `CounterStore` |
| **The cautionary test** | `tests/CounterShift.Api.Tests/NaiveSwitchTests.cs` |

### Data layout

```
cs:{payment_failed_by_card:c_42}:1m    → { 1760702820: 2, 1760702880: 1, … }   (fields expire after bucket end + 3 h)
cs:{payment_failed_by_card:c_42}:15m   → { 1760702400: 3 }                      (bucket end + 8 d)
cs:{payment_failed_by_card:c_42}:1h    → { 1760702400: 3 }
cs:{payment_failed_by_card:c_42}:1d    → { 1760659200: 3 }
```

Compare with PostgreSQL, where those are **4 rows per bucket per counter**, plus two index entries each. Here it's **one record per counter per tier**, with buckets as fields inside it. That's Grab's redesign.

## Why this way

- **Why one hash per counter-and-tier?** It mirrors Grab's new model (one record per counter with a map of buckets), and grouping a counter's buckets together is what made their reads cheaper. See ADR 0008 for the options we rejected.
- **Why Lua?** One event touches up to 8 buckets. One `EVAL` means one round trip, and Redis runs the script without interleaving other commands, so a concurrent reader never sees half an event.
- **Why per-field expiry instead of a cleanup job?** Redis 7.4 added `HPEXPIREAT`, which expires individual hash fields. Each bucket now expires at exactly the moment the Postgres cleanup *would* delete it (bucket end + retention, ADR 0002). That matters in M6: when we compare the two stores, they must agree on which old buckets still exist.
- **Why `AbortOnConnectFail=false`?** If Redis is down when the API starts, the API still starts, and Redis calls fail fast with `StorageUnavailableException`. During the first half of a migration nothing *needs* Redis, so it must not be a single point of failure.

## Why now

M6 needs two real backends behind one interface before it can compare them. And the contract tests from M4 now pay off: Redis had to satisfy the same 9 tests as PostgreSQL, including "no lost updates under concurrency", **and it passed all of them on the first run**.

## The most important test in this milestone

`NaiveSwitchTests.Flipping_the_backend_without_migrating_data_silently_allows_fraud`:

1. On PostgreSQL: a card gets 6 failed payments, so the next payment is `block`.
2. Change one setting, `Storage:Backend = redis`.
3. Same card, same request: **`allow`**.

There were no errors and no 500s, and every health check was green. The new store simply has no history. With the M4 seam, switching is *easy*, and that's precisely what makes it dangerous. Branch by abstraction makes migration *possible*; it doesn't make it *safe*. M6 adds what's missing:

- **dual writes**, so the new store starts collecting history;
- a **backfill**, so it gets the old history;
- **shadow reads**, so we can *prove* the two agree;
- a **gradual split**, so if we're wrong, only a little traffic is affected.

## What broke / surprised us

1. **Test data broke retention, not the backend.** The contract tests first used a fixed date (noon) and wrote 500 one-minute buckets stretching 8 hours back. That's far past the 1m tier's 3-hour retention, so Redis (correctly) expired them on arrival. PostgreSQL has no clock-based expiry at write time, so it hadn't noticed. Fix: anchor contract tests to the current time and keep bucket ages inside retention. It's a reminder that "real" stores have their own clocks.
2. **I had added a clamp** that kept already-expired writes alive for 1 second "just in case". That hid the issue above and made the backend lie. It's gone: an expired bucket is dropped immediately, as the contract says. Neither ingestion (ADR 0003) nor backfill ever writes that far back.
3. **The whole API suite passes on Redis.** Running `CounterShift__Storage__Backend=redis dotnet test …` gives 23 of 25 passing. The 2 failures query or stop PostgreSQL directly, so they are tests *of PostgreSQL*, not of behaviour.

## Concepts introduced

- Redis hashes, `HINCRBY`, per-field TTLs (`HPEXPIREAT`, Redis 7.4+), key TTLs (§12.6)
- Lua scripting: isolation without rollback (§11.2)
- Durability tradeoffs: AOF `everysec` vs a PostgreSQL commit (§13)
- Cluster hash tags and why multi-key scripts are slot-sensitive (§10.16)
- Fail-fast clients: connect timeouts, `AbortOnConnectFail` (§17)
- LSP in practice: a new implementation proven by an existing contract suite (§2.3)

## Commands

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
docker exec -it countershift-redis-1 redis-cli
  > KEYS cs:*
  > HGETALL "cs:{payment_failed_by_card:c_42}:15m"
  > HPEXPIRETIME "cs:{payment_failed_by_card:c_42}:15m" FIELDS 1 <field>

# run the API on Redis instead of Postgres (no migration, so expect empty history)
CounterShift__Storage__Backend=redis ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/CounterShift.Api --no-launch-profile --urls http://localhost:5072
```

## Self-check

1. Why does each Redis field expire at *bucket end + retention* and not at *write time + retention*?
2. A Lua script runs `HINCRBY` on 3 buckets and then fails on the 4th. What state is Redis in? How is that different from the PostgreSQL upsert?
3. What could you lose if the Redis host loses power, with `appendfsync everysec`?
4. Why must Redis being down at API startup *not* crash the API?
5. In `NaiveSwitchTests`, nothing failed. Which of your monitoring signals would have caught the problem in production? (Probably none, and that is the point of shadow reads.)
