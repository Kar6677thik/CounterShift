using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Time;
using CounterShift.Storage.Migration;
using CounterShift.Storage.Postgres;
using CounterShift.Storage.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using static CounterShift.Storage.Tests.MigrationHarness;

namespace CounterShift.Storage.Tests;

/// <summary>ADR 0012: copy only final, pre-T0, unexpired buckets, with SET so that re-runs are safe.</summary>
public sealed class BackfillTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_bucket_that_straddles_T0_is_repaired_once_it_is_final()
    {
        // The case spec §8 originally missed. Today's 1-day bucket holds events from before T0 (legacy only)
        // and after T0 (both stores). Dual writes alone can never make the new store's copy right.
        await using var h = new MigrationHarness();
        var t0 = Start;
        var day = Bucket("card", Granularity.OneDay, t0);
        await h.Store.IncrementAsync([new(day, 4)], Ct);          // 4 events before T0: old only
        h.Config.SetMode("Dual", "old", "new", "Single", "old");
        await h.Store.IncrementAsync([new(day, 2)], Ct);          // 2 events after T0: both
        Assert.Equal(6, (await h.Old.ReadAsync([day], Ct))[day]);
        Assert.Equal(2, (await h.New.ReadAsync([day], Ct))[day]); // the gap

        // Same day: the bucket is still open, so it can't be copied yet. The report says when to come back.
        h.Clock.Advance(TimeSpan.FromHours(1));
        var early = await h.Backfill.RunAsync(h.Old, h.New, t0, 100, Ct);
        Assert.Equal(1, early.NotYetFinal);
        Assert.Equal(day.Span.End + IngestionPolicy.Default.MaxLateness + BackfillRunner.InFlightMargin, early.RerunAfter);

        // After the day ends (+ lateness + margin), the value can no longer change: copy it.
        h.Clock.SetUtcNow(early.RerunAfter!.Value);
        var final = await h.Backfill.RunAsync(h.Old, h.New, t0, 100, Ct);

        Assert.Equal(1, final.Copied);
        Assert.Equal(6, (await h.New.ReadAsync([day], Ct))[day]);
    }

    [Fact]
    public async Task Buckets_after_T0_are_left_alone_because_dual_writes_already_made_them_complete()
    {
        await using var h = new MigrationHarness();
        var afterT0 = Bucket("card", Granularity.OneMinute, Start.AddMinutes(10));
        h.Old.Set(afterT0, 3);
        h.New.Set(afterT0, 3);
        h.Clock.Advance(TimeSpan.FromHours(1));

        var report = await h.Backfill.RunAsync(h.Old, h.New, Start, 100, Ct);

        Assert.Equal(1, report.SkippedAfterT0);
        Assert.Equal(0, report.Copied);
    }

    [Fact]
    public async Task Expired_buckets_are_not_resurrected()
    {
        await using var h = new MigrationHarness();
        var old = Bucket("card", Granularity.OneMinute, Start.AddHours(-5)); // 1m retention is 3 h
        h.Old.Set(old, 1);

        var report = await h.Backfill.RunAsync(h.Old, h.New, Start, 100, Ct);

        Assert.Equal(1, report.SkippedExpired);
        Assert.Empty(await h.New.ReadAsync([old], Ct));
    }

    [Fact]
    public async Task Running_twice_gives_the_same_result()
    {
        await using var h = new MigrationHarness();
        var buckets = Enumerable.Range(0, 250).Select(i => Bucket($"c{i}", Granularity.OneHour, Start.AddHours(-3))).ToList();
        foreach (var (b, i) in buckets.Select((b, i) => (b, i)))
        {
            h.Old.Set(b, i + 1);
        }

        await h.Backfill.RunAsync(h.Old, h.New, Start, 100, Ct);
        await h.Backfill.RunAsync(h.Old, h.New, Start, 100, Ct); // e.g. a Kubernetes Job retried after a crash

        var copied = await h.New.ReadAsync(buckets, Ct);
        Assert.All(buckets.Select((b, i) => (b, i)), x => Assert.Equal(x.i + 1, copied[x.b]));
    }

    [Fact]
    public async Task T0_in_the_future_is_refused()
    {
        await using var h = new MigrationHarness();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Backfill.RunAsync(h.Old, h.New, Start.AddMinutes(1), 100, Ct));
    }
}

/// <summary>The real thing: PostgreSQL (keyset scan) → Redis (Lua SET), on the shared containers.</summary>
public sealed class PostgresToRedisBackfillTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task History_in_postgres_arrives_in_redis_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new PostgresCounterBackend(postgres.DataSource);
        var target = new RedisCounterBackend(redis.Connection);
        // Real time, because Redis expires fields by its own clock.
        var t0 = DateTimeOffset.UtcNow;
        var key = CounterKey.For("backfill", Guid.NewGuid().ToString("N"));
        var history = Enumerable.Range(1, 40)
            .Select(i => new BucketIncrement(new BucketRef(key, new BucketSpan(Granularity.FifteenMinutes, Granularity.FifteenMinutes.AlignDown(t0.AddHours(-i)))), i))
            .ToList();
        await source.IncrementAsync(history, ct);

        var runner = new BackfillRunner(IngestionPolicy.Default, TimeProvider.System, NullLogger<BackfillRunner>.Instance);
        var report = await runner.RunAsync(source, target, t0, batchSize: 7, ct); // small batches: exercise keyset paging

        var inRedis = await target.ReadAsync([.. history.Select(h => h.Bucket)], ct);
        Assert.True(report.Copied >= 40, report.ToString());
        Assert.All(history, h => Assert.Equal(h.Delta, inRedis[h.Bucket]));
    }
}
