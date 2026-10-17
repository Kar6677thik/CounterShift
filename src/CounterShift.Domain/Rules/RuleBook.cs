using CounterShift.Domain.Counters;
using CounterShift.Domain.Time;

namespace CounterShift.Domain.Rules;

/// <summary>Ordered by severity, so the most severe outcome is simply the maximum (spec §4.7).</summary>
public enum Outcome
{
    Allow = 0,
    Challenge = 1,
    Block = 2,
}

public static class Actions
{
    public const string Login = "login";
    public const string Payment = "payment";
    public const string Ride = "ride";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal) { Login, Payment, Ride };
}

/// <summary>"If <see cref="CounterName"/> over the last <see cref="Window"/> is greater than <see cref="Threshold"/>, then <see cref="Outcome"/>."</summary>
public sealed record RuleDefinition(
    string Id,
    string Description,
    IReadOnlySet<string> AppliesTo,
    string CounterName,
    TimeSpan Window,
    long Threshold,
    Outcome Outcome);

/// <summary>One counter lookup a rule needs, already decomposed into buckets.</summary>
public sealed record RuleQuery(RuleDefinition Rule, CounterKey Key, Decomposition Decomposition);

/// <summary>
/// Everything needed to answer a decision request. All the buckets are read in one batch, then
/// <see cref="RuleBook.Evaluate"/> runs with no I/O.
/// </summary>
public sealed record DecisionPlan(string Action, DateTimeOffset Now, IReadOnlyList<RuleQuery> Queries)
{
    public IReadOnlyList<BucketRef> AllBuckets { get; } =
        Queries.SelectMany(q => q.Decomposition.Buckets.Select(span => new BucketRef(q.Key, span))).Distinct().ToList();
}

public sealed record RuleResult(RuleDefinition Rule, CounterKey Key, long Count, bool Triggered, DateTimeOffset EffectiveFrom);

public sealed record Decision(Outcome Outcome, DateTimeOffset EvaluatedAt, IReadOnlyList<RuleResult> Rules);

/// <summary>
/// The configured fraud rules. This is split into Plan (decide what to read) and Evaluate (pure
/// arithmetic) so the storage call sits between them, outside the domain.
/// </summary>
public sealed class RuleBook
{
    private readonly IReadOnlyList<RuleDefinition> _rules;
    private readonly CounterCatalog _catalog;

    public RuleBook(IEnumerable<RuleDefinition> rules, CounterCatalog catalog, TimeSpan maxWindow)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(catalog);
        _rules = rules.ToList();
        _catalog = catalog;

        var errors = new List<ValidationError>();
        if (maxWindow > catalog.Tiers.MaxRetention)
        {
            errors.Add(new("MaxWindow", $"{maxWindow} exceeds the longest tier retention {catalog.Tiers.MaxRetention}"));
        }
        foreach (var (rule, i) in _rules.Select((r, i) => (r, i)))
        {
            var field = $"Rules[{i}] ({rule.Id})";
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                errors.Add(new(field, "id is required"));
            }
            if (catalog.Find(rule.CounterName) is null)
            {
                errors.Add(new(field, $"unknown counter '{rule.CounterName}'"));
            }
            if (rule.Window <= TimeSpan.Zero || rule.Window > maxWindow)
            {
                errors.Add(new(field, $"window must be within (0, {maxWindow}]"));
            }
            if (rule.Threshold < 0)
            {
                errors.Add(new(field, "threshold must be >= 0"));
            }
            if (rule.AppliesTo is null || rule.AppliesTo.Count == 0 || rule.AppliesTo.Any(a => !Actions.All.Contains(a)))
            {
                errors.Add(new(field, $"appliesTo must be a non-empty subset of {string.Join(", ", Actions.All)}"));
            }
            if (rule.Outcome == Outcome.Allow)
            {
                errors.Add(new(field, "a rule whose outcome is 'allow' can never change a decision"));
            }
        }
        foreach (var duplicate in _rules.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add(new("Rules", $"duplicate rule id '{duplicate.Key}'"));
        }
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }
    }

    public IReadOnlyList<RuleDefinition> Rules => _rules;

    /// <exception cref="DomainValidationException">Unknown action, or an entity a rule needs is missing.</exception>
    public DecisionPlan Plan(string action, IReadOnlyDictionary<string, string> entities, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entities);
        action ??= "";
        if (!Actions.All.Contains(action))
        {
            throw new DomainValidationException("action", $"unknown action '{action}'; expected one of {string.Join(", ", Actions.All)}");
        }

        var errors = new List<ValidationError>();
        var queries = new List<RuleQuery>();
        foreach (var rule in _rules.Where(r => r.AppliesTo.Contains(action)))
        {
            var counter = _catalog.Find(rule.CounterName)!; // validated in the constructor
            if (!entities.TryGetValue(counter.Entity, out var entityValue))
            {
                // Explicit 400 rather than silently skipping a fraud rule.
                errors.Add(new($"entities.{counter.Entity}", $"is required for action '{action}' (rule {rule.Id})"));
                continue;
            }
            if (Events.EntityFields.ValidateValue(entityValue) is { } problem)
            {
                errors.Add(new($"entities.{counter.Entity}", problem));
                continue;
            }
            var decomposition = WindowDecomposer.DecomposeTrailing(rule.Window, now, _catalog.Tiers);
            queries.Add(new RuleQuery(rule, CounterKey.For(counter.Name, entityValue), decomposition));
        }
        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors.DistinctBy(e => e.Field).ToList());
        }
        return new DecisionPlan(action, now, queries);
    }

    /// <summary>Pure: sums each rule's buckets and picks the most severe triggered outcome. Missing buckets count as 0.</summary>
    public static Decision Evaluate(DecisionPlan plan, IReadOnlyDictionary<BucketRef, long> counts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(counts);

        var results = plan.Queries.Select(q =>
        {
            var count = q.Decomposition.Buckets.Sum(span => counts.GetValueOrDefault(new BucketRef(q.Key, span)));
            return new RuleResult(q.Rule, q.Key, count, count > q.Rule.Threshold, q.Decomposition.EffectiveFrom);
        }).ToList();

        var outcome = results.Where(r => r.Triggered).Select(r => r.Rule.Outcome).DefaultIfEmpty(Outcome.Allow).Max();
        return new Decision(outcome, plan.Now, results);
    }
}
