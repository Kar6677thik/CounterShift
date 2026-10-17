# ADR 0007: A storage seam: `ICounterBackend` behind a `CounterStore` facade, with error translation

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §6, §7; experiment B2

## Context

In `v0-scattered`, SQL lived in 4 production files outside any storage module: the endpoints, the cleanup job, the migration runner and the error handler. The error handler knew about `NpgsqlException`. Swapping the database meant editing all of them at once, with no way to run old and new side by side. That is exactly the "just swap the database" trap from the brief.

## Decision

1. **`ICounterBackend`** is a small interface (Interface Segregation): `IncrementAsync`, `ReadAsync`, `PingAsync`, `GetStatsAsync`. Backends are "dumb": they store numbers by `BucketRef`, while decomposition and summing stay in the domain. This is the same split Grab made, where backends emit tuples and the orchestrator sums them, and it's what makes a parity comparison meaningful.
2. **`CounterStore`** is a concrete **facade**, and the only storage type the API uses. In M4 it forwards to one backend. In M6 it gains modes, and the API does not change.
3. **Error translation:** every backend converts *transient* driver failures into `StorageUnavailableException(backend)`. The API maps that to 503 without knowing any driver. Non-transient errors (bugs, constraint violations) are deliberately **not** translated and surface as 500s.
4. **Packaging:** `CounterShift.Storage` (abstraction + facade + in-memory mock) and one project per backend (`CounterShift.Storage.Postgres`, later `.Redis`). Each backend registers itself with one extension method (`AddPostgresCounterBackend`), and **only `Program.cs` (the composition root) names a concrete backend**.
5. **Backend-specific chores stay with the backend:** migrations implement `IStorageInitializer`, and retention cleanup is a hosted service registered by the Postgres package. Redis expires data natively, so it needs neither.
6. **Enforced, not hoped for:**
   - *Contract tests* (`CounterBackendContract`) run the same 9 tests against every backend.
   - *Architecture tests* inspect compiled assembly references: the API and Storage assemblies must not reference Npgsql, Dapper or StackExchange.Redis.

## Alternatives considered

| Option | Why not |
|---|---|
| Repository per aggregate (`ICounterRepository.GetCount(key, window)`) | Puts window logic inside each backend, which duplicates the trickiest code and makes "backends disagree" ambiguous (storage bug or summing bug?). |
| Facade as an interface (`ICounterStore`) | There will only ever be one facade. Its *variation* lives in the backends and modes. An interface with one implementation is ceremony (YAGNI). |
| Trait objects / `IEnumerable<ICounterBackend>` without a facade | Pushes "which backend for this request?" logic into every endpoint. That logic belongs in exactly one place. |
| One combined "Storage" project with all backends | Every consumer would transitively pull in every driver, and the architecture tests couldn't tell the layers apart. |

## Consequences

- B2 result: production files outside a backend project that know about PostgreSQL went from **4 → 0**. The composition root keeps 3 lines of registration.
- Adding Redis (M5) is now a new project plus one registration line, with zero endpoint changes. M5 will confirm it.
- Error translation means a `StorageUnavailableException` can come from *either* store during a migration, and its `Backend` property says which one. M6 relies on this to decide which failures must fail a request.
