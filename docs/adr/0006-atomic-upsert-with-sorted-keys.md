# ADR 0006: Increment with one atomic multi-row upsert, with rows locked in a consistent order

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §6.1, experiments B1 and B5
- **Supersedes:** the v0 read-modify-write (tag `v0-scattered`)

## Context

v0 incremented each bucket with `SELECT` → `+1` in C# → `INSERT`/`UPDATE`, inside a READ COMMITTED transaction. Experiment B1 (50 writers × 1,000 increments on one bucket) measured:

| Strategy | Final count (of 50,000) | Lost | Throughput |
|---|---:|---:|---:|
| v0 read-modify-write | 1,050 | **97.9%**, plus 32 crashed calls (23505) | 1,479/s |
| **atomic upsert** | 50,000 | 0 | **7,840/s** |
| `SELECT … FOR UPDATE` | 50,000 | 0 | 625/s |
| SERIALIZABLE + retry | 50,000 | 0 | 142/s (1.25 M retries) |

## Decision

Each event's increments are written by **one statement**:

```sql
INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count)
SELECT * FROM unnest(@keys, @granularities, @starts, @deltas)
ORDER BY counter_key, granularity, bucket_start
ON CONFLICT (counter_key, granularity, bucket_start)
DO UPDATE SET count = counter_buckets.count + EXCLUDED.count;
```

1. **Atomic:** the database reads, adds and writes each row while holding that row's lock, so concurrent increments queue up instead of overwriting each other.
2. **All-or-nothing per event:** a single statement is implicitly its own transaction.
3. **`ORDER BY` gives a consistent lock order:** two events touching overlapping rows lock them in the same order, so neither can hold a row the other needs while waiting for one it holds. That prevents deadlocks (experiment B5). The order only has to be *consistent across writers*, not any particular order. That's why sorting in SQL works even though PostgreSQL's text collation orders strings differently from C#'s ordinal sort.
4. **Duplicates merged first:** `ON CONFLICT DO UPDATE` raises an error if one statement touches the same row twice, so increments for the same bucket are summed in C# beforehand.
5. **Bounded connection pool** (`MaxPoolSize`, default 20): found while load-testing this fix. Npgsql's default pool of 100 equals PostgreSQL's default `max_connections`, so one busy replica could take every connection the server has (`53300 too many clients`).

## Why not the alternatives

| Option | Correct? | Why not |
|---|---|---|
| Read-modify-write in a transaction | **No** | READ COMMITTED doesn't stop two transactions from reading the same old value. A transaction makes its writes atomic, but it doesn't make *your read* stay true. |
| `SELECT … FOR UPDATE` | Yes | Three statements and a lock held across round trips: 12× slower here. Worth it only when the new value needs application logic the database can't compute. |
| SERIALIZABLE + retry | Yes | Correct but very wasteful on a hot row: about 25 retries per successful increment. It shines when conflicts are rare and the invariants are complex. |
| Optimistic version column | Yes | Same retry storm as SERIALIZABLE on a hot counter. |

## Consequences

- A hot counter (one card hammered by an attacker) serialises on its row lock. That is still correct, and the throughput ceiling is now measured, not guessed.
- The `+` happens in SQL, so the new store must offer an equivalent **atomic increment primitive**. Redis `HINCRBY` does. This becomes a requirement on every backend in the M4/M5 contract tests.
