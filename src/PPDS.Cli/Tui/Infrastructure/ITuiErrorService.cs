namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Service for centralized error handling in the TUI.
/// Captures, displays, and logs errors with consistent formatting.
/// </summary>
public interface ITuiErrorService
{
    /// <summary>
    /// Reports an error to the error service.
    /// </summary>
    /// <param name="message">User-friendly error message.</param>
    /// <param name="ex">Optional exception that caused the error.</param>
    /// <param name="context">Optional context (e.g., operation name).</param>
    void ReportError(string message, Exception? ex = null, string? context = null);

    /// <summary>
    /// Gets the list of recent errors, ordered newest first.
    /// </summary>
    IReadOnlyList<TuiError> RecentErrors { get; }

    /// <summary>
    /// Gets the most recent error, or null if no errors have occurred.
    /// </summary>
    TuiError? LatestError { get; }

    /// <summary>
    /// Clears all stored errors.
    /// </summary>
    void ClearErrors();

    /// <summary>
    /// Event raised when an error is reported.
    /// </summary>
    event Action<TuiError>? ErrorOccurred;

    /// <summary>
    /// Gets the path to the TUI debug log file.
    /// </summary>
    /// <returns>The full path to the log file.</returns>
    string GetLogFilePath();
}
