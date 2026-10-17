using CounterShift.Api.Database;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace CounterShift.Api.Tests;

public sealed class DatabaseTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        var runner = api.Factory.Services.GetRequiredService<MigrationRunner>();

        await runner.ApplyAsync(Ct); // the host already applied them at startup; this must be a no-op

        await using var conn = await api.Database.OpenConnectionAsync(Ct);
        var applied = await conn.QueryAsync<int>(new CommandDefinition("SELECT version FROM schema_migrations ORDER BY version", cancellationToken: Ct));
        Assert.Equal(MigrationRunner.LoadMigrations().Select(m => m.Version), applied);
    }

    [Fact]
    public void Migration_files_are_embedded_in_the_assembly()
    {
        Assert.Contains(MigrationRunner.LoadMigrations(), m => m.Name == "0001_create_counter_buckets.sql");
    }

    [Fact]
    public async Task Retention_cleanup_deletes_a_bucket_only_after_it_has_fully_aged_out()
    {
        // 1m tier: retention 3h. Now is 12:07:30, so the cutoff is 09:07:30.
        //   bucket 09:06 ends 09:07 → fully older than the cutoff → delete
        //   bucket 09:07 ends 09:08 → still overlaps the retained range → keep
        var key = $"login_failed_by_user:{Guid.NewGuid():N}";
        var expired = new DateTime(2026, 10, 17, 9, 6, 0, DateTimeKind.Utc);
        var kept = new DateTime(2026, 10, 17, 9, 7, 0, DateTimeKind.Utc);
        await using var conn = await api.Database.OpenConnectionAsync(Ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count) VALUES (@key, 1, @expired, 1), (@key, 1, @kept, 1)",
            new { key, expired, kept }, cancellationToken: Ct));

        await api.Factory.Services.GetRequiredService<RetentionCleanupService>().RunOnceAsync(Ct);

        var remaining = await conn.QueryAsync<DateTime>(new CommandDefinition(
            "SELECT bucket_start FROM counter_buckets WHERE counter_key = @key", new { key }, cancellationToken: Ct));
        Assert.Equal([kept], remaining);
    }
}
