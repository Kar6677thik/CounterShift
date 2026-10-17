using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using CounterShift.Api;
using CounterShift.Api.Configuration;
using CounterShift.Api.Database;
using CounterShift.Api.ErrorHandling;
using CounterShift.Domain;
using CounterShift.Domain.Counters;
using CounterShift.Domain.Events;
using CounterShift.Domain.Rules;
using CounterShift.Domain.Time;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// v0 "scattered": the honest first version (spec §9, experiment B2).
// SQL is written straight inside the endpoint handlers below, as first versions usually are. It works,
// it's readable, and it ties every handler to PostgreSQL. M4 measures how far that SQL has spread, then
// moves it behind a storage interface.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<CounterShiftOptions>().Bind(builder.Configuration.GetSection(CounterShiftOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

// Config → validated domain objects, built once. An invalid config throws here, and startup fails (fail fast).
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CounterShiftOptions>>().Value.ToCatalog());
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CounterShiftOptions>>().Value.ToRuleBook(sp.GetRequiredService<CounterCatalog>()));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CounterShiftOptions>>().Value.ToIngestionPolicy());

// One NpgsqlDataSource per app: it owns the connection pool.
builder.Services.AddSingleton(sp =>
{
    var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(cs))
    {
        throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
    }
    return NpgsqlDataSource.Create(cs);
});
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<RetentionCleanupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionCleanupService>());

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase))); // "block", not 2

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Resolve the domain singletons now, so a bad config fails at startup rather than on the first request.
app.Services.GetRequiredService<RuleBook>();
await app.Services.GetRequiredService<MigrationRunner>().ApplyAsync();

// Liveness: is the process alive? It deliberately checks nothing else (spec §5).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Readiness: can we serve traffic, i.e. is the database reachable?
app.MapGet("/readyz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var conn = await db.OpenConnectionAsync(ct);
    await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
    return Results.Ok(new { status = "ready" });
});

// ── Ingest one event ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/v1/events", async (
    EventRequest request, CounterCatalog catalog, IngestionPolicy policy, TimeProvider clock,
    NpgsqlDataSource db, CancellationToken ct) =>
{
    var increments = catalog.PlanIncrements(request.ToDomain(), clock.GetUtcNow(), policy);

    await using var conn = await db.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    foreach (var increment in increments)
    {
        var bucket = new
        {
            key = increment.Bucket.Key.Value,
            granularity = (short)increment.Bucket.Span.Granularity.Minutes,
            start = increment.Bucket.Span.Start.UtcDateTime,
        };

        // Read the current value, add in C#, write it back. It's inside a transaction, so it must be
        // safe... right? (M3 measures what happens under concurrency.)
        var current = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT count FROM counter_buckets WHERE counter_key = @key AND granularity = @granularity AND bucket_start = @start",
            bucket, tx, cancellationToken: ct));

        if (current is null)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO counter_buckets (counter_key, granularity, bucket_start, count) VALUES (@key, @granularity, @start, @count)",
                new { bucket.key, bucket.granularity, bucket.start, count = increment.Delta }, tx, cancellationToken: ct));
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE counter_buckets SET count = @count WHERE counter_key = @key AND granularity = @granularity AND bucket_start = @start",
                new { bucket.key, bucket.granularity, bucket.start, count = current.Value + increment.Delta }, tx, cancellationToken: ct));
        }
    }
    await tx.CommitAsync(ct);

    // 200, not 202: the counts are committed by the time we answer. 202 would promise "accepted, done later".
    return Results.Ok(new EventAccepted([.. increments.Select(i => i.Bucket.Key.Value).Distinct()]));
});

// ── Evaluate fraud rules ─────────────────────────────────────────────────────────────────────────
app.MapPost("/v1/decisions", async (
    DecisionRequest request, RuleBook rules, TimeProvider clock, NpgsqlDataSource db, CancellationToken ct) =>
{
    var plan = rules.Plan(request.Action ?? "", request.Entities ?? [], clock.GetUtcNow());
    var buckets = plan.AllBuckets;

    // One round trip for every bucket of every rule: unnest() turns three parallel arrays into rows to join against.
    await using var conn = await db.OpenConnectionAsync(ct);
    var rows = await conn.QueryAsync<(string Key, short Granularity, DateTime Start, long Count)>(new CommandDefinition(
        """
        SELECT b.counter_key, b.granularity, b.bucket_start, b.count
        FROM counter_buckets b
        JOIN unnest(@keys, @granularities, @starts) AS q(counter_key, granularity, bucket_start)
          ON b.counter_key = q.counter_key AND b.granularity = q.granularity AND b.bucket_start = q.bucket_start
        """,
        new
        {
            keys = buckets.Select(b => b.Key.Value).ToArray(),
            granularities = buckets.Select(b => (short)b.Span.Granularity.Minutes).ToArray(),
            starts = buckets.Select(b => b.Span.Start.UtcDateTime).ToArray(),
        },
        cancellationToken: ct));

    var counts = rows.ToDictionary(
        r => new BucketRef(CounterKey.Parse(r.Key), new BucketSpan(Granularity.FromMinutes(r.Granularity), new DateTimeOffset(r.Start, TimeSpan.Zero))),
        r => r.Count);

    return Results.Ok(DecisionResponse.From(RuleBook.Evaluate(plan, counts)));
});

// ── Inspect one counter (debugging and teaching) ──────────────────────────────────────────────────
app.MapGet("/v1/counters/{counter}/{entity}", async (
    string counter, string entity, string? window, CounterCatalog catalog, IOptions<CounterShiftOptions> options,
    TimeProvider clock, NpgsqlDataSource db, CancellationToken ct) =>
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

    await using var conn = await db.OpenConnectionAsync(ct);
    var rows = await conn.QueryAsync<(short Granularity, DateTime Start, long Count)>(new CommandDefinition(
        """
        SELECT b.granularity, b.bucket_start, b.count
        FROM counter_buckets b
        JOIN unnest(@granularities, @starts) AS q(granularity, bucket_start)
          ON b.granularity = q.granularity AND b.bucket_start = q.bucket_start
        WHERE b.counter_key = @key
        """,
        new
        {
            key = key.Value,
            granularities = decomposition.Buckets.Select(b => (short)b.Granularity.Minutes).ToArray(),
            starts = decomposition.Buckets.Select(b => b.Start.UtcDateTime).ToArray(),
        },
        cancellationToken: ct));
    var found = rows.ToDictionary(r => (Minutes: (int)r.Granularity, Start: new DateTimeOffset(r.Start, TimeSpan.Zero)), r => r.Count);

    var buckets = decomposition.Buckets
        .Select(b => new BucketValueResponse(b.Granularity.Name, b.Start, found.GetValueOrDefault((b.Granularity.Minutes, b.Start))))
        .ToList();
    return Results.Ok(new CounterResponse(key.Value, buckets.Sum(b => b.Count), new EffectiveWindow(decomposition.EffectiveFrom, now), buckets));
});

app.Run();

// Exposes the compiler-generated Program class to WebApplicationFactory in tests.
public partial class Program;
