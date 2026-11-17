using PPDS.Dataverse.Security;

namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// Represents a structured error with code, message, and optional context.
/// Designed for consistent error reporting in both interactive and RPC modes.
/// </summary>
/// <param name="Code">Hierarchical error code (e.g., "Auth.ProfileNotFound").</param>
/// <param name="Message">Human-readable error message.</param>
/// <param name="Details">Optional additional context or technical details.</param>
/// <param name="Target">Optional target of the error (e.g., file path, entity name).</param>
public sealed record StructuredError(
    string Code,
    string Message,
    string? Details = null,
    string? Target = null)
{
    /// <summary>
    /// For a SQL run interrupted after DML already ran: what those statements did (not rolled back) and which one may
    /// be partly applied. Written to the JSON error envelope as <c>error.dmlCommitted</c> when set.
    /// </summary>
    public string? DmlCommitted { get; init; }

    /// <summary>
    /// Creates a StructuredError with redacted details when not in debug mode.
    /// Uses <see cref="ConnectionStringRedactor"/> to sanitize sensitive data.
    /// </summary>
    /// <param name="code">The error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="details">The details (may contain sensitive info).</param>
    /// <param name="target">The target of the error.</param>
    /// <param name="debug">Whether to include full details without redaction.</param>
    /// <returns>A StructuredError with appropriately redacted details.</returns>
    public static StructuredError Create(
        string code,
        string message,
        string? details = null,
        string? target = null,
        bool debug = false)
    {
        var safeMessage = ConnectionStringRedactor.RedactExceptionMessage(message);
        var safeDetails = debug
            ? details
            : details != null
                ? ConnectionStringRedactor.RedactExceptionMessage(details)
                : null;

        return new StructuredError(code, safeMessage, safeDetails, target);
    }

    /// <summary>
    /// Creates a StructuredError from an exception.
    /// </summary>
    /// <param name="code">The error code.</param>
    /// <param name="exception">The exception to extract message from.</param>
    /// <param name="target">The target of the error.</param>
    /// <param name="debug">Whether to include stack trace in details.</param>
    /// <returns>A StructuredError representing the exception.</returns>
    public static StructuredError FromException(
        string code,
        Exception exception,
        string? target = null,
        bool debug = false)
    {
        var details = debug ? exception.StackTrace : null;
        return Create(code, exception.Message, details, target, debug);
    }
}
