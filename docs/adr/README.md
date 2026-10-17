# Architecture Decision Records

An ADR records one decision: the **context** that forced it, the **decision**, the **alternatives** we rejected, and the **consequences** we accept. ADRs are never edited after acceptance. To change a decision, write a new ADR that supersedes the old one, so the history of *why* survives.

Format: Michael Nygard's original ADR template, lightly extended with "Alternatives considered".

| # | Title | Status |
|---|---|---|
| [0001](0001-stack-dotnet-postgres-dapper.md) | .NET 10, PostgreSQL, Npgsql + Dapper, hand-rolled migrations | Accepted |
| [0002](0002-granularities-and-rounding.md) | Four tiers, retention-aware start rounding, buckets may extend past now | Accepted |
| [0003](0003-event-time-lateness-and-skew.md) | Bucket by event time; reject events more than 5 min late or more than 30 s ahead | Accepted |
| [0004](0004-fail-closed-decisions.md) | Decision API fails closed (503) when storage is unavailable | Accepted |
| [0005](0005-no-dedup-in-v1.md) | No event deduplication in v1 | Accepted |
| [0006](0006-atomic-upsert-with-sorted-keys.md) | Atomic multi-row upsert, consistent lock order, bounded connection pool | Accepted |
| [0007](0007-storage-seam.md) | Storage seam: `ICounterBackend` behind a `CounterStore` facade, with error translation | Accepted |
| [0008](0008-redis-hash-backend.md) | New backend: one Redis hash per (counter, tier), Lua writes, native field expiry | Accepted |
