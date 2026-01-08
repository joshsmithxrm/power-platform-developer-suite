using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

/// <summary>
/// Unit tests for <see cref="TuiThemeService"/>.
/// </summary>
/// <remarks>
/// These tests verify environment detection logic and color scheme selection.
/// Tests can run without Terminal.Gui context since detection is pure logic.
/// </remarks>
public class TuiThemeServiceTests
{
    private readonly TuiThemeService _service = new();

    #region DetectEnvironmentType Tests

    [Theory]
    [InlineData(null, EnvironmentType.Unknown)]
    [InlineData("", EnvironmentType.Unknown)]
    [InlineData("   ", EnvironmentType.Unknown)]
    public void DetectEnvironmentType_NullOrEmpty_ReturnsUnknown(string? url, EnvironmentType expected)
    {
        var result = _service.DetectEnvironmentType(url);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://contoso.crm.dynamics.com")]
    [InlineData("https://contoso.CRM.DYNAMICS.COM")]
    [InlineData("https://myorg.crm.dynamics.com/")]
    public void DetectEnvironmentType_StandardCrm_ReturnsProduction(string url)
    {
        var result = _service.DetectEnvironmentType(url);

        Assert.Equal(EnvironmentType.Production, result);
    }

    [Theory]
    [InlineData("https://contoso.crm4.dynamics.com")]
    [InlineData("https://contoso.crm9.dynamics.com")]
    [InlineData("https://contoso.crm11.dynamics.com")]
    [InlineData("https://contoso.CRM9.DYNAMICS.COM")]
    public void DetectEnvironmentType_RegionalCrm_ReturnsSandbox(string url)
    {
        var result = _service.DetectEnvironmentType(url);

        Assert.Equal(EnvironmentType.Sandbox, result);
    }

    [Theory]
    [InlineData("https://contoso-dev.crm.dynamics.com")]
    [InlineData("https://dev-contoso.crm9.dynamics.com")]
    [InlineData("https://contoso-development.crm.dynamics.com")]
    [InlineData("https://contoso-test.crm.dynamics.com")]
    [InlineData("https://contoso-qa.crm.dynamics.com")]
    [InlineData("https://contoso-uat.crm.dynamics.com")]
    public void DetectEnvironmentType_DevKeywords_ReturnsDevelopment(string url)
    {
        var result = _service.DetectEnvironmentType(url);

        Assert.Equal(EnvironmentType.Development, result);
    }

    [Theory]
    [InlineData("https://contoso-trial.crm.dynamics.com")]
    [InlineData("https://trial-contoso.crm9.dynamics.com")]
    [InlineData("https://contoso-demo.crm.dynamics.com")]
    [InlineData("https://contoso-preview.crm.dynamics.com")]
    public void DetectEnvironmentType_TrialKeywords_ReturnsTrial(string url)
    {
        var result = _service.DetectEnvironmentType(url);

        Assert.Equal(EnvironmentType.Trial, result);
    }

    [Theory]
    [InlineData("https://custom.example.com")]
    [InlineData("https://localhost:5001")]
    public void DetectEnvironmentType_NonDataverse_ReturnsUnknown(string url)
    {
        var result = _service.DetectEnvironmentType(url);

        Assert.Equal(EnvironmentType.Unknown, result);
    }

    [Fact]
    public void DetectEnvironmentType_DevKeywordTakesPrecedence_OverSandboxRegion()
    {
        // dev keyword should be detected even with regional instance
        var result = _service.DetectEnvironmentType("https://contoso-dev.crm9.dynamics.com");

        Assert.Equal(EnvironmentType.Development, result);
    }

    [Fact]
    public void DetectEnvironmentType_TrialKeywordTakesPrecedence_OverProduction()
    {
        // trial keyword should be detected even with production-like URL
        var result = _service.DetectEnvironmentType("https://trial-org.crm.dynamics.com");

        Assert.Equal(EnvironmentType.Trial, result);
    }

    #endregion

    #region GetEnvironmentLabel Tests

    [Theory]
    [InlineData(EnvironmentType.Production, "PROD")]
    [InlineData(EnvironmentType.Sandbox, "SANDBOX")]
    [InlineData(EnvironmentType.Development, "DEV")]
    [InlineData(EnvironmentType.Trial, "TRIAL")]
    [InlineData(EnvironmentType.Unknown, "")]
    public void GetEnvironmentLabel_ReturnsExpectedLabel(EnvironmentType envType, string expected)
    {
        var result = _service.GetEnvironmentLabel(envType);

        Assert.Equal(expected, result);
    }

    #endregion

    #region Color Scheme Tests (Null-Safe)

    [Fact]
    public void GetStatusBarScheme_AllEnvironmentTypes_DoesNotThrow()
    {
        // These should not throw even without Terminal.Gui Application.Init()
        foreach (var envType in Enum.GetValues<EnvironmentType>())
        {
            var scheme = _service.GetStatusBarScheme(envType);
            Assert.NotNull(scheme);
        }
    }

    [Fact]
    public void GetDefaultScheme_ReturnsNonNull()
    {
        var scheme = _service.GetDefaultScheme();

        Assert.NotNull(scheme);
    }

    [Fact]
    public void GetErrorScheme_ReturnsNonNull()
    {
        var scheme = _service.GetErrorScheme();

        Assert.NotNull(scheme);
    }

    [Fact]
    public void GetSuccessScheme_ReturnsNonNull()
    {
        var scheme = _service.GetSuccessScheme();

        Assert.NotNull(scheme);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void FullWorkflow_DetectAndApplyTheme_DoesNotThrow()
    {
        // Simulate the full workflow of detecting environment and getting theme
        var url = "https://contoso.crm.dynamics.com";

        var envType = _service.DetectEnvironmentType(url);
        var label = _service.GetEnvironmentLabel(envType);
        var scheme = _service.GetStatusBarScheme(envType);

        Assert.Equal(EnvironmentType.Production, envType);
        Assert.Equal("PROD", label);
        Assert.NotNull(scheme);
    }

    [Fact]
    public void FullWorkflow_UnknownEnvironment_GracefulHandling()
    {
        // Simulate unknown environment
        var url = "https://custom.example.com";

        var envType = _service.DetectEnvironmentType(url);
        var label = _service.GetEnvironmentLabel(envType);
        var scheme = _service.GetStatusBarScheme(envType);

        Assert.Equal(EnvironmentType.Unknown, envType);
        Assert.Equal("", label);
        Assert.NotNull(scheme);
    }

    #endregion
}
