# ADR 0002: Four granularity tiers, retention-aware start rounding, and buckets that may extend into the future

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §4.3, §4.4, §4.5 (this ADR refines them)

## Context

Rules ask about trailing windows ("last 10 minutes"). Counts are stored in aligned buckets. A window's edges rarely fall on bucket boundaries, so we need precise rules for which buckets answer a window.

Grab uses 15m, 1h and 1d tiers. With those, "last 10 minutes" at 12:07 must either include all of `[11:45, 12:00)`, giving an effective window of 22 minutes, or drop it and under-count.

## Decision

1. **Tiers: 1m (kept 3 h), 15m, 1h, 1d (each kept 8 d).** Each tier must divide the next, and coarser tiers must be kept at least as long as finer ones. Both are validated at startup.
2. **All buckets are aligned to UTC** (epoch multiples). An event stamped in IST lands in the UTC bucket for that instant.
3. **The start edge rounds down (outward) to the finest tier still retained at that age.** A 10-minute window starts on a 1-minute boundary. A 7-day window starts on a 15-minute boundary, because 1-minute buckets from 7 days ago no longer exist.
   - *This refines the spec.* The spec only said "round to the finest tier". Writing the code exposed that the 1m tier's 3-hour retention makes that impossible for long windows.
4. **The end edge is always `now`, and a bucket may extend past it.** No stored event is later than now (apart from the 30 s skew allowance, ADR 0003), so the future part of a bucket is empty. This lets the greedy step simply take *the coarsest tier the cursor is aligned to*, without checking the right edge.
5. **Every answer reports `EffectiveFrom`**, so the rounding is visible to callers.

## Consequences

- Short windows are accurate to within 1 minute. Long windows are accurate to within 15 minutes, which is the same precision as Grab.
- Rounding outward can **over-count** by less than one bucket of the start tier. It never under-counts. For fraud, over-counting is the safe direction: it can produce a false challenge, never a missed block.
- Each event writes **4** increments per counter instead of 3. That costs write volume and storage, and we measure it in the final report.
- The decomposition is **minimal**. A brute-force dynamic-programming oracle in `WindowDecomposerProperties` checks this for 300 random windows on every run.
- Grab's example window (`9/10 23:15:20 → 9/12 17:20:18`) takes **5 buckets** here, because the final day bucket "contains now".
- **Retention cleanup (M2+) must delete a bucket only when it ends at or before `now − retention`.** Deleting by bucket *start* would remove buckets the decomposer still relies on. `Every_bucket_used_is_still_within_its_tiers_retention` pins this contract down.

## Alternatives considered

| Option | Why not |
|---|---|
| Grab's exact tiers (15m/1h/1d) | Gives a 10-to-24-minute effective window for a "10 minute" rule. It is still supported: tiers are configurable, and a test covers this mode. |
| Round the start *inward* (skip the partial bucket) | Under-counts, so an attacker could stay just under a threshold. That is the wrong direction for fraud. |
| Interpolate a partial bucket (count × fraction) | Assumes events are spread evenly inside a bucket, which bursts violate by definition. It is also non-integer and hard to explain. |
| Store raw events and scan them | Exact, but costs O(events) per query. Avoiding that is the whole point of pre-aggregation. |
