# 03: Break: lost updates and deadlocks → Prove: atomic upserts

**Batch A · Milestone M3** · Experiments B1, B5 · Field Guide: §11 (2, 3, 4, 6, 8, 9, 10, 11), §10.12, §14, §17

## What we did

1. Built a measurement harness, `experiments/CounterShift.Experiments`, that runs one SQL pattern under real concurrency against a throwaway PostgreSQL and prints a table.
2. **Broke it (B1):** ran the exact v0 increment with 50 concurrent writers.
3. Compared four strategies, then **fixed the API** with an atomic multi-row upsert (ADR 0006).
4. **Broke the fix (B5):** showed that multi-row upserts can deadlock, and that a consistent lock order prevents it.
5. Found and fixed a third problem while testing the fix: **connection exhaustion**.
6. Added `ConcurrencyTests`: 200 simultaneous events through the real API must lose nothing in any tier.

## B1 results: 50 writers × 1,000 increments on one bucket (expected 50,000)

| Strategy | Final count | Lost | Throughput | Notes |
|---|---:|---:|---:|---|
| v0 read-modify-write | **1,050** | **97.9%** | 1,479/s | plus 32 calls crashed with `23505` |
| **atomic upsert** | 50,000 | 0 | **7,840/s** | the fix: 5× faster *and* correct |
| `SELECT … FOR UPDATE` | 50,000 | 0 | 625/s | correct, 12× slower than upsert |
| SERIALIZABLE + retry | 50,000 | 0 | 142/s | correct; **1,251,593 retries** |

Raw output with machine specs: [`docs/results/b1-lost-updates.md`](../results/b1-lost-updates.md).

### Why the transaction didn't help

The v0 code ran inside a transaction, so many people expect it to be safe. Here is the timeline with two writers at READ COMMITTED, PostgreSQL's default isolation level:

```
Writer A                                  Writer B
BEGIN                                     BEGIN
SELECT count → 7                          SELECT count → 7        ← both read 7: plain reads take no lock
UPDATE count = 8   (A now locks the row)
                                          UPDATE count = 8         ← blocks, waiting for A's row lock
COMMIT
                                          (unblocks) writes 8     ← B's value was computed from a stale read
                                          COMMIT
Result: 8. Two increments happened; one was lost.
```

A transaction makes **your writes** atomic and isolated. It does **not** promise that the value you *read* is still current when you write. READ COMMITTED only guarantees you never read *uncommitted* data. With 50 writers, almost every increment collides, which is why 97.9% were lost.

**The "first insert" race (the 32 crashes):** when the row doesn't exist yet, several writers see "no row" and all try to `INSERT`. One wins, and the rest fail with `23505 unique_violation`. In the API, that would have been a 500 error *and* a lost event.

### Why the upsert works

`INSERT … ON CONFLICT DO UPDATE SET count = counter_buckets.count + EXCLUDED.count`. The database finds the row, **locks it**, reads the *current* value, adds and writes, all inside one statement. A second writer blocks on the row lock, and when it proceeds it re-reads the value the first writer committed. There is no gap between read and write for anyone to slip into. The insert race disappears too, because `ON CONFLICT` turns "row already exists" into an update instead of an error.

### Why the other correct strategies are slower

- **`FOR UPDATE`** also locks the row, but holds the lock across **three round trips** (insert-if-missing, select, update). Every other writer waits for all of them.
- **SERIALIZABLE** lets everyone proceed optimistically, detects the conflict at write time (`40001`) and makes the loser retry. On one hot row nearly everyone loses, about 25 retries per success. Optimistic strategies are great when conflicts are *rare*. A counter under attack is the opposite.

## B5 results: deadlocks (10 writers × 30 statements × 10 shared rows)

| Lock order inside each multi-row upsert | Deadlocks | Lost increments | Elapsed |
|---|---:|---:|---:|
| shuffled | **297 of 300 statements** | 2,970 | 282 s |
| **sorted** | 0 | 0 | **0.09 s** |

