# ADR 0001: .NET 10, PostgreSQL, Npgsql + Dapper, hand-rolled migrations

- **Status:** Accepted
- **Date:** 2026-10-17
- **Spec:** §3

## Context

CounterShift is a learning project. The stack should get out of the way of the ideas: bucketing, atomic increments and storage migration. It also needs to be a stack that will carry over to Projects 1, 5 and 8.

## Decision

- **C# on .NET 10 (LTS)** with ASP.NET Core minimal APIs.
- **PostgreSQL 17** as the Legacy store.
- **Npgsql + Dapper**, with no Entity Framework.
- **Plain versioned `.sql` files** applied by a small runner of our own, which records applied versions in a `schema_migrations` table.
- **xunit v3 on Microsoft.Testing.Platform**, with **Testcontainers** for anything that touches a database.
- **Central Package Management** (`Directory.Packages.props`) and strict compilation: warnings are errors, and the recommended analyzers are on.

## Alternatives considered

| Option | Why not |
|---|---|
| Python / FastAPI | Less reuse across the project series. The guide's testing flow is xUnit + Testcontainers. |
| Entity Framework Core | It hides the generated SQL. The core lesson of M3 is *which exact SQL statement* is atomic, so we want to write it ourselves. |
| DbUp / FluentMigrator / EF migrations | All are fine tools. Writing the ~50-line version once teaches what they do (ordering, checksums, a lock so two pods can't migrate at once). We can switch later. |
| xunit v2 + VSTest | The .NET 10 SDK has moved `dotnet test` to Microsoft.Testing.Platform. xunit v3 is the maintained line. |

## Consequences

- We write SQL by hand, so we are responsible for SQL injection safety. **Every value is a parameter, and no value is ever concatenated into SQL.**
- The migration runner is ours to maintain, so it gets its own tests.
- `global.json` opts `dotnet test` into Microsoft.Testing.Platform. The command is `dotnet test --solution CounterShift.slnx`.
