# ADR 0003: Bucket by event time; reject events more than 5 minutes late or more than 30 seconds ahead

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §4.1, §8

## Context

An event has two timestamps: when it **happened** (`occurredAt`, set by the client) and when it **arrived** (our clock). Networks delay events, clients retry, and client clocks drift.

## Decision

- Bucket by **event time** (`occurredAt`). A login failure that happened at 10:03 counts toward 10:03, even if it arrives at 10:05.
- Reject with **422** any event whose `occurredAt` is:
  - older than `now − MaxLateness` (default **5 minutes**), or
  - newer than `now + MaxClockSkew` (default **30 seconds**).
- Both limits are inclusive: an event exactly 5 minutes late is accepted.
- Both limits are configuration, but **changing `MaxLateness` invalidates the backfill safety argument** in spec §8, so it must not change during a migration.

## Why these limits exist

1. **Backfill correctness (the main reason).** During migration, the backfill copies buckets that ended before `T0 − MaxLateness` using SET. That is only safe if no event can still land in those buckets. A bound on lateness turns "probably safe" into "provably safe".
2. **Retention correctness.** Without a bound, a very late event could recreate a bucket that the cleanup job has already deleted.
3. **Clock sanity.** A client whose clock is an hour fast would otherwise write into future buckets that every later query includes.
4. **Window precision.** Decomposition treats the bucket containing "now" as complete (ADR 0002). Future-dated events up to 30 s are the only exception, and that skew is small and bounded.

## Consequences

- An event delayed more than 5 minutes (for example by a long outage upstream) is **lost** for counting purposes. The client receives a 422, so the loss is visible. In a streaming system, this is the job of a watermark plus an "allowed lateness" setting. Project 3 covers those properly.
- The 400/422 split helps callers: 400 means "your request is wrong, don't retry it", and 422 means "it was right but arrived too late or too early".
