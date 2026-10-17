# CounterShift — Specification

> Project 2 of *10 Engineering Projects from Real Company Problems*.
> Inspired by Grab's Counter Service and its zero-downtime storage migration (July 2026).
> Status: **v1.0 — specification approved 2026-10-17.** All open questions are resolved (§16). Changes from here on go through an ADR.

---

## 0. How to read this document

This spec is the single source of truth for *what* we build and *why*. Every decision carries a short reason, so you can still tell later whether it should change. Learning notes (the *how* and *when*, written as we go) live in `docs/learning/`. Formal decision records live in `docs/adr/` (see §13).

Vocabulary used throughout:

| Term | Meaning |
|---|---|
| **Event** | Something that happened, e.g. `payment.failed` for card `c_42` at 10:03:12Z. |
| **Counter** | A named thing we count, e.g. `payment_failed_by_card`. It is defined by an event type and an entity field. |
| **Counter key** | A counter plus one entity value: `payment_failed_by_card:c_42`. This is the unit of storage. |
| **Granularity** | Bucket width: `1m`, `15m`, `1h`, `1d` (see §4.3). |
| **Bucket** | The count for one counter key in one aligned time slot, e.g. `[10:00, 10:15)`. |
| **Window** | The time range a rule asks about, e.g. "last 10 minutes". |
| **Decomposition** | Splitting a window into the fewest aligned buckets that cover it. |
| **Backend** | One storage implementation: Legacy (PostgreSQL, row per bucket), New (see §6.2), or Mock (in-memory). |
| **Facade** | The single entry point every caller uses for storage (`CounterStore`). It hides which backend(s) run. |
| **Read mode / write mode** | Configuration that tells the facade which backend(s) to read from and write to (see §7). |

---

## 1. The problem, in one paragraph

Fraud rules ask time-windowed questions: *"how many payments failed on this card in the last 10 minutes?"* You cannot scan raw events per request at high queries per second (QPS), so counts are **pre-aggregated into time buckets** when events arrive, and **summed at query time**. That is the first half of the project. The second half follows Grab's real 2026 work: the storage design that worked at first becomes the bottleneck, and you must **move to a new storage design while traffic keeps flowing**, with every step reversible by configuration alone.

## 2. Goals and non-goals

### Goals
1. A working fraud-counter service: ingest events, maintain bucketed counters, and evaluate three rules that return `allow`, `challenge` or `block`.
2. Demonstrate the two failures named in the project brief, with measurements:
   - **Lost updates** from read-modify-write under 50 concurrent writers.
   - **"Just swap the database"**: count the files a naive swap would touch.
3. A zero-downtime migration from the Legacy to the New storage design using a facade, shadow reads with a mismatch metric, a deterministic traffic split, a config-only cutover, and **a rollback in the middle of a load test with zero failed requests**.
4. Report mismatch rate, p50 and p99 per backend, and storage used per million events, with machine specs and the exact commands.
5. Deploy to the home k3s server through GitHub Actions only, never by hand.
6. **Learning-grade documentation:** every milestone produces a learning note covering what was built, why, when in the sequence, what broke and what you should be able to explain afterwards.

### Non-goals (and why)
| Not doing | Why |
|---|---|
| Kafka or stream ingestion | Project 3 covers streaming. HTTP ingestion keeps the focus on storage. |
| A user interface for creating counters (Grab has one) | Counters and rules come from a config file. A UI teaches nothing about storage. |
| Authentication on the API | Project 1 is the gateway that adds auth in front of services. The service is reachable only on the tailnet and LAN (§11.4). |
| Multi-node databases, replication or sharding | We have one machine. We discuss these in the report but do not fake them. |
| Exactly-once event ingestion | See §4.6. Recorded as a known limitation. |
| Machine-learning fraud scoring | Rules are simple thresholds. The project is about the counter infrastructure. |

---

## 3. Technology stack

