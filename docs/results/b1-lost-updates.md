# B1: lost updates (raw output)

- **Date:** 2026-10-17
- **Command:** `dotnet run -c Release --project experiments/CounterShift.Experiments -- lost-updates`
- **Machine:** development laptop, 14 logical CPUs, Windows 11 (10.0.26100), .NET 10.0.12, PostgreSQL 17 in Docker Desktop (WSL2, 7.2 GB). These are *development* numbers; the final report re-runs on the home server.

## 50 concurrent writers × 1000 increments on ONE bucket (expected 50,000)

| Strategy | Final count | Lost | Lost % | Failed calls | Retries | Elapsed | Throughput |
|---|---:|---:|---:|---:|---:|---:|---:|
| `naive-rmw` | 1,050 | 48,950 | 97.9% | 32 (23505×32) | 0 | 33.80 s | 1,479/s |
| `atomic-upsert` | 50,000 | 0 | 0.0% | 0 | 0 | 6.38 s | 7,840/s |
| `select-for-update` | 50,000 | 0 | 0.0% | 0 | 0 | 80.04 s | 625/s |
| `serializable-retry` | 50,000 | 0 | 0.0% | 0 | 1,251,593 | 351.17 s | 142/s |

- `naive-rmw`: v0: SELECT, +1 in C#, INSERT or UPDATE (READ COMMITTED transaction)
- `atomic-upsert`: INSERT ... ON CONFLICT DO UPDATE SET count = count + 1 (single statement)
- `select-for-update`: ensure row exists, SELECT ... FOR UPDATE, +1 in C#, UPDATE
- `serializable-retry`: naive read-modify-write at SERIALIZABLE, retry on 40001/23505

SQLSTATE 23505 = unique_violation (two writers both saw "no row" and both INSERTed); 40001 = serialization_failure.

"Throughput" counts *intended* increments per second. For `naive-rmw` it is flattering: most of those increments were lost.
