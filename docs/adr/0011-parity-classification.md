# ADR 0011: Shadow parity: compare per bucket, classify match / mismatch / race / error, gate on closed buckets

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §7.2

## Context

Grab verified parity "through metrics" but didn't describe its method. A naive comparison (equal sums or not) raises false alarms: between the primary read and the shadow read, an increment can land in one store and not yet the other.

## Decision

1. The request path **only enqueues** a sampled read into a **bounded queue (10,000, drop when full)**. Four background workers read the shadow store (2 s timeout) and compare. Dropped work is counted (`cs.parity.shadow.dropped`). A shadow can never slow down or fail a real request.
2. Compare **per bucket**, not just on the sum, so we know *which* bucket differs and can log it.
3. Classify each comparison:

| Result | Rule | Meaning |
|---|---|---|
| `match` | every bucket equal | |
| `mismatch` | some differing bucket ended at or before `readAt − MaxLateness` | a **closed** bucket differs. No legitimate write can explain it: **real bug or missing data** |
| `race` | differences only in still-open buckets | expected noise from in-flight writes |
| `error` | shadow read failed or timed out | |

4. **Mismatch rate = mismatch ÷ (match + mismatch).** Races and errors are excluded but still visible.
5. **Gate for each ramp step: 0 mismatches across at least 100,000 comparisons**, plus races at a stable, low rate. (One mismatch means a real bug, so the gate is zero rather than a percentage.)

## Consequences

- **Shadowing before the backfill fails the gate, as it should:** history is missing in closed buckets. The end-to-end test proves this: 25 mismatches before the backfill, 0 new ones after. With the backfill deliberately removed, the test fails at the gate (50 mismatches).
- "Closed" relies on the lateness bound (ADR 0003). If `MaxLateness` were raised mid-migration, older buckets could legitimately change, and the classifier would report false mismatches.
- Bucket retention matches between stores (ADR 0008), so expiry alone can't produce a mismatch. The tiny edge where a bucket expires between the two reads is a theoretical race we accept.
