# Runbook: migrate counters from PostgreSQL to Redis with zero downtime

Every step is a change to the storage-mode file (locally `storage-mode.json`; in Kubernetes the `countershift-storage-mode` ConfigMap, applied in M9). Each step lists **how to check it** and **how to undo it**. Never skip a gate.

Watch throughout:

```bash
curl -s <api>/admin/storage-mode | jq '{version, summary, loadedAt, parity}'
```

With more than one replica, check every pod. A step has taken effect only when **all** pods show the new `version`.

---

## Phase 0: baseline

```json
{ "CounterShift": { "Storage": {
  "Write": { "Mode": "Single", "Primary": "postgres" },
  "Read":  { "Mode": "Single", "Primary": "postgres" } } } }
```

Record baseline latency (p50/p99 per backend) before changing anything (M7 dashboards).

## Phase 1: dual writes (T0)

```json
"Write": { "Mode": "Dual", "Primary": "postgres", "Secondary": "redis" },
"Read":  { "Mode": "Single", "Primary": "postgres" }
```

- **Check:** every pod shows `Dual(postgres→redis)`. **T0 = the latest `loadedAt` across pods.** Write it down.
- **Check:** `cs.storage.secondary_write.failures` stays at 0.
- **Undo:** back to Phase 0. Redis just stops receiving writes; nothing reads it yet.

## Backfill (pass 1, soon after T0)

```bash
dotnet CounterShift.Api.dll backfill --from postgres --to redis --dual-write-since <T0>
```

- Copies every *final* pre-T0 bucket. Prints `Not final yet: N` and **"Run again after <time>"**, which is today's day bucket closing.
- Safe to re-run at any time (SET is idempotent).
- It refuses to run unless the current mode writes to `redis`.

## Backfill (pass 2, after the time it printed, about T0 + 1 day)

Same command. Expected: `Not final yet: 0`.

## Phase 2: shadow reads, ramp 5 → 20 → 50 → 100 %

```json
"Read": { "Mode": "Shadow", "Primary": "postgres", "Secondary": "redis", "Percent": 5 }
```

- **Gate at each step:** `parity.mismatch == 0` over at least 100,000 comparisons (ADR 0011); `race` low and stable; `dropped` ≈ 0 (if not, the shadow can't keep up, so lower the percent).
- A mismatch is a **real problem**. `parity.lastMismatch` names the bucket and both values. Typical causes: backfill not run, or a secondary write failed. Fix, re-run the backfill, then restart the step.
- **Undo:** `Read: Single(postgres)`. Callers were never served by Redis.

## Phase 3: split traffic, ramp 5 → 30 → 70 → 100 %

```json
"Read": { "Mode": "Split", "Primary": "postgres", "Secondary": "redis", "Percent": 5 }
```

- Now Redis **serves** that share of counters. Watch error rate and p99 for `role=secondary`.
- **Rollback drill (do it at 70%, under load):** change to `Read: Single(postgres)` and measure (a) the time until every pod reports the new version and (b) non-2xx responses during the switch (expected: 0). Then return to 70%.
- **Undo:** lower the percent, or `Read: Single(postgres)`.

## Phase 4: Redis becomes primary; PostgreSQL stays current

```json
"Write": { "Mode": "Dual", "Primary": "redis", "Secondary": "postgres" },
"Read":  { "Mode": "Single", "Primary": "redis" }
```

- Write failures now fail requests if *Redis* is down, and PostgreSQL failures are tolerated. Check `/readyz` reflects this.
- **Undo:** back to Phase 3 (100%) or Phase 1. PostgreSQL has every write, so no backfill is needed.
- Stay here until confident: days, not minutes.

## Phase 5: contract

```json
"Write": { "Mode": "Single", "Primary": "redis" },
"Read":  { "Mode": "Single", "Primary": "redis" }
```

- **Point of no easy return:** PostgreSQL stops receiving writes. Rolling back after this needs a reverse backfill, which isn't built.
- Later: remove the Postgres backend registration and drop `counter_buckets` (a separate change, after a waiting period).

## Never

- Read from a store that isn't being written. The service rejects this, but don't try.
- Change `Ingestion:MaxLateness` during a migration. The backfill and parity rules depend on it (ADRs 0003, 0011, 0012).
- Change two knobs in one step.
