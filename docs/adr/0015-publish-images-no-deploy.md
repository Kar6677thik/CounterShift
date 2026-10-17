# ADR 0015: Publish container images; no hosted deployment

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §11, §14 (M9–M10). **This replaces the planned deployment milestones.**

## Context

The spec planned a deployment to a private single-node k3s cluster through GitHub Actions (M9), followed by a multi-day migration rehearsal there (M10). That cluster is reachable only on a private network, so a deployment there could not be seen or used by anyone evaluating the project. The service also has no authentication by design (spec §2), so exposing it publicly would need an auth layer that is out of scope.

## Decision

- CI builds the API image on every push and pull request, and **publishes it to GHCR on every merge to `main`**, tagged `sha-<commit>` and `latest`. Only that job has `packages: write`.
- `deploy/compose/docker-compose.ghcr.yml` runs the full stack (API, PostgreSQL, Redis, Prometheus, Grafana) from the published image with Docker alone.
- The demo (`load/demo.sh`, recorded in `docs/images/demo.svg`) shows the migration and a rollback against that stack.
- No hosted deployment and no deploy job.

## Consequences

- Anyone can run exactly the published artifact locally in about a minute.
- **Not exercised:**
  - the backfill's second pass under real elapsed time (proven with a simulated clock in `MigrationEndToEndTests` and `BackfillTests`);
  - mode propagation through a Kubernetes ConfigMap (about a minute, versus 1–5 s through a Compose bind mount);
  - measurements on dedicated hardware.
- A Kubernetes deployment remains straightforward: the image, readiness/liveness endpoints, a directory-mounted mode file and the `backfill` verb are already shaped for it (ADR 0013).
