using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

public sealed class StartupUpdateNotifierTests
{
    #region FormatNotification

    [Fact]
    public void FormatNotification_NullResult_ReturnsNull()
    {
        Assert.Null(StartupUpdateNotifier.FormatNotification(null));
    }

    [Fact]
    public void FormatNotification_NoUpdateAvailable_ReturnsNull()
    {
        var result = MakeResult(stableUpdate: false, preReleaseUpdate: false);
        Assert.Null(StartupUpdateNotifier.FormatNotification(result));
    }

    [Fact]
    public void FormatNotification_StableOnlyUpdate_ReturnsSingleLine()
    {
        var result = MakeResult(
            currentVersion: "0.4.0", // stable track
            stableUpdate: true,
            preReleaseUpdate: false,
            latestStable: "0.6.0",
            updateCommand: "dotnet tool update PPDS.Cli -g");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        Assert.Contains("0.6.0", message);
        Assert.Contains("dotnet tool update PPDS.Cli -g", message);
        Assert.DoesNotContain("\n", message!.Trim());
    }

    [Fact]
    public void FormatNotification_StableUser_HidesPreRelease()
    {
        // Stable user (even minor) should only see stable in startup notification
        var result = MakeResult(
            currentVersion: "0.4.0", // even minor = stable track
            stableUpdate: true,
            preReleaseUpdate: true,
            latestStable: "0.6.0",
            latestPreRelease: "0.7.0-alpha.1",
            updateCommand: "dotnet tool update PPDS.Cli -g",
            preReleaseUpdateCommand: "dotnet tool update PPDS.Cli -g --prerelease");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        Assert.Contains("0.6.0", message);
        // Stable user should NOT see pre-release in startup notification
        Assert.DoesNotContain("0.7.0-alpha.1", message);
        Assert.DoesNotContain("\n", message!.Trim());
    }

    [Fact]
    public void FormatNotification_PreReleaseUser_BothUpdatesAvailable_ReturnsTwoLines()
    {
        // Pre-release user (odd minor) should see both
        var result = MakeResult(
            currentVersion: "0.5.0-beta.1", // odd minor = pre-release track
            stableUpdate: true,
            preReleaseUpdate: true,
            latestStable: "0.6.0",
            latestPreRelease: "0.7.0-alpha.1",
            updateCommand: "dotnet tool update PPDS.Cli -g",
            preReleaseUpdateCommand: "dotnet tool update PPDS.Cli -g --prerelease");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        var lines = message!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("0.6.0", lines[0]);
        Assert.Contains("0.7.0-alpha.1", lines[1]);
    }

    [Fact]
    public void FormatNotification_PreReleaseOnlyUpdate_ReturnsSingleLine()
    {
        var result = MakeResult(
            currentVersion: "0.5.0-beta.1",
            stableUpdate: false,
            preReleaseUpdate: true,
            latestPreRelease: "0.7.0-alpha.1",
            updateCommand: "dotnet tool update PPDS.Cli -g --prerelease");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        Assert.Contains("0.7.0-alpha.1", message);
        Assert.DoesNotContain("\n", message!.Trim());
    }

    #endregion

    #region ShouldShow

    [Fact]
    public void ShouldShow_EmptyArgs_ReturnsTrue()
    {
        Assert.True(StartupUpdateNotifier.ShouldShow(Array.Empty<string>()));
    }

    [Fact]
    public void ShouldShow_NormalArgs_ReturnsTrue()
    {
        Assert.True(StartupUpdateNotifier.ShouldShow(new[] { "query", "sql" }));
    }

    [Fact]
    public void ShouldShow_QuietLong_ReturnsFalse()
    {
        Assert.False(StartupUpdateNotifier.ShouldShow(new[] { "--quiet" }));
    }

    [Fact]
    public void ShouldShow_QuietShort_ReturnsFalse()
    {
        Assert.False(StartupUpdateNotifier.ShouldShow(new[] { "-q" }));
    }

    [Fact]
    public void ShouldShow_HelpFlag_ReturnsTrue()
    {
        // --help suppression handled by Program.cs SkipVersionHeaderArgs, not ShouldShow
        Assert.True(StartupUpdateNotifier.ShouldShow(new[] { "--help" }));
    }

    [Fact]
    public void ShouldShow_QuietAmongOtherArgs_ReturnsFalse()
    {
        Assert.False(StartupUpdateNotifier.ShouldShow(new[] { "query", "sql", "--quiet" }));
    }

    #endregion

    #region Helpers

    private static UpdateCheckResult MakeResult(
        string currentVersion = "0.5.0-beta.1",
        bool stableUpdate = false,
        bool preReleaseUpdate = false,
        string? latestStable = null,
        string? latestPreRelease = null,
        string? updateCommand = null,
        string? preReleaseUpdateCommand = null)
    {
        return new UpdateCheckResult
        {
            CurrentVersion = currentVersion,
            LatestStableVersion = latestStable,
            LatestPreReleaseVersion = latestPreRelease,
            StableUpdateAvailable = stableUpdate,
            PreReleaseUpdateAvailable = preReleaseUpdate,
            UpdateCommand = updateCommand,
            PreReleaseUpdateCommand = preReleaseUpdateCommand,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    #endregion
}
