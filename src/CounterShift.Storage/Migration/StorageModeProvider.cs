using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CounterShift.Storage.Migration;

/// <summary>The "CounterShift:Storage" configuration section, as bound from JSON. Mutable, unvalidated.</summary>
public sealed class StorageModeOptions
{
    public const string SectionName = "CounterShift:Storage";

    public WriteOptions Write { get; set; } = new();
    public ReadOptions Read { get; set; } = new();

    public sealed class WriteOptions
    {
        public WriteKind Mode { get; set; } = WriteKind.Single;
        public string Primary { get; set; } = "postgres";
        public string? Secondary { get; set; }
    }

    public sealed class ReadOptions
    {
        public ReadKind Mode { get; set; } = ReadKind.Single;
        public string Primary { get; set; } = "postgres";
        public string? Secondary { get; set; }
        public double Percent { get; set; }
    }

    public StorageMode ToMode() => new(
        new WriteMode(Write.Mode, Write.Primary, string.IsNullOrWhiteSpace(Write.Secondary) ? null : Write.Secondary),
        new ReadMode(Read.Mode, Read.Primary, string.IsNullOrWhiteSpace(Read.Secondary) ? null : Read.Secondary, Read.Percent));
}

public sealed record ActiveStorageMode(StorageMode Mode, DateTimeOffset LoadedAt, int Version);

/// <summary>
/// Holds the current, validated <see cref="StorageMode"/> and swaps it when configuration changes, with no
/// restart (spec §7.3). An invalid change is rejected: the last good mode stays in force, an error is logged,
/// and a metric is incremented. A typo in a ConfigMap must never take traffic down. At startup, though, an
/// invalid mode is fatal, because there is no "last good" to fall back on.
/// </summary>
public sealed partial class StorageModeProvider : IDisposable
{
    private readonly IReadOnlySet<string> _registered;
    private readonly TimeProvider _clock;
    private readonly StorageMetrics _metrics;
    private readonly ILogger<StorageModeProvider> _logger;
    private readonly IDisposable? _subscription;
    private ActiveStorageMode _active;

    public StorageModeProvider(
        IOptionsMonitor<StorageModeOptions> options, IEnumerable<ICounterBackend> backends,
        TimeProvider clock, StorageMetrics metrics, IMeterFactory meterFactory, ILogger<StorageModeProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registered = backends.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        _clock = clock;
        _metrics = metrics;
        _logger = logger;

        var initial = options.CurrentValue.ToMode();
        var errors = initial.Validate(_registered);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid storage mode {initial}: {string.Join("; ", errors)}");
        }
        _active = new ActiveStorageMode(initial, clock.GetUtcNow(), 1);
        LogModeLoaded(logger, initial.ToString(), 1);
        _subscription = options.OnChange(Apply);

        // Observable gauges: sampled at export time. On a dashboard they draw the migration as step lines
        // (mode version, shadow %, split %), so every latency or parity change lines up with the phase that caused it.
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(StorageMetrics.MeterName);
        meter.CreateObservableGauge("cs.storage.mode.version", () => Current.Version, description: "Version of the active storage mode; increments on every accepted change.");
        meter.CreateObservableGauge("cs.storage.read.shadow_percent", () => Current.Mode.Read.Kind == ReadKind.Shadow ? Current.Mode.Read.Percent : 0, description: "Share of counters shadow-compared.");
        meter.CreateObservableGauge("cs.storage.read.split_percent", () => Current.Mode.Read.Kind == ReadKind.Split ? Current.Mode.Read.Percent : 0, description: "Share of counters served by the read secondary.");
        meter.CreateObservableGauge("cs.storage.write.dual", () => Current.Mode.Write.Kind == WriteKind.Dual ? 1 : 0, description: "1 while dual writes are on.");
    }

    /// <summary>Read once per request and use that snapshot throughout, so a mode switch never splits a request.</summary>
    public ActiveStorageMode Current => Volatile.Read(ref _active);

    private void Apply(StorageModeOptions options)
    {
        StorageMode candidate;
        try
        {
            candidate = options.ToMode();
        }
        catch (ArgumentException ex)
        {
            Reject(ex.Message);
            return;
        }

        var current = Current;
        if (candidate == current.Mode)
        {
            return; // file watchers often fire several times for one save
        }
        var errors = candidate.Validate(_registered);
        if (errors.Count > 0)
        {
            Reject($"{candidate}: {string.Join("; ", errors)}");
            return;
        }
        var next = new ActiveStorageMode(candidate, _clock.GetUtcNow(), current.Version + 1);
        Volatile.Write(ref _active, next);
        LogModeChanged(_logger, current.Mode.ToString(), candidate.ToString(), next.Version);
    }

    private void Reject(string reason)
    {
        _metrics.ModeReloadFailed();
        LogModeRejected(_logger, reason, Current.Mode.ToString());
    }

    public void Dispose() => _subscription?.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage mode v{Version}: {Mode}")]
    private static partial void LogModeLoaded(ILogger logger, string mode, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage mode changed: {From} ⇒ {To} (v{Version})")]
    private static partial void LogModeChanged(ILogger logger, string from, string to, int version);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rejected storage mode change ({Reason}); keeping {Current}")]
    private static partial void LogModeRejected(ILogger logger, string reason, string current);
}
