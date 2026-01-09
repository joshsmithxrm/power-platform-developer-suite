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

        // Write a launcher script to avoid quote escaping issues with wt -> powershell
        var launcherPath = Path.Combine(request.WorkingDirectory, ".claude", "start-worker.ps1");
        var launcherContent = $@"$env:PPDS_INTERNAL = '1'
claude '/ralph-loop --file ""{request.PromptFilePath}"" --max-iterations {request.MaxIterations} --completion-promise PR_READY'
";
        File.WriteAllText(launcherPath, launcherContent);

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

        var process = Process.Start(startInfo);

        return Task.FromResult(new SpawnedWorker
        {
            SessionId = request.SessionId,
            ProcessId = process?.Id,
            TerminalTitle = terminalTitle
        });
    }
}
