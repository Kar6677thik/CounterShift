using static CounterShift.Api.Tests.ApiClientExtensions;

namespace CounterShift.Api.Tests;

/// <summary>
/// The motivation for M6, as a test. With the seam from M4, switching storage is a one-line config
/// change, and that is exactly the danger. Nothing errors, every request succeeds, and fraud gets through.
/// </summary>
public sealed class NaiveSwitchTests(ApiFixture api)
{
    [Fact]
    public async Task Flipping_the_backend_without_migrating_data_silently_allows_fraud()
    {
        var card = Unique("card");
        var user = Unique("user");

        // Day 1 on Postgres: a card racks up six failed payments, so the next payment is blocked.
        await using (var onPostgres = api.WithSettings(("CounterShift:Storage:Backend", "postgres")))
        {
            using var client = onPostgres.CreateClient();
            await client.PostEventsAsync(6, "payment.failed", new { cardId = card, userId = user });
            Assert.Equal("block", (await client.DecideAsync("payment", new { cardId = card, userId = user })).GetProperty("decision").GetString());
        }

        // "Just switch to Redis": a config-only change, no errors, 200 OK everywhere...
        await using var onRedis = api.WithSettings(("CounterShift:Storage:Backend", "redis"));
        using var switched = onRedis.CreateClient();
        var decision = await switched.DecideAsync("payment", new { cardId = card, userId = user });

        // ...and the attacker's history is gone. Redis has never seen this card.
        Assert.Equal("allow", decision.GetProperty("decision").GetString());
    }
}
