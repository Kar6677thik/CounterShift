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
| [0009](0009-dual-write-semantics.md) | Dual writes: primary required, secondary synchronous and tolerated, every key | Accepted |
| [0010](0010-rollout-hashing.md) | Rollout membership: XxHash64(counter key) mod 10,000 | Accepted |
| [0011](0011-parity-classification.md) | Shadow parity per bucket: match / mismatch / race / error; gate on closed buckets | Accepted |
| [0012](0012-backfill-final-pre-t0-buckets.md) | Backfill final, pre-T0, unexpired buckets with SET; re-runnable (corrects spec §8) | Accepted |
| [0013](0013-runtime-mode-reload.md) | Storage mode from a watched file; validated on reload; last good mode kept | Accepted |
| [0014](0014-otlp-push-to-prometheus.md) | Metrics via OpenTelemetry, pushed over OTLP to Prometheus (supersedes the spec's `/metrics` scrape) | Accepted |
| [0015](0015-publish-images-no-deploy.md) | Publish container images; no hosted deployment (replaces spec M9–M10) | Accepted |
