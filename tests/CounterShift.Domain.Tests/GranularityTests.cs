using CounterShift.Domain.Time;

namespace CounterShift.Domain.Tests;

public sealed class GranularityTests
{
    [Theory]
    [InlineData("1m", "2026-10-17T12:07:30Z", "2026-10-17T12:07:00Z")]
    [InlineData("15m", "2026-10-17T12:07:30Z", "2026-10-17T12:00:00Z")]
    [InlineData("15m", "2026-10-17T12:15:00Z", "2026-10-17T12:15:00Z")] // already aligned: unchanged
    [InlineData("1h", "2026-10-17T12:59:59.999Z", "2026-10-17T12:00:00Z")]
    [InlineData("1d", "2026-10-17T23:59:59Z", "2026-10-17T00:00:00Z")]
    public void AlignDown_rounds_to_the_start_of_the_enclosing_bucket(string tier, string instant, string expected)
    {
        var aligned = Granularity.FromName(tier).AlignDown(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), aligned);
        Assert.Equal(TimeSpan.Zero, aligned.Offset);
    }

    [Fact]
    public void Buckets_are_UTC_aligned_even_for_events_stamped_in_another_time_zone()
    {
        // 01:00 on 18 Oct in India (UTC+05:30) is 19:30 on 17 Oct in UTC.
        var ist = new DateTimeOffset(2026, 10, 18, 1, 0, 0, TimeSpan.FromHours(5.5));

        Assert.Equal(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), Granularity.OneDay.AlignDown(ist));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 19, 30, 0, TimeSpan.Zero), Granularity.FifteenMinutes.AlignDown(ist));
    }

    [Fact]
    public void Width_must_divide_a_day()
    {
        Assert.Throws<ArgumentException>(() => new Granularity("7m", TimeSpan.FromMinutes(7), TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Tiers_must_nest()
    {
        var tenMinutes = new Granularity("10m", TimeSpan.FromMinutes(10), TimeSpan.FromDays(8));

        // 15m is not a multiple of 10m, so a 15m boundary might not be a 10m boundary.
        Assert.Throws<ArgumentException>(() => new GranularitySet([tenMinutes, Granularity.FifteenMinutes]));
    }

    [Fact]
    public void Coarser_tiers_must_be_retained_at_least_as_long()
    {
        var shortLivedHour = new Granularity("1h", TimeSpan.FromHours(1), TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new GranularitySet([Granularity.FifteenMinutes, shortLivedHour]));
    }

    [Theory]
    [InlineData(10, "1m")]      // 10 minutes ago: 1m buckets still exist
    [InlineData(180, "1m")]     // exactly 3 hours: still within 1m retention
    [InlineData(181, "15m")]    // beyond 3 hours: fall back to 15m
    [InlineData(10080, "15m")]  // 7 days
    public void FinestRetainedAt_falls_back_to_coarser_tiers_with_age(int minutesAgo, string expected)
    {
        Assert.Equal(expected, GranularitySet.Default.FinestRetainedAt(TimeSpan.FromMinutes(minutesAgo))!.Name);
    }
}
