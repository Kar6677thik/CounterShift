using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Rules;
using CounterShift.Domain.Time;

namespace CounterShift.Domain.Tests;

/// <summary>The counters and rules from spec §4.2 and §4.7, built in code for tests.</summary>
internal static class TestConfig
{
    public static readonly DateTimeOffset Noon = new(2026, 10, 17, 12, 7, 30, TimeSpan.Zero);

    public static CounterCatalog Catalog() => new(
    [
        new("payment_failed_by_card", EventTypes.PaymentFailed, EntityFields.CardId),
        new("payment_failed_by_user", EventTypes.PaymentFailed, EntityFields.UserId),
        new("login_failed_by_user", EventTypes.LoginFailed, EntityFields.UserId),
        new("ride_requested_by_user", EventTypes.RideRequested, EntityFields.UserId),
    ], GranularitySet.Default);

    public static RuleBook Book(CounterCatalog? catalog = null) => new(
    [
        new("R1", "Too many failed payments on one card", Set(Actions.Payment), "payment_failed_by_card", TimeSpan.FromMinutes(10), 5, Outcome.Block),
        new("R2", "Repeated failed logins", Set(Actions.Login, Actions.Payment), "login_failed_by_user", TimeSpan.FromMinutes(15), 3, Outcome.Challenge),
        new("R3", "Ride-request burst", Set(Actions.Ride), "ride_requested_by_user", TimeSpan.FromMinutes(90), 8, Outcome.Challenge),
    ], catalog ?? Catalog(), TimeSpan.FromDays(7));

    public static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.Ordinal);

    public static Dictionary<string, string> Entities(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