| Concern | Choice | Why |
|---|---|---|
| Language and runtime | **C# on .NET 10 (LTS)**, ASP.NET Core minimal APIs | Default stack in the guide. You already have the SDK. Same stack as Project 1, so ideas carry over. First-class xUnit and Testcontainers support. |
| Legacy store | **PostgreSQL 17** | Required by the brief (row per bucket, `INSERT … ON CONFLICT`). |
| New store | **Redis 8, one hash per counter key per granularity** (see §6.2) | Closest to Grab's Aerospike map-per-record model, and it changes the engine as well as the schema, as Grab did. |
| Data access | **Npgsql + Dapper** (no Entity Framework) | The SQL *is* the lesson. An object-relational mapper (ORM) would hide the upsert and locking behaviour we want to see. |
| Schema migrations | Versioned `.sql` files plus a ~50-line runner with a `schema_migrations` table | You see exactly how migration tools work, with nothing hidden. |
| Redis client | StackExchange.Redis | The standard .NET client. It supports pipelining and hash-field expiry. |
| Hashing for the split | `System.IO.Hashing.XxHash64` | Stable across processes and machines. **`string.GetHashCode()` is randomised per process in .NET**, so it would send the same key to different backends on different pods. This is a real bug we will deliberately avoid and document. |
| Time | `TimeProvider` (built into .NET) | Bucketing logic becomes testable with a fake clock. |
| Metrics | OpenTelemetry metrics exposed on `/metrics` in Prometheus format | One standard instrumentation API. Prometheus and Grafana read it. |
| Tests | xUnit, Testcontainers (real PostgreSQL and Redis), FsCheck for property tests on decomposition | Unit tests where logic is pure, real containers where the database is the point. |
| Load | k6 | Named in the brief. Scripts are JavaScript. |
| Local environment | Docker Compose | One `docker compose up` for API, PostgreSQL, Redis, Prometheus and Grafana. |
| Deploy | Docker image → GHCR → k3s through GitHub Actions | Matches the server (k3s, no Docker) and your existing proven pattern. |

---

## 4. Domain model

### 4.1 Events

```json
POST /v1/events
{
  "eventId":   "8f1c…",                    // client-generated UUID (used for logging; see §4.6)
  "type":      "payment.failed",           // one of the known types below
  "occurredAt":"2026-10-17T10:03:12Z",     // event time, UTC, ISO-8601
  "entities":  { "userId": "u_7", "cardId": "c_42", "deviceId": "d_9" }
}
```

Known event types: `login.failed`, `payment.failed`, `ride.requested`. Unknown types return **400**.

**Event time, not arrival time.** An event is bucketed by `occurredAt`. If the network delays an event by 3 minutes, it should still count toward the minute it happened in.

**Lateness and skew limits:**
- `occurredAt` older than `now − MaxLateness` (default **5 minutes**) is rejected with **422**.
- `occurredAt` later than `now + MaxClockSkew` (default **30 seconds**) is rejected with **422**.
- Why bound lateness: it keeps the backfill race in §8 provably safe, and it prevents a client with a broken clock from writing into old buckets. Grab's stream ingestion has the same concern. We state it explicitly.

### 4.2 Counters (configured, not coded)

```jsonc
"Counters": [
  { "name": "payment_failed_by_card", "eventType": "payment.failed", "entity": "cardId" },
  { "name": "payment_failed_by_user", "eventType": "payment.failed", "entity": "userId" },
  { "name": "login_failed_by_user",   "eventType": "login.failed",   "entity": "userId" },
  { "name": "ride_requested_by_user", "eventType": "ride.requested", "entity": "userId" }
]
```

One event can feed several counters. Each counter then writes **one increment per granularity**, so one `payment.failed` event becomes 2 counters × 3 granularities = **6 bucket increments**. This *write amplification* buys cheap reads. We measure it and report it.

Counter key format: `{counterName}:{entityValue}`, for example `payment_failed_by_card:c_42`.

### 4.3 Buckets and granularities

- All buckets are aligned to **UTC** boundaries. A 15-minute bucket starts at :00, :15, :30 or :45. A day bucket starts at 00:00Z.
- Granularities: **1m, 15m, 1h, 1d**. Grab's three tiers, plus a 1-minute tier (decision Q3).
- **Short windows and precision (decided: option B).** "Last 10 minutes" does not line up with 15-minute buckets. Key insight: the *end* of a window ("now") is never a problem, because the current bucket holds only events up to now. Only the **start edge** is imprecise. Two options:
  - **(A) Follow Grab exactly (15m, 1h, 1d), round the start edge outward.** At 12:07, "last 10 min" starts at 11:57, which falls in bucket `[11:45, 12:00)`. We include that whole bucket, so the effective window is 22 minutes. Rounding outward over-counts, which suits fraud (it fails safe toward catching more). Worst-case error is just under one 15-minute bucket.
  - **(B) Add a 1-minute tier, retained for 3 hours.** Short windows become accurate to within 1 minute. The cost is a fourth increment per counter per event and many more rows.
  - *Chosen: **(B)**.* The brief's own example rule is "10 minutes", and (A) would make it effectively 10 to 24 minutes, which is a confusing first rule. Decomposition stays general either way: the tiers are a config list, so (A) remains one config change away, and we can measure both.
