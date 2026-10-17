using System.Diagnostics;
using System.Globalization;
using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Time;
using CounterShift.Storage.Postgres;
using CounterShift.Storage.Redis;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace CounterShift.Experiments;

/// <summary>
/// "Storage used per million events" (spec §10), for the Legacy and New designs, fed identical data.
/// </summary>
/// <remarks>
/// Workload (it shapes the result, so it's stated in the output): events spread uniformly over the last 2 hours
/// (so every tier, including 1m, is still retained in both stores), 60% payment.failed / 25% login.failed /
/// 15% ride.requested, 30% of card traffic on 1% of cards. Each event is planned by the real CounterCatalog,
/// so it causes counters × 4 tiers increments, exactly as the API does. One event per write call, like the API.
/// </remarks>
internal static class Footprint
{
    public static async Task RunAsync(int events, int cards, int users, int concurrency)
    {
        Console.WriteLine($"## Storage footprint: {events:N0} events, {cards:N0} cards, {users:N0} users, {concurrency} concurrent writers\n");
        await using var pgContainer = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await using var redisContainer = new RedisBuilder("redis:8-alpine").WithCommand("--appendonly", "yes", "--appendfsync", "everysec").Build();
        await Task.WhenAll(pgContainer.StartAsync(), redisContainer.StartAsync());

        var builder = new NpgsqlDataSourceBuilder(pgContainer.GetConnectionString());
        builder.ConnectionStringBuilder.MaxPoolSize = concurrency + 2;
        await using var dataSource = builder.Build();
        await new MigrationRunner(dataSource, NullLogger<MigrationRunner>.Instance).ApplyAsync();
        var redisOptions = RedisStorage.Options(redisContainer.GetConnectionString());
        redisOptions.AllowAdmin = true; // INFO is an "admin" command in StackExchange.Redis. Experiment only; the API never needs it.
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        var postgres = new PostgresCounterBackend(dataSource);
        var redisBackend = new RedisCounterBackend(redis);

        var catalog = new CounterCatalog(
        [
            new("payment_failed_by_card", EventTypes.PaymentFailed, EntityFields.CardId),
            new("payment_failed_by_user", EventTypes.PaymentFailed, EntityFields.UserId),
            new("login_failed_by_user", EventTypes.LoginFailed, EntityFields.UserId),
            new("ride_requested_by_user", EventTypes.RideRequested, EntityFields.UserId),
        ], GranularitySet.Default);
        var now = DateTimeOffset.UtcNow;
        // Generated history spans 2 hours, so this run accepts events up to 2 h late (the API allows 5 min).
        var policy = new IngestionPolicy(TimeSpan.FromHours(2), TimeSpan.FromSeconds(30));
        var redisBaseline = await UsedMemoryAsync(redis);

        // Event i is generated from seed i, so both stores receive exactly the same events.
        IReadOnlyList<BucketIncrement> Plan(int i)
        {
            var rnd = new Random(i);
            var hot = Math.Max(1, cards / 100);
            var card = rnd.NextDouble() < 0.3 ? rnd.Next(hot) : rnd.Next(cards);
            var user = $"u{rnd.Next(users)}";
            var r = rnd.NextDouble();
            var type = r < 0.6 ? EventTypes.PaymentFailed : r < 0.85 ? EventTypes.LoginFailed : EventTypes.RideRequested;
            var entities = type == EventTypes.PaymentFailed
                ? new Dictionary<string, string> { [EntityFields.CardId] = $"c{card}", [EntityFields.UserId] = user }
                : new Dictionary<string, string> { [EntityFields.UserId] = user };
            var at = now - TimeSpan.FromSeconds(rnd.NextDouble() * 7200);
            return catalog.PlanIncrements(new FraudEvent(Guid.NewGuid(), type, at, entities), now, policy);
        }

        long increments = 0;
        async Task<TimeSpan> Load(Func<IReadOnlyList<BucketIncrement>, Task> write, bool count)
        {
            var watch = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, events), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, _) =>
            {
                var plan = Plan(i);
                if (count)
                {
                    Interlocked.Add(ref increments, plan.Count);
                }
                await write(plan);
            });
            return watch.Elapsed;
        }

        Console.WriteLine("Loading PostgreSQL…");
        var pgTime = await Load(p => postgres.IncrementAsync(p, CancellationToken.None), count: true);
        Console.WriteLine("Loading Redis…");
        var redisTime = await Load(p => redisBackend.IncrementAsync(p, CancellationToken.None), count: false);

        await using var conn = await dataSource.OpenConnectionAsync();
        var asWritten = await postgres.GetStatsAsync(CancellationToken.None);
        var dead = await conn.ExecuteScalarAsync<long>("SELECT n_dead_tup FROM pg_stat_user_tables WHERE relname = 'counter_buckets'");
        await conn.ExecuteAsync("VACUUM counter_buckets", commandTimeout: 600);
        var vacuumed = await postgres.GetStatsAsync(CancellationToken.None);
        await conn.ExecuteAsync("VACUUM FULL counter_buckets", commandTimeout: 600);
        var compacted = await postgres.GetStatsAsync(CancellationToken.None);
        var tableOnly = await conn.ExecuteScalarAsync<long>("SELECT pg_relation_size('counter_buckets')");
        var redisStats = await redisBackend.GetStatsAsync(CancellationToken.None);
        var redisUsed = await UsedMemoryAsync(redis) - redisBaseline;

        double PerMillion(long bytes) => bytes / (events / 1_000_000.0);
        string Mb(double bytes) => (bytes / 1024 / 1024).ToString("N1", CultureInfo.InvariantCulture) + " MiB";
        string Rate(TimeSpan t) => (events / t.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture) + " events/s";

        Console.WriteLine($"\nIncrements planned: {increments:N0} ({(double)increments / events:F1} per event: counters × 4 tiers)");
        Console.WriteLine($"Distinct buckets: PostgreSQL rows {compacted.Records:N0} · Redis hash fields {redisStats.Records:N0} " +
                          $"{(compacted.Records == redisStats.Records ? "(identical ✓)" : "(DIFFERENT ✗)")}\n");
        Console.WriteLine("| Store | Measured as | Size | Per 1M events | Bytes per bucket | Write throughput |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|");
        Console.WriteLine($"| PostgreSQL | table + indexes, as written ({dead:N0} dead tuples) | {Mb(asWritten.Bytes)} | {Mb(PerMillion(asWritten.Bytes))} | {asWritten.Bytes / Math.Max(1, compacted.Records):N0} | {Rate(pgTime)} |");
        Console.WriteLine($"| PostgreSQL | after VACUUM (space reusable, file not shrunk) | {Mb(vacuumed.Bytes)} | {Mb(PerMillion(vacuumed.Bytes))} | {vacuumed.Bytes / Math.Max(1, compacted.Records):N0} | |");
        Console.WriteLine($"| PostgreSQL | after VACUUM FULL (compacted; table alone {Mb(tableOnly)}) | {Mb(compacted.Bytes)} | {Mb(PerMillion(compacted.Bytes))} | {compacted.Bytes / Math.Max(1, compacted.Records):N0} | |");
        Console.WriteLine($"| Redis | sum of MEMORY USAGE per key | {Mb(redisStats.Bytes)} | {Mb(PerMillion(redisStats.Bytes))} | {redisStats.Bytes / Math.Max(1, redisStats.Records):N0} | {Rate(redisTime)} |");
        Console.WriteLine($"| Redis | INFO used_memory growth | {Mb(redisUsed)} | {Mb(PerMillion(redisUsed))} | {redisUsed / Math.Max(1, redisStats.Records):N0} | |");
        Console.WriteLine("\nPostgreSQL sizes are on disk; Redis sizes are RAM. That's the real cost of each in its own currency.");
    }

    private static async Task<long> UsedMemoryAsync(ConnectionMultiplexer redis)
    {
        var info = (string)(await redis.GetDatabase().ExecuteAsync("INFO", "memory"))!;
        var line = info.Split('\n').First(l => l.StartsWith("used_memory:", StringComparison.Ordinal));
        return long.Parse(line["used_memory:".Length..].Trim(), CultureInfo.InvariantCulture);
    }
}
