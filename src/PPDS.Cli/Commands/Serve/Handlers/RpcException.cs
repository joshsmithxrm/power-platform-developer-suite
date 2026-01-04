using StreamJsonRpc;

namespace PPDS.Cli.Commands.Serve.Handlers;

/// <summary>
/// Custom exception for RPC errors that maps to JSON-RPC error responses.
/// Uses the error code as the JSON-RPC error code for structured error handling.
/// </summary>
public class RpcException : LocalRpcException
{
    /// <summary>
    /// The hierarchical error code (e.g., "Auth.ProfileNotFound").
    /// </summary>
    public string StructuredErrorCode { get; }

    /// <summary>
    /// Creates a new RPC exception with a structured error code.
    /// </summary>
    /// <param name="errorCode">Hierarchical error code from <see cref="Infrastructure.Errors.ErrorCodes"/>.</param>
    /// <param name="message">Human-readable error message.</param>
    public RpcException(string errorCode, string message)
        : base(message)
    {
        StructuredErrorCode = errorCode;

        // Store the structured error code in the error data
        // The client can use this for programmatic error handling
        ErrorData = new RpcErrorData
        {
            Code = errorCode,
            Message = message
        };
    }

    /// <summary>
    /// Creates a new RPC exception from an existing exception.
    /// </summary>
    /// <param name="errorCode">Hierarchical error code.</param>
    /// <param name="innerException">The original exception.</param>
    public RpcException(string errorCode, Exception innerException)
        : base(innerException.Message, innerException)
    {
        StructuredErrorCode = errorCode;
        ErrorData = new RpcErrorData
        {
            Code = errorCode,
            Message = innerException.Message,
#if DEBUG
            // Only include stack trace in debug builds to avoid leaking internal details
            Details = innerException.ToString()
#endif
        };
    }
}

/// <summary>
/// Structured error data included in JSON-RPC error responses.
/// </summary>
public class RpcErrorData
{
    /// <summary>
    /// Hierarchical error code for programmatic handling.
    /// </summary>
    public string Code { get; set; } = "";

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Optional additional details (e.g., stack trace in debug mode).
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Optional target of the error (e.g., parameter name, entity).
    /// </summary>
    public string? Target { get; set; }
}
