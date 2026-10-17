using CounterShift.Storage.Migration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace CounterShift.Api.Observability;

/// <summary>
/// OpenTelemetry metrics, pushed over OTLP to Prometheus's OTLP receiver (ADR 0014).
/// </summary>
public static class Telemetry
{
    /// <summary>
    /// Finer than ASP.NET Core's default buckets (which start at 5 ms). A p99 computed from a histogram is only
    /// as precise as the bucket edges around it, and this service answers in single-digit milliseconds.
    /// </summary>
    private static readonly double[] LatencyBuckets = [0.0005, 0.001, 0.002, 0.003, 0.005, 0.0075, 0.01, 0.015, 0.02, 0.03, 0.05, 0.075, 0.1, 0.25, 0.5, 1, 2.5];

    /// <summary>
    /// Export is enabled only when an OTLP endpoint is configured, via the standard OpenTelemetry environment
    /// variables (OTEL_EXPORTER_OTLP_METRICS_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL). Tests and casual runs
    /// therefore don't try to push anywhere, while the instruments still record and stay testable.
    /// </summary>
    public static string? ExportEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"] ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
    }

    public static IServiceCollection AddCounterShiftTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ApiMetrics>();
        if (ExportEndpoint(configuration) is not { } endpoint)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("countershift-api", serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()   // http.server.request.duration, by route and status
                .AddRuntimeInstrumentation()      // GC, thread pool, allocations
                .AddMeter(StorageMetrics.MeterName, ApiMetrics.MeterName)
                .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBuckets })
                .AddView("cs.storage.operation.duration", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBuckets })
                .AddOtlpExporter((exporter, reader) =>
                {
                    // Set explicitly. AddOtlpExporter() honours the generic OTEL_EXPORTER_OTLP_ENDPOINT but silently
                    // ignores the per-signal OTEL_EXPORTER_OTLP_METRICS_ENDPOINT, and falls back to localhost:4318.
                    // Found with the SDK's self-diagnostics log (learning note 07).
                    exporter.Endpoint = new Uri(endpoint);
                    exporter.Protocol = OtlpExportProtocol.HttpProtobuf; // Prometheus's OTLP receiver speaks HTTP/protobuf only
                    // Default is 60 s, far too coarse for watching a 30-second ramp step.
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000;
                }));
        return services;
    }
}
