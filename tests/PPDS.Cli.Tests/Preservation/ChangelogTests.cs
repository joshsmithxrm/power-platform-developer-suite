namespace PPDS.Cli.Tests.Preservation;

using System;
using System.IO;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

public class ChangelogTests
{
    private const string UnreleasedHeader = "## [Unreleased]";

    [Fact]
    public void Dataverse_Changelog_DocumentsRelocation()
    {
        var path = Path.Combine(PathHelpers.RepoRoot(), "src", "PPDS.Dataverse", "CHANGELOG.md");
        Assert.True(File.Exists(path), $"CHANGELOG not found: {path}");
        var changelog = File.ReadAllText(path);

        Assert.Contains(UnreleasedHeader, changelog);

        var interfaces = new[]
        {
            "IPluginTraceService", "IWebResourceService", "IMetadataAuthoringService",
            "IFlowService", "IConnectionReferenceService", "IDeploymentSettingsService"
        };
        foreach (var iface in interfaces)
        {
            Assert.Contains(iface, changelog);
        }
    }
}
