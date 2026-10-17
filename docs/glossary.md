# Glossary

Terms are added the first time they appear in a milestone. The milestone that introduced each term is in brackets.

| Term | Definition |
|---|---|
| **ADR** [M0] | Architecture Decision Record. A short, immutable note recording one decision and why it was made. See `docs/adr/`. |
| **Bucket** [M1] | The stored count for one counter key in one aligned time slot, e.g. `[12:00, 12:15)`. |
| **Central Package Management** [M0] | NuGet feature (`Directory.Packages.props`) that keeps every package version in one file. |
| **CI** [M0] | Continuous Integration: every push is built and tested automatically (`.github/workflows/ci.yml`). |
| **Counter key** [M1] | Counter name + entity value, e.g. `payment_failed_by_card:c_42`. The unit of storage. |
| **Decomposition** [M1] | Turning a time window into the fewest aligned buckets that cover it (ADR 0002). |
| **Effective window** [M1] | The time range actually summed, after the start edge has been rounded down. Always returned to callers. |
| **Event time vs processing time** [M1] | When something happened vs when we received it. We bucket by event time (ADR 0003). |
| **Granularity / tier** [M1] | A bucket width: 1m, 15m, 1h, 1d. |
| **Liveness vs readiness** [M0] | Liveness (`/healthz`): "is the process alive? If not, restart it." Readiness (`/readyz`): "can it serve traffic right now? If not, stop sending it requests." Mixing them up causes restart loops when a database blips. |
| **Microsoft.Testing.Platform (MTP)** [M0] | The newer .NET test runner. xunit v3 test projects are executables that host it. |
| **Mutation testing** [M1] | Deliberately introducing bugs to check that the tests catch them. |
| **Property-based testing** [M1] | Stating rules that must hold for all inputs and letting a tool (FsCheck) search for counter-examples. |
| **Retention** [M1] | How long a tier's buckets are kept. A bucket is deleted only once it *ends* at or before `now − retention`. |
| **Shrinking** [M1] | When a property fails, FsCheck simplifies the failing input to a minimal counter-example. |
| **Testcontainers** [M0] | A library that starts real throwaway Docker containers (e.g. PostgreSQL) for a test run, then removes them. |
| **Write amplification** [M1] | One logical write causing many physical writes (1 event → counters × tiers increments). |
