using CounterShift.Domain.Counters;
using CounterShift.Domain.Time;
using Dapper;
using Npgsql;

namespace CounterShift.Storage.Postgres;

/// <summary>
/// Legacy backend: one row per (counter key, granularity, bucket start). This is an Adapter: it
/// translates the storage contract into SQL, and Npgsql's failures into <see cref="StorageUnavailableException"/>.
/// </summary>
public sealed class PostgresCounterBackend(NpgsqlDataSource dataSource) : ICounterBackend
{
    public const string BackendName = "postgres";

    public string Name => BackendName;

    public Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct) => Translate(async () =>
    {
        // ADR 0006: one atomic statement; ORDER BY gives a consistent lock order (no deadlocks);
        // duplicates merged first because ON CONFLICT can't update the same row twice in one statement.
        var merged = BucketIncrements.Merge(increments);
        if (merged.Count == 0)
        {
            return;
        }
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count)
            SELECT * FROM unnest(@keys, @granularities, @starts, @deltas) AS t(counter_key, granularity, bucket_start, delta)
            ORDER BY counter_key, granularity, bucket_start
            ON CONFLICT (counter_key, granularity, bucket_start)
            DO UPDATE SET count = counter_buckets.count + EXCLUDED.count
            """,
            new
            {
                keys = merged.Select(m => m.Bucket.Key.Value).ToArray(),
                granularities = merged.Select(m => (short)m.Bucket.Span.Granularity.Minutes).ToArray(),
                starts = merged.Select(m => m.Bucket.Span.Start.UtcDateTime).ToArray(),
                deltas = merged.Select(m => m.Delta).ToArray(),
            },
            cancellationToken: ct));
    });

    public Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct) => Translate(async () =>
    {
        if (buckets.Count == 0)
        {
            return (IReadOnlyDictionary<BucketRef, long>)new Dictionary<BucketRef, long>();
        }
        // One round trip for every bucket: unnest() turns three parallel arrays into rows to join against.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Key, short Granularity, DateTime Start, long Count)>(new CommandDefinition(
            """
            SELECT b.counter_key, b.granularity, b.bucket_start, b.count
            FROM counter_buckets b
            JOIN unnest(@keys, @granularities, @starts) AS q(counter_key, granularity, bucket_start)
              ON b.counter_key = q.counter_key AND b.granularity = q.granularity AND b.bucket_start = q.bucket_start
            """,
            new
            {
                keys = buckets.Select(b => b.Key.Value).ToArray(),
                granularities = buckets.Select(b => (short)b.Span.Granularity.Minutes).ToArray(),
                starts = buckets.Select(b => b.Span.Start.UtcDateTime).ToArray(),
            },
            cancellationToken: ct));

        var result = new Dictionary<BucketRef, long>();
        foreach (var r in rows)
        {
            var bucket = new BucketRef(CounterKey.Parse(r.Key), new BucketSpan(Granularity.FromMinutes(r.Granularity), new DateTimeOffset(r.Start, TimeSpan.Zero)));
            result[bucket] = r.Count; // the same bucket asked for twice joins twice: harmless overwrite
        }
        return (IReadOnlyDictionary<BucketRef, long>)result;
    });

    public Task PingAsync(CancellationToken ct) => Translate(async () =>
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
    });

    public Task<StorageStats> GetStatsAsync(CancellationToken ct) => Translate(async () =>
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var (bytes, rows) = await conn.QuerySingleAsync<(long Bytes, long Rows)>(new CommandDefinition(
            // Table + all indexes + TOAST: what the data really costs on disk.
            "SELECT pg_total_relation_size('counter_buckets'), (SELECT count(*) FROM counter_buckets)",
            cancellationToken: ct));
        return new StorageStats(Name, bytes, rows);
    });

    // Only *transient* failures (can't connect, connection dropped, pool exhausted, timeout) mean "unavailable".
    // A non-transient PostgresException, such as a constraint violation, is a bug and should surface as a 500.
    private static async Task Translate(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is NpgsqlException { IsTransient: true } or TimeoutException)
        {
            throw new StorageUnavailableException(BackendName, ex);
        }
    }

    private static async Task<T> Translate<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is NpgsqlException { IsTransient: true } or TimeoutException)
        {
            throw new StorageUnavailableException(BackendName, ex);
        }
    }
}
