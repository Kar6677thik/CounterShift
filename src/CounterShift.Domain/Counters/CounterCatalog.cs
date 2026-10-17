using System.Text.RegularExpressions;
using CounterShift.Domain.Events;
using CounterShift.Domain.Time;

namespace CounterShift.Domain.Counters;

/// <summary>A counter is defined by configuration (spec §4.2): which event type it counts, per which entity.</summary>
public sealed record CounterDefinition(string Name, string EventType, string Entity);

/// <summary>
/// The configured counters, validated once at startup (fail fast). Turns an event into the bucket
/// increments it causes.
/// </summary>
public sealed partial class CounterCatalog
{
    private readonly Dictionary<string, CounterDefinition> _byName;
    private readonly ILookup<string, CounterDefinition> _byEventType;

    public CounterCatalog(IEnumerable<CounterDefinition> counters, GranularitySet tiers)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(tiers);
        var list = counters.ToList();
        var errors = new List<ValidationError>();

        foreach (var (counter, i) in list.Select((c, i) => (c, i)))
        {
            var field = $"Counters[{i}]";
            if (counter.Name is null || !NamePattern().IsMatch(counter.Name))
            {
                errors.Add(new(field, $"name '{counter.Name}' must match {NamePattern()}"));
            }
            if (!EventTypes.All.Contains(counter.EventType))
            {
                errors.Add(new(field, $"unknown event type '{counter.EventType}'"));
            }
            if (!EntityFields.All.Contains(counter.Entity))
            {
                errors.Add(new(field, $"unknown entity '{counter.Entity}'"));
            }
        }
        foreach (var duplicate in list.GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add(new("Counters", $"duplicate counter name '{duplicate.Key}'"));
        }
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        _byName = list.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _byEventType = list.ToLookup(c => c.EventType, StringComparer.Ordinal);
        Tiers = tiers;
    }

    public GranularitySet Tiers { get; }

    public IEnumerable<CounterDefinition> All => _byName.Values;

    public CounterDefinition? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Validates an event and returns every bucket increment it causes:
    /// (matching counters) × (granularity tiers). One payment.failed event with two counters and four
    /// tiers means 8 increments. That write amplification is what makes reads cheap.
    /// </summary>
    /// <exception cref="DomainValidationException">The event is malformed, too late or too far in the future.</exception>
    public IReadOnlyList<BucketIncrement> PlanIncrements(FraudEvent evt, DateTimeOffset now, IngestionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(policy);

        var errors = Validate(evt, now, policy);
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        var increments = new List<BucketIncrement>();
        foreach (var counter in _byEventType[evt.Type])
        {
            var key = CounterKey.For(counter.Name, evt.Entities[counter.Entity]);
            foreach (var tier in Tiers.FinestFirst)
            {
                var span = new BucketSpan(tier, tier.AlignDown(evt.OccurredAt));
                increments.Add(new BucketIncrement(new BucketRef(key, span), 1));
            }
        }
        return increments;
    }

    private List<ValidationError> Validate(FraudEvent evt, DateTimeOffset now, IngestionPolicy policy)
    {
        var errors = new List<ValidationError>();
        if (evt.EventId == Guid.Empty)
        {
            errors.Add(new("eventId", "is required"));
        }
        // JSON deserialisation can still hand us nulls despite the non-nullable declarations.
        var type = evt.Type ?? "";
        if (!EventTypes.All.Contains(type))
        {
            errors.Add(new("type", $"unknown event type '{type}'; expected one of {string.Join(", ", EventTypes.All)}"));
            return errors; // no point checking entities for a type we don't know
        }

        foreach (var (name, value) in evt.Entities ?? new Dictionary<string, string>())
        {
            if (!EntityFields.All.Contains(name))
            {
                errors.Add(new($"entities.{name}", "unknown entity field"));
            }
            else if (EntityFields.ValidateValue(value) is { } problem)
            {
                errors.Add(new($"entities.{name}", problem));
            }
        }
        // Least astonishment: a payment.failed event without a cardId would silently skip the card
        // counter, so we reject it instead.
        foreach (var required in _byEventType[type].Select(c => c.Entity).Distinct())
        {
            if (evt.Entities is null || !evt.Entities.ContainsKey(required))
            {
                errors.Add(new($"entities.{required}", $"is required for {type}"));
            }
        }

        if (evt.OccurredAt < now - policy.MaxLateness)
        {
            errors.Add(new("occurredAt", $"is more than {policy.MaxLateness} in the past", ValidationErrorKind.Unprocessable));
        }
        else if (evt.OccurredAt > now + policy.MaxClockSkew)
        {
            errors.Add(new("occurredAt", $"is more than {policy.MaxClockSkew} in the future", ValidationErrorKind.Unprocessable));
        }
        return errors;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();
}
