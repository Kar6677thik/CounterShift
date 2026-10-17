using CounterShift.Domain.Counters;
using CounterShift.Domain.Time;
using CounterShift.Storage.Migration;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.DependencyInjection;
using static CounterShift.Storage.Tests.MigrationHarness;

namespace CounterShift.Storage.Tests;

/// <summary>The facade's behaviour in every write and read mode, against two in-memory backends.</summary>
public sealed class CounterStoreMigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BucketIncrement Inc(BucketRef bucket, long delta = 1) => new(bucket, delta);

    // ── Writes ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Single_write_reaches_only_the_primary()
    {
        await using var h = new MigrationHarness();
        var b = Bucket("k", Granularity.OneMinute, Start);

        await h.Store.IncrementAsync([Inc(b)], Ct);

        Assert.Single(await h.Old.ReadAsync([b], Ct));
        Assert.Empty(await h.New.ReadAsync([b], Ct));
    }

    [Fact]
    public async Task Dual_write_reaches_both_stores()
    {
        await using var h = new MigrationHarness("Dual", "old", "new");
        var b = Bucket("k", Granularity.OneMinute, Start);

        await h.Store.IncrementAsync([Inc(b, 3)], Ct);

        Assert.Equal(3, (await h.Old.ReadAsync([b], Ct))[b]);
        Assert.Equal(3, (await h.New.ReadAsync([b], Ct))[b]);
    }

    [Fact]
    public async Task A_failing_secondary_never_fails_the_request_but_is_counted()
    {
        await using var h = new MigrationHarness("Dual", "old", "new");
        using var failures = new MetricCollector<long>(h.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            StorageMetrics.MeterName, "cs.storage.secondary_write.failures");
        h.New.Unavailable = true;
        var b = Bucket("k", Granularity.OneMinute, Start);

        await h.Store.IncrementAsync([Inc(b)], Ct); // no exception

        Assert.Equal(1, (await h.Old.ReadAsync([b], Ct))[b]);
        Assert.Equal(1, failures.GetMeasurementSnapshot().EvaluateAsCounter());
    }

    [Fact]
    public async Task A_failing_primary_fails_the_request()
    {
        await using var h = new MigrationHarness("Dual", "old", "new");
        h.Old.Unavailable = true;

        await Assert.ThrowsAsync<StorageUnavailableException>(() => h.Store.IncrementAsync([Inc(Bucket("k", Granularity.OneMinute, Start))], Ct));
    }

    // ── Split reads ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(100)]
    public async Task Split_serves_exactly_the_rolled_out_counters_from_the_secondary(double percent)
    {
        await using var h = new MigrationHarness("Dual", "old", "new", "Split", "old", "new", percent);
        var buckets = Enumerable.Range(0, 300).Select(i => Bucket($"card_{i}", Granularity.OneHour, Start)).ToList();
        foreach (var b in buckets)
        {
            h.Old.Set(b, 1); // the two stores disagree on purpose, so we can see who answered
            h.New.Set(b, 2);
        }

        var read = await h.Store.ReadAsync(buckets, Ct);

        Assert.All(buckets, b => Assert.Equal(Rollout.Includes(b.Key, percent) ? 2 : 1, read[b]));
    }

    // ── Shadow reads ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Shadow_always_serves_the_primary_and_flags_a_closed_bucket_mismatch()
    {
        await using var h = new MigrationHarness("Dual", "old", "new", "Shadow", "old", "new", 100);
        await h.StartShadowAsync();
        var closed = Bucket("k", Granularity.OneMinute, Start.AddMinutes(-30)); // ended 29 min ago: no more writes possible
        h.Old.Set(closed, 5);
        h.New.Set(closed, 4); // a lost write in the new store

        var read = await h.Store.ReadAsync([closed], Ct);
        var parity = await h.WaitForComparisonsAsync(1);

        Assert.Equal(5, read[closed]); // the caller never sees the shadow's value
        Assert.Equal(1, parity.Mismatch);
        Assert.Contains("old=5 new=4", parity.LastMismatch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_difference_in_a_still_open_bucket_is_a_race_not_a_mismatch()
    {
        await using var h = new MigrationHarness("Dual", "old", "new", "Shadow", "old", "new", 100);
        await h.StartShadowAsync();
        var open = Bucket("k", Granularity.OneMinute, Start); // the current minute: writes are still arriving
        h.Old.Set(open, 5);
        h.New.Set(open, 4);  // e.g. the dual write to "new" hasn't landed yet

        await h.Store.ReadAsync([open], Ct);
        var parity = await h.WaitForComparisonsAsync(1);

        Assert.Equal(0, parity.Mismatch);
        Assert.Equal(1, parity.Race);
    }

    [Fact]
    public async Task A_shadow_outage_never_affects_reads()
    {
        await using var h = new MigrationHarness("Dual", "old", "new", "Shadow", "old", "new", 100);
        await h.StartShadowAsync();
        h.New.Unavailable = true;
        var b = Bucket("k", Granularity.OneHour, Start);
        h.Old.Set(b, 7);

        var read = await h.Store.ReadAsync([b], Ct);
        var parity = await h.WaitForComparisonsAsync(1);

        Assert.Equal(7, read[b]);
        Assert.Equal(1, parity.Error);
    }

    [Fact]
    public async Task Shadow_work_is_dropped_not_queued_forever_when_the_queue_is_full()
    {
        await using var h = new MigrationHarness();
        var item = new ShadowRead("old", "new", [], new Dictionary<BucketRef, long>(), Start);

        // Workers are not started, so nothing drains the queue (capacity 10,000).
        for (var i = 0; i < 10_005; i++)
        {
            h.Comparer.Enqueue(item);
        }

        Assert.Equal(5, h.Comparer.Snapshot.Dropped);
    }

    [Fact]
    public async Task Only_sampled_counters_are_shadowed()
    {
        await using var h = new MigrationHarness("Dual", "old", "new", "Shadow", "old", "new", 20);
        await h.StartShadowAsync();
        var buckets = Enumerable.Range(0, 500).Select(i => Bucket($"card_{i}", Granularity.OneHour, Start)).ToList();

        foreach (var b in buckets)
        {
            await h.Store.ReadAsync([b], Ct);
        }
        var expected = buckets.Count(b => Rollout.Includes(b.Key, 20));
        var parity = await h.WaitForComparisonsAsync(expected);

        Assert.Equal(expected, parity.Match);
    }

    // ── Runtime mode changes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_config_change_switches_mode_without_a_restart()
    {
        await using var h = new MigrationHarness();
        var b = Bucket("k", Granularity.OneMinute, Start);

        h.Config.SetMode("Dual", "old", "new", "Single", "old");
        await h.Store.IncrementAsync([Inc(b)], Ct);

        Assert.Equal(2, h.Store.ActiveMode.Version);
        Assert.Equal(1, (await h.New.ReadAsync([b], Ct))[b]);
    }

    [Fact]
    public async Task An_unsafe_config_change_is_rejected_and_the_last_good_mode_stays()
    {
        await using var h = new MigrationHarness();
        using var failures = new MetricCollector<long>(h.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            StorageMetrics.MeterName, "cs.storage.mode.reload_failures");

        h.Config.SetMode("Single", "old", null, "Single", "new"); // the naive switch: read a store nobody writes to

        Assert.Equal("write=Single(old) read=Single(old)", h.Store.ActiveMode.Mode.ToString());
        Assert.Equal(1, h.Store.ActiveMode.Version);
        Assert.Equal(1, failures.GetMeasurementSnapshot().EvaluateAsCounter());
    }

    [Fact]
    public async Task Readiness_ignores_a_down_dual_write_secondary_but_not_a_split_secondary()
    {
        await using var h = new MigrationHarness("Dual", "old", "new", "Shadow", "old", "new", 50);
        h.New.Unavailable = true;

        await h.Store.CheckReadyAsync(Ct); // fine: "new" only receives tolerated writes and shadows

        h.Config.SetMode("Dual", "old", "new", "Split", "old", "new", 50);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => h.Store.CheckReadyAsync(Ct));
    }
}
