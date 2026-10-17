using System.Globalization;
using CounterShift.Domain.Time;

namespace CounterShift.Domain.Tests;

/// <summary>Worked examples. These double as documentation of the algorithm; see also the property tests.</summary>
public sealed class WindowDecomposerTests
{
    private static DateTimeOffset At(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    private static string[] Describe(Decomposition d) => [.. d.Buckets.Select(b => b.ToString())];

    [Fact]
    public void Last_10_minutes_uses_minute_buckets_up_to_the_hour_then_the_current_hour_bucket()
    {
        var d = WindowDecomposer.DecomposeTrailing(TimeSpan.FromMinutes(10), At("2026-10-17T12:07:30Z"), GranularitySet.Default);

        // The start edge 11:57:30 rounds down to 11:57. 12:00 is an hour boundary, so the greedy rule takes
        // the whole 1h bucket [12:00, 13:00). That's exact, because nothing after now (12:07:30) is stored yet.
        Assert.Equal(["1m@2026-10-17T11:57Z", "1m@2026-10-17T11:58Z", "1m@2026-10-17T11:59Z", "1h@2026-10-17T12:00Z"], Describe(d));
        Assert.Equal(At("2026-10-17T11:57:00Z"), d.EffectiveFrom);
    }

    [Fact]
    public void Grabs_example_window_needs_only_five_buckets()
    {
        // The example from Grab's original Counter Service post.
        var d = WindowDecomposer.Decompose(At("2026-09-10T23:15:20Z"), At("2026-09-12T17:20:18Z"), GranularitySet.Default);

        // The window is about 42 h old at its start, beyond 1m retention (3 h), so the start rounds to 15m.
        Assert.Equal(
            ["15m@2026-09-10T23:15Z", "15m@2026-09-10T23:30Z", "15m@2026-09-10T23:45Z", "1d@2026-09-11T00:00Z", "1d@2026-09-12T00:00Z"],
            Describe(d));
        Assert.Equal(At("2026-09-10T23:15:00Z"), d.EffectiveFrom);
    }

    [Fact]
    public void Ninety_minutes_mixes_minute_quarter_and_hour_buckets()
    {
        var d = WindowDecomposer.DecomposeTrailing(TimeSpan.FromMinutes(90), At("2026-10-17T12:07:30Z"), GranularitySet.Default);

        // 10:37:30 → 10:37, then minutes to 10:45, a quarter to 11:00, then 11:00 (1h) and 12:00 (1h, holds now).
        Assert.Equal(
            ["1m@2026-10-17T10:37Z", "1m@2026-10-17T10:38Z", "1m@2026-10-17T10:39Z", "1m@2026-10-17T10:40Z",
             "1m@2026-10-17T10:41Z", "1m@2026-10-17T10:42Z", "1m@2026-10-17T10:43Z", "1m@2026-10-17T10:44Z",
             "15m@2026-10-17T10:45Z", "1h@2026-10-17T11:00Z", "1h@2026-10-17T12:00Z"],
            Describe(d));
    }

    [Fact]
    public void A_window_starting_exactly_at_midnight_is_one_day_bucket()
    {
        var d = WindowDecomposer.Decompose(At("2026-10-17T00:00:00Z"), At("2026-10-17T00:05:00Z"), GranularitySet.Default);

        Assert.Equal(["1d@2026-10-17T00:00Z"], Describe(d));
    }

    [Fact]
    public void Now_exactly_on_a_boundary_includes_the_bucket_that_starts_at_now()
    {
        // An event stamped exactly 12:00:00.000 sits in the bucket starting at 12:00, and it is "in the window".
        var d = WindowDecomposer.DecomposeTrailing(TimeSpan.FromMinutes(2), At("2026-10-17T12:00:00Z"), GranularitySet.Default);

        Assert.Equal(["1m@2026-10-17T11:58Z", "1m@2026-10-17T11:59Z", "1h@2026-10-17T12:00Z"], Describe(d));
    }

    [Fact]
    public void Grab_tiers_only_round_short_windows_outward_to_the_quarter_hour()
    {
        // ADR 0002, option A: with Grab's three tiers, "last 10 min" at 12:07 really counts from 11:45.
        var grabTiers = new GranularitySet([Granularity.FifteenMinutes, Granularity.OneHour, Granularity.OneDay]);

        var d = WindowDecomposer.DecomposeTrailing(TimeSpan.FromMinutes(10), At("2026-10-17T12:07:30Z"), grabTiers);

        Assert.Equal(At("2026-10-17T11:45:00Z"), d.EffectiveFrom);
        Assert.Equal(["15m@2026-10-17T11:45Z", "1h@2026-10-17T12:00Z"], Describe(d));
    }

    [Fact]
    public void Rejects_non_positive_windows_and_windows_beyond_retention()
    {
        var now = At("2026-10-17T12:00:00Z");

        Assert.Throws<ArgumentOutOfRangeException>(() => WindowDecomposer.DecomposeTrailing(TimeSpan.Zero, now, GranularitySet.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowDecomposer.DecomposeTrailing(TimeSpan.FromDays(9), now, GranularitySet.Default));
    }
}
