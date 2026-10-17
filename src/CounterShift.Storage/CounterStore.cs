using CounterShift.Domain.Counters;
using Microsoft.Extensions.Options;

namespace CounterShift.Storage;

/// <summary>
/// The Facade (spec §6, §7): the only storage entry point the API uses. In M5 it picks one backend by
/// name from configuration. M6 teaches it read and write modes (dual-write, shadow, split) without the
/// API changing at all.
/// </summary>
public sealed class CounterStore
{
    private readonly ICounterBackend _backend;

    public CounterStore(IEnumerable<ICounterBackend> backends, IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(options);
        var available = backends.ToDictionary(b => b.Name, StringComparer.Ordinal);
        var wanted = options.Value.Backend;
        _backend = available.GetValueOrDefault(wanted)
            ?? throw new InvalidOperationException(
                $"Storage backend '{wanted}' is not registered. Available: {string.Join(", ", available.Keys)}. " +
                "Is its connection string configured?");
    }

    public Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct) =>
        _backend.IncrementAsync(increments, ct);

    public Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct) =>
        _backend.ReadAsync(buckets, ct);

    /// <summary>Throws <see cref="StorageUnavailableException"/> if a backend needed right now is unreachable.</summary>
    public Task CheckReadyAsync(CancellationToken ct) => _backend.PingAsync(ct);
}
