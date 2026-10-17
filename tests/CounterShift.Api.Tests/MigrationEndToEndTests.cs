using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CounterShift.Domain.Time;
using CounterShift.Storage;
using CounterShift.Storage.Migration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace CounterShift.Api.Tests;

/// <summary>
/// The whole migration plan from spec §7.4, through the real HTTP API, with a mid-migration rollback.
/// The invariant: every count the API returns is identical in every phase, whichever store answers.
/// </summary>
public sealed class MigrationEndToEndTests : IAsyncLifetime
{
    private const int Cards = 25;
    private static readonly string[] ParityResults = ["match", "mismatch", "race", "error"];

    // Own containers: the backfill scans the whole table, and on the shared database it would copy other tests'
    // rows into Redis, with results that depend on the time of day. Isolation beats cleverness here.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();
    private readonly ModeSource _mode = new();

    // Real "now" (not a fixed date), because Redis expires fields by its own clock.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly string[] _cards = [.. Enumerable.Range(0, Cards).Select(i => $"mig_{i}_{Guid.NewGuid():N}")];
    private readonly Dictionary<string, long> _expected = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _mode.Set("Single", "postgres", null, "Single", "postgres");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                    ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
                });
                config.Add(_mode);
            });
            host.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock));
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Full_migration_with_a_rollback_keeps_every_answer_identical()
    {
        // ── Phase 0: legacy only ─────────────────────────────────────────────────────────────────
        await IngestAsync(3);
        _clock.Advance(TimeSpan.FromMinutes(10));

        // ── Phase 1: dual writes. T0 = now. ──────────────────────────────────────────────────────
        _mode.Set("Dual", "postgres", "redis", "Single", "postgres");
        var t0 = _clock.GetUtcNow();
        await IngestAsync(2);
        await AssertCountsAsync("phase 1");

        // Jump past the end of T0's day (+ lateness + margin), so every pre-T0 bucket is final.
        _clock.SetUtcNow(Granularity.OneDay.AlignDown(t0).AddDays(1) + TimeSpan.FromMinutes(7));

        // ── Phase 2, too early: shadow BEFORE backfill. The parity gate must catch the missing history. ──
        _mode.Set("Dual", "postgres", "redis", "Shadow", "postgres", "redis", 100);
        await AssertCountsAsync("shadow before backfill"); // callers still get correct answers from postgres
        var beforeBackfill = await WaitForParityAsync(Cards);
        Assert.True(beforeBackfill.GetProperty("mismatch").GetInt64() > 0, "parity gate should catch the un-backfilled history");

        // ── Backfill ─────────────────────────────────────────────────────────────────────────────
        var backends = _factory.Services.GetServices<ICounterBackend>().ToDictionary(b => b.Name);
        var report = await _factory.Services.GetRequiredService<BackfillRunner>().RunAsync(
            (IBucketScanner)backends["postgres"], (IBucketWriter)backends["redis"], t0, 500, Ct);
        Assert.True(report.Copied > 0);
        Assert.Equal(0, report.NotYetFinal);

        // ── Phase 2 again: now the shadow must agree everywhere ──────────────────────────────────
        var mismatchesSoFar = beforeBackfill.GetProperty("mismatch").GetInt64();
        await AssertCountsAsync("shadow after backfill");
        var afterBackfill = await WaitForParityAsync(2 * Cards);
        Assert.Equal(mismatchesSoFar, afterBackfill.GetProperty("mismatch").GetInt64()); // zero NEW mismatches

        // ── Phase 3: split, with new traffic flowing ─────────────────────────────────────────────
        _mode.Set("Dual", "postgres", "redis", "Split", "postgres", "redis", 30);
        await IngestAsync(1);
        await AssertCountsAsync("split 30%");

        // ── Rollback drill: one config change back to legacy reads, writes stay dual ─────────────
        _mode.Set("Dual", "postgres", "redis", "Single", "postgres");
        await IngestAsync(1);
        await AssertCountsAsync("rolled back");

        _mode.Set("Dual", "postgres", "redis", "Split", "postgres", "redis", 100);
        await AssertCountsAsync("split 100%");

        // An unsafe change mid-migration (the naive switch) is refused; the running mode is untouched.
        var before = await AdminAsync();
        _mode.Set("Single", "postgres", null, "Single", "redis");
        Assert.Equal(before.GetProperty("summary").GetString(), (await AdminAsync()).GetProperty("summary").GetString());

        // ── Phase 4: Redis is primary; Postgres still receives writes, so we can still roll back ──
        _mode.Set("Dual", "redis", "postgres", "Single", "redis");
        await IngestAsync(1);
        await AssertCountsAsync("phase 4");

        // ── Phase 5: contract. Legacy no longer written. ─────────────────────────────────────────
        _mode.Set("Single", "redis", null, "Single", "redis");
        await AssertCountsAsync("phase 5");
        Assert.Equal("write=Single(redis) read=Single(redis)", (await AdminAsync()).GetProperty("summary").GetString());
    }

    private async Task IngestAsync(int perCard)
    {
        foreach (var card in _cards)
        {
            for (var i = 0; i < perCard; i++)
            {
                var response = await _client.PostAsJsonAsync("/v1/events", new
                {
                    eventId = Guid.NewGuid(), type = "payment.failed", occurredAt = _clock.GetUtcNow(),
                    entities = new { cardId = card, userId = "u_" + card },
                }, Ct);
                response.EnsureSuccessStatusCode();
            }
            _expected[card] = _expected.GetValueOrDefault(card) + perCard;
        }
    }

    private async Task AssertCountsAsync(string phase)
    {
        foreach (var card in _cards)
        {
            var body = await _client.GetFromJsonAsync<JsonElement>($"/v1/counters/payment_failed_by_card/{card}?window=P2D", Ct);
            Assert.True(_expected[card] == body.GetProperty("count").GetInt64(),
                string.Create(CultureInfo.InvariantCulture, $"{phase}: {card} expected {_expected[card]}, got {body.GetProperty("count").GetInt64()}"));
        }
    }

    private Task<JsonElement> AdminAsync() => _client.GetFromJsonAsync<JsonElement>("/admin/storage-mode", Ct);

    private async Task<JsonElement> WaitForParityAsync(long atLeast)
    {
        for (var i = 0; i < 300; i++)
        {
            var parity = (await AdminAsync()).GetProperty("parity");
            var total = ParityResults.Sum(p => parity.GetProperty(p).GetInt64());
            if (total >= atLeast)
            {
                return parity;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("shadow comparisons did not complete");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    /// <summary>A config source the test can change; firing OnReload is exactly what a file watcher does.</summary>
    private sealed class ModeSource : ConfigurationProvider, IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        public void Set(string writeMode, string writePrimary, string? writeSecondary, string readMode, string readPrimary, string? readSecondary = null, double percent = 0)
        {
            Data["CounterShift:Storage:Write:Mode"] = writeMode;
            Data["CounterShift:Storage:Write:Primary"] = writePrimary;
            Data["CounterShift:Storage:Write:Secondary"] = writeSecondary ?? "";
            Data["CounterShift:Storage:Read:Mode"] = readMode;
            Data["CounterShift:Storage:Read:Primary"] = readPrimary;
            Data["CounterShift:Storage:Read:Secondary"] = readSecondary ?? "";
            Data["CounterShift:Storage:Read:Percent"] = percent.ToString(CultureInfo.InvariantCulture);
            OnReload();
        }
    }
}