The fixed API writes all of an event's buckets in **one** statement (for example 8 rows for `payment.failed`). Two events that share some rows (same card, different users) lock them one by one. If A locks row 1 then wants row 2, while B locked row 2 and wants row 1, both wait forever. PostgreSQL detects this after `deadlock_timeout` (1 s) and kills one statement, and *all* of its increments are lost. Sorting the rows (`ORDER BY counter_key, granularity, bucket_start`) gives every writer the same order, so a wait cycle can't form. This is *consistent lock ordering* (Field Guide §11.11).

Subtle point: the order only has to be **consistent**, not "correct". Sorting in SQL works even though PostgreSQL collation and C#'s ordinal string sort disagree on some strings, because all writers use the same SQL sort.

The default experiment size (50 × 200 × 20) never finished: each deadlock stalls for a full second. Deadlocks don't only lose data. They also turn milliseconds into seconds. Raw output: [`docs/results/b5-deadlocks.md`](../results/b5-deadlocks.md).

## The third problem: connection exhaustion

The first run of `ConcurrencyTests` (200 simultaneous events) failed with `53300: sorry, too many clients already`. Npgsql's default pool allows **100** connections, and PostgreSQL's default `max_connections` is also **100**. One busy process can take every connection the server has, starving other replicas, migrations and admins. With 2 replicas in k3s, it's 200 pool slots against 100 server connections.

Fix: `CounterShift:MaxPoolSize = 20`. The rule is **replicas × pool size < max_connections, with headroom**. When all 20 are busy, request 21 *waits* for a free connection. This is backpressure (Field Guide §17): the queue forms in our process, where it's cheap, instead of overwhelming the database.

## Why now

Every later milestone depends on increments being correct. The parity checks in M6 compare the Legacy and New stores. If the Legacy store lost updates, every comparison would show "mismatches" caused by our own bug, not by the migration. Correctness first, then migration.

## What surprised us

- How *extreme* the loss was: 97.9%, not "a few percent". Contention on a hot key is close to the worst case, and it is exactly the case fraud detection cares about, because an attacker hammering one card is a hot key.
- The fix was **faster** as well as correct. Correctness and performance aren't always a tradeoff. Here, fewer round trips and shorter lock holds won on both.
- Load-testing the fix revealed a problem in a completely different place (the pool). Concurrency tests are worth writing for that reason alone.

## Concepts introduced

- Lost updates; isolation levels and what READ COMMITTED actually promises (§11.6–11.9)
- Atomic operations and upserts (§11.2)
- Pessimistic (`FOR UPDATE`) vs optimistic (SERIALIZABLE / retry) concurrency control (§11.3–11.4)
- Deadlocks and consistent lock ordering (§11.10–11.11)
- Connection pooling limits and backpressure (§10.12, §17)
- Measuring instead of guessing: a harness, raw outputs saved with machine specs (§27)

## Commands

```bash
dotnet run -c Release --project experiments/CounterShift.Experiments -- lost-updates            # about 8 min (SERIALIZABLE is slow)
dotnet run -c Release --project experiments/CounterShift.Experiments -- lost-updates --increments 100   # quicker
dotnet run -c Release --project experiments/CounterShift.Experiments -- deadlocks --writers 10 --statements 30 --rows 10
dotnet test --project tests/CounterShift.Api.Tests -- --filter-class "*ConcurrencyTests"
```

## Self-check

1. Draw the timeline for two writers losing an update at READ COMMITTED. Where exactly is the gap?
2. Would REPEATABLE READ fix the v0 code? What would happen instead? (Hint: look up what PostgreSQL does at REPEATABLE READ when the row you are updating was changed by a transaction that committed after yours started. Bonus: add it as a fifth strategy in `IncrementStrategies.cs` and measure it.)
3. Why did 32 v0 calls fail with `23505`, and why can't that happen with `ON CONFLICT`?
4. When would you choose `SELECT … FOR UPDATE` over an atomic upsert?
5. Why does sorting in SQL prevent deadlocks even though PostgreSQL and C# sort strings differently?
6. Two replicas, Postgres `max_connections = 100`, three admins who sometimes connect. What `MaxPoolSize` would you choose, and why?
