# Migration rehearsal 20261017T120044Z: ACCIDENTAL 2× OVERLOAD

> **Read this first.** This run was meant to be 200 events/s + 400 decisions/s. A k6 container orphaned by the
> previous (crashed) run was still sending the same load, so the system actually received about **400 + 800 req/s**,
> the ~1,200 req/s ceiling found by the capacity probe. The Prometheus request rates show it; the rates halve
> in P4/P5 when the orphan's run ended. The numbers below are therefore an **overload** test, not the intended
> rehearsal. See the clean run and learning note 08. Kept because overload behaviour is worth studying.

- Machine: Windows-11-10.0.26100-SP0, 14 logical CPUs (API, PostgreSQL, Redis, Prometheus and k6 all on this one machine)
- Load: 200 events/s + 400 decisions/s, open model (k6 constant arrival rate), 30% of traffic on 1% of cards
- T0 (dual writes on): 2026-10-17T12:01:57.4957848+00:00

**k6, Phase 0 baseline run:** 41,565 requests, **failed rate 0.00000**, dropped iterations 436, client latency p50/p95/p99 = 13.14/381.67/565.23 ms

**k6, Migration run (P1→P5):** 324,748 requests, **failed rate 0.00000**, dropped iterations 5254, client latency p50/p95/p99 = 15.33/1131.62/1581.73 ms

## Per step: traffic, errors, HTTP latency (server side, from Prometheus)

