namespace CounterShift.Domain.Time;

/// <summary>
/// The buckets that answer "how many events in [EffectiveFrom, Now]?".
/// EffectiveFrom is the requested start rounded down to the finest tier. It is returned so that
/// callers always see exactly what was counted (spec §4.3: rounding is never hidden).
/// </summary>
public sealed record Decomposition(
    DateTimeOffset RequestedFrom,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset Now,
    IReadOnlyList<BucketSpan> Buckets);

/// <summary>
/// Splits a trailing window ("the last N minutes up to now") into the fewest aligned buckets.
/// </summary>
/// <remarks>
/// Two facts make this simple (ADR 0002):
/// <list type="number">
/// <item>The <b>start</b> edge is rounded <i>down</i> to the finest tier that is still retained that far
/// back. A 10-minute window starts on a 1-minute boundary; a 7-day window starts on a 15-minute boundary,
/// because 1-minute buckets are only kept for 3 hours. Rounding down over-counts by less than one bucket
/// of that tier, and over-counting is the safe direction for fraud.</item>
/// <item>The <b>end</b> is always "now", and no stored event is later than now (apart from the allowed
/// clock skew). So a bucket that starts inside the window but ends in the future is still exact,
/// because its future part is empty. We never need to check the right edge.</item>
/// </list>
/// Greedy rule: at each cursor, take the coarsest tier the cursor is aligned to, then move past it.
/// Because tiers nest, this is minimal. The property tests check it against brute force.
/// </remarks>
public static class WindowDecomposer
{
    public static Decomposition DecomposeTrailing(TimeSpan window, DateTimeOffset now, GranularitySet tiers)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "Window must be positive.");
        }
        return Decompose(now - window, now, tiers);
    }

    public static Decomposition Decompose(DateTimeOffset from, DateTimeOffset now, GranularitySet tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (from >= now)
        {
            throw new ArgumentOutOfRangeException(nameof(from), from, "Window start must be before now.");
        }

        var startTier = tiers.FinestRetainedAt(now - from)
            ?? throw new ArgumentOutOfRangeException(nameof(from), from, "Window starts before every tier's retention.");
        var effectiveFrom = startTier.AlignDown(from);
        var buckets = new List<BucketSpan>();
        var cursor = effectiveFrom;

        // "<=" rather than "<": an event stamped exactly at `now` belongs to the bucket starting at `now`.
        while (cursor <= now)
        {
            var tier = CoarsestAlignedAt(cursor, tiers);
            buckets.Add(new BucketSpan(tier, cursor));
            cursor += tier.Width;
        }

        return new Decomposition(from, effectiveFrom, now, buckets);
    }

    private static Granularity CoarsestAlignedAt(DateTimeOffset cursor, GranularitySet tiers)
    {
        foreach (var tier in tiers.CoarsestFirst)
        {
            if (tier.IsAligned(cursor))
            {
                return tier;
            }
        }
        // Unreachable: the cursor starts aligned to the start tier and only ever moves by whole widths
        // of that tier or coarser ones, each a multiple of the start tier's width.
        throw new InvalidOperationException($"Cursor {cursor:O} is not aligned to any tier.");
    }
}
