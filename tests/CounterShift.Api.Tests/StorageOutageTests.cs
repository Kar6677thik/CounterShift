using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace CounterShift.Api.Tests;

/// <summary>
/// ADR 0004: when storage is down, the decision API must fail closed (503), never answer "allow".
/// This uses its own database container, because stopping the shared one would break every other test.
/// </summary>
public sealed class StorageOutageTests
{
    [Fact]
    public async Task Decisions_fail_closed_with_503_when_the_database_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Short timeouts so the test doesn't wait 15 s for the default connect timeout.
                ["ConnectionStrings:Postgres"] = postgres.GetConnectionString() + ";Timeout=2;Command Timeout=2",
            })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/readyz", UriKind.Relative), ct)).StatusCode);

        await postgres.StopAsync(ct);

        var decision = await client.PostAsJsonAsync("/v1/decisions", new { action = "ride", entities = new { userId = "u_1" } }, ct);
        var ready = await client.GetAsync(new Uri("/readyz", UriKind.Relative), ct);
        var alive = await client.GetAsync(new Uri("/healthz", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, decision.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode); // stop sending us traffic...
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);                 // ...but don't restart us: restarting won't fix the database
    }
}
