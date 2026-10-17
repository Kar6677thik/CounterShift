using System.Data;
using Dapper;
using Npgsql;

namespace CounterShift.Experiments;

/// <summary>
/// Different ways to "add 1 to a bucket", from broken to correct. Every strategy targets the same
/// table and row, so the only variable is the SQL pattern.
/// </summary>
internal abstract class IncrementStrategy
{
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>Performs one increment. Returns how many retries it needed.</summary>
    public abstract Task<int> IncrementAsync(NpgsqlConnection conn, Bucket bucket, CancellationToken ct);

    protected const string Where = "counter_key = @Key AND granularity = @Granularity AND bucket_start = @Start";
}

internal sealed record Bucket(string Key, short Granularity, DateTime Start);

/// <summary>Exactly the v0 code path (tag v0-scattered): SELECT, add in C#, then INSERT or UPDATE, in a READ COMMITTED transaction.</summary>
internal sealed class NaiveReadModifyWrite : IncrementStrategy
{
    public override string Name => "naive-rmw";
    public override string Description => "v0: SELECT, +1 in C#, INSERT or UPDATE (READ COMMITTED transaction)";

    public override async Task<int> IncrementAsync(NpgsqlConnection conn, Bucket bucket, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var current = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            $"SELECT count FROM counter_buckets WHERE {Where}", bucket, tx, cancellationToken: ct));
        // ← The gap: between this read and the write below, other writers read the same value.
        if (current is null)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count) VALUES (@Key, @Granularity, @Start, 1)",
                bucket, tx, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE counter_buckets SET count = @Next WHERE {Where}",
                new { bucket.Key, bucket.Granularity, bucket.Start, Next = current.Value + 1 }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return 0;
    }
}

/// <summary>The fix: one statement, so the database does the read-add-write while holding the row lock.</summary>
internal sealed class AtomicUpsert : IncrementStrategy
{
    public override string Name => "atomic-upsert";
    public override string Description => "INSERT ... ON CONFLICT DO UPDATE SET count = count + 1 (single statement)";

    public override async Task<int> IncrementAsync(NpgsqlConnection conn, Bucket bucket, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count) VALUES (@Key, @Granularity, @Start, 1)
            ON CONFLICT (counter_key, granularity, bucket_start) DO UPDATE SET count = counter_buckets.count + 1
            """, bucket, cancellationToken: ct));
        return 0;
    }
}

/// <summary>Pessimistic locking: lock the row first so others wait. Correct, but two statements and a held lock.</summary>
internal sealed class SelectForUpdate : IncrementStrategy
{
    public override string Name => "select-for-update";
    public override string Description => "ensure row exists, SELECT ... FOR UPDATE, +1 in C#, UPDATE";

    public override async Task<int> IncrementAsync(NpgsqlConnection conn, Bucket bucket, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // FOR UPDATE can't lock a row that doesn't exist yet, so create it first if needed.
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count) VALUES (@Key, @Granularity, @Start, 0) ON CONFLICT DO NOTHING",
            bucket, tx, cancellationToken: ct));
        var current = await conn.QuerySingleAsync<long>(new CommandDefinition(
            $"SELECT count FROM counter_buckets WHERE {Where} FOR UPDATE", bucket, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE counter_buckets SET count = @Next WHERE {Where}",
            new { bucket.Key, bucket.Granularity, bucket.Start, Next = current + 1 }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return 0;
    }
}

/// <summary>
/// Optimistic: keep the naive read-modify-write, but run it SERIALIZABLE. Postgres detects the conflict
/// and aborts one transaction (SQLSTATE 40001), which we must retry. Correct, but wasted work under contention.
/// </summary>
internal sealed class SerializableWithRetry : IncrementStrategy
{
    public override string Name => "serializable-retry";
    public override string Description => "naive read-modify-write at SERIALIZABLE, retry on 40001/23505";

    public override async Task<int> IncrementAsync(NpgsqlConnection conn, Bucket bucket, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var current = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                    $"SELECT count FROM counter_buckets WHERE {Where}", bucket, tx, cancellationToken: ct));
                if (current is null)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count) VALUES (@Key, @Granularity, @Start, 1)",
                        bucket, tx, cancellationToken: ct));
                }
                else
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        $"UPDATE counter_buckets SET count = @Next WHERE {Where}",
                        new { bucket.Key, bucket.Granularity, bucket.Start, Next = current.Value + 1 }, tx, cancellationToken: ct));
                }
                await tx.CommitAsync(ct);
                return attempt;
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
            {
                // Retrying is the contract of SERIALIZABLE: the database tells you to try again.
            }
        }
    }
}
