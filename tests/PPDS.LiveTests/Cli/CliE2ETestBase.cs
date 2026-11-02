using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PPDS.LiveTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// Result of a CLI command execution.
/// </summary>
public sealed class CliResult
{
    /// <summary>
    /// The exit code from the CLI process.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Standard output content.
    /// </summary>
    public string StdOut { get; init; } = string.Empty;

    /// <summary>
    /// Standard error content.
    /// </summary>
    public string StdErr { get; init; } = string.Empty;

    /// <summary>
    /// Whether the command succeeded (exit code 0).
    /// </summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Base class for CLI E2E tests.
/// Provides helper methods for running CLI commands and managing test resources.
/// </summary>
[Collection("CliE2E")]
[Trait("Category", "Integration")]
public abstract class CliE2ETestBase : IAsyncLifetime
{
    private readonly TimeProvider _timeProvider;
    private readonly FixtureProcessInterruption _interruption;
    private readonly Dictionary<string, Guid[]> _retainedCleanupIds = new(StringComparer.Ordinal);
    private readonly object _evidenceGate = new();
    private readonly HashSet<string> _ownedProfileNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _possibleOwnedResources = new(StringComparer.Ordinal);
    private string? _failureType;
    private readonly object _admissionGate = new();
    private bool _commandRunning;

    // Per-instance offline seam: teardown regressions never construct an SDK client.
    internal Func<LiveTestConfiguration, IEnumerable<Guid>, Task> DeleteAccountsAsync { get; set; }
        = LiveTestHelpers.DeleteAccountsAsync;

    /// <summary>An interrupted command has an unknown remote outcome, even after parent exit.</summary>
    protected bool RequiresManualReview => _interruption.RequiresManualReview;

    internal FixtureProcessStopEvidence? StopEvidence => _interruption.Evidence;
    internal string InterruptionEvidencePath => Path.Combine(IsolatedConfigDir, "cli-interruption.json");

    /// <summary>
    /// Configuration containing credentials and connection details.
    /// </summary>
    protected Infrastructure.LiveTestConfiguration Configuration { get; }

    /// <summary>
    /// Path to the CLI project for dotnet run.
    /// </summary>
    protected static string CliProjectPath { get; } = GetCliProjectPath();

    /// <summary>
    /// Timeout for CLI commands.
    /// </summary>
    protected virtual TimeSpan CommandTimeout => TimeSpan.FromMinutes(2);

    /// <summary>
    /// Tracks profile names created during tests for cleanup.
    /// </summary>
    protected List<string> CreatedProfiles { get; } = new();

    /// <summary>
    /// Tracks file paths created during tests for cleanup.
    /// </summary>
    protected List<string> CreatedFiles { get; } = new();

    /// <summary>
    /// Isolated config directory for this test instance.
    /// Each test class gets a unique directory to avoid race conditions.
    /// </summary>
    protected string IsolatedConfigDir { get; }

    /// <summary>
    /// Initializes a new instance of the CLI E2E test base.
    /// </summary>
    protected CliE2ETestBase() : this(TimeProvider.System) { }

    internal CliE2ETestBase(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _interruption = new FixtureProcessInterruption(timeProvider);
        Configuration = new Infrastructure.LiveTestConfiguration();
        // Create unique config directory per test instance to isolate profile stores
        IsolatedConfigDir = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(IsolatedConfigDir);
    }

    /// <summary>
    /// Runs a CLI command and captures the result.
    /// The PPDS_TEST_CLIENT_SECRET env var is inherited from the parent process,
    /// and CredentialProviderFactory uses it as a fallback to bypass SecureCredentialStore.
    /// </summary>
    /// <param name="args">Command arguments (e.g., "auth", "list").</param>
    /// <returns>The CLI execution result.</returns>
    protected Task<CliResult> RunCliAsync(params string[] args)
        => RunCliWithEnvAsync(null, args);

