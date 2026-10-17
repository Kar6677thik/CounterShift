namespace CounterShift.Domain.Time;

/// <summary>One aligned time slot at one granularity: [Start, Start + Width).</summary>
public readonly record struct BucketSpan(Granularity Granularity, DateTimeOffset Start)
{
    public DateTimeOffset End => Start + Granularity.Width;

    public override string ToString() => $"{Granularity}@{Start:yyyy-MM-ddTHH:mm}Z";
}
