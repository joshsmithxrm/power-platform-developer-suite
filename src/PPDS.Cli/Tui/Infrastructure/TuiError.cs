namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Represents an error captured by the TUI error service.
/// </summary>
/// <param name="Timestamp">When the error occurred.</param>
/// <param name="Message">User-friendly error message.</param>
/// <param name="Context">Optional context (e.g., operation name, method).</param>
/// <param name="ExceptionType">The exception type name, if from an exception.</param>
/// <param name="ExceptionMessage">The exception message, if from an exception.</param>
/// <param name="StackTrace">The stack trace, if from an exception.</param>
public sealed record TuiError(
    DateTimeOffset Timestamp,
    string Message,
    string? Context,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace)
{
    /// <summary>
    /// Creates a TuiError from an exception.
    /// </summary>
    /// <param name="message">User-friendly error message.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="context">Optional context information.</param>
    /// <returns>A new TuiError instance.</returns>
    public static TuiError FromException(string message, Exception ex, string? context = null)
    {
        // Unwrap AggregateException to get the inner exception
        var innerEx = ex is AggregateException agg ? agg.InnerException ?? ex : ex;

        return new TuiError(
            DateTimeOffset.UtcNow,
            message,
            context,
            innerEx.GetType().Name,
            innerEx.Message,
            innerEx.StackTrace);
    }

    /// <summary>
    /// Creates a TuiError from a message only (no exception).
    /// </summary>
    /// <param name="message">User-friendly error message.</param>
    /// <param name="context">Optional context information.</param>
    /// <returns>A new TuiError instance.</returns>
    public static TuiError FromMessage(string message, string? context = null)
    {
        return new TuiError(
            DateTimeOffset.UtcNow,
            message,
            context,
            null,
            null,
            null);
    }

    /// <summary>
    /// Gets a brief summary of the error for status bar display.
    /// </summary>
    public string BriefSummary
    {
        get
        {
            // Truncate message to reasonable length for status bar
            const int maxLength = 60;
            if (Message.Length <= maxLength)
                return Message;

            return Message.Substring(0, maxLength - 3) + "...";
        }
    }

    /// <summary>
    /// Gets the full error details formatted for display or copying.
    /// </summary>
    public string GetFullDetails()
    {
        var lines = new List<string>
        {
            $"Timestamp: {Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}",
            $"Message: {Message}"
        };

        if (!string.IsNullOrEmpty(Context))
            lines.Add($"Context: {Context}");

        if (!string.IsNullOrEmpty(ExceptionType))
            lines.Add($"Exception Type: {ExceptionType}");

        if (!string.IsNullOrEmpty(ExceptionMessage))
            lines.Add($"Exception Message: {ExceptionMessage}");

        if (!string.IsNullOrEmpty(StackTrace))
        {
            lines.Add("Stack Trace:");
            lines.Add(StackTrace);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
