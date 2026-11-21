using Xunit;

namespace PPDS.Cli.Tests.TestHelpers;

/// <summary>
/// tests/Directory.Build.props turns on PPDS.Auth's interactive sign-in guard for this unit test
/// project, so no test here can open a browser or wait on a device code (#1757).
/// </summary>
public class InteractiveSignInBlockedTests
{
    [Fact]
    public void UnitTestRun_HasInteractiveSignInBlocked()
    {
        Assert.True(
            System.AppContext.TryGetSwitch("PPDS.Auth.BlockInteractiveSignIn", out var blocked) && blocked,
            "The AppContext switch PPDS.Auth.BlockInteractiveSignIn must be on in unit test runs.");
    }
}
