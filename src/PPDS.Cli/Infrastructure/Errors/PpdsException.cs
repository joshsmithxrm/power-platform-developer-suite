namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// Base exception for all PPDS application service errors.
/// </summary>
/// <remarks>
/// <para>
/// Services throw <see cref="PpdsException"/> (or subclasses) with structured error information.
/// See ADR-0026 for architectural context.
/// </para>
/// <para>
/// Use specific subclasses for error types that require programmatic handling:
/// <list type="bullet">
/// <item><see cref="PpdsAuthException"/> for authentication failures</item>
/// <item><see cref="PpdsThrottleException"/> for rate limiting</item>
/// <item><see cref="PpdsValidationException"/> for input validation errors</item>
/// </list>
/// </para>
/// </remarks>
public class PpdsException : Exception
{
    /// <summary>
    /// Machine-readable error code for programmatic handling.
    /// Use codes from <see cref="ErrorCodes"/>.
    /// </summary>
    public string ErrorCode { get; init; }

    /// <summary>
    /// Human-readable message safe to display to users.
    /// </summary>
    public string UserMessage { get; init; }

    /// <summary>
    /// Severity level for UI display decisions.
    /// </summary>
    public PpdsSeverity Severity { get; init; } = PpdsSeverity.Error;

    /// <summary>
    /// Additional context for debugging (not shown to users).
    /// </summary>
    public IDictionary<string, object>? Context { get; init; }

    /// <summary>
    /// Creates a new PPDS exception.
    /// </summary>
    public PpdsException(string errorCode, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }

    /// <summary>
    /// Creates a new PPDS exception with context.
    /// </summary>
    public PpdsException(string errorCode, string userMessage, IDictionary<string, object> context, Exception? inner = null)
        : base(userMessage, inner)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
        Context = context;
    }
}

/// <summary>
/// Severity level for PPDS exceptions.
/// </summary>
public enum PpdsSeverity
{
    /// <summary>Informational message.</summary>
    Info,

    /// <summary>Warning that doesn't block operation.</summary>
    Warning,

    /// <summary>Error that blocked operation.</summary>
    Error
}

/// <summary>
/// Authentication or authorization failure.
/// </summary>
public class PpdsAuthException : PpdsException
{
    /// <summary>
    /// Whether the user needs to re-authenticate.
    /// </summary>
    public bool RequiresReauthentication { get; init; }

    /// <summary>
    /// Creates a new auth exception.
    /// </summary>
    public PpdsAuthException(string errorCode, string userMessage, Exception? inner = null)
        : base(errorCode, userMessage, inner)
    {
    }
}

/// <summary>
/// User declined authentication when prompted.
/// </summary>
public class PpdsAuthDeclinedException : PpdsAuthException
{
    /// <summary>
    /// Creates a new auth declined exception.
    /// </summary>
    public PpdsAuthDeclinedException()
        : base(ErrorCodes.Auth.Declined, "Authentication was declined by the user.")
    {
    }
}

/// <summary>
/// Rate limiting (429) from Dataverse service protection.
/// </summary>
public class PpdsThrottleException : PpdsException
{
    /// <summary>
    /// Time to wait before retrying.
    /// </summary>
    public TimeSpan RetryAfter { get; init; }

    /// <summary>
    /// Creates a new throttle exception.
    /// </summary>
    public PpdsThrottleException(TimeSpan retryAfter)
        : base(ErrorCodes.Connection.Throttled, $"Rate limited. Retry after {retryAfter.TotalSeconds:F0} seconds.")
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>
/// Input validation failure.
/// </summary>
public class PpdsValidationException : PpdsException
{
    /// <summary>
    /// List of validation errors.
    /// </summary>
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    /// <summary>
    /// Creates a new validation exception with multiple errors.
    /// </summary>
    public PpdsValidationException(IEnumerable<ValidationError> errors)
        : base(ErrorCodes.Validation.InvalidValue, "One or more validation errors occurred.")
    {
        Errors = errors.ToList();
        UserMessage = Errors.Count == 1
            ? Errors[0].Message
            : $"{Errors.Count} validation errors occurred.";
    }

    /// <summary>
    /// Creates a new validation exception with a single error.
    /// </summary>
    public PpdsValidationException(string field, string message)
        : base(ErrorCodes.Validation.InvalidValue, message)
    {
        Errors = [new ValidationError(field, message)];
    }
}

/// <summary>
/// A single validation error.
/// </summary>
/// <param name="Field">The field or parameter that failed validation.</param>
/// <param name="Message">Human-readable description of the error.</param>
public sealed record ValidationError(string Field, string Message);

/// <summary>
/// Resource not found.
/// </summary>
public class PpdsNotFoundException : PpdsException
{
    /// <summary>
    /// The type of resource that was not found.
    /// </summary>
    public string ResourceType { get; init; }

    /// <summary>
    /// The identifier of the resource that was not found.
    /// </summary>
    public string ResourceId { get; init; }

    /// <summary>
    /// Creates a new not found exception.
    /// </summary>
    public PpdsNotFoundException(string resourceType, string resourceId)
        : base(ErrorCodes.Operation.NotFound, $"{resourceType} '{resourceId}' not found.")
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
    }
}
