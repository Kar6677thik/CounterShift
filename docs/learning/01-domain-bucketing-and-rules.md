# 01: The domain: bucketing, window decomposition and rules

**Batch A · Milestone M1** · Field Guide: §2 (SRP, CQS, fail fast, design by contract), §9.16 (temporal data), §23 (property-based testing), §27

## What we built

Pure logic with **no I/O and no clock reads**. `now` is always a parameter.

| Concept | Code | One line |
|---|---|---|
| Bucket width | `Time/Granularity.cs` | 1m, 15m, 1h, 1d, each with a retention period. `AlignDown` rounds an instant to its bucket start (UTC). |
| Tier set | `Time/GranularitySet.cs` | Validates that tiers *nest* and that retention grows with width. |
| Bucket | `Time/BucketSpan.cs` | `[Start, Start + Width)` at one granularity. |
| **Decomposition** | `Time/WindowDecomposer.cs` | "Last N minutes" becomes the fewest buckets. The core algorithm. |
| Counter key, bucket address | `Counters/CounterKey.cs` | `payment_failed_by_card:c_42`, and `BucketRef` = key + span, with a total sort order (needed in M3). |
| Counters | `Counters/CounterCatalog.cs` | Config-defined. Turns one event into its bucket increments, after validation. |
| Rules | `Rules/RuleBook.cs` | `Plan()` decides which buckets to read; `Evaluate()` sums them and picks the most severe outcome. |

## The algorithm, in plain words

Window "last 90 minutes" at **12:07:30**:

```
requested start 10:37:30 ──round down (1m tier is still retained 90 min back)──► 10:37

10:37 10:38 … 10:44 │ 10:45 ─ 11:00 │ 11:00 ──────── 12:00 │ 12:00 ─────── 13:00
  eight 1m buckets  │  one 15m      │      one 1h         │   one 1h (holds 12:00–12:07:30 so far)
```

At each cursor position, take the **coarsest** tier the cursor is aligned to, then move past it. Two facts make this simple:

1. **Start edge:** rounded *down*, so the cursor always begins on a bucket boundary. We over-count by less than one bucket, and over-counting is the safe direction for fraud.
2. **End edge:** it is always *now*, and nothing after now has been stored. So the last bucket may stretch into the future, and it is still exact. We never have to check the right edge.

Grab's example window `9/10 23:15:20 → 9/12 17:20:18` becomes **5 buckets**: three 15m buckets, then 1d for 9/11, then 1d for 9/12, which so far holds only today's events.

## Why this way

- **Why pure functions?** Decomposition is the riskiest logic in the project. If it's wrong, every count is wrong and nothing visibly fails. Pure functions can be tested exhaustively in milliseconds. This is *Separation of Concerns* (§2.9).
- **Why `Plan` / `Evaluate` instead of a single `Decide()`?** Because storage I/O has to happen in between. `Plan` says *what* to read, the API reads it in **one batched call**, and `Evaluate` does the arithmetic. The domain never knows a database exists, and in M4 this is exactly what lets us swap storage.
- **Why validate config in constructors?** *Fail fast* (§2.18): a typo in `appsettings.json` should stop the app at startup with every error listed at once, not cause a rule that silently never fires.
- **Why 400 for a missing entity instead of skipping the rule?** If a `payment` request lacks `cardId`, skipping R1 would *allow* a payment that should be blocked. Errors should be loud (*Principle of Least Astonishment*, §2.17).

## Why now

Everything after this (storage, migration, parity) compares **bucket values**. If bucketing is wrong, a parity check between two stores will happily report that both are equally wrong. So the domain has to be proven correct first, while it is still small.

## What broke / surprised us (worth re-reading)

1. **The spec had a hole.** It said "round the start to the finest tier", but the 1m tier is only kept for 3 hours, so a 7-day window would need 1m buckets from 7 days ago that no longer exist. Fix: round to the finest tier *still retained at that age* (ADR 0002). Writing precise code is a form of specification review.
2. **My own example test was wrong, not the code.** I expected `15m@12:00` for "last 10 minutes at 12:07:30", but 12:00 is also an *hour* boundary, so the minimal answer uses `1h@12:00`. The property tests (minimality against brute force) had already passed. The hand-written example encoded my faulty intuition.
3. **Tests that can't fail are worthless, so we tested the tests.** We deliberately broke the algorithm three ways (*mutation testing*). The first version of the property generator **missed** an off-by-one (`cursor < now`) because random instants almost never land exactly on a boundary. After biasing the generator towards aligned values, every mutant was caught:

   | Mutation | Caught by |
   |---|---|
   | `cursor <= now` → `cursor < now` | 1 example + 2 properties |
   | ignore retention when rounding the start | 2 properties |
   | finest tier instead of coarsest | 6 examples + 2 properties |

## Concepts introduced

- **Time bucketing / pre-aggregation**: trade write amplification (4 increments per counter per event) for cheap reads (§27, §9.16).
- **Event time vs processing time** (ADR 0003). Project 3 goes deeper with watermarks.
- **Greedy algorithms and why nesting makes greedy optimal.** Every coarse boundary is also a fine boundary, so taking the biggest block never blocks a better choice later.
- **Property-based testing**: generators, properties, oracles (brute-force DP), and *shrinking* (§23).
- **Mutation testing**: break the code on purpose to check that the tests notice (§23).
- **Design by contract** (§2.19): constructors enforce invariants, so an invalid `GranularitySet` can't exist.

## Commands

```bash
dotnet test --project tests/CounterShift.Domain.Tests                  # 46 tests, about 2 s
dotnet test --project tests/CounterShift.Domain.Tests -- --filter-class "*Properties"   # property tests only
```

Try it: change `CoarsestFirst` to `FinestFirst` in `WindowDecomposer.CoarsestAlignedAt` and watch which tests fail. Then revert.

## Self-check

1. Why may the last bucket in a decomposition extend past `now`, while the first may not start after `EffectiveFrom`?
2. "Last 7 days" at 12:07:30: what is `EffectiveFrom`, and why isn't it 1-minute precise?
3. Why must coarser tiers be retained at least as long as finer ones? Give a window that would break otherwise.
4. Why is over-counting acceptable for fraud and under-counting not?
5. What does the brute-force oracle in the property tests prove that the example tests can't?
6. An event stamped 10:03 arrives at 10:09. What happens, and which ADR explains why?
