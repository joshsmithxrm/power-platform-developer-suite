using System;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace PPDS.Cli.Tests.Architecture;

/// <summary>
/// Verifies critical runtime dependencies are declared as direct references in PPDS.Cli,
/// preventing assembly-not-found errors in single-file publish and .NET tool deployments.
/// </summary>
public class DependencyBundlingTests
{
    [Fact]
    public void Cli_DirectlyReferences_MicrosoftDataSqlClient()
    {
        // Regression guard for #889: without a direct PackageReference the assembly
        // only appears as a transitive dep of PPDS.Dataverse, and the .NET host's
        // probing may skip it in single-file or tool-install layouts.
        var depsPath = Path.Combine(AppContext.BaseDirectory, $"{typeof(DependencyBundlingTests).Assembly.GetName().Name}.deps.json");
        File.Exists(depsPath).Should().BeTrue("deps.json must exist in the test output");

        using var stream = File.OpenRead(depsPath);
        using var doc = JsonDocument.Parse(stream);
        var targets = doc.RootElement.GetProperty("targets");

        bool found = false;
        foreach (var target in targets.EnumerateObject())
        {
            foreach (var lib in target.Value.EnumerateObject())
            {
                if (!lib.Name.StartsWith("PPDS.Cli/", StringComparison.Ordinal))
                    continue;

                found = true;

                lib.Value.TryGetProperty("dependencies", out var deps).Should().BeTrue();
                deps.TryGetProperty("Microsoft.Data.SqlClient", out _).Should().BeTrue(
                    "PPDS.Cli must directly reference Microsoft.Data.SqlClient so the " +
                    "assembly is listed as a first-level dependency in deps.json (#889)");
                break;
            }

            if (found) break;
        }

        found.Should().BeTrue("PPDS.Cli entry must exist in deps.json targets");
    }
}
