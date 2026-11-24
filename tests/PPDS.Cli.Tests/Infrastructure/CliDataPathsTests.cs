using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Logs;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.UpdateCheck;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="CliDataPaths"/> and the components that use it (#1850): with
/// <c>PPDS_CONFIG_DIR</c> unset every path is the one PPDS has always used; with it set, history,
/// logs and update-check files are created, read and deleted only under that folder.
/// </summary>
/// <remarks>
/// On macOS and Linux the user profile folder comes from <c>HOME</c>, so these tests point
/// <c>HOME</c> at a temporary folder and can assert that its <c>.ppds</c> folder is never created.
/// On Windows the profile folder cannot be redirected; there the tests assert only that every path
/// is under the configured folder, and nothing is written when the variable is unset.
/// </remarks>
[Collection(EnvironmentVariableMutatingCollection.Name)]
public sealed class CliDataPathsTests : IDisposable
{
    private const string HomeVariable = "HOME";
    private const string UpdateInProgressMessage = "An update is already in progress. Try again later.";

    private readonly string? _originalConfigDir;
    private readonly string? _originalHome;
    private readonly string _root;
    private readonly string _configDir;
    private readonly string _fakeHome;

    public CliDataPathsTests()
    {
        _originalConfigDir = Environment.GetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar);
        _originalHome = Environment.GetEnvironmentVariable(HomeVariable);

        _root = Path.Combine(Path.GetTempPath(), $"ppds-cli-paths-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_root, "config");
        _fakeHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_fakeHome);

        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, null);
        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(HomeVariable, _fakeHome);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _originalConfigDir);
        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable(HomeVariable, _originalHome);
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temporary folder.
        }
    }

    private string FakeHomePpds => Path.Combine(_fakeHome, ".ppds");

    private static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ppds");

    [Fact]
    public void TestRun_ConfigDirPointsAtTemporaryFolder()
    {
        // Set by ConfigDirTestInit before any test runs, so tests never use a real configuration folder.
        Assert.Equal(ConfigDirTestInit.ConfigDirectory, _originalConfigDir);
        Assert.StartsWith(Path.GetTempPath(), _originalConfigDir!, StringComparison.Ordinal);
    }

    [Fact]
    public void VariableUnset_PathsAreUnchangedFromEarlierReleases()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(FakeHomePpds, CliDataPaths.DataDirectory);
        }

        // <user profile>/.ppds on every platform, including Windows (not %LOCALAPPDATA%\PPDS).
        Assert.Equal(DefaultFolder, CliDataPaths.DataDirectory);
        Assert.Equal(Path.Combine(DefaultFolder, "history"), CliDataPaths.HistoryDirectory);
        Assert.Equal(Path.Combine(DefaultFolder, "tui-debug.log"), CliDataPaths.TuiDebugLogFile);
        Assert.Equal(Path.Combine(DefaultFolder, "update-check.json"), CliDataPaths.UpdateCheckFile);
        Assert.Equal(Path.Combine(DefaultFolder, "update-status.json"), CliDataPaths.UpdateStatusFile);
        Assert.Equal(Path.Combine(DefaultFolder, "update.lock"), CliDataPaths.UpdateLockFile);
        Assert.Equal(Path.Combine(DefaultFolder, "tui-debug.log"), TuiDebugLog.LogPath);
        Assert.Equal(DefaultFolder, LogsTailCommand.GetPpdsDirectory());
    }

    [Fact]
    public void VariableBlank_TreatedAsUnset()
    {
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, "   ");

        Assert.Equal(DefaultFolder, CliDataPaths.DataDirectory);
    }

    [Fact]
    public void VariableSet_AllPathsAreUnderConfigFolder()
    {
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);

        Assert.Equal(_configDir, CliDataPaths.DataDirectory);
        Assert.Equal(ProfilePaths.DataDirectory, CliDataPaths.DataDirectory);
        Assert.Equal(Path.Combine(_configDir, "history"), CliDataPaths.HistoryDirectory);
        Assert.Equal(Path.Combine(_configDir, "tui-debug.log"), CliDataPaths.TuiDebugLogFile);
        Assert.Equal(Path.Combine(_configDir, "update-check.json"), CliDataPaths.UpdateCheckFile);
        Assert.Equal(Path.Combine(_configDir, "update-status.json"), CliDataPaths.UpdateStatusFile);
        Assert.Equal(Path.Combine(_configDir, "tui-debug.log"), TuiDebugLog.LogPath);
        Assert.Equal(_configDir, LogsTailCommand.GetPpdsDirectory());
    }

    [Fact]
    public void VariableSet_UpdaterWritesStatusWhereNextStartReadsIt()
    {
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);

        // The path the service hands to the detached update script (UpdateScriptWriter).
        var statusPath = new UpdateCheckService().StatusPath;
        Assert.Equal(Path.Combine(_configDir, "update-status.json"), statusPath);

        // Write the file as the script would, then let the next CLI start consume it.
        File.WriteAllText(statusPath, "{\"success\":false,\"exitCode\":1,\"targetVersion\":\"9.9.9\"}");
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            Program.ReadAndDeleteUpdateStatus();
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("Update failed.", error.ToString());
        Assert.False(File.Exists(statusPath));
    }

    [Fact]
    public void VariableSet_UpdateLockStaysInDefaultFolder()
    {
        // The lock guards the one global-tool install, which every configuration folder shares.
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);

        Assert.Equal(Path.Combine(DefaultFolder, "update.lock"), CliDataPaths.UpdateLockFile);
        Assert.Equal(Path.Combine(DefaultFolder, "update.lock"), new UpdateCheckService().LockPath);
    }

    [Fact]
    public async Task VariableSet_LockInDefaultFolderBlocksUpdate_LockInConfigFolderDoesNot()
    {
        if (OperatingSystem.IsWindows())
        {
            // The user profile folder cannot be redirected on Windows, and this test writes a lock
            // file into it. VariableSet_UpdateLockStaysInDefaultFolder covers the path there.
            return;
        }

        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);
        var livePid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Every NuGet version is older than the running build, so an update that gets past the
        // lock check returns "Already up to date" and never starts the detached updater. (Were the
        // version unreadable, the test build is not a global-tool install, which also stops it.)
        var service = new UpdateCheckService(
            handler: new VersionsHandler("{\"versions\":[\"0.0.1\"]}"),
            cachePath: Path.Combine(_root, "update-check.json"));

        // A lock held by a running process in the configuration folder is not the update lock.
        await File.WriteAllTextAsync(Path.Combine(_configDir, "update.lock"), livePid);
        var notBlocked = await service.UpdateAsync(UpdateChannel.Stable);
        Assert.NotEqual(UpdateInProgressMessage, notBlocked.ErrorMessage);

        // The same lock in the default folder blocks the update, whatever PPDS_CONFIG_DIR says.
        Directory.CreateDirectory(FakeHomePpds);
        await File.WriteAllTextAsync(Path.Combine(FakeHomePpds, "update.lock"), livePid);
        var blocked = await service.UpdateAsync(UpdateChannel.Stable);
        Assert.False(blocked.Success);
        Assert.Equal(UpdateInProgressMessage, blocked.ErrorMessage);
    }

    [Fact]
    public void TuiDebugLogPath_FollowsVariableWithoutRestart()
    {
        var other = Path.Combine(_root, "other");

        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);
        Assert.Equal(Path.Combine(_configDir, "tui-debug.log"), TuiDebugLog.LogPath);

        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, other);
        Assert.Equal(Path.Combine(other, "tui-debug.log"), TuiDebugLog.LogPath);
    }

    [Fact]
    public async Task VariableSet_HistoryUpdateCheckDebugLogAndUpdateStatus_TouchOnlyConfigFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Guard against a vacuous pass: the fake home must really be the user profile folder.
            Assert.Equal(_fakeHome, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, _configDir);
        const string environmentUrl = "https://isolation-test.crm.dynamics.com";

        // Query history through the public constructor, the one dependency injection uses
        // for the CLI, the TUI and the daemon.
        var history = new QueryHistoryService(NullLogger<QueryHistoryService>.Instance);
        await history.AddQueryAsync(environmentUrl, "SELECT name FROM account");
        Assert.Single(await history.GetHistoryAsync(environmentUrl));
        Assert.Single(Directory.GetFiles(Path.Combine(_configDir, "history"), "*.json"));
        await history.ClearHistoryAsync(environmentUrl);
        Assert.Empty(await history.GetHistoryAsync(environmentUrl));

        // Update check: the cache is written and read back from the configured folder.
        var updateCheck = new UpdateCheckService(handler: new VersionsHandler());
        Assert.NotNull(await updateCheck.CheckAsync("1.0.0"));
        Assert.True(File.Exists(Path.Combine(_configDir, "update-check.json")));
        Assert.NotNull(updateCheck.GetCachedResult());

        // TUI debug log: written, then cleared as PpdsApplication.Run does at start.
        TuiDebugLog.Log("isolation test");
        Assert.True(File.Exists(Path.Combine(_configDir, "tui-debug.log")));
        TuiDebugLog.Clear();
        Assert.False(File.Exists(Path.Combine(_configDir, "tui-debug.log")));

        // Post-update status: read and deleted from the configured folder at CLI start.
        var statusFile = Path.Combine(_configDir, "update-status.json");
        await File.WriteAllTextAsync(statusFile, "{\"success\":true,\"targetVersion\":\"9.9.9\"}");
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            Program.ReadAndDeleteUpdateStatus();
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains("Successfully updated to 9.9.9.", error.ToString());
        Assert.False(File.Exists(statusFile));

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(
                Directory.Exists(FakeHomePpds),
                "A file was written under the default .ppds folder although PPDS_CONFIG_DIR was set.");
        }
    }

    /// <summary>Answers every request with a fixed NuGet version list; nothing leaves the process.</summary>
    private sealed class VersionsHandler(string json = "{\"versions\":[\"1.0.0\",\"1.1.0\"]}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
