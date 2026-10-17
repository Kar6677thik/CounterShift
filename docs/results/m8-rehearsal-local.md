# M8: local migration rehearsal under load (clean run 20261017T121317Z)

Command: `python load/rehearse.py --mode-file <file>` (defaults: 30 s steps, 200 events/s + 400 decisions/s).
Machine: Intel Core Ultra 5 225H (14 logical CPUs), Windows 11, Docker Desktop on WSL2. Everything (API, PostgreSQL, Redis, Prometheus and k6) shares this one machine.

- Machine: Windows-11-10.0.26100-SP0, 14 logical CPUs (API, PostgreSQL, Redis, Prometheus and k6 all on this one machine)
- Load: 200 events/s + 400 decisions/s, open model (k6 constant arrival rate), 30% of traffic on 1% of cards
- T0 (dual writes on): 2026-10-17T12:14:29.0217223+00:00

**k6, Phase 0 baseline run:** 41,981 requests, **failed rate 0.00000**, dropped iterations 21, client latency p50/p95/p99 = 2.45/4.03/6.06 ms

**k6, Migration run (P1→P5):** 329,851 requests, **failed rate 0.00000**, dropped iterations 151, client latency p50/p95/p99 = 2.61/6.2/12.67 ms

## Per step: traffic, errors, HTTP latency (server side, from Prometheus)

| Step | Mode | Propagation (ms) | req/s events | req/s decisions | non-2xx | events p50 / p99 (ms) | decisions p50 / p99 (ms) |
|---|---|---:|---:|---:|---:|---:|---:|
| P0 baseline | `write=Single(postgres) read=Single(postgres)` |  | 200 | 400 | 0 | 2.5 / 5.0 | 1.5 / 2.9 |
| P1 dual writes | `write=Dual(postgres→redis) read=Single(postgres)` | 268 | 200 | 400 | 0 | 3.9 / 7.2 | 1.5 / 3.0 |
| P2 shadow 5% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 5%)` | 265 | 200 | 400 | 0 | 3.9 / 7.0 | 1.5 / 3.0 |
| P2 shadow 20% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 20%)` | 265 | 200 | 400 | 0 | 4.4 / 11.8 | 1.9 / 5.6 |
| P2 shadow 50% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 50%)` | 316 | 200 | 400 | 0 | 4.1 / 10.4 | 1.6 / 4.8 |
| P2 shadow 100% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 100%)` | 266 | 200 | 400 | 0 | 3.7 / 7.2 | 1.5 / 3.0 |
| P3 split 5% | `write=Dual(postgres→redis) read=Split(postgres, redis, 5%)` | 275 | 200 | 400 | 0 | 4.5 / 15.4 | 2.0 / 6.9 |
| P3 split 30% | `write=Dual(postgres→redis) read=Split(postgres, redis, 30%)` | 321 | 200 | 400 | 0 | 3.5 / 7.3 | 1.5 / 3.1 |
| P3 split 70% | `write=Dual(postgres→redis) read=Split(postgres, redis, 70%)` | 263 | 200 | 400 | 0 | 3.6 / 195.4 | 1.0 / 28.2 |
| ROLLBACK to postgres reads | `write=Dual(postgres→redis) read=Single(postgres)` | 333 | 200 | 400 | 0 | 3.4 / 8.4 | 1.5 / 2.9 |
| P3 split 70% (again) | `write=Dual(postgres→redis) read=Split(postgres, redis, 70%)` | 284 | 200 | 400 | 0 | 3.7 / 11.8 | 1.2 / 3.1 |
| P3 split 100% | `write=Dual(postgres→redis) read=Split(postgres, redis, 100%)` | 279 | 200 | 400 | 0 | 3.0 / 11.5 | 0.8 / 2.9 |
| P4 redis primary, pg dual | `write=Dual(redis→postgres) read=Single(redis)` | 291 | 200 | 400 | 0 | 3.4 / 14.4 | 0.8 / 2.0 |
| P5 redis only | `write=Single(redis) read=Single(redis)` | 292 | 200 | 400 | 0 | 0.8 / 2.8 | 0.7 / 2.8 |

## Per step: storage p99 by backend / op / role (ms)

