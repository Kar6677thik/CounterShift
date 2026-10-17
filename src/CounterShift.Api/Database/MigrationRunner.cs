using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace CounterShift.Api.Database;

/// <summary>
/// Applies the versioned SQL files in Database/Migrations, in order, exactly once each (ADR 0001).
/// This is what tools like Flyway, DbUp and EF migrations do underneath:
/// <list type="bullet">
/// <item>a <c>schema_migrations</c> table records what has run;</item>
/// <item>each file's checksum is stored, so editing an already-applied migration fails loudly instead of
/// silently diverging between environments;</item>
/// <item>a Postgres advisory lock means that when two pods start at once, one migrates and the other waits.</item>
/// </list>
/// </summary>
public sealed partial class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    // An arbitrary constant that identifies "the CounterShift migration lock" across all sessions.
    private const long AdvisoryLockKey = 0x436F756E74; // "Count" in ASCII

    private const string ResourcePrefix = "CounterShift.Api.Database.Migrations.";

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: ct));
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    int         PRIMARY KEY,
                    name       text        NOT NULL,
                    checksum   text        NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, cancellationToken: ct));

            var applied = (await conn.QueryAsync<(int Version, string Checksum)>(
                new CommandDefinition("SELECT version, checksum FROM schema_migrations", cancellationToken: ct)))
                .ToDictionary(r => r.Version, r => r.Checksum);

            foreach (var migration in LoadMigrations())
            {
                if (applied.TryGetValue(migration.Version, out var checksum))
                {
                    if (checksum != migration.Checksum)
                    {
                        throw new InvalidOperationException(
                            $"Migration {migration.Name} was edited after it was applied. Add a new migration instead.");
                    }
                    continue;
                }

                // Each migration and its bookkeeping row commit together, or not at all.
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: tx, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations (version, name, checksum) VALUES (@Version, @Name, @Checksum)",
                    migration, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                LogApplied(logger, migration.Name);
            }
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey }, cancellationToken: CancellationToken.None));
        }
    }

    internal static IReadOnlyList<Migration> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var name = n[ResourcePrefix.Length..];
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                var sql = reader.ReadToEnd().ReplaceLineEndings("\n");
                var version = int.Parse(name.AsSpan(0, name.IndexOf('_', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                return new Migration(version, name, sql, checksum);
            })
            .OrderBy(m => m.Version)
            .ToList();
    }

    internal sealed record Migration(int Version, string Name, string Sql, string Checksum);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied migration {Name}")]
    private static partial void LogApplied(ILogger logger, string name);
}
