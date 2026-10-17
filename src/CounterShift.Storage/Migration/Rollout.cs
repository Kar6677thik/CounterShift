using System.IO.Hashing;
using System.Text;
using CounterShift.Domain.Counters;

namespace CounterShift.Storage.Migration;

/// <summary>
/// Decides which counters are "in" a percentage rollout, deterministically (ADR 0010).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Keyed on the counter key</b>, not the request: a given card always reads from the same store at a
/// given percent, so its decisions don't flip between stores from one request to the next.</item>
/// <item><b>Stable across processes and machines:</b> XxHash64 of the UTF-8 bytes. <c>string.GetHashCode()</c>
/// is randomised per process in .NET, so two pods would disagree about the same key.</item>
/// <item><b>Monotonic:</b> a key is in the rollout when its hash bucket (0–9999) is below percent × 100.
/// Raising the percent only adds keys and never removes any, so ramping 5 → 30 → 70 never flips a key back.</item>
/// </list>
/// </remarks>
public static class Rollout
{
    private const int Buckets = 10_000; // 0.01 % resolution

    public static int BucketOf(CounterKey key) =>
        (int)(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key.Value)) % Buckets);

    public static bool Includes(CounterKey key, double percent) =>
        percent > 0 && (percent >= 100 || BucketOf(key) < (int)Math.Round(percent * (Buckets / 100.0)));
}
