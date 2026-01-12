using PPDS.Auth.Credentials;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;
using PPDS.Migration.Import;

namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// Maps exceptions to StructuredError instances with appropriate codes.
/// </summary>
/// <remarks>
/// This class centralizes exception-to-error mapping to ensure consistent
/// error codes and messages across all CLI commands.
/// </remarks>
public static class ExceptionMapper
{
    /// <summary>
    /// Maps an exception to a StructuredError.
    /// </summary>
    /// <param name="ex">The exception to map.</param>
    /// <param name="context">Optional context about what was happening.</param>
    /// <param name="debug">Whether to include full exception details (stack trace).</param>
    /// <returns>A StructuredError representing the exception.</returns>
    public static StructuredError Map(Exception ex, string? context = null, bool debug = false)
    {
        var (code, target) = MapExceptionToCode(ex);
        var details = BuildDetails(context, ex, debug);

        return StructuredError.Create(code, ex.Message, details, target, debug);
    }

    /// <summary>
    /// Maps an exception to an appropriate exit code.
    /// </summary>
    /// <param name="ex">The exception to map.</param>
    /// <returns>The exit code for this exception type.</returns>
    public static int ToExitCode(Exception ex)
    {
        return ex switch
        {
            // Auth errors
            AuthenticationException => ExitCodes.AuthError,

            // Connection errors
            DataverseConnectionException => ExitCodes.ConnectionError,
            ServiceProtectionException => ExitCodes.ConnectionError,
            TimeoutException => ExitCodes.ConnectionError,

            // Not found errors
            FileNotFoundException => ExitCodes.NotFoundError,
            DirectoryNotFoundException => ExitCodes.NotFoundError,

            // Validation/argument errors
            ArgumentException => ExitCodes.InvalidArguments,
            ConfigurationException => ExitCodes.InvalidArguments,

            // Cancellation
            OperationCanceledException => ExitCodes.Failure,

            // PPDS exceptions - use error code category to determine exit code
            PpdsNotFoundException => ExitCodes.NotFoundError,
            PpdsValidationException => ExitCodes.InvalidArguments,
            PpdsAuthException => ExitCodes.AuthError,
            PpdsThrottleException => ExitCodes.ConnectionError,
            PpdsException { ErrorCode: var code } when code.StartsWith("Plugin.") => ExitCodes.Failure,
            PpdsException { ErrorCode: var code } when code.StartsWith("Validation.") => ExitCodes.InvalidArguments,
            PpdsException { ErrorCode: var code } when code.StartsWith("Auth.") => ExitCodes.AuthError,
            PpdsException { ErrorCode: var code } when code.StartsWith("Connection.") => ExitCodes.ConnectionError,
            PpdsException => ExitCodes.Failure,

            // Default
            _ => ExitCodes.Failure
        };
    }

    /// <summary>
    /// Maps an exception and returns both the error and exit code.
    /// </summary>
    /// <param name="ex">The exception to map.</param>
    /// <param name="context">Optional context about what was happening.</param>
    /// <param name="debug">Whether to include full exception details.</param>
    /// <returns>A tuple of the structured error and exit code.</returns>
    public static (StructuredError Error, int ExitCode) MapWithExitCode(Exception ex, string? context = null, bool debug = false)
    {
        return (Map(ex, context, debug), ToExitCode(ex));
    }

    private static (string Code, string? Target) MapExceptionToCode(Exception ex)
    {
        return ex switch
        {
            // Auth exceptions
            AuthenticationException authEx when authEx.ErrorCode != null =>
                (authEx.ErrorCode, null),
            AuthenticationException =>
                (ErrorCodes.Auth.InvalidCredentials, null),

            // Connection exceptions
            DataverseConnectionException connEx =>
                (ErrorCodes.Connection.Failed, connEx.ConnectionName),

            ServiceProtectionException throttleEx =>
                (ErrorCodes.Connection.Throttled, throttleEx.ConnectionName),

            TimeoutException =>
                (ErrorCodes.Connection.Timeout, null),

            // Configuration exceptions
            ConfigurationException configEx =>
                (ErrorCodes.Validation.InvalidValue, configEx.PropertyName),

            // Schema exceptions
            SchemaMismatchException =>
                (ErrorCodes.Validation.SchemaInvalid, null),

            // File system exceptions
            FileNotFoundException fileEx =>
                (ErrorCodes.Validation.FileNotFound, fileEx.FileName),

            DirectoryNotFoundException =>
                (ErrorCodes.Validation.DirectoryNotFound, null),

            // Argument exceptions
            ArgumentNullException argNullEx =>
                (ErrorCodes.Validation.RequiredField, argNullEx.ParamName),

            ArgumentException argEx =>
                (ErrorCodes.Validation.InvalidValue, argEx.ParamName),

            // Operation exceptions
            OperationCanceledException =>
                (ErrorCodes.Operation.Cancelled, null),

            InvalidOperationException =>
                (ErrorCodes.Operation.Internal, null),

            NotSupportedException =>
                (ErrorCodes.Operation.NotSupported, null),

            // Ambiguous match (environment resolution)
            System.Reflection.AmbiguousMatchException =>
                (ErrorCodes.Connection.AmbiguousEnvironment, null),

            // PPDS exceptions - preserve the error code
            PpdsException ppdsEx => (ppdsEx.ErrorCode, null),

            // Default fallback
            _ => (ErrorCodes.Operation.Internal, null)
        };
    }

    private static string? BuildDetails(string? context, Exception ex, bool debug)
    {
        if (!debug && context == null)
        {
            return null;
        }

        if (debug)
        {
            return context != null
                ? $"{context}\n{ex.StackTrace}"
                : ex.StackTrace;
        }

        return context;
    }
}
