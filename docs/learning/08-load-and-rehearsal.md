# 08: Load testing and the local migration rehearsal

**Batch C · Milestone M8** · Field Guide: §23 (load testing), §24, §27 (performance), §17 (load shedding)

## What we built

| Piece | Where | Job |
|---|---|---|
| Load generator | `load/traffic.js` (k6, run in Docker) | Open-model traffic: events + decisions, 30% on 1% of cards |
| Rehearsal driver | `load/rehearse.py` | Runs the whole runbook under load by editing the mode file; times each change; queries Prometheus per step; writes a report |
| Footprint experiment | `experiments/.../Footprint.cs` | 1M identical events into fresh PostgreSQL and Redis; bytes, throughput, bloat |
| Results | `docs/results/m8-rehearsal-local.md`, `m8-footprint.md`, `m8-rehearsal-accidental-2x-overload.md` | |

## Headline results (development laptop: everything on one machine; the server run in M10 is the one to quote)

**Rehearsal at 200 events/s + 400 decisions/s, all phases P0 → P5, with a rollback at 70% split:**

| Measure | Result |
|---|---|
| Requests sent (k6) | 371,832 |
| **Failed requests / non-2xx** | **0 / 0 in every phase** |
| **Parity mismatches** | **0** in 36,491 shadow comparisons (0 races, 0 dropped) |
| **Rollback** (Split 70% → Single(postgres)): file edit → applied | **333 ms**, 0 errors |
| Every mode change: propagation | 263–333 ms (.NET's 250 ms config-reload debounce + 50 ms polling) |
| Events p50 / p99, Phase 0 (PostgreSQL only) | 2.5 / 5.0 ms |
| Events p50 / p99, Phase 1 (dual writes) | 3.9 / 7.2 ms: **dual writes cost about 1.4 ms at p50** |
| Decisions p99 with 100% shadow | 3.0 ms, the same as no shadow: **the bounded queue keeps shadow work off the request path** |
| Events p50 / p99, Phase 5 (Redis only) | **0.8 / 2.8 ms** |

**Footprint, 1M events, identical data (3,356,445 buckets in both):** PostgreSQL 457–552 MiB on disk, Redis **150 MiB** in RAM. Writes: 4,792 vs **15,045** events/s.

## Why this way

- **Open model (constant arrival rate).** A closed model (N users in a loop) slows its own sending when the server slows down, which hides the problem you're testing for. Real clients keep arriving. When the server falls behind, k6 reports **dropped iterations** (requests it couldn't even start), and we report those too.
- **Skewed keys.** Fraud traffic *is* hot keys: an attacker hammering one card. Uniform random keys would make every lock and every hash slot look uncontended.
- **Server-side percentiles from Prometheus, client-side from k6.** The two disagree by the network and client queueing. We report both and label which is which.
- **Prometheus queries per step window.** The driver records each step's start and end, then evaluates `histogram_quantile(…, increase(…_bucket[30s]))` at the step's end. Every number in the report has a reproducible query behind it.
- **Fresh keys after T0.** A 10-minute rehearsal can't wait a day for the backfill's second pass (ADR 0012). Without fresh keys, a split would serve Redis's incomplete open buckets for pre-T0 keys. So the rehearsal proves the *operational* path (switches, rollback, latency, parity under load). Backfill *correctness* was proven in M6 with a fake clock, and the multi-day real migration is M10.

## What broke / surprised us (the most useful section)

1. **An orphaned load generator doubled the load of a whole run.** A run crashed (point 2). Killing the Python script killed the `docker run` *client*, but **the k6 container kept sending traffic** for another 10 minutes, overlapping the next run. That run's report looked plausible until the Prometheus request rates showed **400 + 800 req/s instead of 200 + 400**, halving exactly when the orphan's run would have ended. Fixes: containers are named and force-removed in `finally`, and the driver **refuses to start if any k6 container is running**. The contaminated run is kept, honestly labelled, as an accidental overload test: at about 1,200 req/s, PostgreSQL p99 went to **2.4 s** (connection-pool queueing counted inside the storage timer), the shadow queue **dropped 2,229 comparisons** (load shedding working as designed), races outnumbered matches, and still: **0 errors, 0 mismatches**. Lesson: *check that the load you think you sent is the load that arrived.*
2. **Windows output encoding.** `print("Dual(postgres→redis)")` to a redirected stdout crashed: cp1252 can't encode `→`. Fixed with `sys.stdout.reconfigure(encoding="utf-8")`.
3. **Locale leaks into reports.** On this machine (`en-IN`), `{1000000:N0}` prints `10,00,000` (Indian digit grouping). Operator and report output now use the invariant culture.
4. **My driver waited forever for a no-op.** Re-applying the mode that's already active produces no new version, because the provider ignores no-op reloads by design. The driver now checks "is the desired mode already active?" first.
5. **One unexplained spike.** In one 30-second window (split 70%), PostgreSQL write p99 hit 193 ms (234 of 6,002 writes over 50 ms) and then recovered. It's *not* GC (pause time was equal in every window). A checkpoint was in progress but also spanned calm windows, and autovacuum isn't logged by default. **Unattributed.** The server run enables `log_autovacuum_min_duration = 0` to settle it. Note that Redis reads in the same window stayed at 2 ms: splitting traffic also splits failure domains.
6. **StackExchange.Redis treats `INFO` as an admin command** (`AllowAdmin`). Only the experiment opts in; the API never needs it.
7. **Redis is about 3× smaller and about 3× faster to write**, similar to Grab's 3 TB → 1 TB. But it's RAM: 8 days at 1M events/day ≈ 1.2 GiB on a server with about 4.9 GB free. That's a sizing input for M9.

## Concepts introduced

- Open vs closed load models; arrival rate; dropped iterations as client-side load shedding (§23, §27)
- Percentiles from histograms vs from raw samples; client vs server latency (§24)
- Capacity probing before choosing test load (we ran at 50% of the measured ceiling)
- Config propagation time as the rollback-time floor (§6.5)
- PostgreSQL MVCC bloat, VACUUM vs VACUUM FULL, checkpoints, autovacuum (§10.19)
- Experimental hygiene: isolate the variable, verify the input load, keep and label failed runs

## Commands

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
COUNTERSHIFT_STORAGE_MODE_FILE=/tmp/mode.json ASPNETCORE_ENVIRONMENT=Development \
OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://127.0.0.1:59090/api/v1/otlp/v1/metrics \
  dotnet run -c Release --project src/CounterShift.Api --no-launch-profile --urls http://0.0.0.0:5072
python load/rehearse.py --mode-file /tmp/mode.json          # about 13 min; watch Grafana at http://localhost:53000
dotnet run -c Release --project experiments/CounterShift.Experiments -- footprint   # about 15 min
```

## Self-check

1. Why does a closed-model load test understate latency problems?
2. The P3 split 100% row shows no PostgreSQL *read* latency but does show PostgreSQL *write*. Why?
3. Why did races appear in the overload run but not the clean one? (Hint: the queue delay between the primary read and the shadow read.)
4. Rollback took 333 ms here. Why will it take about a minute on Kubernetes, and what does that imply for the drill?
5. Redis used 3× less space. Why might a team still keep PostgreSQL?
6. What single check would have caught the orphaned k6 container before it spoiled a run?