    /// <summary>
    /// Runs a CLI command WITHOUT the credential bypass.
    /// Explicitly clears PPDS_SPN_SECRET and PPDS_TEST_CLIENT_SECRET to force SecureCredentialStore usage.
    /// Use this for tests that specifically need to exercise the SecureCredentialStore path.
    /// These tests should be marked with [Trait("Category", "SecureStorage")] and skipped in CI.
    /// </summary>
    /// <param name="args">Command arguments (e.g., "auth", "list").</param>
    /// <returns>The CLI execution result.</returns>
    protected Task<CliResult> RunCliWithoutBypassAsync(params string[] args)
    {
        // Remove both env vars to force SecureCredentialStore path
        // Using null signals RunCliWithEnvAsync to remove the key entirely
        var clearVars = new Dictionary<string, string?>
        {
            ["PPDS_SPN_SECRET"] = null,
            ["PPDS_TEST_CLIENT_SECRET"] = null
        };
        return RunCliWithEnvAsync(clearVars!, args);
    }

    /// <summary>
    /// Runs a CLI command with additional environment variables.
    /// </summary>
    /// <param name="envVars">Additional environment variables to set.</param>
    /// <param name="args">Command arguments (e.g., "auth", "list").</param>
    /// <returns>The CLI execution result.</returns>
    protected Task<CliResult> RunCliWithEnvAsync(
        IDictionary<string, string>? envVars,
        params string[] args)
        => RunCliCoreAsync(envVars, CancellationToken.None, args);

    /// <summary>Runs a command bounded by both the normal command timeout and the caller's deadline.</summary>
    protected Task<CliResult> RunCliWithCancellationAsync(CancellationToken cancellationToken, params string[] args)
        => RunCliCoreAsync(null, cancellationToken, args);

