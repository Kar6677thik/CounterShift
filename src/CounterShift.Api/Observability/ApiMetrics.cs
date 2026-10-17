using System.Diagnostics.Metrics;
using CounterShift.Domain.Rules;

namespace CounterShift.Api.Observability;

/// <summary>
/// Business metrics: what the service is <i>doing</i>, not just how fast. If a migration silently changes
/// answers, the decision mix (allow / challenge / block) shifts, and this is where it shows.
/// </summary>
public sealed class ApiMetrics
{
    public const string MeterName = "CounterShift.Api";

    private readonly Counter<long> _events;
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _ruleTriggers;

    public ApiMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _events = meter.CreateCounter<long>("cs.events.ingested", description: "Events accepted, by type.");
        _decisions = meter.CreateCounter<long>("cs.decisions", description: "Decisions returned, by outcome.");
        _ruleTriggers = meter.CreateCounter<long>("cs.rules.triggered", description: "Rules that fired, by rule id.");
    }

    public void EventIngested(string type) => _events.Add(1, new KeyValuePair<string, object?>("type", type));

    public void Decided(Decision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _decisions.Add(1, new KeyValuePair<string, object?>("outcome", decision.Outcome.ToString().ToLowerInvariant()));
        foreach (var rule in decision.Rules.Where(r => r.Triggered))
        {
            _ruleTriggers.Add(1, new KeyValuePair<string, object?>("rule", rule.Rule.Id));
        }
    }
}
