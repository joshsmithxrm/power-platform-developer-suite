using System;

namespace PPDS.Auth;

/// <summary>
/// Controls debug logging output for the auth library.
/// </summary>
/// <remarks>
/// By default, no debug output is emitted.
/// TUI consumers can set <see cref="Writer"/> to redirect debug messages
/// to a file-based logger like TuiDebugLog.
/// </remarks>
public static class AuthDebugLog
{
    private static volatile Action<string>? _writer;

    /// <summary>
    /// Gets or sets the action used to write debug messages.
    /// Set to null to suppress all output (default).
    /// </summary>
    /// <example>
    /// <code>
    /// // Redirect to TUI debug log
    /// AuthDebugLog.Writer = msg => TuiDebugLog.Log($"[Auth] {msg}");
    ///
    /// // Redirect to console for CLI debugging
    /// AuthDebugLog.Writer = Console.WriteLine;
    /// </code>
    /// </example>
    public static Action<string>? Writer
    {
        get => _writer;
        set => _writer = value;
    }

    /// <summary>
    /// Writes a debug message using the configured writer.
    /// Does nothing if Writer is null.
    /// </summary>
    /// <param name="message">The message to write.</param>
    internal static void WriteLine(string message)
    {
        _writer?.Invoke(message);
    }

    /// <summary>
    /// Resets the writer to null (no output).
    /// </summary>
    public static void Reset()
    {
        _writer = null;
    }
}