- Every response that returns a count also returns the **effective window**, the exact range actually summed, so the rounding is never hidden.

### 4.4 Window decomposition (the core algorithm)

Given a window `[start, end)` and the configured granularities from coarse to fine, cover the range with the **fewest buckets**:

1. Round `start` *down* to the finest granularity (outward rounding, as in §4.3).
2. Greedily, from the left: take the largest granularity whose bucket starts exactly at the cursor and ends at or before `end`'s enclosing boundary. Otherwise take the next smaller one.
3. The final bucket(s) at the right edge contain "now", which is fine (see §4.3).

Example from Grab's original post: `9/10 23:15:20 → 9/12 17:20:18` decomposes into 15m buckets for the rest of the first hour, hourly buckets for the rest of that day, daily buckets for full days, then hourly and 15m buckets up to the end.

This is a **pure function**, `Decompose(window, granularities) → IReadOnlyList<BucketRef>`, with no I/O. It gets the heaviest testing: example tests plus property tests (buckets never overlap, they are contiguous, they cover the window, and the count is minimal).

### 4.5 Retention

| Granularity | Kept for | Why |
|---|---|---|
| 1m | 3 h | Used only at window edges of short windows. |
| 15m | 8 d | Grab uses 15m buckets at **both** edges of long windows, so every tier must cover the maximum window. |
| 1h | 8 d | Same reason. |
| 1d | 8 d | Same reason. |

**Maximum query window: 7 days.** Retention is 7 days plus a 1-day safety margin.

How old data disappears differs per backend, and that difference is a lesson in its own right:
- **Legacy (PostgreSQL):** a cleanup background service deletes expired rows in small batches. The report discusses why *time-partitioned tables plus `DROP PARTITION`* is the production answer, and that is a stretch goal.
- **New (Redis hash):** expiry per hash field (`HEXPIRE`, Redis 7.4+) on every increment, plus a key-level TTL as a safety net. Grab prunes inside the write operation for the same reason.

### 4.6 Idempotency (a decided limitation)

HTTP clients retry, and a retried event would be counted twice. We **do not deduplicate in v1**. Reason: deduplication needs its own store (an `eventId` seen-set with a TTL), and that store would itself need migrating, which doubles the scope of the migration story. Grab's design accepts the same tradeoff: counts self-heal as new events flow. `eventId` is logged so a duplicate can be diagnosed. Deduplication is listed as a stretch goal (§14).

### 4.7 Rules and decisions

```jsonc
"Rules": [
  { "id": "R1", "description": "Too many failed payments on one card",
    "appliesTo": ["payment"], "counter": "payment_failed_by_card",
    "window": "PT10M", "threshold": 5,  "outcome": "block" },
  { "id": "R2", "description": "Repeated failed logins",
    "appliesTo": ["login", "payment"], "counter": "login_failed_by_user",
    "window": "PT15M", "threshold": 3,  "outcome": "challenge" },
  { "id": "R3", "description": "Ride-request burst (exercises multi-granularity decomposition)",
    "appliesTo": ["ride"], "counter": "ride_requested_by_user",
    "window": "PT90M", "threshold": 8,  "outcome": "challenge" }
]
```

- A rule triggers when `count > threshold`. The comparison is strictly greater than, matching the brief's wording "more than 5".
- The decision is the **most severe** triggered outcome: `block` beats `challenge`, which beats `allow`.
- All counters a decision needs are read in **one batched storage call**. This matters for latency and mirrors Grab's batch reads.
- If storage fails, the decision API returns **503**. It does not return `allow`, because we fail closed. We write the reason in an ADR, and the report discusses whether a real payment flow would prefer to fail open.

---

## 5. API

