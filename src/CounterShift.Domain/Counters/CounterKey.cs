using CounterShift.Domain.Time;

namespace CounterShift.Domain.Counters;

/// <summary>
/// One counter for one entity, e.g. <c>payment_failed_by_card:c_42</c>. This is the unit of storage
/// and, later, the unit the traffic split hashes on.
/// </summary>
public readonly record struct CounterKey
{
    private CounterKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>Counter names can't contain ':', so the first ':' always separates name from entity value.</summary>
    public static CounterKey For(string counterName, string entityValue) => new($"{counterName}:{entityValue}");

    public static CounterKey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.Contains(':', StringComparison.Ordinal))
        {
            throw new FormatException($"'{value}' is not a counter key.");
        }
        return new CounterKey(value);
    }

    public override string ToString() => Value;
}

/// <summary>A specific bucket of a specific counter: the address of one stored count.</summary>
public readonly record struct BucketRef(CounterKey Key, BucketSpan Span) : IComparable<BucketRef>
{
    /// <summary>
    /// A total order over buckets. Writers sort by it so concurrent transactions always lock rows in the
    /// same order, which prevents deadlocks (ADR 0006).
    /// </summary>
    public int CompareTo(BucketRef other)
    {
        var byKey = string.CompareOrdinal(Key.Value, other.Key.Value);
        if (byKey != 0)
        {
            return byKey;
        }
        var byGranularity = Span.Granularity.Minutes.CompareTo(other.Span.Granularity.Minutes);
        return byGranularity != 0 ? byGranularity : Span.Start.CompareTo(other.Span.Start);
    }

    public static bool operator <(BucketRef left, BucketRef right) => left.CompareTo(right) < 0;
    public static bool operator >(BucketRef left, BucketRef right) => left.CompareTo(right) > 0;
    public static bool operator <=(BucketRef left, BucketRef right) => left.CompareTo(right) <= 0;
    public static bool operator >=(BucketRef left, BucketRef right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Key}/{Span}";
}

public readonly record struct BucketIncrement(BucketRef Bucket, long Delta);