| Step | Mode | Propagation (ms) | req/s events | req/s decisions | non-2xx | events p50 / p99 (ms) | decisions p50 / p99 (ms) |
|---|---|---:|---:|---:|---:|---:|---:|
| P0 baseline | `write=Single(postgres) read=Single(postgres)` |  | 400 | 800 | 0 | 9.8 / 410.5 | 5.0 / 407.1 |
| P1 dual writes | `write=Dual(postgres→redis) read=Single(postgres)` | 278 | 400 | 799 | 0 | 23.5 / 459.1 | 8.0 / 426.9 |
| P2 shadow 5% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 5%)` | 316 | 403 | 806 | 0 | 36.8 / 749.8 | 13.4 / 674.3 |
| P2 shadow 20% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 20%)` | 345 | 385 | 760 | 0 | 155.9 / 2352.1 | 124.3 / 2327.1 |
| P2 shadow 50% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 50%)` | 312 | 409 | 687 | 0 | 1248.1 / 2475.0 | 1057.7 / 2471.2 |
| P2 shadow 100% | `write=Dual(postgres→redis) read=Shadow(postgres, redis, 100%)` | 337 | 395 | 754 | 0 | 95.5 / 2414.6 | 58.0 / 2385.3 |
| P3 split 5% | `write=Dual(postgres→redis) read=Split(postgres, redis, 5%)` | 309 | 416 | 830 | 0 | 64.8 / 989.4 | 30.9 / 973.5 |
| P3 split 30% | `write=Dual(postgres→redis) read=Split(postgres, redis, 30%)` | 333 | 402 | 803 | 0 | 18.1 / 356.5 | 6.1 / 220.7 |
| P3 split 70% | `write=Dual(postgres→redis) read=Split(postgres, redis, 70%)` | 346 | 402 | 802 | 0 | 10.5 / 168.6 | 3.5 / 52.1 |
| ROLLBACK to postgres reads | `write=Dual(postgres→redis) read=Single(postgres)` | 292 | 398 | 796 | 0 | 21.6 / 238.7 | 7.1 / 231.3 |
| P3 split 70% (again) | `write=Dual(postgres→redis) read=Split(postgres, redis, 70%)` | 317 | 401 | 801 | 0 | 12.5 / 207.1 | 4.0 / 66.0 |
| P3 split 100% | `write=Dual(postgres→redis) read=Split(postgres, redis, 100%)` | 328 | 400 | 800 | 0 | 7.2 / 66.3 | 1.9 / 14.7 |
| P4 redis primary, pg dual | `write=Dual(redis→postgres) read=Single(redis)` | 324 | 340 | 680 | 0 | 8.6 / 153.3 | 2.2 / 29.5 |
| P5 redis only | `write=Single(redis) read=Single(redis)` | 312 | 200 | 400 | 0 | 0.8 / 3.3 | 0.7 / 3.0 |

## Per step: storage p99 by backend / op / role (ms)

| Step | postgres read primary | postgres write primary | postgres write secondary | redis read primary | redis read secondary | redis write primary | redis write secondary |
|---|---:|---:|---:|---:|---:|---:|---:|
| P0 baseline | 407.0 | 410.5 |  |  |  |  |  |
| P1 dual writes | 426.8 | 449.5 |  |  |  |  | 38.3 |
| P2 shadow 5% | 673.2 | 711.0 |  |  |  |  | 40.0 |
| P2 shadow 20% | 2327.1 | 2346.3 |  |  |  |  | 48.8 |
| P2 shadow 50% | 2471.1 | 2474.7 |  |  |  |  | 56.7 |
| P2 shadow 100% | 2385.3 | 2413.3 |  |  |  |  | 49.0 |
| P3 split 5% | 973.8 | 981.5 |  |  | 43.4 |  | 45.2 |
| P3 split 30% | 225.8 | 248.6 |  |  | 28.9 |  | 29.6 |
| P3 split 70% | 73.5 | 94.8 |  |  | 28.0 |  | 29.1 |
| ROLLBACK to postgres reads | 231.3 | 235.5 |  |  |  |  | 38.0 |
| P3 split 70% (again) | 83.5 | 162.2 |  |  | 29.8 |  | 34.6 |
| P3 split 100% |  | 50.0 |  |  | 14.5 |  | 16.3 |
| P4 redis primary, pg dual |  |  | 121.4 | 29.3 |  | 29.2 |  |
| P5 redis only |  |  |  | 2.2 |  | 2.5 |  |

## Per step: parity and failure counters

| Step | match | mismatch | race | error | shadow dropped | secondary write failures |
|---|---:|---:|---:|---:|---:|---:|
| P0 baseline | 0 | 0 | 0 | 0 | 0 | 0 |
| P1 dual writes | 0 | 0 | 0 | 0 | 0 | 0 |
| P2 shadow 5% | 1235 | 0 | 722 | 0 | 0 | 0 |
| P2 shadow 20% | 4242 | 0 | 2826 | 0 | 0 | 0 |
| P2 shadow 50% | 7265 | 0 | 5709 | 0 | 0 | 0 |
| P2 shadow 100% | 13456 | 0 | 20413 | 0 | 2229 | 0 |
| P3 split 5% | 3112 | 0 | 8233 | 0 | 0 | 0 |
| P3 split 30% | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 70% | 0 | 0 | 0 | 0 | 0 | 0 |
| ROLLBACK to postgres reads | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 70% (again) | 0 | 0 | 0 | 0 | 0 | 0 |
| P3 split 100% | 0 | 0 | 0 | 0 | 0 | 0 |
| P4 redis primary, pg dual | 0 | 0 | 0 | 0 | 0 | 0 |
| P5 redis only | 0 | 0 | 0 | 0 | 0 | 0 |

Final parity totals from /admin/storage-mode: `{"match": 29028, "mismatch": 0, "race": 36941, "error": 0, "dropped": 3130, "mismatchRate": 0, "lastMismatch": null}`

## Backfill command output

```
Scanned:              2,10,966
Copied:               9,228
Skipped (after T0):   7,359   complete via dual writes
Skipped (expired):    0
Not final yet:        1,94,379
Elapsed:              00:00:07.8895103
Some pre-T0 buckets can still receive events. Run again after 2026-10-18T00:06:00.0000000+00:00 (re-runs are safe: SET is idempotent).
```

Notes: Prometheus `increase()` over a step window is approximate (samples every 5 s, extrapolated at window edges). Server-side latency excludes the network and k6; the k6 numbers include both.
