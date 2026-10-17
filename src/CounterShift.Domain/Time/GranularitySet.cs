namespace CounterShift.Domain.Time;

/// <summary>
/// The configured bucket tiers. Each tier must divide the next one exactly (1m | 15m | 1h | 1d).
/// That nesting is what makes greedy decomposition optimal: every boundary of a coarse tier is
/// also a boundary of every finer tier.
/// </summary>
public sealed class GranularitySet
{
    public static GranularitySet Default { get; } = new(Granularity.All);

    public GranularitySet(IEnumerable<Granularity> granularities)
    {
        ArgumentNullException.ThrowIfNull(granularities);
        var ordered = granularities.Distinct().OrderBy(g => g.Width).ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one granularity is required.", nameof(granularities));
        }
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Width.Ticks % ordered[i - 1].Width.Ticks != 0)
            {
                throw new ArgumentException(
                    $"Granularities must nest: {ordered[i]} is not a multiple of {ordered[i - 1]}.", nameof(granularities));
            }
            if (ordered[i].Retention < ordered[i - 1].Retention)
            {
                // Coarser tiers must live at least as long as finer ones. Otherwise a long window could
                // need a coarse bucket that has already been deleted while finer ones still exist.
                throw new ArgumentException(
                    $"{ordered[i]} must be retained at least as long as {ordered[i - 1]}.", nameof(granularities));
            }
        }
        FinestFirst = ordered;
        CoarsestFirst = [.. ordered.Reverse()];
    }

    public IReadOnlyList<Granularity> FinestFirst { get; }
    public IReadOnlyList<Granularity> CoarsestFirst { get; }
    public Granularity Finest => FinestFirst[0];

    /// <summary>The longest window any tier can still answer. Rule windows are validated against it.</summary>
    public TimeSpan MaxRetention => CoarsestFirst[0].Retention;

    /// <summary>
    /// The finest tier that still holds data <paramref name="age"/> ago, or null if none does.
    /// "Retention" is a promise that a tier holds at least that much recent history.
    /// </summary>
    public Granularity? FinestRetainedAt(TimeSpan age) => FinestFirst.FirstOrDefault(g => g.Retention >= age);
}
