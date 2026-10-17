using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using Microsoft.Extensions.Logging;

namespace CounterShift.Storage.Migration;

public readonly record struct BucketValue(BucketRef Bucket, long Count);

/// <summary>A backend that can list everything it stores. Implemented by the source of a backfill.</summary>
public interface IBucketScanner
{
    string Name { get; }

    /// <summary>Every stored bucket, in a stable order, in batches.</summary>
    IAsyncEnumerable<IReadOnlyList<BucketValue>> ScanAsync(int batchSize, CancellationToken ct);
}

/// <summary>A backend that can overwrite buckets with exact values. Implemented by the target of a backfill.</summary>
public interface IBucketWriter
{
    string Name { get; }

    /// <summary>SET, not increment: running the same copy twice gives the same result (idempotent).</summary>
    Task SetAsync(IReadOnlyList<BucketValue> values, CancellationToken ct);
}

public sealed record BackfillReport(
    long Scanned, long Copied, long SkippedAfterT0, long SkippedExpired, long NotYetFinal,
    DateTimeOffset? RerunAfter, TimeSpan Elapsed);

/// <summary>
/// Copies history from the legacy store into the new one (spec §8, corrected by ADR 0012).
/// </summary>
/// <remarks>
/// Dual writes start at <c>T0</c>. From then on both stores receive every new event. History, meaning
/// events that arrived <i>before</i> T0, exists only in the source. A bucket is copied when:
/// <list type="number">
/// <item>it may hold pre-T0 events: <c>start &lt; T0 + MaxClockSkew</c>. Buckets that start later are complete
/// through dual writes alone;</item>
/// <item>it is <b>final</b>: <c>end + MaxLateness + margin ≤ now</c>. No event can still arrive for it, so the
/// value we copy is the value it will keep forever;</item>
/// <item>it hasn't expired: <c>end + retention &gt; now</c>.</item>
/// </list>
/// Buckets that hold pre-T0 events but aren't final yet (for example, today's 1-day bucket) are counted in
/// <see cref="BackfillReport.NotYetFinal"/>, and the report says when to run again. SET makes re-runs safe.
/// </remarks>
public sealed partial class BackfillRunner(IngestionPolicy policy, TimeProvider clock, ILogger<BackfillRunner> logger)
{
    /// <summary>Allowance for a request accepted at the lateness limit that is still being written.</summary>
    public static readonly TimeSpan InFlightMargin = TimeSpan.FromMinutes(1);

    public async Task<BackfillReport> RunAsync(
        IBucketScanner source, IBucketWriter target, DateTimeOffset dualWriteSince, int batchSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var startedAt = clock.GetUtcNow();
        if (dualWriteSince > startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(dualWriteSince), dualWriteSince, "T0 is in the future: dual writes must already be on.");
        }
        var mayHoldPreT0 = dualWriteSince + policy.MaxClockSkew;
        long scanned = 0, copied = 0, afterT0 = 0, expired = 0, notFinal = 0;
        DateTimeOffset? rerunAfter = null;

        LogStarting(logger, source.Name, target.Name, dualWriteSince);
        await foreach (var batch in source.ScanAsync(batchSize, ct))
        {
            var now = clock.GetUtcNow();
            var toCopy = new List<BucketValue>(batch.Count);
            foreach (var value in batch)
            {
                scanned++;
                var span = value.Bucket.Span;
                var finalAt = span.End + policy.MaxLateness + InFlightMargin;
                if (span.Start >= mayHoldPreT0)
                {
                    afterT0++;
                }
                else if (span.End + span.Granularity.Retention <= now)
                {
                    expired++;
                }
                else if (finalAt > now)
                {
                    notFinal++;
                    rerunAfter = rerunAfter is null || finalAt > rerunAfter ? finalAt : rerunAfter;
                }
                else
                {
                    toCopy.Add(value);
                }
            }
            if (toCopy.Count > 0)
            {
                await target.SetAsync(toCopy, ct);
                copied += toCopy.Count;
            }
            LogProgress(logger, scanned, copied);
        }

        var report = new BackfillReport(scanned, copied, afterT0, expired, notFinal, rerunAfter, clock.GetUtcNow() - startedAt);
        LogFinished(logger, report.ToString());
        return report;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfill {Source} → {Target} for history before T0={T0:O}")]
    private static partial void LogStarting(ILogger logger, string source, string target, DateTimeOffset t0);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfill progress: scanned {Scanned}, copied {Copied}")]
    private static partial void LogProgress(ILogger logger, long scanned, long copied);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfill finished: {Report}")]
    private static partial void LogFinished(ILogger logger, string report);
}
