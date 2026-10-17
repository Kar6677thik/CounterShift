using CounterShift.Domain.Time;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CounterShift.Storage.Postgres;

/// <summary>
/// Deletes buckets that have aged out of their tier's retention (spec §4.5), every few minutes.
/// </summary>
/// <remarks>
/// A bucket may only be deleted once it <b>ends</b> at or before <c>now − retention</c> (ADR 0002).
/// Deleting by start time would remove buckets the decomposer still uses.
/// Deletes run in small batches. One huge DELETE would hold locks and bloat the write-ahead log for a
/// long time, stalling live increments. At production scale, time-partitioned tables with
/// <c>DROP PARTITION</c> make this nearly free; that is a stretch goal in the spec.
/// </remarks>
public sealed partial class RetentionCleanupService(
    NpgsqlDataSource dataSource,
    TimeProvider clock,
    IOptions<PostgresOptions> options,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    private const int BatchSize = 5_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RetentionCleanupInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                // A missed cleanup is harmless: the next tick catches up. Never crash the API over it.
                LogCleanupFailed(logger, ex);
            }
        }
    }

    public async Task<long> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        long total = 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var tier in Granularity.All)
        {
            // bucket end <= now - retention  ⇔  bucket start <= now - retention - width
            var cutoff = (now - tier.Retention - tier.Width).UtcDateTime;
            int deleted;
            do
            {
                deleted = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM counter_buckets
                    WHERE ctid = ANY (ARRAY(
                        SELECT ctid FROM counter_buckets
                        WHERE granularity = @granularity AND bucket_start <= @cutoff
                        LIMIT @batch))
                    """,
                    new { granularity = (short)tier.Minutes, cutoff, batch = BatchSize },
                    cancellationToken: ct));
                total += deleted;
            } while (deleted == BatchSize);
        }
        LogCleanupDone(logger, total);
        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention cleanup deleted {Count} expired buckets")]
    private static partial void LogCleanupDone(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention cleanup failed; will retry next interval")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
