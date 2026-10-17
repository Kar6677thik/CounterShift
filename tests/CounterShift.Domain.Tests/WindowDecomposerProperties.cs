using CounterShift.Domain.Time;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CounterShift.Domain.Tests;

/// <summary>
/// Property-based tests: instead of hand-picking examples, state rules that must hold for <i>every</i>
/// window and let FsCheck search for a counter-example. When one fails, FsCheck "shrinks" it to the
/// smallest failing input, which is usually an edge case nobody would have thought to write by hand.
/// </summary>
public sealed class WindowDecomposerProperties
{
    private static readonly DateTimeOffset Epoch2026 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly GranularitySet Tiers = GranularitySet.Default;

    /// <summary>
    /// Random (now, window) pairs. Half the windows are short (up to 3 h, the 1m tier range); half are
    /// anything up to 7 days.
    /// Bugs live on boundaries, and a uniformly random instant almost never lands exactly on one. So half
    /// the time `now` and the window length are whole minutes, which makes aligned edges common. A first
    /// version without this bias let an off-by-one mutation (`cursor &lt; now`) slip through.
    /// </summary>
    private static Arbitrary<(DateTimeOffset Now, TimeSpan Window)> Windows() =>
        (from nowMinutes in Gen.Choose(0, 364 * 24 * 60)
         from subMinuteMillis in Gen.OneOf(Gen.Constant(0), Gen.Choose(0, 59_999))
         from windowMinutes in Gen.OneOf(Gen.Choose(0, 3 * 60), Gen.Choose(0, 7 * 24 * 60 - 1))
         from windowExtraMillis in Gen.OneOf(Gen.Constant(0), Gen.Choose(1, 59_999))
         let window = TimeSpan.FromMinutes(windowMinutes) + TimeSpan.FromMilliseconds(windowExtraMillis)
         where window > TimeSpan.Zero
         select (Epoch2026.AddMinutes(nowMinutes).AddMilliseconds(subMinuteMillis), window))
        .ToArbitrary();

    [Property(MaxTest = 1000)]
    public Property Buckets_are_contiguous_aligned_and_cover_the_window() =>
        Prop.ForAll(Windows(), input =>
        {
            var d = WindowDecomposer.DecomposeTrailing(input.Window, input.Now, Tiers);
            var b = d.Buckets;

            var startsAtEffectiveFrom = b[0].Start == d.EffectiveFrom;
            var effectiveFromCoversRequest = d.EffectiveFrom <= d.RequestedFrom;
            var contiguous = b.Zip(b.Skip(1)).All(pair => pair.First.End == pair.Second.Start);
            var aligned = b.All(x => x.Granularity.IsAligned(x.Start));
            var lastHoldsNow = b[^1].Start <= input.Now && input.Now < b[^1].End;

            return startsAtEffectiveFrom && effectiveFromCoversRequest && contiguous && aligned && lastHoldsNow;
        });

    [Property(MaxTest = 1000)]
    public Property Rounding_overcounts_by_less_than_one_bucket_of_the_start_tier() =>
        Prop.ForAll(Windows(), input =>
        {
            var d = WindowDecomposer.DecomposeTrailing(input.Window, input.Now, Tiers);
            var startTier = Tiers.FinestRetainedAt(input.Window)!;

            return d.RequestedFrom - d.EffectiveFrom < startTier.Width;
        });

    [Property(MaxTest = 1000)]
    public Property Every_bucket_used_is_still_within_its_tiers_retention() =>
        Prop.ForAll(Windows(), input =>
        {
            var d = WindowDecomposer.DecomposeTrailing(input.Window, input.Now, Tiers);

            // A bucket is kept while it still overlaps the last `Retention` of time.
            return d.Buckets.All(b => b.End > input.Now - b.Granularity.Retention);
        });

    [Property(MaxTest = 300)]
    public Property Greedy_uses_the_minimum_possible_number_of_buckets() =>
        Prop.ForAll(Windows(), input =>
        {
            var d = WindowDecomposer.DecomposeTrailing(input.Window, input.Now, Tiers);

            return d.Buckets.Count == BruteForceMinimum(d.EffectiveFrom, input.Now);
        });

    /// <summary>
    /// The real proof of correctness: scatter random events over the last 8 days, store them the way
    /// the service will (one increment per tier), then check that summing the decomposed buckets equals
    /// counting the raw events directly.
    /// </summary>
    [Property(MaxTest = 300)]
    public Property Summing_the_buckets_equals_counting_the_raw_events() =>
        Prop.ForAll(
            Windows(),
            Gen.ListOf(Gen.Choose(0, 8 * 24 * 3600)).ToArbitrary(),
            (input, eventAgesSeconds) =>
            {
                var events = eventAgesSeconds.Select(age => input.Now.AddSeconds(-age)).ToList();
                var store = new Dictionary<BucketSpan, long>();
                foreach (var t in events)
                {
                    foreach (var tier in Tiers.FinestFirst)
                    {
                        var key = new BucketSpan(tier, tier.AlignDown(t));
                        store[key] = store.GetValueOrDefault(key) + 1;
                    }
                }

                var d = WindowDecomposer.DecomposeTrailing(input.Window, input.Now, Tiers);
                var fromBuckets = d.Buckets.Sum(b => store.GetValueOrDefault(b));
                var direct = events.Count(t => t >= d.EffectiveFrom && t <= input.Now);

                return fromBuckets == direct;
            });

    /// <summary>
    /// Dynamic programming over every possible bucket choice: f(c) = 1 + min over allowed tiers at c of f(c + width).
    /// Slow but obviously correct, which is exactly what you want from a test oracle.
    /// </summary>
    private static int BruteForceMinimum(DateTimeOffset effectiveFrom, DateTimeOffset now)
    {
        var memo = new Dictionary<DateTimeOffset, int>();
        int Best(DateTimeOffset cursor)
        {
            if (cursor > now)
            {
                return 0;
            }
            if (memo.TryGetValue(cursor, out var known))
            {
                return known;
            }
            var best = int.MaxValue;
            foreach (var tier in Tiers.FinestFirst)
            {
                var retained = cursor + tier.Width > now - tier.Retention;
                if (tier.IsAligned(cursor) && retained)
                {
                    best = Math.Min(best, 1 + Best(cursor + tier.Width));
                }
            }
            return memo[cursor] = best;
        }
        return Best(effectiveFrom);
    }
}
