using CounterShift.Domain.Counters;
using CounterShift.Domain.Time;

namespace CounterShift.Storage.Tests;

/// <summary>
/// The storage contract, written once and run against every backend (Liskov Substitution Principle,
/// made executable). A new backend becomes eligible for the facade by subclassing this and passing.
/// If a test here needs a backend-specific exception, the contract is wrong, not the test.
/// </summary>
public abstract class CounterBackendContract
{
    /// <summary>
    /// Real "now", aligned to the hour. Real time rather than a fixed date, because Redis expires fields by its
    /// own clock: a test bucket older than its tier's retention would (correctly) vanish straight away.
    /// </summary>
    protected static readonly DateTimeOffset Anchor = Granularity.OneHour.AlignDown(DateTimeOffset.UtcNow);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A backend connected to a working store.</summary>
    protected abstract ICounterBackend Backend { get; }

    /// <summary>A backend pointed at a store that isn't there.</summary>
    protected abstract ICounterBackend CreateUnreachableBackend();

    /// <summary>Unique per call, so tests sharing one database never see each other's data.</summary>
    protected static BucketRef Bucket(string? key = null, Granularity? tier = null, int minuteOffset = 0)
    {
        var granularity = tier ?? Granularity.OneMinute;
        var start = granularity.AlignDown(Anchor.AddMinutes(minuteOffset));
        return new BucketRef(CounterKey.For("contract", key ?? Guid.NewGuid().ToString("N")), new BucketSpan(granularity, start));
    }

    protected static BucketIncrement Inc(BucketRef bucket, long delta = 1) => new(bucket, delta);

    [Fact]
    public async Task Increment_then_read_round_trips_across_all_tiers()
    {
        var key = Guid.NewGuid().ToString("N");
        var buckets = Granularity.All.Select(g => Bucket(key, g)).ToList();

        await Backend.IncrementAsync([.. buckets.Select(b => Inc(b, 3))], Ct);
        await Backend.IncrementAsync([.. buckets.Select(b => Inc(b, 4))], Ct);
        var read = await Backend.ReadAsync(buckets, Ct);

        Assert.Equal(buckets.Count, read.Count);
        Assert.All(buckets, b => Assert.Equal(7, read[b]));
    }

    [Fact]
    public async Task Buckets_that_were_never_written_are_absent()
    {
        var written = Bucket();
        var neverWritten = Bucket();
        await Backend.IncrementAsync([Inc(written)], Ct);

        var read = await Backend.ReadAsync([written, neverWritten], Ct);

        Assert.True(read.ContainsKey(written));
        Assert.False(read.ContainsKey(neverWritten));
    }

    [Fact]
    public async Task Duplicate_buckets_in_one_call_are_summed()
    {
        var bucket = Bucket();

        await Backend.IncrementAsync([Inc(bucket, 2), Inc(bucket, 5), Inc(bucket)], Ct);

        Assert.Equal(8, (await Backend.ReadAsync([bucket], Ct))[bucket]);
    }

    [Fact]
    public async Task Concurrent_increments_lose_nothing()
    {
        // Two hot buckets shared by every writer: the B1 scenario, at contract level.
        var hotA = Bucket();
        var hotB = Bucket(tier: Granularity.OneHour);
        const int writers = 32, perWriter = 25;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                // Alternate the order, so a backend without consistent lock ordering would deadlock (B5).
                IReadOnlyList<BucketIncrement> batch = w % 2 == 0 ? [Inc(hotA), Inc(hotB)] : [Inc(hotB), Inc(hotA)];
                await Backend.IncrementAsync(batch, Ct);
            }
        }, Ct)));

        var read = await Backend.ReadAsync([hotA, hotB], Ct);
        Assert.Equal(writers * perWriter, read[hotA]);
        Assert.Equal(writers * perWriter, read[hotB]);
    }

    [Fact]
    public async Task Many_buckets_can_be_read_in_one_call()
    {
        var key = Guid.NewGuid().ToString("N");
        // 500 quarter-hours ≈ 5.2 days back: inside the 15m tier's 8-day retention.
        var buckets = Enumerable.Range(0, 500).Select(i => Bucket(key, Granularity.FifteenMinutes, -15 * i)).ToList();
        await Backend.IncrementAsync([.. buckets.Select((b, i) => Inc(b, i + 1))], Ct);

        var read = await Backend.ReadAsync(buckets, Ct);

        Assert.Equal(500, read.Count);
        Assert.Equal(Enumerable.Range(1, 500).Sum(), read.Values.Sum());
    }

    [Fact]
    public async Task Asking_for_the_same_bucket_twice_is_harmless()
    {
        var bucket = Bucket();
        await Backend.IncrementAsync([Inc(bucket)], Ct);

        var read = await Backend.ReadAsync([bucket, bucket], Ct);

        Assert.Equal(1, Assert.Single(read).Value);
    }

    [Fact]
    public async Task Empty_calls_are_no_ops()
    {
        await Backend.IncrementAsync([], Ct);

        Assert.Empty(await Backend.ReadAsync([], Ct));
    }

    [Fact]
    public async Task Ping_and_stats_work_on_a_healthy_store()
    {
        await Backend.IncrementAsync([Inc(Bucket())], Ct);

        await Backend.PingAsync(Ct);
        var stats = await Backend.GetStatsAsync(Ct);

        Assert.Equal(Backend.Name, stats.Backend);
        Assert.True(stats.Records > 0);
    }

    [Fact]
    public async Task An_unreachable_store_raises_StorageUnavailableException_not_a_driver_exception()
    {
        var backend = CreateUnreachableBackend();

        var ex = await Assert.ThrowsAsync<StorageUnavailableException>(() => backend.ReadAsync([Bucket()], Ct));

        Assert.Equal(backend.Name, ex.Backend);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => backend.IncrementAsync([Inc(Bucket())], Ct));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => backend.PingAsync(Ct));
    }
}
