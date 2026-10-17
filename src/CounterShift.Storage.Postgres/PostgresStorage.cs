using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CounterShift.Storage.Postgres;

/// <summary>Configuration for the PostgreSQL backend (section "CounterShift:Postgres").</summary>
public sealed class PostgresOptions
{
    public const string SectionName = "CounterShift:Postgres";

    /// <summary>
    /// Upper bound on this process's open PostgreSQL connections. Npgsql's default (100) equals Postgres's
    /// default max_connections (100), so one busy replica could take every connection the server has.
    /// Keep (replicas × MaxPoolSize) well below max_connections. Extra requests wait for a free connection,
    /// which is backpressure, instead of overwhelming the database (ADR 0006).
    /// </summary>
    public int MaxPoolSize { get; set; } = 20;

    public TimeSpan RetentionCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}

public static class PostgresStorage
{
    /// <summary>
    /// Registers the PostgreSQL backend and everything it needs: connection pool, migrations, retention
    /// cleanup. The API's composition root calls this one method; nothing else in the API knows about SQL.
    /// </summary>
    public static IServiceCollection AddPostgresCounterBackend(this IServiceCollection services, Func<IServiceProvider, string?> connectionString)
    {
        services.AddSingleton(sp =>
        {
            var cs = connectionString(sp);
            if (string.IsNullOrWhiteSpace(cs))
            {
                throw new InvalidOperationException("The PostgreSQL connection string (ConnectionStrings:Postgres) is not configured.");
            }
            var builder = new NpgsqlDataSourceBuilder(cs);
            builder.ConnectionStringBuilder.MaxPoolSize = sp.GetRequiredService<IOptions<PostgresOptions>>().Value.MaxPoolSize;
            return builder.Build();
        });
        services.AddSingleton<PostgresCounterBackend>();
        services.AddSingleton<ICounterBackend>(sp => sp.GetRequiredService<PostgresCounterBackend>());
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<IStorageInitializer>(sp => sp.GetRequiredService<MigrationRunner>());
        services.AddSingleton<RetentionCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<RetentionCleanupService>());
        return services;
    }
}
