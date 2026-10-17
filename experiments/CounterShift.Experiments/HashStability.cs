using System.Diagnostics;
using System.IO.Hashing;
using System.Text;

namespace CounterShift.Experiments;

/// <summary>
/// Experiment B4: why the traffic split can't use string.GetHashCode(). Each "pod" here is a separate
/// process, as it would be in Kubernetes. .NET randomises string hashing per process (to defeat hash-flooding
/// attacks), so the same key lands in different rollout buckets on different pods. XxHash64 does not.
/// </summary>
internal static class HashStability
{
    private static readonly string[] Keys = ["payment_failed_by_card:c_42", "login_failed_by_user:u_7", "ride_requested_by_user:u_9"];

    public static async Task RunAsync(int pods)
    {
        Console.WriteLine($"## B4: is the rollout bucket the same on every pod? ({pods} processes)\n");
        var results = new List<string[]>();
        for (var pod = 0; pod < pods; pod++)
        {
            var self = Environment.ProcessPath!;
            var dll = typeof(HashStability).Assembly.Location;
            var start = new ProcessStartInfo(self, self.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || self.EndsWith("dotnet", StringComparison.Ordinal) ? $"\"{dll}\" hash-print" : "hash-print")
            {
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(start)!;
            results.Add((await process.StandardOutput.ReadToEndAsync()).Trim().Split('\n'));
            await process.WaitForExitAsync();
        }

        Console.WriteLine("| Key | " + string.Join(" | ", Enumerable.Range(1, pods).Select(p => $"pod {p}: GetHashCode % 10000")) + " | every pod: XxHash64 % 10000 |");
        Console.WriteLine("|---|" + string.Concat(Enumerable.Repeat("---:|", pods)) + "---:|");
        for (var k = 0; k < Keys.Length; k++)
        {
            var perPod = results.Select(r => r[k].Split(' ')).ToList();
            var xx = perPod.Select(p => p[1]).Distinct().ToList();
            Console.WriteLine($"| `{Keys[k]}` | " + string.Join(" | ", perPod.Select(p => p[0])) + $" | {string.Join(", ", xx)} |");
        }
        Console.WriteLine("\nAt a 30% split (buckets 0-2999), a key is \"in\" on a pod when its number there is below 3000.");
    }

    /// <summary>Child-process mode: print both hashes for each key.</summary>
    public static void Print()
    {
        foreach (var key in Keys)
        {
            var randomized = (key.GetHashCode() & 0x7FFFFFFF) % 10_000;
            var stable = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key)) % 10_000;
            Console.WriteLine($"{randomized} {stable}");
        }
    }
}
