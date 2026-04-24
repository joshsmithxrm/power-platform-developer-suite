namespace PPDS.Cli.Tests.Preservation;

using System;
using System.IO;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

public class ChangelogTests
{
    private const string UnreleasedHeader = "## [Unreleased]";
    private const string ReleasedHeader = "## [1.0.0]";

    [Fact]
    public void Dataverse_Changelog_DocumentsRelocatedServicesInReleasedSection()
    {
        var path = Path.Combine(PathHelpers.RepoRoot(), "src", "PPDS.Dataverse", "CHANGELOG.md");
        Assert.True(File.Exists(path), $"CHANGELOG not found: {path}");
        var changelog = File.ReadAllText(path);

        Assert.Contains(UnreleasedHeader, changelog);
        Assert.Contains(ReleasedHeader, changelog);

        var released = ExtractSection(changelog, ReleasedHeader);

        var interfaces = new[]
        {
            "IPluginTraceService", "IWebResourceService", "IMetadataAuthoringService",
            "IFlowService", "IConnectionReferenceService", "IDeploymentSettingsService"
        };
        foreach (var iface in interfaces)
        {
            Assert.Contains(iface, released);
        }
    }

    private static string ExtractSection(string changelog, string header)
    {
        var start = changelog.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Header not found: {header}");
        var afterHeader = start + header.Length;
        var nextH2 = changelog.IndexOf("\n## ", afterHeader, StringComparison.Ordinal);
        return nextH2 < 0 ? changelog[afterHeader..] : changelog[afterHeader..nextH2];
    }
}