| Method and path | Purpose | Success |
|---|---|---|
| `POST /v1/events` | Ingest one event | `202` with `{ counterKeysUpdated: [...] }` |
| `POST /v1/decisions` | Evaluate rules for an action | `200` (see below) |
| `GET /v1/counters/{counter}/{entity}?window=PT90M` | Debugging and teaching: count plus the buckets used | `200` |
| `GET /healthz` | Liveness: the process is up | `200` |
| `GET /readyz` | Readiness: the stores *required by the current mode* are reachable | `200` / `503` |
| `GET /admin/storage-mode` | Read-only: the effective read and write modes, and when they loaded | `200` |
| `GET /metrics` | Prometheus scrape endpoint | `200` |

```json
POST /v1/decisions
{ "action": "payment", "entities": { "userId": "u_7", "cardId": "c_42" } }

200 OK
{
  "decision": "block",
  "evaluatedAt": "2026-10-17T10:05:00Z",
  "rules": [
    { "id": "R1", "triggered": true,  "count": 7, "threshold": 5,
      "effectiveWindow": { "from": "2026-10-17T09:55:00Z", "to": "2026-10-17T10:05:00Z" } },
    { "id": "R2", "triggered": false, "count": 1, "threshold": 3, "effectiveWindow": { … } }
  ]
}
```

Errors use RFC 9457 `application/problem+json`, the built-in ASP.NET Core format.

---

## 6. Storage designs

All backends implement one small interface (Interface Segregation, §2 of the Field Guide):

```csharp
interface ICounterBackend
{
    string Name { get; }                                         // "legacy" | "new" | "mock"
    Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct);
    Task<IReadOnlyList<BucketValue>> ReadAsync(IReadOnlyList<BucketRef> buckets, CancellationToken ct);
    Task<StorageStats> GetStatsAsync(CancellationToken ct);      // for "bytes per million events"
}
```

Decomposition and summing stay **outside** the backends, so every backend is dumb storage. That is why parity comparison is meaningful: the same buckets go in and the same sum logic runs. Grab made the same choice: backends emit tuples and the orchestrator sums them.

### 6.1 Legacy: PostgreSQL, one row per bucket

```sql
CREATE TABLE counter_buckets (
  counter_key  text        NOT NULL,
  granularity  smallint    NOT NULL,   -- seconds/60: 1, 15, 60, 1440
  bucket_start timestamptz NOT NULL,
  count        bigint      NOT NULL CHECK (count >= 0),
  PRIMARY KEY (counter_key, granularity, bucket_start)
);
```

