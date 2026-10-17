var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness: "is the process alive?" Deliberately checks nothing else (spec §5).
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposes the compiler-generated Program class to WebApplicationFactory in tests.
public partial class Program;
