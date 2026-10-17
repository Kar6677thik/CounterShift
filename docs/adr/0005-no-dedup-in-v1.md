# ADR 0005: No event deduplication in v1

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §4.6

## Context

HTTP clients retry on timeouts. If the first attempt committed but the response was lost, the retry counts the event **twice**. Deduplicating on `eventId` would prevent that.

## Decision

v1 does **not** deduplicate. `eventId` is required and accepted, so it can be logged and correlated, but it is not stored.

## Why

- Deduplication needs its own store: a "seen eventIds" set with a TTL at least as long as the retry horizon. That store must be written **atomically with** the increments, or it can disagree with them.
- During the storage migration (M6) that store would *also* need migrating, with shadowing and parity checks. That doubles the scope of the core lesson.
- Fraud counters tolerate small over-counts. Over-counting fails safe (ADR 0002), and Grab accepts the same tradeoff: counts "self-heal" as new events arrive.

## Consequences

- Retries can inflate counts by the number of retried requests. For a threshold of 5, a few duplicates can flip `allow` to `block`. This is a false positive, never a missed fraud.
- Deduplication is listed as a stretch goal. The likely design: `INSERT INTO processed_events (event_id) … ON CONFLICT DO NOTHING` in the **same transaction** as the increments. Only increment if that insert actually inserted a row. This is a *transactional inbox* (Field Guide §11.19).
