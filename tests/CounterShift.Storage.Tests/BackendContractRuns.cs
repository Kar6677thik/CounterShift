using CounterShift.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(CounterShift.Storage.Tests.PostgresFixture))]

namespace CounterShift.Storage.Tests;

/// <summary>One real PostgreSQL (with migrations applied) shared by all storage tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await new MigrationRunner(DataSource, NullLogger<MigrationRunner>.Instance).ApplyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class InMemoryBackendContract : CounterBackendContract
{
    protected override ICounterBackend Backend { get; } = new InMemoryCounterBackend();

    protected override ICounterBackend CreateUnreachableBackend() => new InMemoryCounterBackend { Unavailable = true };
}

public sealed class PostgresBackendContract(PostgresFixture postgres) : CounterBackendContract
{
    protected override ICounterBackend Backend { get; } = new PostgresCounterBackend(postgres.DataSource);

    // Port 1 on localhost: nothing listens there, so the connection is refused immediately.
    protected override ICounterBackend CreateUnreachableBackend() =>
        new PostgresCounterBackend(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=2"));
}
