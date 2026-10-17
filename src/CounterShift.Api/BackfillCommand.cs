using System.Globalization;
using CounterShift.Storage;
using CounterShift.Storage.Migration;

namespace CounterShift.Api;

/// <summary>
/// <c>dotnet CounterShift.Api.dll backfill --from postgres --to redis --dual-write-since 2026-10-17T10:00:00Z [--batch 1000] [--force]</c>
/// <para>
/// Runs from the same binary and image as the API, so it uses exactly the same configuration, backends and
/// code version. In Kubernetes it runs as a Job (M9). See ADR 0012 for the algorithm and runbook.
/// </para>
/// </summary>
internal static class BackfillCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var from = Arg("--from");
        var to = Arg("--to");
        var sinceText = Arg("--dual-write-since");
        var batch = int.Parse(Arg("--batch") ?? "1000", CultureInfo.InvariantCulture);
        if (from is null || to is null || sinceText is null
            || !DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dualWriteSince))
        {
            await Console.Error.WriteLineAsync("usage: backfill --from <backend> --to <backend> --dual-write-since <ISO-8601 UTC> [--batch N] [--force]");
            return 64; // EX_USAGE
        }

        var backends = services.GetServices<ICounterBackend>().ToDictionary(b => b.Name, StringComparer.Ordinal);
        if (backends.GetValueOrDefault(from) is not IBucketScanner source)
        {
            await Console.Error.WriteLineAsync($"'{from}' is not a registered backend that can be scanned.");
            return 64;
        }
        if (backends.GetValueOrDefault(to) is not IBucketWriter target)
        {
            await Console.Error.WriteLineAsync($"'{to}' is not a registered backend that can be written by backfill.");
            return 64;
        }

        // Safety check: copying history into a store that isn't receiving new writes would leave it stale the
        // moment the backfill finishes. Refuse unless the current mode dual-writes into the target.
        var mode = services.GetRequiredService<StorageModeProvider>().Current.Mode;
        if (!mode.WrittenBackends.Contains(to) && !args.Contains("--force"))
        {
            await Console.Error.WriteLineAsync($"Refusing: the current mode ({mode}) does not write to '{to}'. Enable Dual writes first, or pass --force.");
            return 65;
        }

        var report = await services.GetRequiredService<BackfillRunner>().RunAsync(source, target, dualWriteSince, batch, CancellationToken.None);
        // Operator output must not depend on the machine's locale: en-IN groups digits as "2,10,966".
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine($"Scanned:              {report.Scanned:N0}");
        Console.WriteLine($"Copied:               {report.Copied:N0}");
        Console.WriteLine($"Skipped (after T0):   {report.SkippedAfterT0:N0}   complete via dual writes");
        Console.WriteLine($"Skipped (expired):    {report.SkippedExpired:N0}");
        Console.WriteLine($"Not final yet:        {report.NotYetFinal:N0}");
        Console.WriteLine($"Elapsed:              {report.Elapsed}");
        if (report.RerunAfter is { } rerun)
        {
            Console.WriteLine($"\nSome pre-T0 buckets can still receive events. Run again after {rerun:O} (re-runs are safe: SET is idempotent).");
        }
        return 0;
    }
}
