using System.Diagnostics;

namespace PPDS.Cli.Services.Session;

/// <summary>
/// Worker spawner implementation using Windows Terminal.
/// </summary>
public sealed class WindowsTerminalWorkerSpawner : IWorkerSpawner
{
    private const string WindowsTerminalPath = "wt.exe";

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
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<SpawnedWorker> SpawnAsync(WorkerSpawnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Build the Claude command
        // Sets PPDS_INTERNAL=1 so workers can use session commands, then runs Claude
        // Uses /ralph-loop with the prompt file
        var claudeCommand = $"$env:PPDS_INTERNAL='1'; claude '/ralph-loop --file \\\"{request.PromptFilePath}\\\" --max-iterations {request.MaxIterations} --completion-promise PR_READY'";

        // Build Windows Terminal arguments:
        // wt -w 0 nt -d "<working-dir>" --title "Issue #N" powershell -NoExit -Command "<claude-command>"
        var terminalTitle = $"Issue #{request.IssueNumber}";
        var wtArgs = $"-w 0 nt -d \"{request.WorkingDirectory}\" --title \"{terminalTitle}\" powershell -NoExit -Command \"{claudeCommand}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsTerminalPath,
            Arguments = wtArgs,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        var process = Process.Start(startInfo);

        return Task.FromResult(new SpawnedWorker
        {
            SessionId = request.SessionId,
            ProcessId = process?.Id,
            TerminalTitle = terminalTitle
        });
    }
}
