# Glossary

Terms are added the first time they appear in a milestone. The milestone that introduced each term is in brackets.

| Term | Definition |
|---|---|
| **Adapter** [M4] | A class that converts one interface into another; `PostgresCounterBackend` adapts SQL/Npgsql to `ICounterBackend`. |
| **ADR** [M0] | Architecture Decision Record. A short, immutable note recording one decision and why it was made. See `docs/adr/`. |
| **Advisory lock** [M2] | A PostgreSQL lock on an arbitrary number rather than a row; used so only one pod runs migrations at a time. |
| **AOF (append-only file)** [M5] | Redis's write log for durability. With `appendfsync everysec`, at most about 1 s of writes can be lost on a crash. |
| **Architecture fitness test** [M4] | An automated test that checks structural rules (e.g. "the API doesn't reference database drivers"). |
| **Atomic operation** [M3] | A change that happens completely or not at all, with no observable in-between state. `count = count + 1` in one SQL statement is atomic per row. |
| **Backpressure** [M3] | Making callers wait (or refusing them) when a resource is saturated, instead of letting load pile up on it. A bounded connection pool is backpressure. |
| **Branch by abstraction** [M4] | Introduce an interface in front of old code so new and old implementations can coexist while you migrate, keeping the main branch releasable. |
| **Bucket** [M1] | The stored count for one counter key in one aligned time slot, e.g. `[12:00, 12:15)`. |
| **Central Package Management** [M0] | NuGet feature (`Directory.Packages.props`) that keeps every package version in one file. |
| **Characterization test** [M4] | A test that records existing behaviour so a refactor can prove it changed nothing. |
| **CI** [M0] | Continuous Integration: every push is built and tested automatically (`.github/workflows/ci.yml`). |
| **Composition root** [M4] | The single place where concrete implementations are chosen and wired together (`Program.cs`). |
| **Connection pool** [M3] | A reusable set of open database connections. Its maximum size must respect the server's `max_connections` across all replicas. |
| **Contract test** [M4] | One test suite run against every implementation of an interface, to prove they're interchangeable. |
| **Counter key** [M1] | Counter name + entity value, e.g. `payment_failed_by_card:c_42`. The unit of storage. |
| **Deadlock** [M3] | Two transactions each holding a lock the other needs. Postgres detects it after `deadlock_timeout` (1 s) and aborts one (SQLSTATE 40P01). |
| **Decomposition** [M1] | Turning a time window into the fewest aligned buckets that cover it (ADR 0002). |
| **Effective window** [M1] | The time range actually summed, after the start edge has been rounded down. Always returned to callers. |
| **Event time vs processing time** [M1] | When something happened vs when we received it. We bucket by event time (ADR 0003). |
| **Facade** [M4] | One simple entry point over a more complex subsystem; `CounterStore`. |
| **Fail closed / fail open** [M2] | On dependency failure, refuse (503) vs allow by default. CounterShift fails closed (ADR 0004). |
| **Granularity / tier** [M1] | A bucket width: 1m, 15m, 1h, 1d. |
| **Hash tag** [M5] | The `{…}` part of a Redis key. In a cluster, only that part decides the slot, so related keys stay together. |
| **Isolation level** [M3] | How much concurrent transactions can see of each other: READ COMMITTED (Postgres default), REPEATABLE READ, SERIALIZABLE. |
| **Liveness vs readiness** [M0] | Liveness (`/healthz`): "is the process alive? If not, restart it." Readiness (`/readyz`): "can it serve traffic right now? If not, stop sending it requests." Mixing them up causes restart loops when a database blips. |
| **Lock ordering** [M3] | Acquiring locks in one consistent order everywhere, so a cycle (deadlock) is impossible. |
| **Lost update** [M3] | Two writers read the same value and both write back value+1, so one increment disappears. |
| **Lua script (Redis)** [M5] | Server-side script run without interleaving other commands: isolated, but not rolled back on error. |
| **Microsoft.Testing.Platform (MTP)** [M0] | The newer .NET test runner. xunit v3 test projects are executables that host it. |
| **Mutation testing** [M1] | Deliberately introducing bugs to check that the tests catch them. |
| **N+1 queries** [M2] | Issuing one query per item instead of one batched query; avoided with `unnest`. |
| **Per-field expiry** [M5] | Redis 7.4+ `HPEXPIREAT`: individual fields of a hash expire at their own times. |
| **Pessimistic vs optimistic locking** [M3] | Lock first then change (`SELECT … FOR UPDATE`), vs change freely and detect conflicts at commit (SERIALIZABLE, version columns), retrying on conflict. |
| **Problem details** [M2] | RFC 9457 standard JSON error format (`application/problem+json`). |
| **Property-based testing** [M1] | Stating rules that must hold for all inputs and letting a tool (FsCheck) search for counter-examples. |
| **Read-modify-write** [M2] | Read a value, change it in application code, write it back. Unsafe under concurrency unless protected (see M3). |
| **Retention** [M1] | How long a tier's buckets are kept. A bucket is deleted only once it *ends* at or before `now − retention`. |
| **Schema migration** [M2] | A versioned, ordered change to the database schema, applied exactly once per environment. |
| **Seam** [M4] | A place where behaviour can be changed without editing the code that depends on it. |
| **Shrinking** [M1] | When a property fails, FsCheck simplifies the failing input to a minimal counter-example. |
| **Testcontainers** [M0] | A library that starts real throwaway Docker containers (e.g. PostgreSQL) for a test run, then removes them. |
| **Upsert** [M3] | Insert-or-update in one statement: `INSERT … ON CONFLICT … DO UPDATE`. |
| **Write amplification** [M1] | One logical write causing many physical writes (1 event → counters × tiers increments). |
