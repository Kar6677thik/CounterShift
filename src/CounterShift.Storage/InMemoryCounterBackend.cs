using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CounterShift.Domain.Counters;
using CounterShift.Storage.Migration;

namespace CounterShift.Storage;

/// <summary>
/// The "Mock" backend from the spec: a thread-safe dictionary. It's used in unit tests, and as a
/// deliberately divergent second backend to prove that the parity checker catches real differences.
/// </summary>
public sealed class InMemoryCounterBackend(string name = "memory") : ICounterBackend, IBucketScanner, IBucketWriter
{
    private readonly ConcurrentDictionary<BucketRef, long> _counts = new();

    public string Name { get; } = name;

    /// <summary>Test hook: make every call fail as if the store were down.</summary>
    public bool Unavailable { get; set; }

    public Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct)
    {
        ThrowIfUnavailable();
        foreach (var increment in BucketIncrements.Merge(increments))
        {
            // AddOrUpdate retries its update delegate on contention, so this is atomic per bucket.
            _counts.AddOrUpdate(increment.Bucket, increment.Delta, (_, current) => current + increment.Delta);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct)
    {
        ThrowIfUnavailable();
        IReadOnlyDictionary<BucketRef, long> found = buckets
            .Distinct()
            .Where(_counts.ContainsKey)
            .ToDictionary(b => b, b => _counts[b]);
        return Task.FromResult(found);
    }

    public Task PingAsync(CancellationToken ct)
    {
        ThrowIfUnavailable();
        return Task.CompletedTask;
    }

    public Task<StorageStats> GetStatsAsync(CancellationToken ct) =>
        Task.FromResult(new StorageStats(Name, Bytes: 0, Records: _counts.Count));

    public async IAsyncEnumerable<IReadOnlyList<BucketValue>> ScanAsync(int batchSize, [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfUnavailable();
        foreach (var chunk in _counts.OrderBy(kv => kv.Key).Select(kv => new BucketValue(kv.Key, kv.Value)).Chunk(batchSize))
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    public Task SetAsync(IReadOnlyList<BucketValue> values, CancellationToken ct)
    {
        ThrowIfUnavailable();
        foreach (var value in values)
        {
            _counts[value.Bucket] = value.Count;
        }
        return Task.CompletedTask;
    }

    /// <summary>Test hook: overwrite a bucket, for example to simulate divergence between two stores.</summary>
    public void Set(BucketRef bucket, long count) => _counts[bucket] = count;

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new StorageUnavailableException(Name, new InvalidOperationException("simulated outage"));
        }
    }
}
