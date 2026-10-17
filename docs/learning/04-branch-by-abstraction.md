# 04: Break B2 and branch by abstraction: putting storage behind a seam

**Batch B · Milestone M4** · Field Guide: §2 (DIP, ISP, LSP, SRP), §3 (Adapter, Facade, Strategy), §6 (branch by abstraction, characterization tests, fitness tests)

## What we did

1. **Measured the coupling (B2)** in `v0-scattered`, then refactored, then measured again.
2. Introduced the storage **seam**: `ICounterBackend` (what a store must do) and `CounterStore` (the facade the API talks to).
3. Moved every PostgreSQL detail into `src/CounterShift.Storage.Postgres`: SQL, migrations, retention cleanup, the connection pool and error translation.
4. Added an in-memory backend (the spec's "Mock").
5. Wrote **contract tests** (one suite, every backend) and **architecture tests** (compiled-reference rules).

## B2: "just swap the database", before and after

| Measure | v0-scattered | After M4 |
|---|---:|---:|
| Production files outside a backend project that know PostgreSQL / Npgsql / Dapper / SQL | **4** (+ the `.sql` file) | **0** |
| API endpoints that open DB connections themselves | 4 of 5 | **0** |
| Lines in the API that name PostgreSQL | dozens (all the SQL) | **3** (registration in `Program.cs`) |
| Files you'd edit to add a second store | 4, rewriting each | 1 new project + 1 line in `Program.cs` |

The refactor touched 15 source files (+384 / −133 lines). **The API tests didn't change at all** apart from one `using` line, and all of them still passed. That's how you know a refactor preserved behaviour: the old tests act as *characterization tests* (Field Guide §6.6).

## The shape after M4

```
          ┌──────────────────────── CounterShift.Api ─────────────────────────┐
request → │ endpoints:  Plan (domain) → CounterStore → Evaluate (domain)        │
          │ Program.cs: AddPostgresCounterBackend(...)   ← only line naming PG  │
          └───────────────────────────────┬──────────────────────────────────────┘
                                          │ CounterStore (Facade)
                           CounterShift.Storage: ICounterBackend, StorageUnavailableException
                                          │
            ┌─────────────────────────────┼─────────────────────────────┐
  Storage.Postgres (Adapter)      InMemoryCounterBackend (Mock)     Storage.Redis (M5)
  SQL, migrations, cleanup
```

## The patterns, and where each one is

| Pattern | Where | What it buys us |
|---|---|---|
| **Adapter** | `PostgresCounterBackend` | Converts "increment these buckets" into SQL, and Npgsql exceptions into `StorageUnavailableException`. Callers speak one language. |
| **Facade** | `CounterStore` | One simple entry point over a subsystem that will soon contain modes, shadows and splits (M6). |
| **Strategy** | `ICounterBackend` implementations | The algorithm for storing counts is chosen at runtime by configuration (M6). |
| **Branch by abstraction** | the whole milestone | Introduce the seam *before* building the new implementation, while keeping `main` releasable at every commit. |

Note the Field Guide's warning (§3): "do not force these patterns". We earn them here because we *will* have three implementations and runtime switching. In a project that will only ever use PostgreSQL, this would be over-engineering.

## Why the error translation matters more than it looks

Before M4, `ProblemExceptionHandler` caught `NpgsqlException`. That's a driver type leaking into HTTP code. If Redis failed, the handler wouldn't recognise it and you'd get 500 instead of 503. Now every backend promises: *transient* failures become `StorageUnavailableException(backend)`. Note the word *transient*: a constraint violation is a bug, and we **want** it to be a 500 that pages someone, not a 503 that looks like an outage.

## Tests that guard the seam

- **Contract tests** (`tests/CounterShift.Storage.Tests/CounterBackendContract.cs`): 9 tests, including concurrent increments with alternating order (B1 and B5 in miniature), and "an unreachable store throws `StorageUnavailableException`". They run against `memory` and `postgres` today, and `redis` joins in M5. A backend that passes may be used by the facade. This is the **Liskov Substitution Principle** turned into code.
- **Architecture tests** (`ArchitectureTests.cs`) read *compiled* references. We checked that they bite: adding one `typeof(Npgsql.NpgsqlConnection)` to `Program.cs` made the test fail immediately.

## Why now

The migration machinery (M6) needs two backends behind one interface, and the second backend (M5) needs a contract to satisfy. Doing the refactor *before* writing Redis code means the seam is shaped by what the system needs, not by what Redis happens to offer.

## What surprised us

- The migration checksum survived the move. The `.sql` file moved projects, and its embedded resource name changed, but the content (and therefore the checksum) didn't, so existing databases accept it. Content-addressed checks are robust to refactoring.
- The interface came out *smaller* than expected. Retention and migrations are not part of it: they're chores particular to one backend, and Redis doesn't need them.

## Commands

```bash
git diff --stat v0-scattered..HEAD -- src        # the refactor's footprint
grep -rlE "Npgsql|Dapper|counter_buckets" src    # where the database knowledge lives now
dotnet test --project tests/CounterShift.Storage.Tests   # contract tests vs memory + postgres
dotnet test --project tests/CounterShift.Api.Tests -- --filter-class "*ArchitectureTests"
```

## Self-check

1. Why does decomposition stay in the domain instead of moving into each backend?
2. What's the difference between the Adapter and the Facade here? Could one class be both?
3. A Postgres `23505 unique_violation` reaches `PostgresCounterBackend`. Should it become `StorageUnavailableException`? Why not?
4. What exactly does the architecture test inspect, and why is that stronger than a code review rule?
5. Compare your answer to self-check 5 in note 02 ("which files would you change to move to Redis?") with the table above.
