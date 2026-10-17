using CounterShift.Domain.Counters;
using CounterShift.Domain.Time;
using CounterShift.Storage.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(CounterShift.Storage.Tests.RedisFixture))]

namespace CounterShift.Storage.Tests;

/// <summary>One real Redis 8, configured the way production will be (append-only file, fsync every second).</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8-alpine")
        .WithCommand("--appendonly", "yes", "--appendfsync", "everysec")
        .Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(RedisStorage.Options(_container.GetConnectionString()));
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class RedisBackendContract(RedisFixture redis) : CounterBackendContract
{
    protected override ICounterBackend Backend { get; } = new RedisCounterBackend(redis.Connection);

    protected override ICounterBackend CreateUnreachableBackend() =>
        new RedisCounterBackend(ConnectionMultiplexer.Connect(RedisStorage.Options("127.0.0.1:1,connectTimeout=500")));
}

/// <summary>Behaviour particular to Redis: storage layout and native expiry.</summary>
public sealed class RedisBackendTests(RedisFixture redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RedisCounterBackend _backend = new(redis.Connection);

    [Fact]
    public async Task One_hash_per_counter_and_tier_with_bucket_starts_as_fields()
    {
        var key = CounterKey.For("layout", Guid.NewGuid().ToString("N"));
        var start = Granularity.FifteenMinutes.AlignDown(DateTimeOffset.UtcNow);
        var bucket = new BucketRef(key, new BucketSpan(Granularity.FifteenMinutes, start));

        await _backend.IncrementAsync([new BucketIncrement(bucket, 3)], Ct);

        var hash = await redis.Connection.GetDatabase().HashGetAllAsync($"cs:{{{key.Value}}}:15m");
        var entry = Assert.Single(hash);
        Assert.Equal(start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Name.ToString());
        Assert.Equal(3, (long)entry.Value);
    }

    [Fact]
    public async Task Each_field_expires_when_its_bucket_ages_out_of_retention()
    {
        var key = CounterKey.For("ttl", Guid.NewGuid().ToString("N"));
        var start = Granularity.OneMinute.AlignDown(DateTimeOffset.UtcNow);
        var bucket = new BucketRef(key, new BucketSpan(Granularity.OneMinute, start));

        await _backend.IncrementAsync([new BucketIncrement(bucket, 1)], Ct);

        var db = redis.Connection.GetDatabase();
        var redisKey = (RedisKey)$"cs:{{{key.Value}}}:1m";
        var expireAt = (long[])(await db.ExecuteAsync("HPEXPIRETIME", redisKey, "FIELDS", 1, RedisCounterBackend.FieldFor(bucket)))!;
        // Bucket end + 3 h retention for the 1m tier: the same rule as the Postgres cleanup (ADR 0002).
        Assert.Equal((start + Granularity.OneMinute.Width + Granularity.OneMinute.Retention).ToUnixTimeMilliseconds(), expireAt[0]);
        Assert.True(await db.KeyTimeToLiveAsync(redisKey) > Granularity.OneMinute.Retention); // key-level safety net
    }

    [Fact]
    public async Task A_bucket_already_past_retention_is_dropped_by_redis()
    {
        var key = CounterKey.For("expired", Guid.NewGuid().ToString("N"));
        var old = new BucketRef(key, new BucketSpan(Granularity.OneMinute, Granularity.OneMinute.AlignDown(DateTimeOffset.UtcNow.AddHours(-4))));

        await _backend.IncrementAsync([new BucketIncrement(old, 1)], Ct);

        Assert.Empty(await _backend.ReadAsync([old], Ct));
    }
}
