namespace PPDS.Cli.Tests.Preservation;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

/// <summary>
/// SQL DML reads an environment's protection only through <c>WriteProtectionResolver</c>, so an
/// environment with no saved type is Production on every surface (#1829). A site that maps the saved
/// type itself (as all four did before, treating "no type" as Development) fails here.
/// </summary>
[Trait("Category", "Unit")]
public class SqlProtectionResolutionPreservationTests
{
    private static readonly string Src = Path.Combine(PathHelpers.RepoRoot(), "src");

    [Theory]
    [InlineData("PPDS.Cli/Services/Query/SqlQueryService.cs", "ResolveForSqlDml(config)")]
    [InlineData("PPDS.Cli/Services/Query/SqlQueryService.cs", "ResolveForCrossEnvironmentTarget(targetConfig)")]
    public void EachSqlSite_ResolvesThroughTheSharedRule(string file, string call)
    {
        var text = File.ReadAllText(Path.Combine(Src, file));

        Assert.Contains("WriteProtectionResolver." + call, text);
    }

    /// <summary>
    /// The CLI, the daemon and the TUI configure the service for the connected environment only through
    /// <c>SqlQueryService.ConfigureEnvironment</c>, which resolves the level with <c>ResolveForSqlDml</c> (above), and
    /// none of them sets the level itself (#1698 merged with #1891).
    /// </summary>
    [Theory]
    [InlineData("PPDS.Cli/Commands/Query/SqlCommand.cs")]
    [InlineData("PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.Query.cs")]
    [InlineData("PPDS.Cli/Tui/InteractiveSession.cs")]
    public void EachSurface_ConfiguresTheConnectedEnvironmentThroughTheService(string file)
    {
        var text = File.ReadAllText(Path.Combine(Src, file));

        Assert.Contains(".ConfigureEnvironment(", text);
        Assert.DoesNotMatch(@"EnvironmentProtectionLevel\s*=(?!=)", text);
    }

    [Fact]
    public void EveryEnvironmentProtectionLevelAssignment_UsesTheResolver()
    {
        var assignment = new Regex(@"EnvironmentProtectionLevel\s*=(?!=)\s*(?<value>[^;]+);");
        var offenders = Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => assignment.Matches(File.ReadAllText(path))
                .Select(m => (path, value: m.Groups["value"].Value.Trim())))
            .Where(a => !a.value.Contains("WriteProtectionResolver.", StringComparison.Ordinal)
                && !a.value.StartsWith("ProtectionLevel.Production", StringComparison.Ordinal))
            .Select(a => $"{Path.GetRelativePath(Src, a.path)}: {a.value}")
            .ToList();

        Assert.True(offenders.Count == 0, "Assignments that bypass WriteProtectionResolver:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void DetectProtectionLevel_IsCalledOnlyByTheResolver()
    {
        var callers = Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllLines(path).Any(line =>
                line.Contains("DetectProtectionLevel(", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(Src, path).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "PPDS.Cli/Services/Environment/WriteProtectionResolver.cs", "PPDS.Cli/Services/Query/DmlSafetyGuard.cs" },
            callers);
    }
}
