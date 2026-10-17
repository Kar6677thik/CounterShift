using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Rules;
using static CounterShift.Domain.Tests.TestConfig;

namespace CounterShift.Domain.Tests;

public sealed class RuleBookTests
{
    private static readonly Dictionary<string, string> CardAndUser =
        Entities((EntityFields.CardId, "c_42"), (EntityFields.UserId, "u_7"));

    /// <summary>Puts <paramref name="count"/> into the first bucket of the query for <paramref name="ruleId"/>.</summary>
    private static Dictionary<BucketRef, long> Counts(DecisionPlan plan, params (string RuleId, int Count)[] values)
    {
        var counts = new Dictionary<BucketRef, long>();
        foreach (var (ruleId, count) in values)
        {
            var query = plan.Queries.Single(q => q.Rule.Id == ruleId);
            counts[new BucketRef(query.Key, query.Decomposition.Buckets[0])] = count;
        }
        return counts;
    }

    [Fact]
    public void Payment_action_evaluates_the_rules_that_apply_to_it()
    {
        var plan = Book().Plan(Actions.Payment, CardAndUser, Noon);

        Assert.Equal(["R1", "R2"], plan.Queries.Select(q => q.Rule.Id));
        Assert.Equal("payment_failed_by_card:c_42", plan.Queries[0].Key.Value);
    }

    [Fact]
    public void Threshold_is_strictly_greater_than()
    {
        var plan = Book().Plan(Actions.Payment, CardAndUser, Noon);

        Assert.Equal(Outcome.Allow, RuleBook.Evaluate(plan, Counts(plan, ("R1", 5))).Outcome);  // exactly 5: not "more than 5"
        Assert.Equal(Outcome.Block, RuleBook.Evaluate(plan, Counts(plan, ("R1", 6))).Outcome);
    }

    [Fact]
    public void Most_severe_triggered_outcome_wins()
    {
        var plan = Book().Plan(Actions.Payment, CardAndUser, Noon);

        var decision = RuleBook.Evaluate(plan, Counts(plan, ("R1", 9), ("R2", 4)));

        Assert.Equal(Outcome.Block, decision.Outcome);
        Assert.All(decision.Rules, r => Assert.True(r.Triggered));
    }

    [Fact]
    public void No_data_means_allow()
    {
        var plan = Book().Plan(Actions.Payment, CardAndUser, Noon);

        var decision = RuleBook.Evaluate(plan, new Dictionary<BucketRef, long>());

        Assert.Equal(Outcome.Allow, decision.Outcome);
        Assert.All(decision.Rules, r => Assert.Equal(0, r.Count));
    }

    [Fact]
    public void Counts_are_summed_across_all_buckets_of_the_window()
    {
        var plan = Book().Plan(Actions.Ride, Entities((EntityFields.UserId, "u_7")), Noon);
        var query = Assert.Single(plan.Queries);
        var counts = query.Decomposition.Buckets.ToDictionary(span => new BucketRef(query.Key, span), _ => 1L);

        var decision = RuleBook.Evaluate(plan, counts);

        Assert.Equal(query.Decomposition.Buckets.Count, decision.Rules[0].Count);
        Assert.True(decision.Rules[0].Count > 8);
        Assert.Equal(Outcome.Challenge, decision.Outcome);
    }

    [Fact]
    public void All_buckets_for_a_decision_are_collected_for_one_batched_read()
    {
        var plan = Book().Plan(Actions.Payment, CardAndUser, Noon);

        var expected = plan.Queries.Sum(q => q.Decomposition.Buckets.Count);
        Assert.Equal(expected, plan.AllBuckets.Count);
    }

    [Fact]
    public void Missing_entity_for_an_applicable_rule_is_a_400_not_a_silent_skip()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Book().Plan(Actions.Payment, Entities((EntityFields.UserId, "u_7")), Noon));

        Assert.Equal("entities.cardId", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public void Unknown_action_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => Book().Plan("teleport", CardAndUser, Noon));
    }

    [Fact]
    public void Rule_configuration_is_validated()
    {
        var ex = Assert.Throws<DomainValidationException>(() => new RuleBook(
        [
            new("X1", "unknown counter", Set(Actions.Payment), "nope", TimeSpan.FromMinutes(5), 1, Outcome.Block),
            new("X2", "window too long", Set(Actions.Payment), "payment_failed_by_card", TimeSpan.FromDays(8), 1, Outcome.Block),
            new("X3", "pointless allow rule", Set(Actions.Payment), "payment_failed_by_card", TimeSpan.FromMinutes(5), 1, Outcome.Allow),
        ], Catalog(), TimeSpan.FromDays(7)));

        Assert.Equal(3, ex.Errors.Count);
    }
}
