# 07: Observability: OpenTelemetry → Prometheus → Grafana

**Batch C · Milestone M7** · Field Guide: §24 (RED metrics, SLIs, dashboards), §27

## What we built

| Piece | Where |
|---|---|
| OpenTelemetry metrics setup, explicit buckets, OTLP push | `src/CounterShift.Api/Observability/Telemetry.cs` |
| Business metrics: events by type, decisions by outcome, rules fired | `src/CounterShift.Api/Observability/ApiMetrics.cs` |
| Phase gauges: mode version, shadow %, split %, dual on/off | `StorageModeProvider` |
| Storage footprint endpoint | `GET /admin/storage-stats` |
| Prometheus (OTLP receiver) + Grafana, provisioned | `deploy/observability/`, `deploy/compose/docker-compose.yml` |
| Dashboard as code | `deploy/observability/build_dashboard.py` → `grafana/dashboards/countershift.json` |

```
API ──(OTLP/HTTP every 5 s)──► Prometheus :59090 ◄──(PromQL)── Grafana :53000
     http.server.request.duration       stores time series        "CounterShift: migration"
     cs.storage.operation.duration
     cs.parity.comparisons …
```

## The dashboard, top to bottom (and the question each row answers)

1. **Migration phase:** *What mode are we in, and has every pod switched?* Step lines for shadow %, split % and dual writes, plus mode version per pod. Everything below lines up against these.
2. **Traffic and errors (RED: Rate, Errors, Duration):** requests/s by route and status; **non-2xx/s, which is the rollback drill's pass criterion**; p50/p99 by route.
3. **Storage:** p50/p99 and calls/s **per backend, op and role**. This is how we can say "Redis reads are X ms faster than PostgreSQL" without client-side guesswork.
4. **Parity:** comparisons/s by result, the mismatch rate, things that must stay at zero, and the decision mix (if a migration silently changed answers, the allow/challenge/block mix would shift).

## Why this way

- **Push over OTLP instead of a `/metrics` scrape** (ADR 0014): the .NET Prometheus exporter is still prerelease, while OTLP is stable and Prometheus 3 receives it natively. The tradeoff is losing the free `up` metric for each target.
- **Explicit histogram buckets:** Prometheus computes p99 by *interpolating inside a bucket*. With ASP.NET Core's default edges (5 ms, 10 ms, …), every request under 5 ms is in one bucket, so a "p99" of 2 ms or 4.9 ms would look identical. Our buckets start at 0.5 ms.
- **A 5 s export interval:** the default is 60 s. A ramp step lasts about 30 s, so we'd see at most one data point per step.
- **Business metrics:** latency and errors say whether the service is *healthy*. The decision mix says whether it's *right*.
- **Dashboard as code:** a dashboard edited in the UI drifts and can't be reviewed. A Python script gives readable PromQL with comments, and the diff appears in pull requests.

## What broke / surprised us (two hours of the classic observability experience)

1. **"Export is on" but no data.** The app logged the right endpoint; Prometheus received nothing; nothing errored. OpenTelemetry swallows export failures by design (telemetry must never crash the app). The **self-diagnostics log** (`OTEL_DIAGNOSTICS.json`, which must sit in the app's *base* directory, not the working directory) showed the truth: it was posting to **`localhost:4318`**, the default. `AddOtlpExporter()` ignores the per-signal `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`. The fix is to set the endpoint and protocol in code. **Lesson: log what a component actually uses, not what you handed it.**
2. **Two processes on one port.** `netstat` showed Docker on `0.0.0.0:59090` *and* `wslrelay.exe` (WSL's localhost forwarder) on `[::1]:59090`. On Windows, `localhost` prefers IPv6. Use `127.0.0.1` in local endpoints.
3. **"No data" vs "zero".** A counter never incremented doesn't exist in Prometheus, so "secondary write failures" showed *No data*. That looks exactly like a broken pipeline. `or vector(0)` makes the healthy state visible.

## Numbers seen while verifying (30 requests, laptop, not a benchmark)

| Backend / op / role | p99 |
|---|---:|
| postgres read primary | 1.99 ms |
| postgres write primary | 29.8 ms (includes connection-pool warm-up) |
| redis write secondary | 2.94 ms |

Real numbers come from M8's load runs.

## Commands

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://127.0.0.1:59090/api/v1/otlp/v1/metrics \
ASPNETCORE_ENVIRONMENT=Development dotnet run -c Release --project src/CounterShift.Api --no-launch-profile --urls http://localhost:5072
# Grafana: http://localhost:53000   Prometheus: http://localhost:59090
curl -s localhost:59090/api/v1/query --data-urlencode \
  'query=histogram_quantile(0.99, sum by (le, backend, op, role) (rate(cs_storage_operation_duration_seconds_bucket[1m])))'
python deploy/observability/build_dashboard.py   # after editing panels
```

## Self-check

1. Why can't Prometheus give you an exact p99, and how do bucket edges limit it?
2. What does `rate(x_count[1m])` measure for a histogram? And `sum by (le, backend)` before `histogram_quantile`?
3. With push, how would you notice that one pod stopped sending metrics?
4. Why is the decision-outcome mix a migration safety signal?
5. Telemetry is "on" but no data arrives. List, in order, what you'd check.
