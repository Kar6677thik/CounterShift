using CounterShift.Domain.Counters;
using CounterShift.Storage.Migration;

namespace CounterShift.Storage.Tests;

public sealed class StorageModeTests
{
    private static readonly IReadOnlySet<string> Registered = new HashSet<string> { "postgres", "redis" };

    public static TheoryData<string, StorageMode> ValidModes => new()
    {
        { "phase 0", new(new(WriteKind.Single, "postgres"), new(ReadKind.Single, "postgres")) },
        { "phase 1", new(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Single, "postgres")) },
        { "phase 2", new(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Shadow, "postgres", "redis", 20)) },
        { "phase 3", new(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Split, "postgres", "redis", 70)) },
        { "phase 4", new(new(WriteKind.Dual, "redis", "postgres"), new(ReadKind.Single, "redis")) },
        { "phase 5", new(new(WriteKind.Single, "redis"), new(ReadKind.Single, "redis")) },
    };

    [Theory]
    [MemberData(nameof(ValidModes))]
    public void Every_phase_of_the_migration_plan_is_valid(string phase, StorageMode mode)
    {
        Assert.True(mode.Validate(Registered).Count == 0, $"{phase}: {string.Join("; ", mode.Validate(Registered))}");
    }

    public static TheoryData<string, StorageMode, string> InvalidModes => new()
    {
        { "naive switch", new(new(WriteKind.Single, "postgres"), new(ReadKind.Single, "redis")), "not written to" },
        { "shadow without dual write", new(new(WriteKind.Single, "postgres"), new(ReadKind.Shadow, "postgres", "redis", 5)), "not written to" },
        { "unknown backend", new(new(WriteKind.Single, "cassandra"), new(ReadKind.Single, "cassandra")), "not registered" },
        { "dual to itself", new(new(WriteKind.Dual, "postgres", "postgres"), new(ReadKind.Single, "postgres")), "differs" },
        { "split without secondary", new(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Split, "postgres")), "differs" },
        { "percent > 100", new(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Split, "postgres", "redis", 150)), "percent" },
    };

    [Theory]
    [MemberData(nameof(InvalidModes))]
    public void Unsafe_modes_are_rejected_with_a_reason(string description, StorageMode mode, string expectedReason)
    {
        var errors = mode.Validate(Registered);

        Assert.Contains(errors, e => e.Contains(expectedReason, StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(description));
    }

    [Fact]
    public void A_dual_write_secondary_and_a_shadow_are_not_required_for_readiness()
    {
        var shadow = new StorageMode(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Shadow, "postgres", "redis", 50));
        var split = new StorageMode(new(WriteKind.Dual, "postgres", "redis"), new(ReadKind.Split, "postgres", "redis", 50));

        Assert.Equal(["postgres"], shadow.RequiredBackends);
        Assert.Equal(["postgres", "redis"], split.RequiredBackends.Order());
    }
}

public sealed class RolloutTests
{
    private static readonly CounterKey[] Keys =
        [.. Enumerable.Range(0, 20_000).Select(i => CounterKey.For("payment_failed_by_card", $"card_{i}"))];

    [Fact]
    public void Bucket_assignment_is_pinned_so_a_hash_change_cannot_slip_in_unnoticed()
    {
        // If this changes, every pod would route keys differently from the version before it, and a rolling
        // deploy during a split would serve the same card from both stores. Change it only on purpose.
        Assert.Equal(Rollout.BucketOf(CounterKey.For("payment_failed_by_card", "c_42")), Rollout.BucketOf(CounterKey.For("payment_failed_by_card", "c_42")));
        Assert.Equal(PinnedBucket, Rollout.BucketOf(CounterKey.For("payment_failed_by_card", "c_42")));
    }

    // XxHash64("payment_failed_by_card:c_42") % 10000, recorded when the rollout code was written.
    private const int PinnedBucket = 5_136;

    [Theory]
    [InlineData(5, 30)]
    [InlineData(30, 70)]
    [InlineData(70, 100)]
    public void Raising_the_percent_only_ever_adds_keys(double lower, double higher)
    {
        var atLower = Keys.Where(k => Rollout.Includes(k, lower)).ToHashSet();
        var atHigher = Keys.Where(k => Rollout.Includes(k, higher)).ToHashSet();

        Assert.True(atLower.IsSubsetOf(atHigher));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(70)]
    [InlineData(100)]
    public void The_share_of_keys_included_matches_the_percent(double percent)
    {
        var share = 100.0 * Keys.Count(k => Rollout.Includes(k, percent)) / Keys.Length;

        Assert.InRange(share, percent - 1.5, percent + 1.5);
    }
}
