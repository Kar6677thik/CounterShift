# CounterShift

A fraud-counter service that answers time-windowed questions such as *"more than 5 failed payments on this card in the last 10 minutes?"*, and then **migrates its own storage to a new design with zero downtime**.

It is a learning-scale rebuild of Grab's Counter Service and its 2026 storage migration. See [`docs/spec.md`](docs/spec.md) for the full specification.

> **Status:** Batch A (M0–M3): scaffolding, domain logic, first naive version, lost-update experiment.

## Architecture (current milestone)

```mermaid
flowchart LR
    client[Client / k6] -->|POST /v1/events| api[CounterShift.Api]
    client -->|POST /v1/decisions| api
    api --> domain[CounterShift.Domain<br/>bucketing · decomposition · rules<br/><i>pure, no I/O</i>]
    api -->|SQL| pg[(PostgreSQL<br/>counter_buckets)]
```

The target architecture after the migration machinery (M6) is described in spec §7.

## Quick start

Prerequisites: .NET SDK 10.0.4xx and Docker.

```bash
# 1. Start PostgreSQL locally (host port 55432)
docker compose -f deploy/compose/docker-compose.yml up -d

# 2. Run the API (applies database migrations on startup)
dotnet run --project src/CounterShift.Api

# 3. Run all tests (integration tests start their own throwaway Postgres via Testcontainers)
dotnet test --solution CounterShift.slnx
```

## Repository map

| Path | What lives there |
|---|---|
| `src/CounterShift.Domain` | Pure logic: events, counters, buckets, window decomposition, rules |
| `src/CounterShift.Api` | HTTP host, configuration, database access |
| `tests/` | Unit, property and integration tests; `CounterShift.Experiments` holds the "Break" harnesses |
| `deploy/compose` | Local dependencies |
| `docs/spec.md` | What we build and why |
| `docs/learning/` | **Start here to learn**: one note per milestone (what, why, when, commands, self-check) |
| `docs/adr/` | Architecture decision records |
| `docs/glossary.md` | Every term, defined once |

## Learning path

Read the notes in [`docs/learning/`](docs/learning/README.md) in order. Each note links to the code and the decision records it introduced.
