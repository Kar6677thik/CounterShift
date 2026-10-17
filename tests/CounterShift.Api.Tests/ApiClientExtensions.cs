using System.Net.Http.Json;
using System.Text.Json;

namespace CounterShift.Api.Tests;

/// <summary>Small helpers so tests read as scenarios rather than HTTP plumbing.</summary>
internal static class ApiClientExtensions
{
    public static Task<HttpResponseMessage> PostEventAsync(
        this HttpClient client, string type, object entities, DateTimeOffset? occurredAt = null) =>
        client.PostAsJsonAsync("/v1/events", new
        {
            eventId = Guid.NewGuid(),
            type,
            occurredAt = occurredAt ?? ApiFixture.Now,
            entities,
        }, TestContext.Current.CancellationToken);

    public static async Task PostEventsAsync(this HttpClient client, int count, string type, object entities)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await client.PostEventAsync(type, entities);
            response.EnsureSuccessStatusCode();
        }
    }

    public static async Task<JsonElement> DecideAsync(this HttpClient client, string action, object entities)
    {
        var response = await client.PostAsJsonAsync("/v1/decisions", new { action, entities }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    public static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
