using System.Threading.Channels;
using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CounterShift.Storage.Migration;

/// <summary>One sampled read to verify: what the primary returned, and when.</summary>
public sealed record ShadowRead(
    string Primary, string Shadow, IReadOnlyList<BucketRef> Buckets,
    IReadOnlyDictionary<BucketRef, long> PrimaryValues, DateTimeOffset ReadAt);

/// <summary>Running totals, so a human can watch parity on /admin/storage-mode without a dashboard.</summary>
public sealed record ParitySnapshot(long Match, long Mismatch, long Race, long Error, long Dropped, string? LastMismatch)
{
    /// <summary>Spec §7.2: mismatches ÷ (matches + mismatches). Races and errors are excluded.</summary>
    public double MismatchRate => Match + Mismatch == 0 ? 0 : (double)Mismatch / (Match + Mismatch);
}

/// <summary>
/// Verifies shadow reads in the background (spec §7.2, ADR 0011). The request path only <i>enqueues</i>
/// work; this service reads the shadow store and compares <b>bucket by bucket</b>.
/// </summary>
/// <remarks>
/// The queue is bounded and drops new items when full. If the shadow store is slow or down, comparisons
/// are lost (and counted as dropped), but real traffic never waits for them. A shadow must never be able
/// to hurt the primary path.
/// </remarks>
public sealed partial class ShadowComparer : BackgroundService
{
    private const int QueueCapacity = 10_000;
    private const int Workers = 4;
    private static readonly TimeSpan ShadowReadTimeout = TimeSpan.FromSeconds(2);

    private readonly Channel<ShadowRead> _queue;
    private readonly Dictionary<string, ICounterBackend> _backends;
    private readonly IngestionPolicy _policy;
    private readonly StorageMetrics _metrics;
    private readonly ILogger<ShadowComparer> _logger;
    private long _match, _mismatch, _race, _error, _dropped;
    private string? _lastMismatch;

    public ShadowComparer(IEnumerable<ICounterBackend> backends, IngestionPolicy policy, StorageMetrics metrics, ILogger<ShadowComparer> logger)
    {
        _backends = backends.ToDictionary(b => b.Name, StringComparer.Ordinal);
        _policy = policy;
        _metrics = metrics;
        _logger = logger;
        _queue = Channel.CreateBounded<ShadowRead>(
            new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite },
            itemDropped: _ =>
            {
                Interlocked.Increment(ref _dropped);
                _metrics.ShadowDropped();
            });
    }

    public ParitySnapshot Snapshot => new(
        Interlocked.Read(ref _match), Interlocked.Read(ref _mismatch), Interlocked.Read(ref _race),
        Interlocked.Read(ref _error), Interlocked.Read(ref _dropped), Volatile.Read(ref _lastMismatch));

    /// <summary>Never blocks. If the queue is full, the item is dropped and counted.</summary>
    public void Enqueue(ShadowRead read) => _queue.Writer.TryWrite(read);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(async _ =>
        {
            await foreach (var read in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await CompareAsync(read, stoppingToken);
            }
        }));

    /// <summary>Reads the shadow store and classifies the comparison. Public so tests can run it synchronously.</summary>
    public async Task<ParityResult> CompareAsync(ShadowRead read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        IReadOnlyDictionary<BucketRef, long> shadowValues;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ShadowReadTimeout);
            shadowValues = await _backends[read.Shadow].ReadAsync(read.Buckets, timeout.Token);
        }
        catch (Exception ex) when (ex is StorageUnavailableException or OperationCanceledException)
        {
            return Record(ParityResult.Error);
        }

        // A bucket is "closed" once no event can still land in it. Events may arrive up to MaxLateness after
        // they happened (ADR 0003), so a bucket stays open until its end + MaxLateness.
        var closedBefore = read.ReadAt - _policy.MaxLateness;
        var differences = read.Buckets
            .Select(b => (Bucket: b, Primary: read.PrimaryValues.GetValueOrDefault(b), Shadow: shadowValues.GetValueOrDefault(b)))
            .Where(d => d.Primary != d.Shadow)
            .ToList();

        if (differences.Count == 0)
        {
            return Record(ParityResult.Match);
        }
        var closed = differences.FirstOrDefault(d => d.Bucket.Span.End <= closedBefore);
        if (closed.Bucket == default)
        {
            return Record(ParityResult.Race);
        }

        var description = $"{closed.Bucket}: {read.Primary}={closed.Primary} {read.Shadow}={closed.Shadow}";
        Volatile.Write(ref _lastMismatch, description);
        LogMismatch(_logger, description, differences.Count);
        return Record(ParityResult.Mismatch);
    }

    private ParityResult Record(ParityResult result)
    {
        _ = result switch
        {
            ParityResult.Match => Interlocked.Increment(ref _match),
            ParityResult.Mismatch => Interlocked.Increment(ref _mismatch),
            ParityResult.Race => Interlocked.Increment(ref _race),
            _ => Interlocked.Increment(ref _error),
        };
        _metrics.ParityCompared(result);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parity mismatch in a closed bucket: {Mismatch} ({Count} differing buckets in this read)")]
    private static partial void LogMismatch(ILogger logger, string mismatch, int count);
}
