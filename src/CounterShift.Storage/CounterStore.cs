using CounterShift.Domain.Counters;

namespace CounterShift.Storage;

/// <summary>
/// The Facade (spec §6, §7): the only storage entry point the API uses. Today it forwards to a single
/// backend. M6 teaches it read and write modes (dual-write, shadow, split) without the API changing
/// at all. That's the point of introducing the seam before we need it ("branch by abstraction").
/// </summary>
public sealed class CounterStore(ICounterBackend backend)
{
    public Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct) =>
        backend.IncrementAsync(increments, ct);

    public Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct) =>
        backend.ReadAsync(buckets, ct);

    /// <summary>Throws <see cref="StorageUnavailableException"/> if a backend needed right now is unreachable.</summary>
    public Task CheckReadyAsync(CancellationToken ct) => backend.PingAsync(ct);
}
