using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Time;
using static CounterShift.Domain.Tests.TestConfig;

namespace CounterShift.Domain.Tests;

public sealed class CounterCatalogTests
{
    private static FraudEvent PaymentFailed(DateTimeOffset at, string card = "c_42", string user = "u_7") =>
        new(Guid.NewGuid(), EventTypes.PaymentFailed, at, Entities((EntityFields.CardId, card), (EntityFields.UserId, user)));

    [Fact]
    public void One_event_fans_out_to_every_matching_counter_times_every_tier()
    {
        var increments = Catalog().PlanIncrements(PaymentFailed(Noon), Noon, IngestionPolicy.Default);

        // payment.failed feeds 2 counters (by card, by user) × 4 tiers = 8 increments: the write amplification.
        Assert.Equal(8, increments.Count);
        Assert.All(increments, i => Assert.Equal(1, i.Delta));
        Assert.Contains(increments, i => i.Bucket == new BucketRef(
            CounterKey.For("payment_failed_by_card", "c_42"),
            new BucketSpan(Granularity.FifteenMinutes, new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Buckets_by_event_time_not_arrival_time()
    {
        var threeMinutesAgo = Noon.AddMinutes(-3);

        var increments = Catalog().PlanIncrements(PaymentFailed(threeMinutesAgo), Noon, IngestionPolicy.Default);

        var minuteBucket = increments.Single(i => i.Bucket.Span.Granularity == Granularity.OneMinute && i.Bucket.Key.Value.StartsWith("payment_failed_by_card", StringComparison.Ordinal));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 12, 4, 0, TimeSpan.Zero), minuteBucket.Bucket.Span.Start);
    }

    [Theory]
    [InlineData(-301, "in the past")]    // 5 min 1 s late
    [InlineData(31, "in the future")]    // 31 s ahead of our clock
    public void Late_and_future_events_are_unprocessable(int offsetSeconds, string expectedMessage)
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            Catalog().PlanIncrements(PaymentFailed(Noon.AddSeconds(offsetSeconds)), Noon, IngestionPolicy.Default));

        var error = Assert.Single(ex.Errors);
        Assert.Equal(ValidationErrorKind.Unprocessable, error.Kind);
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-300)] // exactly at the lateness limit
    [InlineData(30)]   // exactly at the skew limit
    public void Limits_are_inclusive(int offsetSeconds)
    {
        var increments = Catalog().PlanIncrements(PaymentFailed(Noon.AddSeconds(offsetSeconds)), Noon, IngestionPolicy.Default);

        Assert.NotEmpty(increments);
    }

    [Fact]
    public void Missing_required_entity_is_rejected_not_silently_skipped()
    {
        var noCard = new FraudEvent(Guid.NewGuid(), EventTypes.PaymentFailed, Noon, Entities((EntityFields.UserId, "u_7")));

        var ex = Assert.Throws<DomainValidationException>(() => Catalog().PlanIncrements(noCard, Noon, IngestionPolicy.Default));

        Assert.Equal("entities.cardId", Assert.Single(ex.Errors).Field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("tab\tchar")]
    public void Bad_entity_values_are_rejected(string card)
    {
        var ex = Assert.Throws<DomainValidationException>(() => Catalog().PlanIncrements(PaymentFailed(Noon, card), Noon, IngestionPolicy.Default));

        Assert.Equal(ValidationErrorKind.Malformed, Assert.Single(ex.Errors).Kind);
    }

    [Fact]
    public void Unknown_event_type_is_rejected()
    {
        var evt = new FraudEvent(Guid.NewGuid(), "coffee.spilled", Noon, Entities((EntityFields.UserId, "u_7")));

        var ex = Assert.Throws<DomainValidationException>(() => Catalog().PlanIncrements(evt, Noon, IngestionPolicy.Default));

        Assert.Equal("type", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public void Configuration_errors_are_all_reported_at_once()
    {
        var ex = Assert.Throws<DomainValidationException>(() => new CounterCatalog(
        [
            new("Bad Name", EventTypes.PaymentFailed, EntityFields.CardId),
            new("dup", "nope", EntityFields.CardId),
            new("dup", EventTypes.LoginFailed, "shoeSize"),
        ], GranularitySet.Default));

        // Fail fast, but report everything: fixing config one error per restart is miserable.
        Assert.Equal(4, ex.Errors.Count);
    }
}
