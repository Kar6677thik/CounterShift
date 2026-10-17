using System.Diagnostics.Metrics;
using CounterShift.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static CounterShift.Api.Tests.ApiClientExtensions;

namespace CounterShift.Api.Tests;

public sealed class MetricsTests(ApiFixture api)
{
    [Fact]
    public async Task Decisions_are_counted_by_outcome_and_triggered_rule()
    {
        var meters = api.Factory.Services.GetRequiredService<IMeterFactory>();
        using var decisions = new MetricCollector<long>(meters, ApiMetrics.MeterName, "cs.decisions");
        using var rules = new MetricCollector<long>(meters, ApiMetrics.MeterName, "cs.rules.triggered");
        using var client = api.CreateClient();
        var card = Unique("card");

        await client.PostEventsAsync(6, "payment.failed", new { cardId = card, userId = Unique("user") });
        await client.DecideAsync("payment", new { cardId = card, userId = Unique("user") });

        // Other tests share the host and run in parallel, so look for *our* measurement rather than exact totals.
        Assert.Contains(decisions.GetMeasurementSnapshot(), m => (string?)m.Tags["outcome"] == "block");
        Assert.Contains(rules.GetMeasurementSnapshot(), m => (string?)m.Tags["rule"] == "R1");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("http://127.0.0.1:59090/api/v1/otlp/v1/metrics", null, "http://127.0.0.1:59090/api/v1/otlp/v1/metrics")]
    [InlineData(null, "http://collector:4318", "http://collector:4318")]
    public void Export_is_on_only_when_an_endpoint_is_configured(string? metricsEndpoint, string? genericEndpoint, string? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"] = metricsEndpoint,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = genericEndpoint,
        }).Build();

        Assert.Equal(expected, Telemetry.ExportEndpoint(config));
    }
}
