using System.Globalization;
using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Time;
using CounterShift.Storage.Migration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CounterShift.Storage.Tests;

/// <summary>
/// A configuration source whose values can be changed during a test. Calling <see cref="Set"/> fires the
/// same reload token a file watcher would, so the code under test goes through the real IOptionsMonitor
/// reload path that production uses when a ConfigMap changes.
/// </summary>
public sealed class MutableConfigurationSource : IConfigurationSource
{
    public MutableProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;

    public sealed class MutableProvider : ConfigurationProvider
    {
        public void Set(IEnumerable<(string Key, string? Value)> values)
        {
            foreach (var (key, value) in values)
            {
                Data[key] = value;
            }
            OnReload();
        }
    }

    /// <summary>Writes a complete storage mode, clearing anything left over from the previous one.</summary>
    public void SetMode(string writeMode, string writePrimary, string? writeSecondary, string readMode, string readPrimary, string? readSecondary = null, double percent = 0) =>
        Provider.Set(
        [
            ("CounterShift:Storage:Write:Mode", writeMode),
            ("CounterShift:Storage:Write:Primary", writePrimary),
            ("CounterShift:Storage:Write:Secondary", writeSecondary ?? ""),
            ("CounterShift:Storage:Read:Mode", readMode),
            ("CounterShift:Storage:Read:Primary", readPrimary),
            ("CounterShift:Storage:Read:Secondary", readSecondary ?? ""),
            ("CounterShift:Storage:Read:Percent", percent.ToString(CultureInfo.InvariantCulture)),
        ]);
}

/// <summary>The facade wired to two in-memory backends, "old" and "new", with a fake clock and mutable config.</summary>
public sealed class MigrationHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 17, 12, 7, 30, TimeSpan.Zero);

    private readonly ServiceProvider _services;

    public MigrationHarness(string writeMode = "Single", string writePrimary = "old", string? writeSecondary = null,
        string readMode = "Single", string readPrimary = "old", string? readSecondary = null, double percent = 0)
    {
        Config.SetMode(writeMode, writePrimary, writeSecondary, readMode, readPrimary, readSecondary, percent);
        var configuration = new ConfigurationBuilder().Add(Config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(IngestionPolicy.Default);
        services.AddSingleton<ICounterBackend>(Old);
        services.AddSingleton<ICounterBackend>(New);
        services.AddOptions<StorageModeOptions>().Bind(configuration.GetSection(StorageModeOptions.SectionName));
        services.AddCounterStore();
        _services = services.BuildServiceProvider();
        // Build the mode provider now, as the API does at startup. Built lazily, it would take whatever mode
        // the test had switched to as its *initial* mode, and the reload path would never run.
        _services.GetRequiredService<StorageModeProvider>();
    }

    public MutableConfigurationSource Config { get; } = new();
    public FakeTimeProvider Clock { get; } = new(Start);
    public InMemoryCounterBackend Old { get; } = new("old");
    public InMemoryCounterBackend New { get; } = new("new");
    public IServiceProvider Services => _services;
    public CounterStore Store => _services.GetRequiredService<CounterStore>();
    public ShadowComparer Comparer => _services.GetRequiredService<ShadowComparer>();
    public BackfillRunner Backfill => _services.GetRequiredService<BackfillRunner>();

    public static BucketRef Bucket(string key, Granularity tier, DateTimeOffset at) =>
        new(CounterKey.For("test", key), new BucketSpan(tier, tier.AlignDown(at)));

    /// <summary>Starts the background shadow workers (they don't run unless a host starts them).</summary>
    public Task StartShadowAsync() => Comparer.StartAsync(CancellationToken.None);

    /// <summary>Waits until the shadow workers have processed at least <paramref name="total"/> comparisons.</summary>
    public async Task<ParitySnapshot> WaitForComparisonsAsync(long total)
    {
        for (var i = 0; i < 200; i++)
        {
            var s = Comparer.Snapshot;
            if (s.Match + s.Mismatch + s.Race + s.Error >= total)
            {
                return s;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Only {Comparer.Snapshot} after waiting.");
    }

    public async ValueTask DisposeAsync()
    {
        await Comparer.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
    }
}
