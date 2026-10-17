using CounterShift.Domain;
using CounterShift.Domain.Events;
using CounterShift.Domain.Rules;

namespace CounterShift.Api;

// HTTP request/response shapes (spec §5). They are kept separate from domain types so that the wire
// format and the domain can evolve independently.

/// <summary>Every field is nullable: a missing field should produce a clear 400, not a silent default value.</summary>
public sealed record EventRequest(Guid? EventId, string? Type, DateTimeOffset? OccurredAt, Dictionary<string, string>? Entities)
{
    public FraudEvent ToDomain()
    {
        var missing = new List<ValidationError>();
        if (EventId is null)
        {
            missing.Add(new("eventId", "is required"));
        }
        if (OccurredAt is null)
        {
            missing.Add(new("occurredAt", "is required"));
        }
        if (Entities is null || Entities.Count == 0)
        {
            missing.Add(new("entities", "at least one entity is required"));
        }
        if (missing.Count > 0)
        {
            throw new DomainValidationException(missing);
        }
        return new FraudEvent(EventId!.Value, Type ?? "", OccurredAt!.Value, Entities!);
    }
}

public sealed record EventAccepted(IReadOnlyList<string> CounterKeysUpdated);

public sealed record DecisionRequest(string? Action, Dictionary<string, string>? Entities);

public sealed record EffectiveWindow(DateTimeOffset From, DateTimeOffset To);

public sealed record RuleResultResponse(string Id, string Description, string CounterKey, bool Triggered, long Count, long Threshold, Outcome Outcome, EffectiveWindow EffectiveWindow);

public sealed record DecisionResponse(Outcome Decision, DateTimeOffset EvaluatedAt, IReadOnlyList<RuleResultResponse> Rules)
{
    public static DecisionResponse From(Decision decision) => new(
        decision.Outcome,
        decision.EvaluatedAt,
        [.. decision.Rules.Select(r => new RuleResultResponse(
            r.Rule.Id, r.Rule.Description, r.Key.Value, r.Triggered, r.Count, r.Rule.Threshold, r.Rule.Outcome,
            new EffectiveWindow(r.EffectiveFrom, decision.EvaluatedAt)))]);
}

public sealed record BucketValueResponse(string Granularity, DateTimeOffset Start, long Count);

/// <summary>A debugging and teaching view: shows exactly which buckets were summed.</summary>
public sealed record CounterResponse(string CounterKey, long Count, EffectiveWindow EffectiveWindow, IReadOnlyList<BucketValueResponse> Buckets);
