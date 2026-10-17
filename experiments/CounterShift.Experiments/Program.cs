using System.Diagnostics;
using System.Globalization;
using CounterShift.Experiments;
using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Break-step experiments (spec §9).
//
//   dotnet run -c Release --project experiments/CounterShift.Experiments -- lost-updates [--writers 50] [--increments 1000]
//   dotnet run -c Release --project experiments/CounterShift.Experiments -- deadlocks    [--writers 50] [--statements 200] [--rows 20]
//   dotnet run -c Release --project experiments/CounterShift.Experiments -- hash-stability
//   add  --connection "Host=...;..."  to use an existing database instead of a throwaway container.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

var experiment = args.FirstOrDefault() ?? "lost-updates";
if (experiment == "hash-print")
{
    HashStability.Print(); // child process for the hash experiment: no database needed
    return 0;
}
if (experiment == "hash-stability")
{
    await HashStability.RunAsync(pods: 3);
    return 0;
}
int Arg(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
}
var connectionIndex = Array.IndexOf(args, "--connection");

PostgreSqlContainer? container = null;
string connectionString;
if (connectionIndex >= 0)
{
    connectionString = args[connectionIndex + 1];
}
else
{
    Console.WriteLine("Starting a throwaway PostgreSQL 17 container...");
    container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    await container.StartAsync();
    connectionString = container.GetConnectionString();
}

try
{
    // Enough pooled connections for every writer to have its own: real concurrency, no pool queueing.
    await using var dataSource = NpgsqlDataSource.Create(connectionString + ";Maximum Pool Size=200");
    await CreateSchemaAsync(dataSource);
    PrintMachine();

    switch (experiment)
    {
        case "lost-updates":
            await LostUpdates.RunAsync(dataSource, Arg("--writers", 50), Arg("--increments", 1000));
            break;
        case "deadlocks":
            await Deadlocks.RunAsync(dataSource, Arg("--writers", 50), Arg("--statements", 200), Arg("--rows", 20));
            break;
        default:
            Console.Error.WriteLine($"Unknown experiment '{experiment}'. Use lost-updates or deadlocks.");
            return 1;
    }
    return 0;
}
finally
{
    if (container is not null)
    {
        await container.DisposeAsync();
    }
}

static async Task CreateSchemaAsync(NpgsqlDataSource dataSource)
{
    await using var conn = await dataSource.OpenConnectionAsync();
    // The same table shape as migration 0001.
    await conn.ExecuteAsync("""
        CREATE TABLE IF NOT EXISTS counter_buckets (
            counter_key text NOT NULL, granularity smallint NOT NULL, bucket_start timestamptz NOT NULL,
            count bigint NOT NULL CHECK (count >= 0),
            PRIMARY KEY (counter_key, granularity, bucket_start))
        """);
}

static void PrintMachine() => Console.WriteLine(
    $"Machine: {Environment.MachineName}, {Environment.ProcessorCount} logical CPUs, {Environment.OSVersion}, .NET {Environment.Version}\n");

/// <summary>Formatting helpers shared by the experiments.</summary>
internal static class Report
{
    public static string Rate(long ops, TimeSpan elapsed) =>
        (ops / elapsed.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture) + "/s";

    public static Stopwatch Start() => Stopwatch.StartNew();
}
