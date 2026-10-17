# ADR 0004: The decision API fails closed (503) when storage is unavailable

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §4.7, §5

## Context

If the counter store is unreachable, `/v1/decisions` cannot know the counts. It can either:

- **fail open**: answer `allow` and let the payment through, or
- **fail closed**: answer `503`, and the caller decides what to do.

## Decision

**Fail closed.** Any storage error (`NpgsqlException`, socket error, timeout) becomes `503 application/problem+json`. The same applies to `/v1/events` and `/readyz`. `/healthz` keeps returning 200, because restarting the API won't fix the database.

`StorageOutageTests` proves this by stopping a real Postgres container mid-test.

## Why

- A fraud service that silently says `allow` during an outage is an **attack surface**: knock over the database and every check passes.
- `503` is honest. The **caller** (for example the payment service) owns the business choice. It may block, queue, or allow small amounts. That choice depends on money and risk, which this service doesn't know.
- Returning a distinct status (not `200 allow`) keeps the outage visible in the caller's metrics.

## Consequences

- Our availability becomes part of the payment path's availability. That raises the stakes for the storage migration in M6: a migration that causes errors causes **declined payments**, not just slow dashboards. This is why Grab treated zero downtime as non-negotiable.
- Callers must handle `503`. The report will discuss what a real payment flow would do (Project 1's gateway is a natural place for that policy).

## Alternatives considered

| Option | Why not |
|---|---|
| Fail open (`allow`) | Unsafe by default; turns an outage into a fraud window. |
| Serve stale counts from a cache | Adds a cache and its invalidation problems in a project about storage. It may make sense later, behind an explicit "degraded" flag in the response. |
