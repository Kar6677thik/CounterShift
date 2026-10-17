using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Rules;
using CounterShift.Domain.Time;

namespace CounterShift.Api.Configuration;

/// <summary>
/// The "CounterShift" section of appsettings.json, bound by the configuration system. These are plain,
/// mutable binding classes. They are turned into validated, immutable domain objects once, at startup.
/// </summary>
public sealed class CounterShiftOptions
{
    public const string SectionName = "CounterShift";

    public IngestionOptions Ingestion { get; set; } = new();
    public TimeSpan MaxWindow { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan RetentionCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
    public List<CounterOptions> Counters { get; set; } = [];
    public List<RuleOptions> Rules { get; set; } = [];

    public sealed class IngestionOptions
    {
        public TimeSpan MaxLateness { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class CounterOptions
    {
        public string Name { get; set; } = "";
        public string EventType { get; set; } = "";
        public string Entity { get; set; } = "";
    }

    public sealed class RuleOptions
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> AppliesTo { get; set; } = [];
        public string Counter { get; set; } = "";
        public TimeSpan Window { get; set; }
        public long Threshold { get; set; }
        public Outcome Outcome { get; set; }
    }

    public IngestionPolicy ToIngestionPolicy() => new(Ingestion.MaxLateness, Ingestion.MaxClockSkew);

    /// <exception cref="Domain.DomainValidationException">Configuration is invalid. Startup fails, listing every problem.</exception>
    public CounterCatalog ToCatalog() =>
        new(Counters.Select(c => new CounterDefinition(c.Name, c.EventType, c.Entity)), GranularitySet.Default);

    public RuleBook ToRuleBook(CounterCatalog catalog) =>
        new(Rules.Select(r => new RuleDefinition(
                r.Id, r.Description, r.AppliesTo.ToHashSet(StringComparer.Ordinal), r.Counter, r.Window, r.Threshold, r.Outcome)),
            catalog,
            MaxWindow);
}
