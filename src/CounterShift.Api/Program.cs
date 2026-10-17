using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using CounterShift.Api;
using CounterShift.Api.Configuration;
using CounterShift.Api.ErrorHandling;
using CounterShift.Domain;
using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Rules;
using CounterShift.Domain.Time;
using CounterShift.Storage;
using CounterShift.Storage.Postgres;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<CounterShiftOptions>().Bind(builder.Configuration.GetSection(CounterShiftOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

// Config → validated domain objects, built once. An invalid config throws here, and startup fails (fail fast).
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CounterShiftOptions>>().Value.ToCatalog());
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CounterShiftOptions>>().Value.ToRuleBook(sp.GetRequiredService<CounterCatalog>()));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CounterShiftOptions>>().Value.ToIngestionPolicy());

// ── Storage: the composition root ───────────────────────────────────────────────────────────────
// The ONLY place in the API that names a concrete backend. Everything below talks to CounterStore.
builder.Services.AddOptions<PostgresOptions>().Bind(builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.AddPostgresCounterBackend(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"));
builder.Services.AddSingleton<CounterStore>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase))); // "block", not 2

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Resolve the domain singletons now, so a bad config fails at startup rather than on the first request.
app.Services.GetRequiredService<RuleBook>();
foreach (var initializer in app.Services.GetServices<IStorageInitializer>())
{
    await initializer.InitializeAsync(CancellationToken.None); // e.g. schema migrations
}

// Liveness: is the process alive? It deliberately checks nothing else (spec §5).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Readiness: can we serve traffic, i.e. are the stores we depend on reachable?
app.MapGet("/readyz", async (CounterStore store, CancellationToken ct) =>
{
    await store.CheckReadyAsync(ct);
    return Results.Ok(new { status = "ready" });
});

// ── Ingest one event ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/v1/events", async (
    EventRequest request, CounterCatalog catalog, IngestionPolicy policy, TimeProvider clock, CounterStore store, CancellationToken ct) =>
{
    var increments = catalog.PlanIncrements(request.ToDomain(), clock.GetUtcNow(), policy);
    await store.IncrementAsync(increments, ct);

    // 200, not 202: the counts are committed by the time we answer. 202 would promise "accepted, done later".
    return Results.Ok(new EventAccepted([.. increments.Select(i => i.Bucket.Key.Value).Distinct()]));
});

// ── Evaluate fraud rules ─────────────────────────────────────────────────────────────────────────
app.MapPost("/v1/decisions", async (
    DecisionRequest request, RuleBook rules, TimeProvider clock, CounterStore store, CancellationToken ct) =>
{
    var plan = rules.Plan(request.Action ?? "", request.Entities ?? [], clock.GetUtcNow());
    var counts = await store.ReadAsync(plan.AllBuckets, ct); // one batched read for every rule
    return Results.Ok(DecisionResponse.From(RuleBook.Evaluate(plan, counts)));
});

// ── Inspect one counter (debugging and teaching) ──────────────────────────────────────────────────
app.MapGet("/v1/counters/{counter}/{entity}", async (
    string counter, string entity, string? window, CounterCatalog catalog, IOptions<CounterShiftOptions> options,
    TimeProvider clock, CounterStore store, CancellationToken ct) =>
{
    if (catalog.Find(counter) is null)
    {
        throw new DomainValidationException("counter", $"unknown counter '{counter}'");
    }
    if (EntityFields.ValidateValue(entity) is { } problem)
    {
        throw new DomainValidationException("entity", problem);
    }
    TimeSpan length;
    try
    {
        length = XmlConvert.ToTimeSpan(window ?? "PT1H"); // ISO 8601 duration, e.g. PT90M
    }
    catch (FormatException)
    {
        throw new DomainValidationException("window", "must be an ISO 8601 duration such as PT90M");
    }
    if (length <= TimeSpan.Zero || length > options.Value.MaxWindow)
    {
        throw new DomainValidationException("window", $"must be within (0, {options.Value.MaxWindow}]");
    }

    var now = clock.GetUtcNow();
    var key = CounterKey.For(counter, entity);
    var decomposition = WindowDecomposer.DecomposeTrailing(length, now, catalog.Tiers);
    var refs = decomposition.Buckets.Select(span => new BucketRef(key, span)).ToList();
    var found = await store.ReadAsync(refs, ct);

    var buckets = refs
        .Select(r => new BucketValueResponse(r.Span.Granularity.Name, r.Span.Start, found.GetValueOrDefault(r)))
        .ToList();
    return Results.Ok(new CounterResponse(key.Value, buckets.Sum(b => b.Count), new EffectiveWindow(decomposition.EffectiveFrom, now), buckets));
});

app.Run();

// Exposes the compiler-generated Program class to WebApplicationFactory in tests.
public partial class Program;
