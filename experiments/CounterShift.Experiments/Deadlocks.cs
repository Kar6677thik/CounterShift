using System.Globalization;
using Dapper;
using Npgsql;

namespace CounterShift.Experiments;

/// <summary>
/// Experiment B5: each statement upserts the same R rows in one multi-row INSERT ... ON CONFLICT, the way
/// the fixed API writes all of an event's buckets at once. If two statements lock the same rows in a
/// different order, each can end up waiting for the other: a deadlock (SQLSTATE 40P01). Postgres
/// detects it after deadlock_timeout (1 s) and kills one statement.
/// </summary>
internal static class Deadlocks
{
    public static async Task RunAsync(NpgsqlDataSource dataSource, int writers, int statementsPerWriter, int rows)
    {
        Console.WriteLine($"## B5: deadlocks: {writers} writers × {statementsPerWriter} multi-row upserts, each touching the same {rows} rows\n");
        Console.WriteLine("| Row order inside each statement | Deadlocks (40P01) | Lost increments | Elapsed |");
        Console.WriteLine("|---|---:|---:|---:|");

        foreach (var sorted in new[] { false, true })
        {
            var prefix = $"deadlock:{(sorted ? "sorted" : "shuffled")}:{Guid.NewGuid():N}";
            var keys = Enumerable.Range(0, rows).Select(i => $"{prefix}:{i:D3}").ToArray();
            var deadlocks = 0;
            var watch = Report.Start();

            await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                var random = new Random(w);
                await using var conn = await dataSource.OpenConnectionAsync();
                for (var i = 0; i < statementsPerWriter; i++)
                {
                    var order = sorted ? keys : [.. keys.OrderBy(_ => random.Next())];
                    try
                    {
                        // WITH ORDINALITY + ORDER BY makes the processing order explicit: either the
                        // shuffled order we passed in, or a consistent sorted order shared by all writers.
                        await conn.ExecuteAsync(
                            $"""
                            INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count)
                            SELECT k, 1, '2026-10-17T12:00:00Z', 1
                            FROM unnest(@keys) WITH ORDINALITY AS t(k, n)
                            ORDER BY {(sorted ? "k" : "n")}
                            ON CONFLICT (counter_key, granularity, bucket_start) DO UPDATE SET count = counter_buckets.count + 1
                            """, new { keys = order });
                    }
                    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
                    {
                        Interlocked.Increment(ref deadlocks);
                    }
                }
            })));
            watch.Stop();

            await using var check = await dataSource.OpenConnectionAsync();
            var total = await check.ExecuteScalarAsync<long>("SELECT coalesce(sum(count), 0) FROM counter_buckets WHERE counter_key LIKE @p", new { p = prefix + ":%" });
            var lost = (long)writers * statementsPerWriter * rows - total;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {(sorted ? "**sorted** (consistent lock order)" : "shuffled (random per statement)")} | {deadlocks:N0} | {lost:N0} | {watch.Elapsed.TotalSeconds:F2} s |"));
        }

        Console.WriteLine("\nA deadlocked statement is rolled back entirely: all its row increments are lost unless the caller retries.");
    }
}
