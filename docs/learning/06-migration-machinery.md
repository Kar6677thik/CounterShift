# 06: The migration machinery: dual writes, shadow parity, split, backfill, live reload

**Batch B · Milestone M6** · Field Guide: §6 (parallel run, expand-and-contract, feature toggles, data migration), §11, §14, §17, §24

This is the heart of Project 2: Grab's July 2026 migration, rebuilt.

## What we built

| Piece | Where | Job |
|---|---|---|
| `StorageMode` (+ validation) | `Storage/Migration/StorageMode.cs` | Two knobs, **write** (`Single` / `Dual`) and **read** (`Single` / `Shadow` / `Split`), and the rules that make a mode safe |
| `StorageModeProvider` | `…/StorageModeProvider.cs` | Live reload from a watched file; rejects unsafe changes and keeps the last good mode |
| `Rollout` | `…/Rollout.cs` | Deterministic "is this counter in the X%?" via XxHash64 |
| `CounterStore` (facade, rewritten) | `Storage/CounterStore.cs` | Routes every read and write by the current mode; times every call |
| `ShadowComparer` | `…/ShadowComparer.cs` | Background, bounded, per-bucket parity: match / mismatch / race / error |
| `StorageMetrics` | `…/StorageMetrics.cs` | Duration per backend and role, parity, secondary failures, drops, reload failures |
| `BackfillRunner` + `backfill` verb | `…/Backfill.cs`, `Api/BackfillCommand.cs` | Copies final pre-T0 history with SET; re-runnable |
| `/admin/storage-mode` | `Api/Program.cs` | Watch a cutover live |
| Runbook | `docs/runbooks/migration.md` | The exact operational procedure, with gates and undo steps |

**The API's endpoints did not change.** The facade from M4 absorbed everything.

## The plan the code enforces

```
Phase 0  Single(pg)       │ Single(pg)
Phase 1  Dual(pg→redis)   │ Single(pg)                ← T0     + backfill (pass 1, then pass 2 after a day)
Phase 2  Dual(pg→redis)   │ Shadow(pg, redis, 5→100%) ← gate: 0 closed-bucket mismatches
Phase 3  Dual(pg→redis)   │ Split(pg, redis, 5→100%)  ← rollback drill at 70%
Phase 4  Dual(redis→pg)   │ Single(redis)             ← still reversible: Postgres stays current
Phase 5  Single(redis)    │ Single(redis)             ← contract
```

At every phase up to 4, rollback is **one config edit**. The validator makes the dangerous combination impossible: you can't read from a store that isn't receiving every write. That's the `NaiveSwitchTests` disaster from M5, now refused automatically.

## Five design decisions worth being able to defend

1. **Dual writes are synchronous, and the secondary's failures are tolerated** (ADR 0009). Async fire-and-forget loses writes during pod restarts, and every lost write becomes a mysterious mismatch later. The secondary also gets its own timeout that ignores the client's cancellation: if a client hangs up between the two writes, the stores must not diverge.
2. **Split by counter key with a stable hash** (ADR 0010). The same card is always served by the same store, and raising the percent only *adds* keys. Experiment B4 shows why `GetHashCode()` would be a disaster: three processes put the same key in three different buckets (`docs/results/b4-hash-stability.md`).
3. **Parity is per bucket and classified** (ADR 0011). Differences in still-open buckets are expected (*race*). A difference in a **closed** bucket, which nothing legitimate can still change, is a *mismatch*, and the gate is zero of them.
4. **A shadow can never hurt real traffic.** Comparisons go through a bounded queue that *drops* when full. A slow or dead shadow store shows up as `dropped`/`error` metrics, never as latency.
5. **The backfill copies only final pre-T0 buckets, with SET, and can be re-run** (ADR 0012). This corrects the spec, below.

## What broke / surprised us

1. **The spec's backfill rule was wrong.** It claimed buckets newer than `T0 − MaxLateness` were complete through dual writes. But today's 1-day bucket holds events from *before* T0 (only in PostgreSQL) **and** after it (both), so dual writes never repair it. The fix: copy a bucket once it's *final* and may hold pre-T0 events, and re-run later for buckets that weren't final yet. `BackfillTests.The_bucket_that_straddles_T0_is_repaired_once_it_is_final` pins this down. Writing the design precisely enough to code it is what exposed the flaw.
2. **The parity gate catches a missing backfill, and we proved it.** In `MigrationEndToEndTests`, shadowing *before* the backfill produces mismatches (25); after it, zero new ones. With the backfill call deliberately removed, the test fails at the gate (50 mismatches). That's mutation testing applied to a *process*, not just code.
3. **Lazy dependency injection hid the reload path in tests.** The mode provider was built on first use, *after* the test had changed the config, so the "new" mode became the *initial* one and reload never ran. Production builds it eagerly at startup, and the test harness now does too.
4. **I guessed a pinned hash value** in a test (7441) instead of recording it. The test caught that immediately (actual 5136, the same number the B4 experiment printed).
5. **The real file watcher works.** Editing a JSON file on disk switched the running app v1 → v2 → v3 with no restart, and an unsafe edit was rejected with its reason in the log. The real `backfill` process copied 6 final buckets, held back 10 open ones, and printed when to re-run (00:06 the next day, when the day bucket closes).

## Why now

Everything before this made M6 possible: correct increments (M3), a seam (M4), and a second store passing the same contract (M5). Observability (M7) comes next because these metrics exist but nothing displays them yet. Running the drill under load (M8) needs both.

## Concepts introduced

- Parallel run / shadow traffic (§6.3); expand and contract (§6.4); feature toggles and runtime config (§6.5)
- Data migration: backfill, idempotent copy, finality, checkpoints vs idempotence (§6.15)
- Deterministic hashing for traffic splitting; monotonic rollouts (§20)
- Bounded queues and load shedding for non-critical work (§17)
- Immutable snapshots for configuration that changes at runtime (§14)
- Metrics with System.Diagnostics.Metrics: histograms, counters, tags (§24)

## Commands

```bash
dotnet test --project tests/CounterShift.Storage.Tests                                # modes, rollout, shadow, backfill
dotnet test --project tests/CounterShift.Api.Tests -- --filter-class "*MigrationEndToEndTests"
dotnet run -c Release --project experiments/CounterShift.Experiments -- hash-stability  # B4

# live: run the API with a mode file, then edit the file and watch
COUNTERSHIFT_STORAGE_MODE_FILE=/path/storage-mode.json ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/CounterShift.Api --no-launch-profile --urls http://localhost:5072
curl -s localhost:5072/admin/storage-mode | jq
dotnet run --project src/CounterShift.Api --no-launch-profile -- backfill --from postgres --to redis --dual-write-since <T0>
```

## Self-check

1. Why must the read validator refuse `write=Single(postgres) read=Single(redis)` even though both stores are healthy?
2. A client disconnects after the primary write succeeds. What happens to the secondary write, and why was it designed that way?
3. At 30% split, a decision reads two counters. Can one be served by Redis and the other by PostgreSQL? Is that safe? Why?
4. Explain, with a 1-day bucket, why spec §8's original backfill rule leaves Redis permanently short.
5. Why is the backfill a SET and not an increment? What would a Kubernetes Job retry do otherwise?
6. Shadow at 100% reports `race: 400, mismatch: 0, dropped: 9000`. Is it safe to move to Split? What would you change first?
7. Why does Phase 4 keep writing to PostgreSQL, and what does Phase 5 give up?
