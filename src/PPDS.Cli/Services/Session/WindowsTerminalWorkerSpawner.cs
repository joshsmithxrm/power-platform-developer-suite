using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PPDS.Cli.Services.Session;

/// <summary>
/// Worker spawner implementation using Windows Terminal.
/// </summary>
public sealed class WindowsTerminalWorkerSpawner : IWorkerSpawner
{
    private const string WindowsTerminalPath = "wt.exe";
    private readonly ILogger<WindowsTerminalWorkerSpawner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsTerminalWorkerSpawner"/> class.
    /// </summary>
    public WindowsTerminalWorkerSpawner()
        : this(NullLogger<WindowsTerminalWorkerSpawner>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsTerminalWorkerSpawner"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public WindowsTerminalWorkerSpawner(ILogger<WindowsTerminalWorkerSpawner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            // Check if wt.exe is in PATH
            var startInfo = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = WindowsTerminalPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Terminal availability check failed");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<SpawnedWorker> SpawnAsync(WorkerSpawnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Create settings.local.json to enable autonomous startup without confirmation prompt.
        // The --permission-mode flag tells Claude to USE bypass mode, but without defaultMode
        // in settings, Claude shows a confirmation prompt. This file sets defaultMode which
        // tells Claude "this session has pre-accepted bypass mode" so it skips the prompt.
        // SECURITY: Project-level settings.json deny/allow rules are still enforced.
        var settingsLocalPath = Path.Combine(request.WorkingDirectory, ".claude", "settings.local.json");
        var settingsLocalContent = """
{
  "permissions": {
    "defaultMode": "bypassPermissions"
  }
}
""";
        Directory.CreateDirectory(Path.GetDirectoryName(settingsLocalPath)!);
        await File.WriteAllTextAsync(settingsLocalPath, settingsLocalContent, cancellationToken);

        // Write a launcher script to avoid quote escaping issues with wt -> powershell
        var launcherPath = Path.Combine(request.WorkingDirectory, ".claude", "start-worker.ps1");

        // Use bypassPermissions to allow autonomous operation without manual approval prompts.
        // SECURITY: This mode relies on project-level .claude/settings.json to restrict dangerous operations.
        // The worktree MUST have settings.json configured with:
        // - deny rules: block commands like git push --force, rm -rf, etc.
        // - allow rules: specific skills like /ship, /test, /commit
        // The final gate is human review of pull requests created by /ship (Claude never merges).
        var launcherContent = $@"$env:PPDS_INTERNAL = '1'
Write-Host 'Worker session for issue #{request.IssueNumber}' -ForegroundColor Cyan
Write-Host ''
claude --permission-mode bypassPermissions ""Read .claude/session-prompt.md and implement issue #{request.IssueNumber}. Start by understanding the issue, then implement the fix.""
";
        await File.WriteAllTextAsync(launcherPath, launcherContent, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Build Windows Terminal arguments:
        // wt -w 0 nt -d "<working-dir>" --title "Issue #N" pwsh -NoExit -File "<launcher>"
        var terminalTitle = $"Issue #{request.IssueNumber}";
        var wtArgs = $"-w 0 nt -d \"{request.WorkingDirectory}\" --title \"{terminalTitle}\" pwsh -NoExit -File \"{launcherPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsTerminalPath,
            Arguments = wtArgs,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        // Start the process and capture its ID before disposing
        // The Process object is just a handle - the actual terminal continues running independently
        int? processId;
        using (var process = Process.Start(startInfo))
        {
            processId = process?.Id;
        }

        return new SpawnedWorker
        {
            SessionId = request.SessionId,
            ProcessId = processId,
            TerminalTitle = terminalTitle
        };
    }
}
