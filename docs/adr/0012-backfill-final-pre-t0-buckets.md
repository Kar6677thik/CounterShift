# ADR 0012: Backfill copies final, pre-T0, unexpired buckets with SET, and can be re-run (corrects spec §8)

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §8. **This ADR corrects it.**

## Context

Spec §8 said: *"copy buckets whose end ≤ T0 − MaxLateness; buckets newer than that already received every event through dual-write."* **The second half is false.** A bucket that **straddles T0** (today's 1-day bucket, the current hour) contains events that arrived *before* T0 (legacy only) and events after T0 (both stores). Dual writes never add the pre-T0 events to the new store, and the spec's rule never copies that bucket, so the new store would be permanently short.

## Decision

T0 is the moment **every** API instance is dual-writing (take the latest `loadedAt` from `/admin/storage-mode` across pods). A bucket is copied from source to target when all three hold:

1. **May hold pre-T0 events:** `start < T0 + MaxClockSkew`. Later buckets are complete through dual writes alone, so we skip them.
2. **Final:** `end + MaxLateness + 1 min ≤ now`. No event can still arrive for it (ADR 0003). The extra minute covers a request accepted at the lateness limit that is still being written. A final bucket's source value is its value forever, so copying it is exact.
3. **Not expired:** `end + retention > now`, so dead buckets aren't resurrected.

The write uses **SET (overwrite), not increment**, so the copy is **idempotent**. Running it twice gives the same result. Buckets that hold pre-T0 events but aren't final yet are counted (`NotYetFinal`), and the report prints when the last of them becomes final: **run it again after that time**. In practice: run once soon after T0 to copy most history, then once more after `T0 + 1 day + MaxLateness + 1 min`, when today's day bucket is final.

The backfill **refuses to run** unless the current mode writes to the target (`--force` overrides). Copying history into a store that isn't receiving new writes would leave it stale the moment the copy finished.

It runs as a verb of the API binary (`backfill --from postgres --to redis --dual-write-since <T0>`), so it uses the same code, configuration and image. In Kubernetes it's a Job.

## Alternatives considered

| Option | Why not |
|---|---|
| Spec §8 as written | Leaves straddling buckets permanently wrong. Shown by `BackfillTests.The_bucket_that_straddles_T0_is_repaired_once_it_is_final`. |
| Copy with increment (`+=`) | Not idempotent: a retried Job would double the history. |
| Copy everything immediately with SET | Overwrites open buckets, and then concurrent dual writes either get lost (SET after them) or counted twice. |
| No backfill; dual-write for 8 days (the longest retention) | Valid, and possibly what Grab did. But it makes the migration a week longer, and you can't shadow-verify old windows until then. |
| Checkpoint and resume support | Idempotent SET makes "start again" correct, just slower. At learning scale it isn't worth the extra state. |

## Consequences

- The migration has a minimum duration: about one day between enabling dual writes and the final backfill pass.
- The parity gate (ADR 0011) independently verifies the backfill: shadowing before it fails, and after it passes. The end-to-end test runs exactly this sequence.
- The source must support scanning (`IBucketScanner`, keyset pagination in PostgreSQL) and the target must support SET (`IBucketWriter`, a Lua `HSET` script in Redis). The reverse direction (Redis → PostgreSQL) isn't implemented, because Phase 4 keeps PostgreSQL current through dual writes, so a rollback never needs it.
