using FluentAssertions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Regression tests for issue #809 — ensures the test harness routes browser
/// launches through <see cref="NoOpBrowserLauncher"/> instead of spawning a
/// real OS browser window during test runs.
/// </summary>
public class BrowserLauncherTests
{
    [Fact]
    public void TestContext_UsesNoOpLauncher()
    {
        BrowserHelper.Launcher.Should().BeOfType<NoOpBrowserLauncher>(
            "BrowserLauncherTestInit must install a NoOpBrowserLauncher before any test runs (issue #809)");
    }

    [Fact]
    public void NoOpLauncher_RecordsUrlInsteadOfLaunching()
    {
        var launcher = new NoOpBrowserLauncher();

        var result = launcher.OpenUrl("https://example.com/record/1");

        result.Should().BeTrue();
        launcher.OpenedUrls.Should().ContainSingle().Which.Should().Be("https://example.com/record/1");
    }

    [Fact]
    public void NoOpLauncher_StillEnforcesSchemeValidation()
    {
        var launcher = new NoOpBrowserLauncher();

        var act = () => launcher.OpenUrl("javascript:alert(1)");

        act.Should().Throw<PpdsException>()
            .Where(e => e.ErrorCode == ErrorCodes.Validation.InvalidUrlScheme);
    }

    [Fact]
    public void BrowserHelper_OpenUrl_DelegatesToLauncher()
    {
        var previous = BrowserHelper.Launcher;
        var fake = new NoOpBrowserLauncher();
        BrowserHelper.Launcher = fake;
        try
        {
            BrowserHelper.OpenUrl("https://dataverse.example.com/main.aspx");

            fake.OpenedUrls.Should().ContainSingle()
                .Which.Should().Be("https://dataverse.example.com/main.aspx");
        }
        finally
        {
            BrowserHelper.Launcher = previous;
        }
    }
}
