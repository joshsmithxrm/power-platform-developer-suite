using System.Diagnostics;
using System.Text;
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
    protected CliE2ETestBase()
    {
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
    protected async Task<CliResult> RunCliWithEnvAsync(
        IDictionary<string, string>? envVars,
        params string[] args)
    {
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

        using var process = new Process { StartInfo = startInfo };
        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdOutBuilder.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdErrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"CLI command timed out after {CommandTimeout.TotalSeconds}s: ppds {string.Join(" ", args)}");
        }

        return new CliResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdOutBuilder.ToString(),
            StdErr = stdErrBuilder.ToString()
        };
    }

    /// <summary>
    /// Generates a unique profile name for testing.
    /// </summary>
    protected string GenerateTestProfileName()
    {
        var name = $"e2e-test-{Guid.NewGuid():N}".Substring(0, 30);
        CreatedProfiles.Add(name);
        return name;
    }

    /// <summary>
    /// Generates a unique temp file path for testing.
    /// </summary>
    protected string GenerateTempFilePath(string extension = ".xml")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppds-e2e-{Guid.NewGuid():N}{extension}");
        CreatedFiles.Add(path);
        return path;
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
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.Sdk.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find PPDS.Sdk.sln starting from: {startPath}");
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
