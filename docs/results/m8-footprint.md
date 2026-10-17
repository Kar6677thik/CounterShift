# M8: storage footprint per million events (raw output)

- **Date:** 2026-10-17
- **Command:** `dotnet run -c Release --project experiments/CounterShift.Experiments -- footprint` (defaults: 1,000,000 events, 100,000 cards, 50,000 users, 32 concurrent writers)
- **Machine:** development laptop, 14 logical CPUs, Windows 11, Docker Desktop (WSL2). Fresh PostgreSQL 17 and Redis 8 containers (AOF everysec).
- **Workload:** event times uniform over the last 2 h (every tier, including 1m, retained in both stores); 60% payment.failed / 25% login.failed / 15% ride.requested; 30% of card traffic on 1% of cards; one event per write call, planned by the real `CounterCatalog`.

Increments planned: 6,400,000 (6.4 per event: counters × 4 tiers)
Distinct buckets: PostgreSQL rows 3,356,445 · Redis hash fields 3,356,445 (identical ✓)

| Store | Measured as | Size | Per 1M events | Bytes per bucket | Write throughput |
|---|---|---:|---:|---:|---:|
| PostgreSQL | table + indexes, as written (0 dead tuples) | 552.1 MiB | 552.1 MiB | 172 | 4,792 events/s |
| PostgreSQL | after VACUUM (space reusable, file not shrunk) | 552.1 MiB | 552.1 MiB | 172 | |
| PostgreSQL | after VACUUM FULL (compacted; table alone 245.1 MiB) | 456.7 MiB | 456.7 MiB | 142 | |
| Redis | sum of MEMORY USAGE per key | 149.7 MiB | 149.7 MiB | 46 | 15,045 events/s |
| Redis | INFO used_memory growth | 196.5 MiB | 196.5 MiB | 61 | |

PostgreSQL sizes are on disk; Redis sizes are RAM. That's the real cost of each in its own currency.

## Reading it

- **The same 3,356,445 buckets in both stores.** The row count and the hash-field count match exactly, which is a free end-to-end consistency check.
- **Redis is 2.8–3.7× smaller** (150 MiB vs 457–552 MiB), depending on PostgreSQL's bloat state and which Redis measure you use. Grab reported 3 TB → 1 TB from their map-per-counter redesign. Same shape of result, different engines.
- **Where PostgreSQL's bytes go:** the table alone is 245 MiB after compaction. The rest (about 211 MiB) is the two indexes: the primary key and the retention index `(granularity, bucket_start)`. Each row also carries a 24-byte tuple header plus the full counter key text.
- **Where Redis saves:** the counter key is stored once per (counter, tier) hash, not once per bucket. Fields are small integers. Small hashes use Redis's compact listpack encoding.
- **Bloat:** VACUUM FULL reclaimed about 17%. `n_dead_tup` read 0, most likely because autovacuum had already processed the table during the load, so treat 17% as a lower bound on update bloat. Plain VACUUM marks space reusable but doesn't shrink the file, so its size is unchanged.
- **Different currencies:** PostgreSQL's bytes are disk (cheap, plentiful); Redis's are RAM (scarcer). On the home server (about 4.9 GB free), 1M events/day at 8-day retention is roughly 8 × 150 MiB ≈ 1.2 GiB of Redis RAM. That's a real sizing constraint for M9.
- **Write throughput** (4,792 vs 15,045 events/s) is client-observed with 32 concurrent single-event calls. It includes PostgreSQL's fsync on commit; Redis fsyncs once per second.
