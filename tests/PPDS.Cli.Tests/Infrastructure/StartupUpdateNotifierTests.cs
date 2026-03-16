using System.Text.Json;
using FluentAssertions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="StartupUpdateNotifier"/>.
/// </summary>
public class StartupUpdateNotifierTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string _cacheFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StartupUpdateNotifierTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);
        _cacheFile = Path.Combine(_tempPath, "update-check.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    #region Helpers

    private void WriteCacheFile(UpdateCheckResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(_cacheFile, json);
    }

    private static UpdateCheckResult MakeResult(
        bool stableUpdateAvailable = false,
        bool preReleaseUpdateAvailable = false,
        string? latestStableVersion = null,
        string? latestPreReleaseVersion = null,
        string? updateCommand = null,
        DateTimeOffset? checkedAt = null)
    {
        return new UpdateCheckResult
        {
            CurrentVersion = "0.5.0",
            LatestStableVersion = latestStableVersion,
            LatestPreReleaseVersion = latestPreReleaseVersion,
            StableUpdateAvailable = stableUpdateAvailable,
            PreReleaseUpdateAvailable = preReleaseUpdateAvailable,
            UpdateCommand = updateCommand,
            CheckedAt = checkedAt ?? DateTimeOffset.UtcNow
        };
    }

    #endregion

    // ─── GetNotificationMessage: update available ──────────────────────────────

    [Fact]
    public void GetNotificationMessage_StableUpdateAvailable_ReturnsMessage()
    {
        WriteCacheFile(MakeResult(
            stableUpdateAvailable: true,
            latestStableVersion: "0.6.0",
            updateCommand: "dotnet tool update PPDS.Cli -g"));

        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().NotBeNull();
        message.Should().Contain("0.6.0");
        message.Should().Contain("dotnet tool update PPDS.Cli -g");
    }

    [Fact]
    public void GetNotificationMessage_PreReleaseUpdateAvailable_ReturnsMessage()
    {
        WriteCacheFile(MakeResult(
            preReleaseUpdateAvailable: true,
            latestPreReleaseVersion: "0.6.0-beta.1",
            updateCommand: "dotnet tool update PPDS.Cli -g --prerelease"));

        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().NotBeNull();
        message.Should().Contain("0.6.0-beta.1");
        message.Should().Contain("dotnet tool update PPDS.Cli -g --prerelease");
    }

    // ─── GetNotificationMessage: no update ────────────────────────────────────

    [Fact]
    public void GetNotificationMessage_NoUpdateAvailable_ReturnsNull()
    {
        WriteCacheFile(MakeResult(
            stableUpdateAvailable: false,
            preReleaseUpdateAvailable: false));

        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_NoCacheFile_ReturnsNull()
    {
        // _cacheFile does not exist
        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_ExpiredCache_ReturnsNull()
    {
        WriteCacheFile(MakeResult(
            stableUpdateAvailable: true,
            latestStableVersion: "0.6.0",
            updateCommand: "dotnet tool update PPDS.Cli -g",
            checkedAt: DateTimeOffset.UtcNow.AddHours(-25)));

        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_CorruptCacheFile_ReturnsNull()
    {
        File.WriteAllText(_cacheFile, "{ not valid json {{{{");

        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().BeNull();
    }

    // ─── GetNotificationMessage: never throws ─────────────────────────────────

    [Fact]
    public void GetNotificationMessage_InvalidPath_NeverThrows()
    {
        var act = () => StartupUpdateNotifier.GetNotificationMessage(
            cachePath: @"Z:\nonexistent\very\deep\path\update-check.json");

        act.Should().NotThrow();
    }

    [Fact]
    public void GetNotificationMessage_NullPath_NeverThrows()
    {
        // With a null cachePath it will fall back to the default path which may not exist — must not throw
        var act = () => StartupUpdateNotifier.GetNotificationMessage(cachePath: null);

        act.Should().NotThrow();
    }

    // ─── ShouldShow ───────────────────────────────────────────────────────────

    [Fact]
    public void ShouldShow_NoArgs_ReturnsTrue()
    {
        StartupUpdateNotifier.ShouldShow(Array.Empty<string>()).Should().BeTrue();
    }

    [Fact]
    public void ShouldShow_NormalArgs_ReturnsTrue()
    {
        StartupUpdateNotifier.ShouldShow(new[] { "env", "list" }).Should().BeTrue();
    }

    [Fact]
    public void ShouldShow_QuietLongFlag_ReturnsFalse()
    {
        StartupUpdateNotifier.ShouldShow(new[] { "--quiet" }).Should().BeFalse();
    }

    [Fact]
    public void ShouldShow_QuietShortFlag_ReturnsFalse()
    {
        StartupUpdateNotifier.ShouldShow(new[] { "-q" }).Should().BeFalse();
    }

    [Fact]
    public void ShouldShow_QuietFlagAmongOtherArgs_ReturnsFalse()
    {
        StartupUpdateNotifier.ShouldShow(new[] { "env", "list", "--quiet" }).Should().BeFalse();
    }

    [Fact]
    public void ShouldShow_QuietShortFlagAmongOtherArgs_ReturnsFalse()
    {
        StartupUpdateNotifier.ShouldShow(new[] { "env", "list", "-q" }).Should().BeFalse();
    }

    // ─── Message format ───────────────────────────────────────────────────────

    [Fact]
    public void GetNotificationMessage_Format_ContainsUpdateAvailablePrefix()
    {
        WriteCacheFile(MakeResult(
            stableUpdateAvailable: true,
            latestStableVersion: "0.6.0",
            updateCommand: "dotnet tool update PPDS.Cli -g"));

        var message = StartupUpdateNotifier.GetNotificationMessage(cachePath: _cacheFile);

        message.Should().StartWith("Update available:");
    }
}