| Step | postgres read primary | postgres write primary | postgres write secondary | redis read primary | redis read secondary | redis write primary | redis write secondary |
|---|---:|---:|---:|---:|---:|---:|---:|
| P0 baseline | 2.6 | 4.9 |  |  |  |  |  |
| P1 dual writes | 2.9 | 4.9 |  |  |  |  | 2.0 |
| P2 shadow 5% | 2.8 | 5.0 |  |  |  |  | 2.0 |
| P2 shadow 20% | 4.9 | 7.6 |  |  |  |  | 4.1 |
| P2 shadow 50% | 4.1 | 7.7 |  |  |  |  | 2.7 |
| P2 shadow 100% | 2.8 | 4.9 |  |  |  |  | 2.0 |
| P3 split 5% | 6.0 | 11.9 |  |  | 6.1 |  | 4.5 |
| P3 split 30% | 2.9 | 5.0 |  |  | 2.0 |  | 2.0 |
| P3 split 70% | 74.9 | 192.8 |  |  | 2.0 |  | 6.2 |
| ROLLBACK to postgres reads | 2.4 | 6.6 |  |  |  |  | 2.0 |
| P3 split 70% (again) | 3.0 | 9.6 |  |  | 2.0 |  | 2.0 |
| P3 split 100% |  | 7.1 |  |  | 2.4 |  | 2.0 |
| P4 redis primary, pg dual |  |  | 12.0 | 2.0 |  | 2.0 |  |
| P5 redis only |  |  |  | 2.2 |  | 2.2 |  |

## Per step: parity and failure counters

| Step | match | mismatch | race | error | shadow dropped | secondary write failures |
|---|---:|---:|---:|---:|---:|---:|
| P0 baseline | 0 | 0 | 0 | 0 | 0 | 0 |
| P1 dual writes | 0 | 0 | 0 | 0 | 0 | 0 |
| P2 shadow 5% | 829 | 0 | 0 | 0 | 0 | 0 |
| P2 shadow 20% | 3606 | 0 | 0 | 0 | 0 | 0 |
| P2 shadow 50% | 7780 | 0 | 0 | 0 | 0 | 0 |
| P2 shadow 100% | 24003 | 0 | 0 | 0 | 0 | 0 |
| P3 split 5% | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 30% | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 70% | 0 | 0 | 0 | 0 | 0 | 0 |
| ROLLBACK to postgres reads | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 70% (again) | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 100% | 0 | 0 | 0 | 0 | 0 | 0 |
| P4 redis primary, pg dual | 0 | 0 | 0 | 0 | 0 | 0 |
| P5 redis only | 0 | 0 | 0 | 0 | 0 | 0 |

Final parity totals from /admin/storage-mode: `{"match": 36491, "mismatch": 0, "race": 0, "error": 0, "dropped": 0, "mismatchRate": 0, "lastMismatch": null}`

## Backfill command output

```
Scanned:              401,218
Copied:               188,899
Skipped (after T0):   15,001   complete via dual writes
Skipped (expired):    0
Not final yet:        197,318
Elapsed:              00:00:18.8290999
Some pre-T0 buckets can still receive events. Run again after 2026-10-18T00:06:00.0000000+00:00 (re-runs are safe: SET is idempotent).
```

Notes: Prometheus `increase()` over a step window is approximate (samples every 5 s, extrapolated at window edges). Server-side latency excludes the network and k6; the k6 numbers include both.

## Analysis notes (added after the run)

- **The P3 split 70% spike** (PostgreSQL write p99 193 ms, read 75 ms) was confined to PostgreSQL: 234 of 6,002 writes in that window took over 50 ms, versus 0 in the windows either side. Redis reads in the same window stayed at about 2 ms.
  - **Not GC:** .NET GC pause time was 0.16–0.21 s in every window.
  - **A PostgreSQL checkpoint was in progress** (12:17:50–12:22:20), but it also spans windows without a spike.
  - Autovacuum isn't logged by default. **Unattributed.** Setting `log_autovacuum_min_duration = 0` would let a repeat run attribute it.
- **Redis p99 of "2.0 ms"** sits on a histogram bucket edge (2 ms), so the true value lies between 1 and 2 ms. Bucket resolution limits what a histogram-based percentile can resolve.
