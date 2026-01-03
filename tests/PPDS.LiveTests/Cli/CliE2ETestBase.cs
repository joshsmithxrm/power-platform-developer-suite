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
    /// Initializes a new instance of the CLI E2E test base.
    /// </summary>
    protected CliE2ETestBase()
    {
        Configuration = new Infrastructure.LiveTestConfiguration();
    }

    /// <summary>
    /// Runs a CLI command and captures the result.
    /// </summary>
    /// <param name="args">Command arguments (e.g., "auth", "list").</param>
    /// <returns>The CLI execution result.</returns>
    protected async Task<CliResult> RunCliAsync(params string[] args)
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

        // Build argument list: run --project <path> --framework net8.0 --no-build -- <args>
        // Use net8.0 (LTS) since CLI behavior is framework-agnostic and libraries
        // are already tested per-TFM via LiveTests/Authentication/*, Pooling/*, etc.
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(CliProjectPath);
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
    /// Deletes any profiles and files created during the test.
    /// </summary>
    public virtual async Task DisposeAsync()
    {
        // Clean up created profiles
        foreach (var profileName in CreatedProfiles)
        {
            try
            {
                await RunCliAsync("auth", "delete", "--name", profileName);
            }
            catch
            {
                // Ignore cleanup errors
            }
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
    /// Finds the solution directory by walking up from the given path.
    /// </summary>
    private static string FindSolutionDirectory(string startPath)
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
