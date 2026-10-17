# Glossary

Terms are added the first time they appear in a milestone. The milestone that introduced each term is in brackets.

| Term | Definition |
|---|---|
| **ADR** [M0] | Architecture Decision Record. A short, immutable note recording one decision and why it was made. See `docs/adr/`. |
| **Central Package Management** [M0] | NuGet feature (`Directory.Packages.props`) that keeps every package version in one file. |
| **CI** [M0] | Continuous Integration: every push is built and tested automatically (`.github/workflows/ci.yml`). |
| **Liveness vs readiness** [M0] | Liveness (`/healthz`): "is the process alive? If not, restart it." Readiness (`/readyz`): "can it serve traffic right now? If not, stop sending it requests." Mixing them up causes restart loops when a database blips. |
| **Microsoft.Testing.Platform (MTP)** [M0] | The newer .NET test runner. xunit v3 test projects are executables that host it. |
| **Testcontainers** [M0] | A library that starts real throwaway Docker containers (e.g. PostgreSQL) for a test run, then removes them. |
