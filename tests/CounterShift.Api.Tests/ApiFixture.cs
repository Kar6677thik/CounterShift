using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(CounterShift.Api.Tests.ApiFixture))]

namespace CounterShift.Api.Tests;

/// <summary>
/// One real PostgreSQL, one real Redis and one in-memory API host, shared by every test in the assembly.
/// Starting containers takes seconds; doing it once instead of per test keeps the suite fast.
/// Tests stay independent by using unique entity IDs instead of wiping data.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    /// <summary>The frozen "now" for every test: 12:07:30Z, deliberately not aligned to any bucket.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 7, 30, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();

    public FakeTimeProvider Clock { get; } = new(Now);
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public NpgsqlDataSource Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        Database = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            }));
            host.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
        });
        _ = Factory.Server; // start the host now (runs migrations) so the first test isn't slowed down
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>A second API host over the same databases, with some settings overridden.</summary>
    public WebApplicationFactory<Program> WithSettings(params (string Key, string? Value)[] settings) =>
        Factory.WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))));

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}
