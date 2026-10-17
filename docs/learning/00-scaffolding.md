# 00: Scaffolding, build discipline, CI

**Batch A · Milestone M0** · Field Guide: §1, §2, §22, §23

## What we built

| Piece | File | Purpose |
|---|---|---|
| Solution | `CounterShift.slnx` | The new XML solution format (the .NET 10 default). It is human-readable and merges cleanly in Git. |
| SDK pin | `global.json` | Everyone, including CI, builds with the same SDK feature band. It also opts `dotnet test` into Microsoft.Testing.Platform. |
| Shared build rules | `Directory.Build.props` | Every project gets nullable reference types, warnings as errors and the recommended analyzers, without repeating them. |
| Package versions | `Directory.Packages.props` | Central Package Management: one version per package across the repo. |
| Test settings | `tests/Directory.Build.props` | Every test project gets xunit v3 automatically. |
| Projects | `src/CounterShift.Domain`, `src/CounterShift.Api`, `tests/*` | Domain has **zero** dependencies, and the compiler enforces that (see below). |
| Local Postgres | `deploy/compose/docker-compose.yml` | `docker compose … up -d` gives you a database on port 55432. |
| CI | `.github/workflows/ci.yml` | Restore → build (Release) → test, on every push and pull request. |
| Line endings | `.gitattributes`, `.editorconfig` | You develop on Windows and deploy to Linux. Normalising to LF avoids "every line changed" diffs and broken shell scripts. |

## Why this way

**Why a separate Domain project, even this early?** Project references form a directed graph that the compiler enforces. `CounterShift.Domain` references nothing. If someone later writes `using Npgsql;` inside it, the build fails. This is the *Dependency Inversion Principle* (Field Guide §2.5) made mechanical rather than a matter of discipline. It matters most in M4: the whole migration story depends on business logic not knowing which database exists.

**Why treat warnings as errors?** A nullable warning ignored today becomes a `NullReferenceException` in production. On a fresh project the cost is zero. On an old one you'd face hundreds of warnings at once. I turned off four analyzer rules that don't fit a service codebase, and each one has a comment explaining why. This is *Fail fast* (§2.18) applied to the build.

**Why pin the SDK?** "Works on my machine" usually means different tool versions. `rollForward: latestFeature` allows patch and feature updates within 10.0.x, so security fixes still flow in.

**Why CI before any real code?** A pipeline is cheapest to set up when nothing can break it. From M1 onward, every commit is proven by a clean machine, not just your laptop.

**Why does CI run `concurrency: cancel-in-progress`?** If you push twice in a minute, the first run is already obsolete, so cancelling it saves runner minutes and gives you faster feedback.

**Why `permissions: contents: read`?** By default the `GITHUB_TOKEN` in a workflow can write to the repo. CI only reads, so we grant only reading. This is *Least privilege* (§2.20), and it matters for a public repo.

## Why now

Everything else stands on this. Milestones M1–M3 add tests constantly. Without a one-command test run and CI, those tests are optional, and optional tests rot.

## Concepts introduced

- Project reference graph as an architectural boundary (§2.5, §6.16)
- Build reproducibility: SDK pinning and central package versions (§22)
- CI basics: triggers, permissions, concurrency groups, artifacts (§22)
- Liveness probes (`/healthz`, §24)
- `WebApplicationFactory`: run the real ASP.NET pipeline in-memory inside a test (§23)

## Commands

```bash
dotnet build CounterShift.slnx              # compile everything
dotnet test --solution CounterShift.slnx    # run every test project
docker compose -f deploy/compose/docker-compose.yml up -d   # local Postgres
dotnet run --project src/CounterShift.Api   # then: curl localhost:5xxx/healthz
```

## What broke / surprised us

- **`dotnet test` failed with "Testing with VSTest target is no longer supported".** The current xunit v3 builds on *Microsoft.Testing.Platform* (MTP), and the .NET 10 SDK requires projects to opt in through `global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`. Under MTP the old packages `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` and `coverlet.collector` are unnecessary, so we removed them. The command also changed to `dotnet test --solution X.slnx`.
- **TRX reports:** under MTP, xunit has its own flag, `--report-xunit-trx`. CI uploads the results as an artifact so you can inspect failures.

## Self-check

1. What stops `CounterShift.Domain` from accidentally talking to the database?
2. What is the difference between `/healthz` and `/readyz`, and why would combining them cause restart storms?
3. Why does the CI workflow declare `permissions: contents: read`?
4. If CI passes but your laptop fails the build, which two files would you inspect first?
