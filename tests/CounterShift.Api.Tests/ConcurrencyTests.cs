using System.Net.Http.Json;
using static CounterShift.Api.Tests.ApiClientExtensions;

namespace CounterShift.Api.Tests;

/// <summary>
/// The regression test for experiment B1. The v0 read-modify-write passed every single-threaded test and
/// still lost about 98% of increments under load. This test fires many events at the SAME counter at the
/// same time through the real API, then checks that nothing was lost in any tier.
/// </summary>
public sealed class ConcurrencyTests(ApiFixture api)
{
    [Fact]
    public async Task Concurrent_events_on_one_counter_lose_no_increments_in_any_tier()
    {
        const int events = 200;
        using var client = api.CreateClient();
        var card = Unique("card");
        var users = Enumerable.Range(0, 10).Select(_ => Unique("user")).ToArray();

        // Ten users share one card. Every event touches the same card buckets (contention) plus one user's
        // buckets (so concurrent statements lock overlapping but different sets of rows, which is where
        // lock order matters).
        var responses = await Task.WhenAll(Enumerable.Range(0, events).Select(i =>
            client.PostEventAsync("payment.failed", new { cardId = card, userId = users[i % users.Length] })));

        Assert.All(responses, r => r.EnsureSuccessStatusCode());
        foreach (var window in new[] { "PT1M", "PT15M", "PT2H", "P1D" })
        {
            var counter = await (await client.GetAsync(
                new Uri($"/v1/counters/payment_failed_by_card/{card}?window={window}", UriKind.Relative), TestContext.Current.CancellationToken)).JsonAsync();
            Assert.Equal(events, counter.GetProperty("count").GetInt64());
        }
    }

    [Fact]
    public async Task Every_tier_holds_the_same_total()
    {
        using var client = api.CreateClient();
        var user = Unique("user");
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => client.PostEventAsync("ride.requested", new { userId = user })));

        await using var conn = await api.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT granularity, sum(count) FROM counter_buckets WHERE counter_key = @k GROUP BY granularity ORDER BY granularity", conn);
        cmd.Parameters.AddWithValue("k", $"ride_requested_by_user:{user}");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var totals = new List<(short, long)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            totals.Add((reader.GetInt16(0), reader.GetInt64(1)));
        }

        // One increment per tier per event: 1m, 15m, 1h and 1d must all agree.
        Assert.Equal([(1, 50L), (15, 50L), (60, 50L), (1440, 50L)], totals.Select(t => ((int)t.Item1, t.Item2)));
    }
}
