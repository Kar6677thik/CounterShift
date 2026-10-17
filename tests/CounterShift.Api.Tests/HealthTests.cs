using System.Net;

namespace CounterShift.Api.Tests;

public sealed class HealthTests(ApiFixture api)
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    public async Task Health_endpoints_return_200(string path)
    {
        using var client = api.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
