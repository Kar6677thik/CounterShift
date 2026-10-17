# 02: v0-scattered, the honest first version

**Batch A · Milestone M2** · Git tag: `v0-scattered` · Field Guide: §7, §9, §10, §23, §24

## What we built

A working fraud service with the storage code written **directly inside the endpoints**. This is deliberate, and it is also how most first versions look.

| Piece | Where | Notes |
|---|---|---|
| Config → domain | `Configuration/CounterShiftOptions.cs`, `appsettings.json` | Counters and rules are config. A bad config **fails startup** and lists every error. |
| Schema | `Database/Migrations/0001_create_counter_buckets.sql` | Row per bucket; primary key `(counter_key, granularity, bucket_start)`. |
| Migration runner | `Database/MigrationRunner.cs` | Ordered, checksummed, advisory-locked. Runs at startup. |
| Endpoints | `Program.cs` | `POST /v1/events`, `POST /v1/decisions`, `GET /v1/counters/{c}/{e}`, `/healthz`, `/readyz`. **SQL inline.** |
| Retention | `RetentionCleanupService.cs` | Deletes aged-out buckets in batches of 5,000, every 5 minutes. |
| Errors | `ErrorHandling/ProblemExceptionHandler.cs` | Validation → 400/422, storage down → 503 (fail closed, ADR 0004). Responses are `problem+json`. |
| Tests | `tests/CounterShift.Api.Tests` | 18 tests against **real PostgreSQL** via Testcontainers, with a frozen clock. |

### The increment, as written in v0

```csharp
await using var tx = await conn.BeginTransactionAsync(ct);
foreach (var increment in increments)
{
    var current = SELECT count FROM counter_buckets WHERE …;            // 1. read
    if (current is null) INSERT … VALUES (…, 1);                        // 2a. create
    else                 UPDATE … SET count = @current + 1 WHERE …;     // 2b. write back
}
await tx.CommitAsync(ct);
```

It passes every test. It's in a transaction. It looks fine. **M3 will show it loses updates under concurrency**, and that being inside a transaction doesn't help. Before reading 03, try to predict *why* the transaction doesn't protect it.

## Why this way

- **Why ship something knowingly naive?** Two Break experiments need an honest "before":
  - **B1** (M3) measures lost updates from this exact read-modify-write.
  - **B2** (M4) measures how much code a storage swap touches. Baseline, measured now:

    | Measure (v0) | Value |
    |---|---|
    | Production files that know about PostgreSQL / Npgsql / Dapper | **5** (`Program.cs`, `RetentionCleanupService.cs`, `MigrationRunner.cs`, `ProblemExceptionHandler.cs`, the `.sql` file) |
    | SQL statements against `counter_buckets` in C# | **7** (5 in `Program.cs`, 2 in the cleanup service) |
    | Endpoints that open a database connection themselves | **4 of 5** |

- **Why read all of a decision's buckets in one query?** `unnest(@keys, @granularities, @starts)` turns three arrays into a virtual table to join against. One round trip instead of one per bucket avoids the classic N+1 problem (Field Guide §10.9).
- **Why does the migration runner take an advisory lock?** In k3s we'll run 2 replicas. Both start at once, and both would try `CREATE TABLE`. `pg_advisory_lock` makes the second one wait, after which it sees the migration already applied.
- **Why are migrations checksummed?** Editing an applied migration means dev and prod silently have different schemas. The checksum turns that into a startup error: *"add a new migration instead"*.
- **Why `200` for events, not the spec's `202`?** `202 Accepted` promises "received, will process later". We commit before replying, so `200` is the honest status. The spec is updated.
- **Why are request DTOs all-nullable?** A missing `occurredAt` would otherwise deserialize to `0001-01-01` and get a confusing "too late" error. Nullable fields let us say "`occurredAt` is required".

## Why now

The domain (M1) is proven correct in isolation. Now it meets a real database, real HTTP and real configuration. From here every later milestone is a **change to working software**, which is the situation Grab was in.

## What broke / surprised us

1. **Invalid JSON returned 500, not 400.** In Development, minimal APIs *throw* `BadHttpRequestException` rather than writing a 400. Our exception handler only knew about domain and database errors, so the framework produced a 500. A client typo would have looked like a server bug and paged someone. The fix is one `case` in the handler, and `Invalid_json_is_400` guards it.
2. **`dotnet run --environment Development` was ignored.** In .NET 10, `dotnet run` treats `--environment` as its own option, so the app saw the Production environment, found no connection string, and **refused to start**. That is fail-fast doing its job, with a clear message. Use `ASPNETCORE_ENVIRONMENT=Development` or the `http` launch profile.
3. **"Last 10 minutes" can be one bucket.** At 08:10:16 the window starts at 08:00:16, which rounds to 08:00, an hour boundary. So the whole window is answered by a single `1h` bucket that, so far, only contains events up to now. ADR 0002 working as designed, visible through `GET /v1/counters/...`.

## Concepts introduced

- Schema migrations: versioning, checksums, advisory locks (§9.9, §22)
- Composite primary keys as the access path; a secondary index for retention, and its write cost (§10.1–10.2)
- Batched reads with `unnest` versus N+1 (§10.9, §10.10)
- Batched deletes with `ctid` and why huge DELETEs hurt (§10.21)
- RFC 9457 problem details; 400 vs 422 vs 503 (§7)
- Liveness vs readiness under a real outage (`StorageOutageTests`, §24)
- Integration tests with Testcontainers and a fake clock (§23)

## Commands

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/CounterShift.Api --no-launch-profile --urls http://localhost:5072

# six failed payments, then ask for a decision
for i in 1 2 3 4 5 6; do
  curl -s -X POST localhost:5072/v1/events -H 'content-type: application/json' -d "{
    \"eventId\":\"$(uuidgen)\",\"type\":\"payment.failed\",
    \"occurredAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",
    \"entities\":{\"cardId\":\"c_42\",\"userId\":\"u_7\"}}"
done
curl -s -X POST localhost:5072/v1/decisions -H 'content-type: application/json' \
  -d '{"action":"payment","entities":{"cardId":"c_42","userId":"u_7"}}'
curl -s "localhost:5072/v1/counters/payment_failed_by_card/c_42?window=PT10M"

# inspect the table directly
docker exec -it countershift-postgres-1 psql -U countershift -c \
  "SELECT * FROM counter_buckets ORDER BY counter_key, granularity, bucket_start;"
```

## Self-check

1. The v0 increment runs inside a transaction. Predict the lost-update scenario step by step, with two writers and the default isolation level, before reading note 03.
2. Two pods start at the same moment against an empty database. Walk through what each does in `MigrationRunner`.
3. Why does `/healthz` stay 200 while the database is down, and what would happen in Kubernetes if it didn't?
4. Why is the retention index `(granularity, bucket_start)` needed, and what does it cost on every event?
5. Which files would you have to change to move counters to Redis right now? (Keep your answer; M4 checks it.)
