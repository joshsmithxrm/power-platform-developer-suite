using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Tests for DataverseUrlBuilder — centralized URL construction for Dynamics 365 and Power Platform.
/// </summary>
[Trait("Category", "Unit")]
public class DataverseUrlBuilderTests
{
    #region BuildRecordUrl Tests

    [Fact]
    public void BuildRecordUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var url = DataverseUrlBuilder.BuildRecordUrl(
            "https://org.crm.dynamics.com",
            "account",
            "12345678-1234-1234-1234-123456789012");

        Assert.Equal(
            "https://org.crm.dynamics.com/main.aspx?etn=account&id=12345678-1234-1234-1234-123456789012&pagetype=entityrecord",
            url);
    }

    [Fact]
    public void BuildRecordUrl_TrailingSlash_TrimsCorrectly()
    {
        var url = DataverseUrlBuilder.BuildRecordUrl(
            "https://org.crm.dynamics.com/",
            "contact",
            "abc123");

        Assert.Equal(
            "https://org.crm.dynamics.com/main.aspx?etn=contact&id=abc123&pagetype=entityrecord",
            url);
    }

    [Theory]
    [InlineData(null, "account", "123")]
    [InlineData("", "account", "123")]
    [InlineData("https://org.crm.dynamics.com", null, "123")]
    [InlineData("https://org.crm.dynamics.com", "", "123")]
    [InlineData("https://org.crm.dynamics.com", "account", null)]
    [InlineData("https://org.crm.dynamics.com", "account", "")]
    public void BuildRecordUrl_MissingInputs_ReturnsNull(
        string? environmentUrl,
        string? entityLogicalName,
        string? recordId)
    {
        var url = DataverseUrlBuilder.BuildRecordUrl(environmentUrl, entityLogicalName, recordId);

        Assert.Null(url);
    }

    #endregion

    #region BuildEntityListUrl Tests

    [Fact]
    public void BuildEntityListUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var url = DataverseUrlBuilder.BuildEntityListUrl(
            "https://org.crm.dynamics.com",
            "account");

        Assert.Equal(
            "https://org.crm.dynamics.com/main.aspx?pagetype=entitylist&etn=account",
            url);
    }

    [Fact]
    public void BuildEntityListUrl_TrailingSlash_TrimsCorrectly()
    {
        var url = DataverseUrlBuilder.BuildEntityListUrl(
            "https://org.crm.dynamics.com/",
            "contact");

        Assert.Equal(
            "https://org.crm.dynamics.com/main.aspx?pagetype=entitylist&etn=contact",
            url);
    }

    #endregion

    #region BuildMakerPortalUrl Tests

    [Fact]
    public void BuildMakerPortalUrl_ValidEnvironmentId_ReturnsCorrectUrl()
    {
        var url = DataverseUrlBuilder.BuildMakerPortalUrl("env-guid-123");

        Assert.Equal(
            "https://make.powerapps.com/environments/env-guid-123/solutions",
            url);
    }

    [Fact]
    public void BuildMakerPortalUrl_WithCustomPath_ReturnsCorrectUrl()
    {
        var url = DataverseUrlBuilder.BuildMakerPortalUrl("env-guid-123", "/entities");

        Assert.Equal(
            "https://make.powerapps.com/environments/env-guid-123/entities",
            url);
    }

    #endregion

    #region BuildSolutionMakerUrl Tests

    [Fact]
    public void BuildSolutionMakerUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var solutionId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = DataverseUrlBuilder.BuildSolutionMakerUrl(
            "https://myorg.crm.dynamics.com",
            solutionId);

        Assert.Equal(
            $"https://make.powerapps.com/environments/Default-myorg/solutions/{solutionId}",
            url);
    }

    #endregion

    #region BuildEnvironmentVariableMakerUrl Tests

    [Fact]
    public void BuildEnvironmentVariableMakerUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var definitionId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = DataverseUrlBuilder.BuildEnvironmentVariableMakerUrl(
            "https://myorg.crm.dynamics.com",
            definitionId);

        Assert.Equal(
            $"https://make.powerapps.com/environments/Default-myorg/solutions/environmentvariables/{definitionId}",
            url);
    }

    #endregion

    #region BuildImportJobMakerUrl Tests

    [Fact]
    public void BuildImportJobMakerUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var importJobId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = DataverseUrlBuilder.BuildImportJobMakerUrl(
            "https://myorg.crm.dynamics.com",
            importJobId);

        Assert.Equal(
            $"https://make.powerapps.com/environments/Default-myorg/solutions/importjob/{importJobId}",
            url);
    }

    #endregion

    #region BuildWebResourceEditorUrl Tests

    [Fact]
    public void BuildWebResourceEditorUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var webResourceId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = DataverseUrlBuilder.BuildWebResourceEditorUrl(
            "https://myorg.crm.dynamics.com",
            webResourceId);

        Assert.Equal(
            $"https://myorg.crm.dynamics.com/main.aspx?appid=&pagetype=webresourceedit&id={{{webResourceId}}}",
            url);
    }

    #endregion

    #region BuildFlowUrl Tests

    [Fact]
    public void BuildFlowUrl_ValidInputs_ReturnsCorrectUrl()
    {
        var flowId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var url = DataverseUrlBuilder.BuildFlowUrl("env-id-123", flowId);

        Assert.Equal(
            $"https://make.powerautomate.com/environments/env-id-123/flows/{flowId}/details",
            url);
    }

    #endregion

    #region NoInlineUrlConstruction Tests

    [Fact]
    public void NoInlineUrlConstruction_NoInlineUrlPatternsInProductionCode()
    {
        var srcDir = FindSrcDirectory();
        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.AltDirectorySeparatorChar + "obj" + Path.AltDirectorySeparatorChar))
            .Where(f => !f.EndsWith("DataverseUrlBuilder.cs"))
            .ToList();

        var forbiddenPatterns = new[]
        {
            "main.aspx?etn=",
            "main.aspx?pagetype=entitylist",
            "main.aspx?appid=&pagetype=webresourceedit",
            "make.powerapps.com/environments",
            "make.powerautomate.com/environments"
        };

        var violations = new List<string>();

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var pattern in forbiddenPatterns)
                {
                    if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        var relativePath = Path.GetRelativePath(srcDir, file);
                        violations.Add($"{relativePath}:{i + 1} contains '{pattern}'");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found inline URL construction in production code:\n{string.Join("\n", violations)}");
    }

    private static string FindSrcDirectory()
    {
        // Walk up from the test assembly location to find the src/ directory
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var srcCandidate = Path.Combine(dir, "src");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not find src/ directory from " + AppContext.BaseDirectory);
    }

    #endregion
}
