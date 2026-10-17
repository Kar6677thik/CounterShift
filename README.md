# CounterShift

A fraud-counter service that answers time-windowed questions such as *"more than 5 failed payments on this card in the last 10 minutes?"*, and then **migrates its own storage to a new design with zero downtime**.

It is a learning-scale rebuild of Grab's Counter Service and its 2026 storage migration. See [`docs/spec.md`](docs/spec.md) for the full specification.

> **Status:** Batches A–C complete (M0–M8). Migration machinery verified end to end, observable in Grafana, and rehearsed under load locally (0 errors, 0 mismatches, 333 ms rollback). Next: Batch D, deploying to k3s through GitHub Actions and the real multi-day migration on the server.

## Architecture

```mermaid
flowchart LR
    client[Client / k6] -->|POST /v1/events<br/>POST /v1/decisions| api[CounterShift.Api]
    api --> domain[CounterShift.Domain<br/>bucketing · decomposition · rules<br/><i>pure, no I/O</i>]
    api --> store[CounterStore facade<br/>write: Single / Dual<br/>read: Single / Shadow / Split]
    mode[(storage-mode.json<br/><i>watched, live reload</i>)] -.-> store
    store --> pg[PostgresCounterBackend] --> pgdb[(PostgreSQL<br/>row per bucket)]
    store --> rd[RedisCounterBackend] --> rdb[(Redis<br/>hash per counter+tier)]
    store -.sampled reads.-> shadow[ShadowComparer<br/>per-bucket parity] -.-> rd
    backfill[backfill verb<br/>final pre-T0 buckets] -.-> pgdb
    backfill -.SET.-> rdb
```

The migration plan and its runbook: [`docs/runbooks/migration.md`](docs/runbooks/migration.md).

## Quick start

Prerequisites: .NET SDK 10.0.4xx and Docker.

```bash
# 1. Start PostgreSQL (55432), Redis (56379), Prometheus (59090) and Grafana (53000)
docker compose -f deploy/compose/docker-compose.yml up -d

# 2. Run the API (applies database migrations on startup)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/CounterShift.Api --no-launch-profile --urls http://localhost:5072

# Optional: push metrics to the local Prometheus (Grafana at http://localhost:53000)
#   export OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://127.0.0.1:59090/api/v1/otlp/v1/metrics

# 3. Run all tests (integration tests start their own throwaway Postgres via Testcontainers)
dotnet test --solution CounterShift.slnx
```

## Repository map

| Path | What lives there |
|---|---|
| `src/CounterShift.Domain` | Pure logic: events, counters, buckets, window decomposition, rules |
| `src/CounterShift.Api` | HTTP host, configuration, composition root |
| `src/CounterShift.Storage` | Storage seam: `ICounterBackend`, `CounterStore` facade, in-memory backend |
| `src/CounterShift.Storage.Postgres` | Legacy backend: SQL, migrations, retention cleanup |
| `src/CounterShift.Storage.Redis` | New backend: one hash per counter and tier, Lua writes, native expiry |
| `tests/` | Unit, property and integration tests |
| `experiments/` | "Break" harnesses (lost updates, deadlocks, hash stability) and the storage-footprint experiment; results in `docs/results/` |
| `load/` | k6 traffic generator and the migration rehearsal driver |
| `deploy/observability/` | Prometheus and Grafana config; dashboard generator |
| `deploy/compose` | Local dependencies |
| `docs/spec.md` | What we build and why |
| `docs/learning/` | **Start here to learn**: one note per milestone (what, why, when, commands, self-check) |
| `docs/adr/` | Architecture decision records |
| `docs/glossary.md` | Every term, defined once |

## Learning path

Read the notes in [`docs/learning/`](docs/learning/README.md) in order. Each note links to the code and the decision records it introduced.
