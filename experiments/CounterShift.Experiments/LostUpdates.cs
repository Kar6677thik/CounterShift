using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using Npgsql;

namespace CounterShift.Experiments;

/// <summary>
/// Experiment B1: N writers each add 1 to the <b>same</b> bucket M times. The final count should be N×M.
/// Each strategy gets a fresh bucket that doesn't exist yet, so the "first insert" race is included too.
/// </summary>
internal static class LostUpdates
{
    public static async Task RunAsync(NpgsqlDataSource dataSource, int writers, int incrementsPerWriter)
    {
        IncrementStrategy[] strategies = [new NaiveReadModifyWrite(), new AtomicUpsert(), new SelectForUpdate(), new SerializableWithRetry()];
        var expected = (long)writers * incrementsPerWriter;
        Console.WriteLine($"## B1: lost updates: {writers} concurrent writers × {incrementsPerWriter} increments on ONE bucket (expected {expected:N0})\n");
        Console.WriteLine("| Strategy | Final count | Lost | Lost % | Failed calls | Retries | Elapsed | Throughput |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var strategy in strategies)
        {
            var bucket = new Bucket($"experiment:{strategy.Name}:{Guid.NewGuid():N}", 1, new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc));
            var failures = new ConcurrentDictionary<string, int>();
            long retries = 0;
            using var start = new ManualResetEventSlim(false);

            var watch = Report.Start();
            var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
            {
                await using var conn = await dataSource.OpenConnectionAsync();
                start.Wait(); // release every writer at the same instant to maximise overlap
                for (var i = 0; i < incrementsPerWriter; i++)
                {
                    try
                    {
                        Interlocked.Add(ref retries, await strategy.IncrementAsync(conn, bucket, CancellationToken.None));
                    }
                    catch (PostgresException ex)
                    {
                        failures.AddOrUpdate(ex.SqlState, 1, (_, n) => n + 1);
                    }
                }
            })).ToList();
            await Task.Delay(200); // let every writer open its connection first
            watch.Restart();
            start.Set();
            await Task.WhenAll(tasks);
            watch.Stop();

            await using var conn = await dataSource.OpenConnectionAsync();
            var actual = await conn.ExecuteScalarAsync<long?>(
                "SELECT count FROM counter_buckets WHERE counter_key = @Key AND granularity = @Granularity AND bucket_start = @Start", bucket) ?? 0;
            var lost = expected - actual;
            var failed = failures.Values.Sum();
            var failedText = failed == 0 ? "0" : $"{failed:N0} ({string.Join(", ", failures.Select(f => $"{f.Key}×{f.Value}"))})";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| `{strategy.Name}` | {actual:N0} | {lost:N0} | {100.0 * lost / expected:F1}% | {failedText} | {retries:N0} | {watch.Elapsed.TotalSeconds:F2} s | {Report.Rate(expected, watch.Elapsed)} |"));
        }

        Console.WriteLine("\nStrategies:");
        foreach (var s in strategies)
        {
            Console.WriteLine($"- `{s.Name}`: {s.Description}");
        }
        Console.WriteLine("\nSQLSTATE 23505 = unique_violation (two writers both saw 'no row' and both INSERTed); 40001 = serialization_failure.");
    }
}
