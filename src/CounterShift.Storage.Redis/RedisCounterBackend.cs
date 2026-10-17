using System.Globalization;
using CounterShift.Domain.Counters;
using CounterShift.Domain.Time;
using StackExchange.Redis;

namespace CounterShift.Storage.Redis;

/// <summary>
/// New backend (ADR 0008): one Redis hash per (counter key, tier). Each field is a bucket start (unix
/// seconds) and its value is the count. This mirrors Grab's new model: one record per counter, holding
/// a map of buckets, pruned by the write path rather than by a separate cleanup job.
/// </summary>
/// <example>
/// <code>
/// HGETALL cs:{payment_failed_by_card:c_42}:15m
///   1760702400 → 3      (bucket 12:00Z)
///   1760703300 → 1      (bucket 12:15Z)
/// </code>
/// </example>
public sealed class RedisCounterBackend(IConnectionMultiplexer redis, string keyPrefix = "cs") : ICounterBackend
{
    public const string BackendName = "redis";

    // Runs atomically: Redis executes a script start to finish without interleaving other commands.
    // For each bucket: add the delta, pin the field's expiry to (bucket end + tier retention), and push
    // the whole key's TTL out as a safety net for keys that stop receiving writes.
    // ARGV holds 4 values per key: field, delta, field-expire-at (unix ms), key TTL (seconds).
    private const string IncrementScript =
        """
        for i = 1, #KEYS do
          local base = (i - 1) * 4
          local field = ARGV[base + 1]
          redis.call('HINCRBY', KEYS[i], field, ARGV[base + 2])
          redis.call('HPEXPIREAT', KEYS[i], ARGV[base + 3], 'FIELDS', 1, field)
          redis.call('EXPIRE', KEYS[i], ARGV[base + 4])
        end
        return #KEYS
        """;

    // Margin on the key-level TTL so it never fires before its newest field's own expiry.
    private static readonly TimeSpan KeyTtlSlack = TimeSpan.FromHours(1);

    public string Name => BackendName;

    public Task IncrementAsync(IReadOnlyList<BucketIncrement> increments, CancellationToken ct) => Translate(async () =>
    {
        var merged = BucketIncrements.Merge(increments);
        if (merged.Count == 0)
        {
            return;
        }
        var keys = new RedisKey[merged.Count];
        var args = new RedisValue[merged.Count * 4];
        for (var i = 0; i < merged.Count; i++)
        {
            var (bucket, delta) = (merged[i].Bucket, merged[i].Delta);
            var tier = bucket.Span.Granularity;
            keys[i] = KeyFor(bucket);
            args[i * 4] = FieldFor(bucket);
            args[(i * 4) + 1] = delta;
            // Same rule as the Postgres cleanup (ADR 0002): a bucket may go once it ENDS before now − retention.
            // A bucket that is already past that point is deleted by Redis as soon as its expiry is set. That
            // matches the contract: ingestion never writes that far back (ADR 0003), and neither does backfill.
            args[(i * 4) + 2] = (bucket.Span.End + tier.Retention).ToUnixTimeMilliseconds();
            args[(i * 4) + 3] = (long)(tier.Width + tier.Retention + KeyTtlSlack).TotalSeconds;
        }
        await redis.GetDatabase().ScriptEvaluateAsync(IncrementScript, keys, args).WaitAsync(ct);
    });

    public Task<IReadOnlyDictionary<BucketRef, long>> ReadAsync(IReadOnlyCollection<BucketRef> buckets, CancellationToken ct) => Translate(async () =>
    {
        var db = redis.GetDatabase();
        // One HMGET per hash. StackExchange.Redis pipelines concurrent commands over one connection, so
        // these go out back-to-back rather than waiting for each reply in turn.
        var lookups = buckets
            .Distinct()
            .GroupBy(KeyFor)
            .Select(async group =>
            {
                var refs = group.ToArray();
                var values = await db.HashGetAsync(group.Key, [.. refs.Select(r => (RedisValue)FieldFor(r))]).WaitAsync(ct);
                return refs.Zip(values);
            })
            .ToList();

        var result = new Dictionary<BucketRef, long>();
        foreach (var lookup in await Task.WhenAll(lookups))
        {
            foreach (var (bucket, value) in lookup)
            {
                if (!value.IsNull)
                {
                    result[bucket] = (long)value;
                }
            }
        }
        return (IReadOnlyDictionary<BucketRef, long>)result;
    });

    public Task PingAsync(CancellationToken ct) => Translate(() => redis.GetDatabase().PingAsync().WaitAsync(ct));

    public Task<StorageStats> GetStatsAsync(CancellationToken ct) => Translate(async () =>
    {
        // Walks every CounterShift key. That's fine for a report run, but never put this on a request path.
        long bytes = 0, fields = 0;
        var db = redis.GetDatabase();
        foreach (var endpoint in redis.GetEndPoints())
        {
            await foreach (var key in redis.GetServer(endpoint).KeysAsync(pattern: $"{keyPrefix}:*", pageSize: 1000).WithCancellation(ct))
            {
                bytes += (long)(await db.ExecuteAsync("MEMORY", "USAGE", key, "SAMPLES", "0"));
                fields += await db.HashLengthAsync(key);
            }
        }
        return new StorageStats(Name, bytes, fields);
    });

    // {...} is a Redis Cluster "hash tag": every tier of one counter lands in the same slot.
    // Harmless on a single node, and it keeps the option of scripting across tiers in a cluster.
    internal string KeyFor(BucketRef bucket) => $"{keyPrefix}:{{{bucket.Key.Value}}}:{bucket.Span.Granularity.Name}";

    internal static string FieldFor(BucketRef bucket) =>
        bucket.Span.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    // Connection problems and timeouts mean "unavailable". A RedisServerException (e.g. a script error) is a bug: let it surface.
    private static async Task Translate(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException or TimeoutException)
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
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException or TimeoutException)
        {
            throw new StorageUnavailableException(BackendName, ex);
        }
    }
}
