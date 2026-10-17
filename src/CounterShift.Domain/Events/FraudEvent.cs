namespace CounterShift.Domain.Events;

/// <summary>The vocabulary the service understands. Fixed in code; counters and rules refer to it by name.</summary>
public static class EventTypes
{
    public const string LoginFailed = "login.failed";
    public const string PaymentFailed = "payment.failed";
    public const string RideRequested = "ride.requested";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { LoginFailed, PaymentFailed, RideRequested };
}

/// <summary>The kinds of thing we count per: a user, a card, a device.</summary>
public static class EntityFields
{
    public const string UserId = "userId";
    public const string CardId = "cardId";
    public const string DeviceId = "deviceId";

    public const int MaxValueLength = 128;

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { UserId, CardId, DeviceId };

    /// <summary>Validates one entity value; returns an error message or null.</summary>
    public static string? ValidateValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "must not be empty";
        }
        if (value.Length > MaxValueLength)
        {
            return $"must be at most {MaxValueLength} characters";
        }
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "must not contain whitespace or control characters";
        }
        return null;
    }
}

/// <summary>Something that happened. Bucketed by <see cref="OccurredAt"/> (event time), not by arrival time.</summary>
public sealed record FraudEvent(
    Guid EventId,
    string Type,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Entities);

/// <summary>How far an event's clock may disagree with ours. See ADR 0003.</summary>
public sealed record IngestionPolicy(TimeSpan MaxLateness, TimeSpan MaxClockSkew)
{
    public static IngestionPolicy Default { get; } = new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
}
