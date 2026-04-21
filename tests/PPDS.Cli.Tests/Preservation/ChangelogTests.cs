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
        var section = ExtractUnreleasedSection(changelog);

        var interfaces = new[]
        {
            "IPluginTraceService", "IWebResourceService", "IEnvironmentVariableService",
            "ISolutionService", "IImportJobService", "IMetadataAuthoringService",
            "IUserService", "IRoleService", "IFlowService",
            "IConnectionReferenceService", "IDeploymentSettingsService", "IComponentNameResolver"
        };
        foreach (var iface in interfaces)
        {
            Assert.Contains(iface, section);
        }
        Assert.Contains("breaking", section, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractUnreleasedSection(string changelog)
    {
        var start = changelog.IndexOf(UnreleasedHeader, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var afterHeader = start + UnreleasedHeader.Length;
        var nextH2 = changelog.IndexOf("\n## ", afterHeader, StringComparison.Ordinal);
        return nextH2 < 0 ? changelog[afterHeader..] : changelog[afterHeader..nextH2];
    }
}
