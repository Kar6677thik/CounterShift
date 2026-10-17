using CounterShift.Domain.Counters;

namespace CounterShift.Storage;

/// <summary>
/// One way of storing bucket counts (spec §6). Backends are deliberately "dumb": they store and fetch
/// numbers by <see cref="BucketRef"/>. Decomposition and summing stay in the domain, so when two
/// backends are compared during a migration, the only thing that can differ is the storage itself.
/// </summary>
/// <remarks>
/// Contract (enforced for every implementation by <c>CounterBackendContract</c> tests):
/// <list type="bullet">
/// <item><see cref="IncrementAsync"/> is <b>atomic per bucket</b> under any concurrency. No lost updates (ADR 0006).</item>
/// <item>Duplicate buckets in one increment call are summed, not rejected.</item>
/// <item><see cref="ReadAsync"/> returns only buckets that exist. A missing bucket means 0.</item>
/// <item>Infrastructure failures surface as <see cref="StorageUnavailableException"/>, never as driver
/// exceptions, so callers don't need to know which driver is underneath.</item>
/// </list>
/// </remarks>
public interface ICounterBackend
{
    /// <summary>Stable identifier used in configuration and metrics: "postgres", "redis", "memory".</summary>
    string Name { get; }

    Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct);

    Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct);

    /// <summary>Cheap reachability check, used by readiness probes.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Storage footprint, for the "bytes per million events" report.</summary>
    Task<StorageStats> GetStatsAsync(CancellationToken ct);
}

/// <param name="Bytes">Bytes used by counter data, including indexes and overhead, as the backend reports it.</param>
/// <param name="Records">Physical records: rows for PostgreSQL, hash fields for Redis.</param>
public sealed record StorageStats(string Backend, long Bytes, long Records);

/// <summary>Raised by any backend when its store can't be reached or doesn't answer in time.</summary>
public sealed class StorageUnavailableException(string backend, Exception inner)
    : Exception($"Counter storage '{backend}' is unavailable: {inner.Message}", inner)
{
    public string Backend { get; } = backend;
}

/// <summary>A one-off setup step a backend needs before serving traffic, such as schema migrations.</summary>
public interface IStorageInitializer
{
    Task InitializeAsync(CancellationToken ct);
}

public static class BucketIncrements
{
    /// <summary>Sums increments that target the same bucket, preserving first-seen order.</summary>
    public static IReadOnlyList<BucketIncrement> Merge(IEnumerable<BucketIncrement> increments) =>
        [.. increments.GroupBy(i => i.Bucket).Select(g => new BucketIncrement(g.Key, g.Sum(i => i.Delta)))];
}