- **Write:** one multi-row `INSERT … ON CONFLICT (pk) DO UPDATE SET count = counter_buckets.count + EXCLUDED.count`. Rows are **sorted by primary key before sending**, because two concurrent multi-row upserts that touch the same rows in different orders can **deadlock**. This is consistent lock ordering (Field Guide §11.11), and we will prove it with a test.
- **Read:** one query with `unnest` arrays of `(key, granularity, start)`, which uses the primary-key index.
- **Broken variant for the Break step:** `NaiveReadModifyWriteBackend` (`SELECT count`, add 1 in C#, `UPDATE`). It is registered **only** in the experiment harness, never in the app.

### 6.2 New design — Redis hash (decided, Q2)

**Chosen: Redis, one hash per (counter key, granularity).**
```
key:   cs:{payment_failed_by_card:c_42}:15m
field: 1760695200        (bucket start, unix seconds)
value: 7
```
- Write: a pipeline of `HINCRBY` plus `HEXPIRE` per field. One round trip, and each command is atomic, so there is no read-modify-write.
- Read: pipelined `HMGET` per key.
- This mirrors Grab's new model: *one record per counter holding a map of buckets*, with pruning in the write path. It also changes the **engine**, which is where cross-engine migration lessons come from: different failure modes and different durability (Redis append-only file with `fsync` every second, a tradeoff we document).

**Rejected for now: PostgreSQL, one row per counter key with a `jsonb` map.**
- Same engine, so it isolates the *schema* change. Grab says most of its gain came from the schema.
- Trap worth learning: every increment rewrites the whole `jsonb` value and creates a new tuple version (MVCC), and all increments to a hot counter serialise on one row lock. It will probably be **slower** for writes. That is an honest and interesting result, but it makes for a weaker migration story.

Implementing jsonb as a third backend and comparing all three stays a stretch goal (§14). The facade makes a third backend cheap.

### 6.3 Mock

An in-memory `ConcurrentDictionary` implementation. It is used in unit tests and as a deliberately divergent backend to prove that the mismatch detector actually detects mismatches.

### 6.4 Contract tests

One shared test suite (`CounterBackendContract`) runs against **every** backend. It covers increment and read round-trips, concurrent increments losing nothing, absent buckets reading as 0, and expiry. This is the Liskov Substitution Principle made executable: if a backend passes, the facade may use it.

---

## 7. Migration machinery (the heart of Project 2)

### 7.1 Two independent knobs

Grab describes *read* modes. We add an explicit **write mode** too, because that is where data consistency is decided:

| Knob | Values |
|---|---|
| `Storage:Write` | `Single(legacy)` → `Dual(primary=legacy, secondary=new)` → `Dual(primary=new, secondary=legacy)` → `Single(new)` |
| `Storage:Read` | `Single(b)`, `Shadow(primary, shadow, percent)`, `Split(primary, secondary, percent)` |

**Write semantics:**
- `Dual` writes to the primary **synchronously**. The request fails only if the primary fails.
- The secondary is written **synchronously after the primary, but its failure does not fail the request**. Instead it increments `cs_secondary_write_failures_total` and logs the keys involved.
- Why synchronous rather than fire-and-forget: a secondary write still sitting in an in-memory queue when the pod restarts is silently lost. That would show up later as "mysterious" mismatches. Synchronous costs some latency, which we measure. Grab's writer used async shadow writes, and we explain the difference in an ADR.
- **Dual-write covers all keys, not a percentage.** Grab gated shadow writes by the rollout percentage. We do not, because then every key entering the rollout would need its history backfilled at that moment. Writing everything plus one backfill (§8) is simpler and easier to prove correct.

**Read semantics:**
- `Shadow`: the primary serves the response. For `percent`% of requests (chosen by deterministic hash), the same bucket refs are read from the shadow **in the background** and compared. Shadow work goes through a **bounded queue that drops when full**, so a slow shadow can never slow down or break real traffic. Drops are counted (`cs_shadow_dropped_total`).
- `Split`: `percent`% of **counter keys** are read from the secondary and the rest from the primary. Selection: `XxHash64(counterKey) mod 10_000 < percent × 100`.
  - It is keyed on the counter key, not the request, so a given card always reads from the same backend at a given percent and decisions stay stable.
  - Raising the percent only *adds* keys. No key flips back and forth during a ramp. We test this property.

### 7.2 Parity: what counts as a mismatch

A naive comparison produces false alarms: between the primary read and the shadow read, a new event may increment the current bucket on one store but not yet the other. So each comparison is classified:

| Class | Meaning | Metric label |
|---|---|---|
| `match` | Sums are equal | `result="match"` |
| `mismatch_closed` | The difference is in buckets that **ended before `now − MaxLateness`** | `result="mismatch"`: **a real bug** |
| `mismatch_open` | The difference involves only still-open buckets | `result="race"`: expected noise, tracked but not alarming |

The comparison is done **per bucket**, not just on the sum, so we know *which* bucket diverged and can log it.

**Reported mismatch rate** = `mismatch / (match + mismatch)`, excluding `race`. **Go/no-go gate for each ramp step: 0 closed-bucket mismatches across at least 100,000 comparisons.** The gate number is our choice, recorded in an ADR.

### 7.3 Configuration and how it changes at runtime

- Modes live in `storage-mode.json`, loaded through `IOptionsMonitor` with reload on change. The app picks up a new mode **without restarting**.
- In k3s this file comes from a **ConfigMap mounted as a directory**. This matters: a `subPath` mount *never* updates, and environment variables never update. The kubelet syncs mounted ConfigMaps within about 60–90 seconds, and we will **measure** that propagation delay because it is our rollback time.
- Invalid modes (unknown backend, percent outside 0–100, `Split` with a secondary that is not being written) are **rejected on load**. The service keeps the last good mode and fires `cs_config_reload_failures_total` (fail fast, Field Guide §2.18).
- `GET /admin/storage-mode` shows the effective mode so you can *see* the cutover happen.
- Normal changes go through Git: a pull request that changes the ConfigMap, deployed by CI (config as code). For the rollback drill, we also allow a direct `kubectl apply` of the ConfigMap, because waiting for CI during an incident is exactly the wrong lesson. Both paths are documented in the runbook.

### 7.4 The migration plan (what we will rehearse)

```
Phase 0  write=Single(legacy)                 read=Single(legacy)
Phase 1  write=Dual(legacy→new)               read=Single(legacy)      + run backfill (§8)
Phase 2  write=Dual(legacy→new)               read=Shadow(legacy,new, 5→20→50→100%)   gate each step
Phase 3  write=Dual(legacy→new)               read=Split(legacy,new, 5→30→70→100%)
         └─ DRILL: during k6 load at 70%, roll back to Split 0% / Single(legacy); expect 0 non-2xx
Phase 4  write=Dual(new→legacy)               read=Single(new)        legacy still current → rollback still possible
Phase 5  write=Single(new)                    read=Single(new)        "contract" step; legacy decommissioned
```

This is **expand and contract** (Field Guide §6.4) combined with **branch by abstraction** (§6.2) and a **parallel run** (§6.3). Every phase until 5 can be reversed with one config change, and the runbook lists the exact reverse step for each.

### 7.5 Observability for the migration

Every storage call records `cs_storage_op_duration_seconds{backend, op=read|write, role=primary|secondary|shadow, mode}`. This is how we get **p99 per backend** cleanly, without client-side guesswork. We also expose `cs_parity_comparisons_total{result}`, `cs_secondary_write_failures_total`, `cs_shadow_dropped_total`, `cs_events_ingested_total{type}` and `cs_decisions_total{outcome}`. A provisioned Grafana dashboard ships in the repo.

---

## 8. Backfill (filling the new store with history)

Grab's post does not say how history reached the new store. We have to decide, because otherwise "last 7 days" on the new store is wrong until 7 days have passed.

**Algorithm (`countershift backfill` CLI command in the same image, run as a Kubernetes Job):**
1. Dual-write is already on (Phase 1). Call the moment it took effect **T0**.
2. Copy every legacy bucket whose **end ≤ T0 − MaxLateness**, using **SET** (overwrite), not increment. Work in key-ordered batches and checkpoint progress, so the job can resume after a crash.
3. Buckets newer than that cutoff already received every one of their events through dual-write, because events older than `MaxLateness` are rejected (§4.1).

**Why this is race-free:** a bucket that ended before `T0 − MaxLateness` can no longer receive *any* writes, so the value we copy is final. Using SET makes re-running the job safe (idempotent). This is the reason §4.1 bounds lateness: the two decisions are linked, and the ADR explains how.

**Alternative we document but do not choose:** skip backfill and simply wait 8 days of dual-writing. It is valid for counters with short retention. It may be how Grab did it.

---

## 9. "Break" experiments (measured failures)

| # | Experiment | How | What we record |
|---|---|---|---|
| B1 | **Lost updates** | xUnit and console harness: 50 concurrent writers × 1,000 increments on **one** bucket with `NaiveReadModifyWriteBackend` (Read Committed). Repeat with the atomic upsert. | Expected 50,000. Actual (naive) vs actual (atomic). Also the same with `SELECT … FOR UPDATE` and `SERIALIZABLE` + retry, for comparison of throughput. |
| B2 | **"Just swap the database"** | Milestone M2 is written *honestly naive*: SQL inline in endpoint handlers, the way first versions usually are. Tagged `v0-scattered`. In M4 we count, from `git grep`, every file that knows about Npgsql or SQL, then refactor behind the facade and count again. | Files and lines touching storage, before and after. The diff stat of the refactor. |
| B3 | **Shadow backend outage** (extra) | Stop Redis during `Shadow` mode under load. | Primary p99 unchanged, 0 failed requests, shadow drops and errors counted. |
| B4 | **Hash bug** (extra) | Split using `string.GetHashCode()` with 2 replicas. | The same key reads from different backends across pods. Shows why the hash must be stable. |
| B5 | **Deadlock** (extra) | Unsorted multi-row upserts under concurrency. | Count of `40P01` deadlock errors, then zero after sorting. |

## 10. "Prove": the final report

`docs/report.md`, one page:
- Machine specs (CPU, RAM, disk, k3s version) and the exact k6 commands used.
- **B1:** lost-update counts.
- **B2:** files touched before and after.
- **Migration:** mismatch rate per ramp step; p50 and p99 read and write latency **per backend**; decision API p99 in each phase; **rollback drill**: config propagation time and the count of non-2xx responses during the switch.
- **Storage per million events** for each backend: PostgreSQL `pg_total_relation_size` including indexes vs Redis `MEMORY USAGE` / `INFO memory`, at the same event mix.
- A résumé line filled in **only with measured numbers**.

**Benchmark venue:** final numbers come from the home server, with k6 running **inside the cluster** as a Job, so tailnet latency does not pollute the results. Local runs are for development only.

**Performance targets:** none invented up front. We measure a baseline in Phase 0, and the report compares phases against that baseline.

---

## 11. Deployment

### 11.1 Target (verified, read-only)
A single-node **k3s** cluster on the home server. There is **no Docker on the host**. Other workloads (Slate) run in their own namespaces, and we must not affect them. About 4.9 GB of RAM is free.

### 11.2 Layout in the cluster
- Namespace: **`countershift`**. Everything we create lives here, which makes it easy to inspect and easy to remove.
- `Deployment` countershift-api (2 replicas, so the split and hash behaviour is real across pods), with readiness and liveness probes on `/readyz` and `/healthz`.
- `StatefulSet` postgres (1) and redis (1) on `local-path` persistent volume claims.
- `Deployment` prometheus and grafana, minimal configs kept in the repo. We do not use the full kube-prometheus-stack, because it is too heavy for one shared box.
- `Job` templates: `backfill`, `k6-load`.
- **Resource limits** on every pod. Total memory budget is about **1.5 GiB**, so we leave headroom for Slate.
- Exposure: `NodePort` services for the API and Grafana, reachable on the **tailnet and LAN only**. We will not touch the existing Tailscale Serve configuration (Slate uses it). Concrete ports are picked from unused ones at deploy time.

### 11.3 Pipeline (GitHub Actions)
```
push / PR ──► ci.yml:     restore → build → unit tests → Testcontainers integration tests
                          (GitHub-hosted runners have Docker)
push main ──► deploy.yml: build image → push ghcr.io/kar6677thik/countershift:<git-sha>
                          → join tailnet (Tailscale GitHub Action, ephemeral, tag-scoped)
                          → SSH with a dedicated deploy key → kubectl apply (namespace-scoped)
                          → kubectl rollout status (fail the job if pods are not ready)
```
- Images are tagged with the **immutable commit SHA**, never `:latest`, so a rollback means redeploying an older SHA.
- **Why not a self-hosted runner on the server:** the repo is **public**. GitHub warns that self-hosted runners on public repos can run untrusted code from forked pull requests. GitHub-hosted runners plus an ephemeral tailnet join avoids that.
- **Least privilege:** the CI SSH key is new and dedicated to CounterShift. The kubeconfig it uses belongs to a `ServiceAccount` whose permissions cover **only** the `countershift` namespace, so a leaked CI credential cannot touch Slate.
- Secrets live in GitHub repository secrets. Host details are **never** committed to the repo or the docs.

### 11.4 Security posture (honest)
There is no API authentication (see non-goals), and the service is not exposed to the public internet. Database passwords are stored as Kubernetes Secrets created by CI from GitHub secrets. They are not in Git.

---

## 12. Repository layout

```
CounterShift/
├─ src/
│  ├─ CounterShift.Domain/          # events, counters, rules, bucketing, decomposition (pure, no I/O)
│  ├─ CounterShift.Storage/         # ICounterBackend, CounterStore facade, modes, parity, split
│  ├─ CounterShift.Storage.Postgres/
│  ├─ CounterShift.Storage.Redis/
│  └─ CounterShift.Api/             # minimal API host, config, metrics, backfill CLI verb
├─ tests/
│  ├─ CounterShift.Domain.Tests/    # unit + property tests
│  ├─ CounterShift.Storage.Tests/   # contract tests via Testcontainers, facade tests with mocks
│  ├─ CounterShift.Api.Tests/       # WebApplicationFactory end-to-end
│  └─ CounterShift.Experiments/     # B1/B4/B5 harnesses (not run in CI by default)
├─ load/                            # k6 scripts + event generator (skewed / hot keys)
├─ deploy/
│  ├─ compose/                      # local docker-compose + prometheus/grafana config
│  └─ k8s/                          # manifests (namespace, statefulsets, api, monitoring, jobs)
├─ .github/workflows/               # ci.yml, deploy.yml
└─ docs/
   ├─ spec.md                       # this file
   ├─ adr/                          # 0001-…md decision records
   ├─ learning/                     # one note per milestone (the "journal")
   ├─ runbooks/                     # migration.md, rollback.md, deploy.md, secrets-setup.md
   ├─ glossary.md
   └─ report.md                     # final numbers
```

Separate projects per layer enforce the dependency direction at compile time: Domain depends on nothing, and Storage does not depend on Api. This is the Dependency Inversion Principle checked by the compiler. An architecture test (Field Guide §6.16) also asserts it.

---

## 13. Documentation plan (learning-first)

| Artifact | When it is written | What it answers |
|---|---|---|
| `docs/learning/NN-<milestone>.md` | At the end of each milestone | **What** we built, **why** this way, **why now** in the sequence, concepts introduced (with Field Guide section numbers), commands run, what broke, and "you should now be able to explain…" self-check questions |
| `docs/adr/NNNN-*.md` | When a decision is made | Context → decision → alternatives → consequences (Michael Nygard format) |
| Code comments | With the code | Only *why*, never *what*. Each non-obvious line links to its ADR or spec section. |
| `docs/runbooks/*` | Before the first deploy and the first migration | Exact step-by-step commands, including how to reverse each step |
| `docs/glossary.md` | Grows continuously | Every term the first time it appears |
| `README.md` | M0, updated each milestone | What it is, a Mermaid architecture diagram, quick start, links |

Starting list of ADRs: stack choice; new-backend choice; granularities and rounding; fail-closed decisions; no deduplication in v1; synchronous dual-write vs Grab's async approach; dual-write covering all keys; hash function choice; parity gate threshold; backfill strategy and lateness bound; deploy via GitHub-hosted runner + tailnet.

---

## 14. Milestones

Each milestone ends with green CI, a learning note, and a commit or pull request you can review.

| # | Milestone | Demonstrates |
|---|---|---|
| M0 | Scaffolding: solution, Compose, CI (build + test), README skeleton | Project structure, CI basics |
| M1 | Domain: bucketing, decomposition, rules engine (pure) + property tests | Time bucketing, testing pure logic |
| M2 | **v0-scattered**: endpoints with inline SQL, naive read-modify-write. Tag `v0-scattered` | The "honest first version" |
| M3 | **B1** lost-update experiment → atomic upsert, lock ordering, **B5** deadlock | Read-modify-write race, atomicity |
| M4 | **B2** measure → refactor behind `ICounterBackend` + facade (Legacy, Mock) → measure again | Branch by abstraction, Adapter, Facade, Strategy |
| M5 | New backend + shared contract tests | Liskov substitution, cross-engine semantics |
| M6 | Write and read modes, shadow comparator, split hashing, config reload, backfill | Feature toggles, parallel run, deterministic hashing |
| M7 | Metrics, Prometheus, Grafana dashboard | RED metrics, per-backend p99 |
| M8 | k6 load + full migration rehearsal locally (Phases 0–5 + rollback drill) | Load testing, runbooks |
| M9 | Container image, k8s manifests, deploy pipeline, secrets setup runbook | CI/CD, Kubernetes, least privilege |
| M10 | Migration rehearsal on the server + `report.md` + résumé line | Measuring honestly |

**Stretch goals** (only after M10): event deduplication; time-partitioned PostgreSQL table with `DROP PARTITION` retention; a reconciliation job that repairs secondary-write failures; the R+J comparison.

---

## 15. Things you (the human) will need to do later

These are listed now so they are not surprises. None are needed until M9.
1. Create a Tailscale OAuth client (or reuse the pattern from your Slate repos) and add it as a GitHub secret on this repo.
2. Approve creation of a new SSH deploy key and its `authorized_keys` entry on the server.
3. Make the GHCR package public, or accept an image-pull secret in the namespace.

---

## 16. Resolved decisions (2026-10-17)

| ID | Question | Decision |
|---|---|---|
| Q1 | Stack | **.NET 10** |
| Q2 | New backend | **Redis, one hash per (counter key, granularity)** |
| Q3 | Granularities | **1m + 15m + 1h + 1d** (1m kept for 3 h) |
| Q4 | Working cadence | **Batches**: several milestones per session. A batch ends with a summary pointing at that batch's learning notes. Each milestone is still its own commit, so the history reads milestone by milestone. |

Proposed batches: **A** = M0–M3 (foundation + first break), **B** = M4–M6 (facade, Redis, migration machinery), **C** = M7–M8 (observability, local rehearsal), **D** = M9–M10 (deploy, server rehearsal, report).
