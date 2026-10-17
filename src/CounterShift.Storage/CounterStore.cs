using System.Diagnostics;
using CounterShift.Domain.Counters;
using CounterShift.Storage.Migration;
using Microsoft.Extensions.Logging;

namespace CounterShift.Storage;

/// <summary>
/// The Facade (spec §7): the only storage entry point the API uses. Every call reads the current
/// <see cref="StorageMode"/> once, then routes accordingly:
/// <list type="bullet">
/// <item><b>Writes:</b> Single(primary), or Dual(primary → secondary), where secondary failures are tolerated and counted (ADR 0009).</item>
/// <item><b>Reads:</b> Single, Shadow (serve the primary and verify the secondary in the background), or Split
/// (a deterministic share of counters is served by the secondary, ADR 0010).</item>
/// </list>
/// The API never knows which mode is active. That's what makes a config-only cutover and rollback possible.
/// </summary>
public sealed partial class CounterStore(
    IEnumerable<ICounterBackend> backends,
    StorageModeProvider modes,
    ShadowComparer shadow,
    StorageMetrics metrics,
    TimeProvider clock,
    ILogger<CounterStore> logger)
{
    /// <summary>
    /// Deliberately independent of the request's cancellation token. If a client disconnects after the
    /// primary write, the secondary must still get the write, or the stores silently diverge.
    /// </summary>
    private static readonly TimeSpan SecondaryWriteTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, ICounterBackend> _backends = backends.ToDictionary(b => b.Name, StringComparer.Ordinal);

    public ActiveStorageMode ActiveMode => modes.Current;

    public ParitySnapshot Parity => shadow.Snapshot;

    public async Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct)
    {
        var mode = modes.Current.Mode.Write;
        var primary = _backends[mode.Primary];
        await Timed(primary, "write", "primary", () => primary.IncrementAsync(increments, ct));

        if (mode.Kind != WriteKind.Dual)
        {
            return;
        }
        var secondary = _backends[mode.Secondary!];
        try
        {
            using var timeout = new CancellationTokenSource(SecondaryWriteTimeout);
            await Timed(secondary, "write", "secondary", () => secondary.IncrementAsync(increments, timeout.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The secondary isn't serving traffic yet: its failure must not fail the request. It is counted,
            // because every failure here is a future parity mismatch (ADR 0009).
            metrics.SecondaryWriteFailed(secondary.Name);
            LogSecondaryWriteFailed(logger, ex, secondary.Name, increments.Count);
        }
    }

    public async Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct)
    {
        var mode = modes.Current.Mode.Read;
        var primary = _backends[mode.Primary];

        switch (mode.Kind)
        {
            case ReadKind.Shadow:
            {
                var result = await Timed(primary, "read", "primary", () => primary.ReadAsync(buckets, ct));
                var sampled = buckets.Where(b => Rollout.Includes(b.Key, mode.Percent)).Distinct().ToList();
                if (sampled.Count > 0)
                {
                    var primaryValues = sampled.Where(result.ContainsKey).ToDictionary(b => b, b => result[b]);
                    shadow.Enqueue(new ShadowRead(primary.Name, mode.Secondary!, sampled, primaryValues, clock.GetUtcNow()));
                }
                return result;
            }

            case ReadKind.Split:
            {
                var secondary = _backends[mode.Secondary!];
                var toSecondary = buckets.Where(b => Rollout.Includes(b.Key, mode.Percent)).ToList();
                var toPrimary = buckets.Where(b => !Rollout.Includes(b.Key, mode.Percent)).ToList();
                var fromPrimary = toPrimary.Count > 0 ? Timed(primary, "read", "primary", () => primary.ReadAsync(toPrimary, ct)) : null;
                var fromSecondary = toSecondary.Count > 0 ? Timed(secondary, "read", "secondary", () => secondary.ReadAsync(toSecondary, ct)) : null;

                var merged = new Dictionary<BucketRef, long>();
                foreach (var part in new[] { fromPrimary, fromSecondary }.OfType<Task<IReadOnlyDictionary<BucketRef, long>>>())
                {
                    foreach (var (bucket, count) in await part)
                    {
                        merged[bucket] = count;
                    }
                }
                return merged;
            }

            default:
                return await Timed(primary, "read", "primary", () => primary.ReadAsync(buckets, ct));
        }
    }

    /// <summary>Pings only the backends whose failure would fail requests in the current mode.</summary>
    public Task CheckReadyAsync(CancellationToken ct) =>
        Task.WhenAll(modes.Current.Mode.RequiredBackends.Select(name => _backends[name].PingAsync(ct)));

    private async Task Timed(ICounterBackend backend, string op, string role, Func<Task> call) =>
        await Timed(backend, op, role, async () =>
        {
            await call();
            return true;
        });

    private async Task<T> Timed<T>(ICounterBackend backend, string op, string role, Func<Task<T>> call)
    {
        var started = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            var result = await call();
            ok = true;
            return result;
        }
        finally
        {
            metrics.RecordOperation(backend.Name, op, role, Stopwatch.GetElapsedTime(started), ok);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secondary write to {Backend} failed for {Count} increments; the stores now differ until repaired")]
    private static partial void LogSecondaryWriteFailed(ILogger logger, Exception exception, string backend, int count);
}
