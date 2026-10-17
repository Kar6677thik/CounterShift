using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CounterShift.Storage.Migration;

public enum ParityResult
{
    /// <summary>Both stores returned the same value for every bucket.</summary>
    Match,

    /// <summary>A bucket that can no longer change differs: a real bug (ADR 0011).</summary>
    Mismatch,

    /// <summary>Only still-open buckets differ: expected noise from writes landing between the two reads.</summary>
    Race,

    /// <summary>The shadow read failed, so no comparison could be made.</summary>
    Error,
}

/// <summary>
/// Every migration signal (spec §7.5) as System.Diagnostics.Metrics instruments. M7 exports them to
/// Prometheus. Names follow OpenTelemetry style ("cs.storage.operation.duration", unit s); the Prometheus
/// exporter turns them into cs_storage_operation_duration_seconds and similar.
/// </summary>
public sealed class StorageMetrics
{
    public const string MeterName = "CounterShift.Storage";

    private readonly Histogram<double> _duration;
    private readonly Counter<long> _parity;
    private readonly Counter<long> _secondaryWriteFailures;
    private readonly Counter<long> _shadowDropped;
    private readonly Counter<long> _modeReloadFailures;

    public StorageMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _duration = meter.CreateHistogram<double>(
            "cs.storage.operation.duration", unit: "s",
            description: "Duration of one storage call, by backend, operation, role and outcome.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2.5] });
        _parity = meter.CreateCounter<long>("cs.parity.comparisons", description: "Shadow comparisons by result.");
        _secondaryWriteFailures = meter.CreateCounter<long>("cs.storage.secondary_write.failures", description: "Dual-write secondary failures (tolerated).");
        _shadowDropped = meter.CreateCounter<long>("cs.parity.shadow.dropped", description: "Shadow comparisons dropped because the queue was full.");
        _modeReloadFailures = meter.CreateCounter<long>("cs.storage.mode.reload_failures", description: "Rejected storage mode changes.");
    }

    public void RecordOperation(string backend, string operation, string role, TimeSpan elapsed, bool ok) =>
        _duration.Record(elapsed.TotalSeconds, new TagList
        {
            { "backend", backend }, { "op", operation }, { "role", role }, { "outcome", ok ? "ok" : "error" },
        });

    public void ParityCompared(ParityResult result) =>
        _parity.Add(1, new KeyValuePair<string, object?>("result", result.ToString().ToLowerInvariant()));

    public void SecondaryWriteFailed(string backend) =>
        _secondaryWriteFailures.Add(1, new KeyValuePair<string, object?>("backend", backend));

    public void ShadowDropped() => _shadowDropped.Add(1);

    public void ModeReloadFailed() => _modeReloadFailures.Add(1);
}
