using System.Runtime.CompilerServices;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Simple file-based debug logger for TUI troubleshooting.
/// Writes to <c>tui-debug.log</c> under <see cref="CliDataPaths.DataDirectory"/>
/// (the <c>PPDS_CONFIG_DIR</c> folder when set, otherwise <c>~/.ppds</c>).
/// </summary>
internal static class TuiDebugLog
{
    /// <summary>
    /// Path to the TUI debug log file, resolved on each access.
    /// </summary>
    public static string LogPath => CliDataPaths.TuiDebugLogFile;

    private static readonly object Lock = new();

    /// <summary>
    /// Logs a debug message with timestamp, thread ID, and caller info.
    /// </summary>
    public static void Log(
        string message,
        [CallerMemberName] string? caller = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;
            var fileName = Path.GetFileName(file ?? "");
            var entry = $"[{timestamp}] T{threadId:D3} {fileName}:{line} {caller}: {message}";

            lock (Lock)
            {
                var logPath = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Logging should never throw
        }
    }

    /// <summary>
    /// Clears the log file.
    /// </summary>
    public static void Clear()
    {
        try
        {
            var logPath = LogPath;
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
        catch
        {
            // Best-effort cleanup - logging utilities should never throw
        }
    }
}
