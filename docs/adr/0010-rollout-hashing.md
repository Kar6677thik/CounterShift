# ADR 0010: Rollout membership: XxHash64(counter key) mod 10,000 below percent × 100

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §7.1 (split), §7.2 (shadow sampling); experiment B4

## Context

Shadow sampling and the traffic split both need "is this in the X%?". Grab says its split is "deterministic" without saying how.

## Decision

`Rollout.Includes(key, percent)` ⇔ `XxHash64(UTF-8(counterKey)) mod 10,000 < round(percent × 100)`

1. **Keyed on the counter key**, not on the request or the user. A given card is always served by the same store at a given percent, so its fraud decisions don't flip from one request to the next. A decision that reads several counters may combine stores. That's safe, because both stores hold complete data (ADR 0009 + backfill).
2. **Stable hash (XxHash64).** It's fast, not cryptographic, and gives the same output on every machine and process.
3. **Threshold comparison**, so raising the percent only ever *adds* keys.
4. **0.01% resolution** (10,000 buckets).
5. The same function picks shadow samples and split membership, so "the keys we verified at 20% shadow" are exactly "the keys we serve at 20% split".

## Why not `string.GetHashCode()`

Experiment B4 (`docs/results/b4-hash-stability.md`): in three processes, `payment_failed_by_card:c_42` hashed to buckets 6601, 6686 and 5401. .NET randomises string hashes per process to defeat hash-flooding attacks. With two pods behind a load balancer, the same card would be served by Redis on one request and by PostgreSQL on the next.

## Consequences

- The hash is now part of the **protocol** between pods. If it changed in a new version, a rolling deploy during a split would route keys differently on old and new pods. `RolloutTests` pins the bucket of one key (5136), so a change has to be deliberate.
- Per-key routing means a hot key is served entirely by one store. That's fine here; per-request routing would spread one hot key across both stores.
