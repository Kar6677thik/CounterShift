namespace CounterShift.Domain.Time;

/// <summary>
/// A bucket width such as 1 minute or 1 day. Buckets are aligned to UTC multiples of the width,
/// counted from the Unix epoch. The epoch is itself UTC midnight, so day buckets start at 00:00Z.
/// </summary>
public sealed record Granularity
{
    public static readonly Granularity OneMinute = new("1m", TimeSpan.FromMinutes(1), TimeSpan.FromHours(3));
    public static readonly Granularity FifteenMinutes = new("15m", TimeSpan.FromMinutes(15), TimeSpan.FromDays(8));
    public static readonly Granularity OneHour = new("1h", TimeSpan.FromHours(1), TimeSpan.FromDays(8));
    public static readonly Granularity OneDay = new("1d", TimeSpan.FromDays(1), TimeSpan.FromDays(8));

    public static IReadOnlyList<Granularity> All { get; } = [OneMinute, FifteenMinutes, OneHour, OneDay];

    public Granularity(string name, TimeSpan width, TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (width <= TimeSpan.Zero || TimeSpan.FromDays(1).Ticks % width.Ticks != 0)
        {
            // The width must divide a day so that every bucket boundary lines up with UTC midnight.
            throw new ArgumentException($"Width {width} must be positive and divide 24h evenly.", nameof(width));
        }
        if (retention < width)
        {
            throw new ArgumentException("Retention must be at least one bucket wide.", nameof(retention));
        }

        Name = name;
        Width = width;
        Retention = retention;
    }

    public string Name { get; }
    public TimeSpan Width { get; }
    public TimeSpan Retention { get; }

    /// <summary>Width in whole minutes. This compact identifier is what the database stores (1, 15, 60, 1440).</summary>
    public int Minutes => (int)Width.TotalMinutes;

    public static Granularity FromMinutes(int minutes) =>
        All.FirstOrDefault(g => g.Minutes == minutes)
        ?? throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Unknown granularity.");

    public static Granularity FromName(string name) =>
        All.FirstOrDefault(g => g.Name == name)
        ?? throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown granularity.");

    public DateTimeOffset AlignDown(DateTimeOffset instant)
    {
        var utcTicks = instant.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % Width.Ticks), TimeSpan.Zero);
    }

    public bool IsAligned(DateTimeOffset instant) => instant.UtcTicks % Width.Ticks == 0;

    public override string ToString() => Name;
}
