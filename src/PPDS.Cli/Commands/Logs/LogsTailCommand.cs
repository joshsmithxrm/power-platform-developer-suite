using System.CommandLine;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Commands.Logs;

/// <summary>
/// <c>ppds logs tail</c> — show the most recent N lines from <c>~/.ppds/*.log</c>.
/// Designed for post-launch triage so users can inspect daemon/TUI/RPC debug logs
/// without opening a file explorer. Reads files on disk only; no remote calls.
/// </summary>
public static class LogsTailCommand
{
    /// <summary>
    /// Upper bound for <c>--lines</c>. The ring buffer holds this many strings per file,
    /// so the cap also bounds peak memory. 10k is plenty for any reasonable tail use
    /// case and prevents accidental OOM from a typo like <c>--lines 10000000</c>.
    /// </summary>
    internal const int MaxLines = 10_000;

    /// <summary>
    /// Creates the 'logs tail' subcommand with --lines and --level options.
    /// </summary>
    public static Command Create()
    {
        var linesOption = new Option<int>("--lines", "-n")
        {
            Description = $"Number of lines to show from each log file (default: 50, max: {MaxLines})",
            DefaultValueFactory = _ => 50
        };

        var levelOption = new Option<string?>("--level", "-l")
        {
            Description = "Filter by log level (e.g., 'error', 'warning'). Case-insensitive substring match."
        };

        var command = new Command("tail", "Show recent lines from PPDS log files")
        {
            linesOption,
            levelOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var lineCount = parseResult.GetValue(linesOption);
            var level = parseResult.GetValue(levelOption);
            return await ExecuteAsync(lineCount, level, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Tails the last <paramref name="lineCount"/> lines from every <c>*.log</c> file
    /// under <c>~/.ppds/</c>. When no log files exist the command exits with a friendly
    /// message rather than an error; absent logs is a valid state.
    /// </summary>
    /// <param name="lineCount">How many trailing lines to show per file.</param>
    /// <param name="levelFilter">Optional case-insensitive substring filter on the line.</param>
    /// <param name="cancellationToken">Cancellation token (honoured between files).</param>
    /// <returns>Exit code.</returns>
    public static Task<int> ExecuteAsync(
        int lineCount,
        string? levelFilter,
        CancellationToken cancellationToken)
        => ExecuteAsync(lineCount, levelFilter, ppdsDirOverride: null, cancellationToken);

    /// <summary>
    /// Testable overload that lets callers substitute a fake <c>~/.ppds</c> directory.
    /// Production code should call the three-argument overload.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        int lineCount,
        string? levelFilter,
        string? ppdsDirOverride,
        CancellationToken cancellationToken)
    {
        if (lineCount <= 0)
        {
            Console.Error.WriteLine($"Error: --lines must be between 1 and {MaxLines} (got {lineCount}). Try --lines 50 for the default tail size.");
            return ExitCodes.Failure;
        }

        if (lineCount > MaxLines)
        {
            Console.Error.WriteLine($"Error: --lines must be between 1 and {MaxLines} (got {lineCount}). Try --lines {MaxLines} if you really need the full buffer, or pipe the log file directly for larger ranges.");
            return ExitCodes.Failure;
        }

        try
        {
            var ppdsDir = ppdsDirOverride ?? GetPpdsDirectory();
            if (!Directory.Exists(ppdsDir))
            {
                Console.Error.WriteLine($"No PPDS log directory found at {ppdsDir}");
                Console.Error.WriteLine("Run a PPDS command first to generate logs.");
                return ExitCodes.Success;
            }

            var logFiles = Directory.EnumerateFiles(ppdsDir, "*.log", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (logFiles.Count == 0)
            {
                Console.Error.WriteLine($"No .log files in {ppdsDir}");
                return ExitCodes.Success;
            }

            foreach (var file in logFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PrintTailAsync(file, lineCount, levelFilter, cancellationToken);
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static async Task PrintTailAsync(
        string path,
        int lineCount,
        string? levelFilter,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        Console.WriteLine($"=== {fileName} ({path}) ===");

        // Ring-buffer the last N lines so we don't load huge log files fully.
        var buffer = new Queue<string>(lineCount);
        try
        {
            // FileShare.ReadWrite so we can still read while the daemon is appending.
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (!string.IsNullOrEmpty(levelFilter) &&
                    line.IndexOf(levelFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (buffer.Count == lineCount)
                {
                    buffer.Dequeue();
                }
                buffer.Enqueue(line);
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"  (could not read: {ex.Message})");
            Console.WriteLine();
            return;
        }

        foreach (var entry in buffer)
        {
            Console.WriteLine(entry);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// <c>%USERPROFILE%\.ppds</c> on Windows, <c>~/.ppds</c> on macOS/Linux.
    /// Mirrors the path used by <c>TuiDebugLog</c> so this command always looks in
    /// the same place other PPDS components write to.
    /// </summary>
    internal static string GetPpdsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ppds");
    }
}
