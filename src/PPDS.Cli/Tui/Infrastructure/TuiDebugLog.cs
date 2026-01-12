using System.Runtime.CompilerServices;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Simple file-based debug logger for TUI troubleshooting.
/// Writes to ~/.ppds/tui-debug.log
/// </summary>
internal static class TuiDebugLog
{
    /// <summary>
    /// Path to the TUI debug log file.
    /// </summary>
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ppds", "tui-debug.log");

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
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, entry + Environment.NewLine);
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
            if (File.Exists(LogPath))
                File.Delete(LogPath);
        }
        catch
        {
            // Best-effort cleanup - logging utilities should never throw
        }
    }
}
