# B4: rollout hashing across pods (raw output)

- **Date:** 2026-10-17
- **Command:** `dotnet run -c Release --project experiments/CounterShift.Experiments -- hash-stability`
- Each "pod" is a separate OS process, which is how separate Kubernetes pods behave.

| Key | pod 1: GetHashCode % 10000 | pod 2: GetHashCode % 10000 | pod 3: GetHashCode % 10000 | every pod: XxHash64 % 10000 |
|---|---:|---:|---:|---:|
| `payment_failed_by_card:c_42` | 6601 | 6686 | 5401 | 5136 |
| `login_failed_by_user:u_7` | 4824 | 7513 | 8513 | 7736 |
| `ride_requested_by_user:u_9` | 6438 | 4709 | 5966 | 4847 |

At a 30% split (buckets 0–2999), a key is "in" on a pod when its number there is below 3000.

**Reading it:** `string.GetHashCode()` is randomised per process in .NET, a defence against hash-flooding attacks. With it, the same card could be served by Redis on one pod and by PostgreSQL on the next, so the same request could get different answers depending on which pod the load balancer picked. XxHash64 gives the same bucket everywhere, and `RolloutTests` pins `5136` so it can't change unnoticed.
