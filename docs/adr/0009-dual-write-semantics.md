# ADR 0009: Dual writes: primary first and required; secondary second, synchronous, tolerated; every key

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §7.1

## Context

From Phase 1 to Phase 4, every event must reach both stores. Grab's writer kept the old store as primary and sent an **asynchronous** shadow write to the new one, **gated by the rollout percentage**. We need to decide ordering, synchronicity, failure handling and coverage.

## Decision

1. **Primary first, and it must succeed.** If the primary write fails, the request fails (503). The primary is the store serving reads, so a lost primary write means a wrong answer.
2. **Secondary second, synchronously, failure tolerated.** It runs after the primary, inside the same request, with its own **2-second timeout that ignores the request's cancellation token**. Any exception is caught, logged with the key count, and counted in `cs.storage.secondary_write.failures`. The request still succeeds.
3. **All keys, not a percentage.** Once dual writes are on, every event goes to both stores.
4. **The write mode is its own knob**, separate from the read mode (`Storage:Write` vs `Storage:Read`). The validator refuses any read mode that reads from a store not receiving writes.

## Why

- **Synchronous, not fire-and-forget:** a write still waiting in an in-memory queue when a pod restarts during a deploy is silently lost, and every such loss becomes a "mysterious" parity mismatch later. Synchronous costs some latency on every write, and M7/M8 measure exactly how much (`role=secondary` in the duration histogram).
- **Independent cancellation:** if the client hangs up after the primary write, cancelling the secondary would make the stores diverge for a reason that has nothing to do with storage.
- **All keys:** if writes were gated by percentage, raising the percentage would bring new keys into the new store with *no history*, so every ramp step would need its own backfill. Writing everything plus one backfill (ADR 0012) is simpler and easier to prove correct.
- **Separate knobs:** the read side and the write side change at different phases (Phase 4 swaps write primaries while reads are already on Redis). One combined "mode" enum would need a value for every combination.

## Consequences

- Write latency during migration ≈ primary + secondary. This is measured, not guessed.
- A failed secondary write leaves the stores different until something repairs them. Detection: the failure counter (immediate) and closed-bucket parity mismatches (later). Repair: re-run the backfill for that period. A reconciliation job is a stretch goal.
- No write is "half done" from the caller's point of view: if the primary succeeded, the caller's answer is right.
