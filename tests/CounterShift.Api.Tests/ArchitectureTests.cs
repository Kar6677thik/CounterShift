using System.Reflection;

namespace CounterShift.Api.Tests;

/// <summary>
/// Architecture fitness tests (Field Guide §6.16). They inspect the <b>compiled</b> assemblies, so the
/// dependency rules from M4 can't quietly erode: if someone writes <c>using Npgsql;</c> in an endpoint, the
/// API assembly gains a reference to Npgsql and this test fails.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string[] DatabaseDrivers = ["Npgsql", "Dapper", "StackExchange.Redis"];

    private static string[] ReferencesOf(string assemblyName) =>
        [.. Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!)];

    [Fact]
    public void Domain_depends_only_on_the_base_class_library()
    {
        Assert.All(ReferencesOf("CounterShift.Domain"), name =>
            Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard", $"Domain references {name}"));
    }

    [Theory]
    [InlineData("CounterShift.Storage")]
    [InlineData("CounterShift.Api")]
    public void Only_backend_projects_reference_database_drivers(string assemblyName)
    {
        var references = ReferencesOf(assemblyName);

        Assert.DoesNotContain(references, r => DatabaseDrivers.Contains(r));
    }

    [Fact]
    public void Storage_abstraction_does_not_reference_the_api_or_any_backend()
    {
        var references = ReferencesOf("CounterShift.Storage");

        Assert.DoesNotContain(references, r => r.StartsWith("CounterShift.Storage.", StringComparison.Ordinal) || r == "CounterShift.Api");
    }
}
