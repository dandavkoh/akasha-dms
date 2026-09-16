using System.Reflection;

namespace Akasha.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    private const string Domain = "Akasha.Domain";
    private const string Application = "Akasha.Application";
    private const string Infrastructure = "Akasha.Infrastructure";
    private const string Api = "Akasha.Api";

    private static readonly string[] AllLayers = [Domain, Application, Infrastructure, Api];

    private static readonly string[] InfrastructureTechnologyPrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
        "System.Net.Http",
        "System.Web",
        "System.DirectoryServices",
        "Microsoft.Graph",
        "Microsoft.SharePoint",
        "PnP.Core",
        "Dropbox",
        "OpenAI",
        "Azure",
        "AWS",
        "Google",
        "Paddle",
        "Novell"
    ];

    [Fact]
    public void AllLayers_AreResolvable()
    {
        foreach (var layer in AllLayers)
        {
            var assembly = Assembly.Load(new AssemblyName(layer));
            Assert.Equal(layer, assembly.GetName().Name);
        }
    }

    [Fact]
    public void Domain_MustNotReference_OuterLayers()
    {
        AssertNoForbiddenReferences(Domain, [Application, Infrastructure, Api]);
    }

    [Fact]
    public void Domain_MustNotReference_InfrastructureTechnologies()
    {
        AssertNoForbiddenReferences(Domain, InfrastructureTechnologyPrefixes);
    }

    [Fact]
    public void Application_MustNotReference_Infrastructure_Or_Api()
    {
        AssertNoForbiddenReferences(Application, [Infrastructure, Api]);
    }

    [Fact]
    public void Application_MustNotReference_InfrastructureTechnologies()
    {
        AssertNoForbiddenReferences(Application, InfrastructureTechnologyPrefixes);
    }

    [Fact]
    public void Infrastructure_MustNotReference_Api()
    {
        AssertNoForbiddenReferences(Infrastructure, [Api]);
    }

    [Fact]
    public void Api_MustNotReference_PersistenceDirectly()
    {
        AssertNoForbiddenReferences(Api,
        [
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Microsoft.Data.SqlClient",
            "System.Data.SqlClient"
        ]);
    }

    private static void AssertNoForbiddenReferences(string layerName, IReadOnlyList<string> forbiddenPrefixes)
    {
        var referencedAssemblies = Assembly.Load(new AssemblyName(layerName))
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToList();

        var violations = forbiddenPrefixes
            .Where(prefix => referencedAssemblies.Any(referenced => Matches(referenced, prefix)))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"{layerName} must not reference: {string.Join(", ", violations)}. " +
            $"Referenced assemblies: {string.Join(", ", referencedAssemblies)}");
    }

    private static bool Matches(string referencedName, string prefix)
    {
        return string.Equals(referencedName, prefix, StringComparison.Ordinal)
            || referencedName.StartsWith(prefix + ".", StringComparison.Ordinal);
    }
}
