using System.Net;
using System.Net.Http.Json;
using static CounterShift.Api.Tests.ApiClientExtensions;

namespace CounterShift.Api.Tests;

/// <summary>End-to-end behaviour through the real HTTP pipeline and a real PostgreSQL.</summary>
public sealed class FraudApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Six_failed_payments_on_one_card_block_the_next_payment()
    {
        using var client = api.CreateClient();
        var card = Unique("card");
        var user = Unique("user");

        await client.PostEventsAsync(6, "payment.failed", new { cardId = card, userId = user });
        var decision = await client.DecideAsync("payment", new { cardId = card, userId = user });

        Assert.Equal("block", decision.GetProperty("decision").GetString());
        var r1 = decision.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "R1");
        Assert.Equal(6, r1.GetProperty("count").GetInt64());
        Assert.True(r1.GetProperty("triggered").GetBoolean());
        // "Last 10 minutes" at 12:07:30 → effective window from 11:57:00 (ADR 0002).
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 11, 57, 0, TimeSpan.Zero),
            r1.GetProperty("effectiveWindow").GetProperty("from").GetDateTimeOffset());
    }

    [Fact]
    public async Task Five_failed_payments_are_allowed_because_the_rule_is_more_than_five()
    {
        using var client = api.CreateClient();
        var card = Unique("card");
        var user = Unique("user");

        await client.PostEventsAsync(5, "payment.failed", new { cardId = card, userId = user });
        var decision = await client.DecideAsync("payment", new { cardId = card, userId = user });

        Assert.Equal("allow", decision.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Failed_logins_challenge_and_events_from_earlier_minutes_still_count()
    {
        using var client = api.CreateClient();
        var user = Unique("user");

        // 4 failed logins spread over the last 4 minutes, all within the allowed lateness.
        for (var minutesAgo = 0; minutesAgo < 4; minutesAgo++)
        {
            (await client.PostEventAsync("login.failed", new { userId = user }, ApiFixture.Now.AddMinutes(-minutesAgo))).EnsureSuccessStatusCode();
        }
        var decision = await client.DecideAsync("login", new { userId = user });

        Assert.Equal("challenge", decision.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Counter_endpoint_shows_exactly_which_buckets_were_summed()
    {
        using var client = api.CreateClient();
        var user = Unique("user");
        await client.PostEventsAsync(3, "ride.requested", new { userId = user });

        var response = await client.GetAsync(new Uri($"/v1/counters/ride_requested_by_user/{user}?window=PT90M", UriKind.Relative), Ct);
        var body = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, body.GetProperty("count").GetInt64());
        var tiers = body.GetProperty("buckets").EnumerateArray().Select(b => b.GetProperty("granularity").GetString()).ToList();
        Assert.Equal(["1m", "1m", "1m", "1m", "1m", "1m", "1m", "1m", "15m", "1h", "1h"], tiers);
    }

    [Theory]
    [InlineData(-6, "occurredAt")] // 6 minutes late
    [InlineData(1, "occurredAt")]  // 1 minute in the future
    public async Task Events_outside_the_time_limits_are_422(int minutesOffset, string field)
    {
        using var client = api.CreateClient();

        var response = await client.PostEventAsync("payment.failed", new { cardId = Unique("card"), userId = Unique("user") }, ApiFixture.Now.AddMinutes(minutesOffset));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Malformed_events_are_400_with_every_problem_listed()
    {
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/events", new { type = "payment.failed" }, Ct);
        var errors = (await response.JsonAsync()).GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(errors.TryGetProperty("eventId", out _));
        Assert.True(errors.TryGetProperty("occurredAt", out _));
        Assert.True(errors.TryGetProperty("entities", out _));
    }

    [Fact]
    public async Task Invalid_json_is_400()
    {
        using var client = api.CreateClient();
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(new Uri("/v1/events", UriKind.Relative), content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Decision_without_a_required_entity_is_400()
    {
        using var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/decisions", new { action = "payment", entities = new { userId = "u_1" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.JsonAsync()).GetProperty("errors").TryGetProperty("entities.cardId", out _));
    }

    [Theory]
    [InlineData("/v1/counters/nope/u_1?window=PT1H")]
    [InlineData("/v1/counters/ride_requested_by_user/u_1?window=90")]
    [InlineData("/v1/counters/ride_requested_by_user/u_1?window=P8D")]
    public async Task Bad_counter_queries_are_400(string path)
    {
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