    private async Task<CliResult> RunCliCoreAsync(
        IDictionary<string, string>? envVars,
        CancellationToken cancellationToken,
        string[] args)
    {
        EnsureCommandMayStart();
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(CliProjectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Set isolated config directory to avoid race conditions between parallel test runs
        startInfo.Environment["PPDS_CONFIG_DIR"] = IsolatedConfigDir;

        // Set or remove environment variables
        if (envVars != null)
        {
            foreach (var (key, value) in envVars)
            {
                if (value == null)
                    startInfo.Environment.Remove(key);
                else
                    startInfo.Environment[key] = value;
            }
        }

        // Build argument list: run --project <path> --configuration Release --framework net8.0 --no-build -- <args>
        // Use net8.0 (LTS) since CLI behavior is framework-agnostic and libraries
        // are already tested per-TFM via LiveTests/Authentication/*, Pooling/*, etc.
        // Use Release configuration to match CI build (--no-build requires matching config).
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(CliProjectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add("net8.0");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return await RunProcessAsync(startInfo, cancellationToken);
    }

    // Internal seam permits harmless local-process regressions without invoking CLI/authentication.
    internal async Task<CliResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        EnsureCommandMayStart();
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();
        var outputGate = new object();
        DataReceivedEventHandler output = (_, e) =>
        {
            if (e.Data != null) lock (outputGate) stdOutBuilder.AppendLine(e.Data);
        };
        DataReceivedEventHandler error = (_, e) =>
        {
            if (e.Data != null) lock (outputGate) stdErrBuilder.AppendLine(e.Data);
        };
        process.OutputDataReceived += output;
        process.ErrorDataReceived += error;
        try
        {
            return await RunCommandAsync(() =>
            {
                // A genuine launch failure has no dispatched operation to terminate.
                if (!process.Start()) throw new InvalidOperationException("CLI test process did not start.");
                return new FixtureProcessInterruption.NativeFixtureProcess(process);
            }, async token =>
            {
                // Reader initialization and final output drain are inside the interruption boundary.
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(token);
                lock (outputGate)
                {
                    return new CliResult
                    {
                        ExitCode = process.ExitCode,
                        StdOut = stdOutBuilder.ToString(),
                        StdErr = stdErrBuilder.ToString()
                    };
                }
            }, cancellationToken);
        }
        finally
        {
            process.OutputDataReceived -= output;
            process.ErrorDataReceived -= error;
        }
    }

    // Admission is atomic and occurs before dispatch. Fixtures are sequential; overlapping
    // commands are rejected rather than allowing an interrupted command to race a new launch.
    internal async Task<CliResult> RunCommandAsync(
        Func<IFixtureProcess> startProcess, Func<CancellationToken, Task<CliResult>> operation, CancellationToken callerToken)
    {
        lock (_admissionGate)
        {
            EnsureCommandMayStart();
            callerToken.ThrowIfCancellationRequested();
            if (_commandRunning) throw new InvalidOperationException("This CLI fixture already has an active command.");
            _commandRunning = true;
        }
        try
        {
            var process = startProcess();
            return await ObserveDispatchedProcessAsync(process, operation, callerToken);
        }
        finally { lock (_admissionGate) _commandRunning = false; }
    }

    // No admission check is allowed here: every failure after dispatch must traverse stop handling.
    private async Task<CliResult> ObserveDispatchedProcessAsync(
        IFixtureProcess process, Func<CancellationToken, Task<CliResult>> operation, CancellationToken callerToken)
    {
        try
        {
            return await _interruption.ObserveAsync(process, async () =>
            {
                using var commandTimeout = new CancellationTokenSource(CommandTimeout, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(commandTimeout.Token, callerToken);
                try
                {
                    return await operation(linked.Token).WaitAsync(linked.Token);
                }
                catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested && commandTimeout.IsCancellationRequested)
                {
                    // Classify now, before stopping: a later caller cancellation cannot turn timeout into cancellation.
                    // Never echo arguments: auth commands can contain credentials.
                    throw new TimeoutException($"CLI command timed out after {CommandTimeout.TotalSeconds}s.", exception);
                }
            });
        }
        catch (Exception exception)
        {
            lock (_evidenceGate) _failureType = exception.GetType().FullName;
            RetainInterruptionEvidence();
            throw;
        }
    }

    private void EnsureCommandMayStart()
    {
        if (RequiresManualReview)
            throw new InvalidOperationException(
                $"A prior CLI command was interrupted; refusing another command or cleanup. " +
                $"Process/remote outcome requires manual review. Retained config: {IsolatedConfigDir}");
    }

    /// <summary>Gates existing CLI, SDK and local cleanup without replacing the interrupted test failure.</summary>
    protected async Task<bool> RunCleanupIfSafeAsync(
        Func<Task> cleanup, string? ownedTable = null, IEnumerable<Guid>? ownedIds = null)
    {
        if (await _interruption.RunCleanupIfSafeAsync(cleanup)) return true;
        if (ownedTable != null && ownedIds != null)
            lock (_evidenceGate) _retainedCleanupIds[ownedTable] = ownedIds.ToArray();
        RetainInterruptionEvidence();
        return false;
    }

    private void RetainInterruptionEvidence()
    {
        // Retain only safe ownership/stop metadata, never arguments, environment values, or output.
        // Evidence/reporting failure cannot replace the primary exception or authorize cleanup.
        try
        {
            lock (_evidenceGate)
            {
                var evidence = new
                {
                    requiresManualReview = true,
                    failureType = _failureType,
                    parentExitConfirmed = StopEvidence?.ExitConfirmed,
                    remoteOutcome = "unknown",
                    stopErrors = StopEvidence?.Errors,
                    configDirectory = IsolatedConfigDir,
                    ownedFiles = CreatedFiles.ToArray(),
                    ownedProfiles = _ownedProfileNames.ToArray(),
                    possibleOwnedResources = _possibleOwnedResources,
                    ownedRecords = _retainedCleanupIds
                };
                File.WriteAllText(InterruptionEvidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception) { /* The retained directory and in-memory stop evidence remain available. */ }
        try { Console.Error.WriteLine($"CLI fixture interrupted; automatic cleanup skipped. Manual review required. Retained config: {IsolatedConfigDir}"); }
        catch (Exception) { /* Reporting must not mask the primary failure. */ }
    }

    /// <summary>
    /// Generates a unique profile name for testing.
    /// </summary>
    protected string GenerateTestProfileName()
    {
        var name = $"e2e-test-{Guid.NewGuid():N}".Substring(0, 30);
        CreatedProfiles.Add(name);
        lock (_evidenceGate) _ownedProfileNames.Add(name);
        return name;
    }

    /// <summary>
    /// Generates a unique temp file path for testing.
    /// </summary>
    protected string GenerateTempFilePath(string extension = ".xml")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppds-e2e-{Guid.NewGuid():N}{extension}");
        TrackOwnedFile(path);
        return path;
    }

    /// <summary>Retains only an explicitly owned local artifact path, never its contents.</summary>
    protected void TrackOwnedFile(string path)
    {
        lock (_evidenceGate) CreatedFiles.Add(path);
    }

    /// <summary>Records a fixture's generated resource name; existence remains unconfirmed after interruption.</summary>
    protected void TrackPossibleOwnedResource(string kind, string name)
    {
        lock (_evidenceGate)
        {
            if (!_possibleOwnedResources.TryGetValue(kind, out var names))
                _possibleOwnedResources[kind] = names = [];
            names.Add(name);
        }
    }

    /// <summary>
    /// Async initialization called before each test.
    /// </summary>
    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Async cleanup called after each test.
    /// Deletes any profiles, files, and the isolated config directory.
    /// </summary>
    public virtual Task DisposeAsync()
    {
        if (RequiresManualReview)
        {
            RetainInterruptionEvidence();
            return Task.CompletedTask;
        }

        // Clean up created files
        foreach (var filePath in CreatedFiles)
        {
            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // Clean up isolated config directory (contains profiles.json and token cache)
        try
        {
            if (Directory.Exists(IsolatedConfigDir))
                Directory.Delete(IsolatedConfigDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the path to the CLI project.
    /// </summary>
    private static string GetCliProjectPath()
    {
        // Navigate from test project to CLI project
        var testDir = AppContext.BaseDirectory;
        var solutionDir = FindSolutionDirectory(testDir);
        var cliProjectPath = Path.Combine(solutionDir, "src", "PPDS.Cli", "PPDS.Cli.csproj");

        if (!File.Exists(cliProjectPath))
        {
            throw new InvalidOperationException(
                $"CLI project not found at: {cliProjectPath}. Ensure the solution structure is correct.");
        }

        return cliProjectPath;
    }

    /// <summary>
    /// Gets the path to the TestData directory.
    /// </summary>
    protected static string TestDataDir { get; } = GetTestDataDir();

    /// <summary>
    /// Gets the path to the test registrations config file.
    /// </summary>
    protected static string TestRegistrationsPath => Path.Combine(TestDataDir, "test-registrations.json");

    /// <summary>
    /// Gets the path to the test plugin assembly.
    /// </summary>
    protected static string TestPluginAssemblyPath => Path.Combine(TestDataDir, "PPDS.LiveTests.Fixtures.dll");

    /// <summary>
    /// Gets the path to the TestData directory.
    /// </summary>
    private static string GetTestDataDir()
    {
        var solutionDir = FindSolutionDirectory(AppContext.BaseDirectory);
        return Path.Combine(solutionDir, "tests", "PPDS.LiveTests", "TestData");
    }

    /// <summary>
    /// Finds the solution directory by walking up from the given path.
    /// </summary>
    protected static string FindSolutionDirectory(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find PPDS.sln starting from: {startPath}");
    }
}

/// <summary>
/// Collection definition for CLI E2E tests.
/// Tests in this collection run sequentially to avoid profile conflicts.
/// </summary>
[CollectionDefinition("CliE2E")]
public class CliE2ECollection : ICollectionFixture<CliE2EFixture>
{
}

/// <summary>
/// Shared fixture for CLI E2E tests.
/// </summary>
public class CliE2EFixture : IAsyncLifetime
{
    /// <summary>
    /// Configuration for the live tests.
    /// </summary>
    public Infrastructure.LiveTestConfiguration Configuration { get; }

    public CliE2EFixture()
    {
        Configuration = new Infrastructure.LiveTestConfiguration();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;
}
