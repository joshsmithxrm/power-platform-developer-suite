using System.Diagnostics;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace PPDS.Cli.DaemonTests;

/// <summary>
/// Manages daemon process lifecycle for integration tests.
/// Starts the daemon, provides a JSON-RPC client, and handles cleanup.
/// </summary>
public sealed class DaemonTestFixture : IAsyncLifetime, IAsyncDisposable
{
    private Process? _daemonProcess;
    private JsonRpc? _rpc;
    private Stream? _duplexStream;

    /// <summary>
    /// Gets the isolated config directory for this test instance.
    /// </summary>
    public string IsolatedConfigDir { get; private set; } = "";

    /// <summary>
    /// Gets the JSON-RPC connection to the daemon.
    /// </summary>
    public JsonRpc Rpc => _rpc ?? throw new InvalidOperationException("Daemon not started");

    /// <summary>
    /// Gets a value indicating whether the daemon process is running.
    /// </summary>
    public bool IsRunning => _daemonProcess != null && !_daemonProcess.HasExited;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        IsolatedConfigDir = Path.Combine(Path.GetTempPath(), $"ppds-daemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(IsolatedConfigDir);

        var cliProjectPath = GetCliProjectPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(cliProjectPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Set isolated config directory to avoid affecting user's real config
        startInfo.Environment["PPDS_CONFIG_DIR"] = IsolatedConfigDir;

        // Build argument list for running the daemon
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(cliProjectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add("net8.0");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("serve");

        _daemonProcess = new Process { StartInfo = startInfo };
        _daemonProcess.Start();

        // Create duplex stream from stdin/stdout for JSON-RPC
        _duplexStream = FullDuplexStream.Splice(
            _daemonProcess.StandardOutput.BaseStream,
            _daemonProcess.StandardInput.BaseStream);

        // Attach JSON-RPC client
        _rpc = JsonRpc.Attach(_duplexStream);

        // Brief delay to let daemon initialize
        await Task.Delay(500);
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await DisposeAsyncCore();
    }

    /// <inheritdoc/>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsyncCore();
    }

    private async ValueTask DisposeAsyncCore()
    {
        if (_rpc != null)
        {
            _rpc.Dispose();
            _rpc = null;
        }

        if (_duplexStream != null)
        {
            await _duplexStream.DisposeAsync();
            _duplexStream = null;
        }

        if (_daemonProcess != null && !_daemonProcess.HasExited)
        {
            try
            {
                _daemonProcess.Kill(entireProcessTree: true);
                await _daemonProcess.WaitForExitAsync();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        _daemonProcess?.Dispose();
        _daemonProcess = null;

        // Cleanup isolated config directory
        if (Directory.Exists(IsolatedConfigDir))
        {
            try
            {
                Directory.Delete(IsolatedConfigDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors - directory may be locked
            }
        }
    }

    /// <summary>
    /// Gets the path to the CLI project.
    /// </summary>
    private static string GetCliProjectPath()
    {
        var testDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(testDir);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.Sdk.sln")))
            {
                var cliPath = Path.Combine(dir.FullName, "src", "PPDS.Cli", "PPDS.Cli.csproj");
                if (File.Exists(cliPath))
                {
                    return cliPath;
                }
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find PPDS.Sdk.sln starting from: {testDir}");
    }
}

/// <summary>
/// Collection definition for daemon integration tests.
/// Tests in this collection share a single daemon instance.
/// </summary>
[CollectionDefinition("Daemon")]
public class DaemonCollection : ICollectionFixture<DaemonTestFixture>
{
}
