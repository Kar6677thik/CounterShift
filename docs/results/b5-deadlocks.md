# B5: deadlocks from inconsistent lock order (raw output)

- **Date:** 2026-10-17
- **Command:** `dotnet run -c Release --project experiments/CounterShift.Experiments -- deadlocks --writers 10 --statements 30 --rows 10`
- **Machine:** development laptop, 14 logical CPUs, Windows 11 (10.0.26100), .NET 10.0.12, PostgreSQL 17 in Docker Desktop (WSL2).

## 10 writers × 30 multi-row upserts, each touching the same 10 rows

| Row order inside each statement | Deadlocks (40P01) | Lost increments | Elapsed |
|---|---:|---:|---:|
| shuffled (random per statement) | 297 | 2,970 | 282.32 s |
| **sorted** (consistent lock order) | 0 | 0 | 0.09 s |

A deadlocked statement is rolled back entirely: all its row increments are lost unless the caller retries.

## Note on scale

The first attempt used the defaults (50 writers × 200 statements × 20 rows). It was stopped after 10 minutes without finishing. Under heavy contention almost every shuffled statement deadlocks, and each deadlock costs PostgreSQL's `deadlock_timeout` (1 s) before detection. The slowness *is* the finding: deadlocks don't just lose writes, they convert lock waits into whole-second stalls.
