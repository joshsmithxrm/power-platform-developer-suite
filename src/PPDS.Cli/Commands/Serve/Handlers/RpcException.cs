using System.Text.Json.Serialization;
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
    /// Creates a new RPC exception with custom error data.
    /// </summary>
    /// <param name="errorCode">Hierarchical error code from <see cref="Infrastructure.Errors.ErrorCodes"/>.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="errorData">Custom error data to include in the JSON-RPC error response.</param>
    public RpcException(string errorCode, string message, RpcErrorData errorData)
        : base(message)
    {
        StructuredErrorCode = errorCode;
        ErrorData = errorData;
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
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>
    /// Optional additional details (e.g., stack trace in debug mode).
    /// </summary>
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    /// <summary>
    /// Optional target of the error (e.g., parameter name, entity).
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>
    /// Whether the user needs to re-authenticate (from PpdsAuthException).
    /// </summary>
    [JsonPropertyName("requiresReauthentication")]
    public bool? RequiresReauthentication { get; set; }

    /// <summary>
    /// Seconds to wait before retrying (from PpdsThrottleException).
    /// </summary>
    [JsonPropertyName("retryAfterSeconds")]
    public double? RetryAfterSeconds { get; set; }

    /// <summary>
    /// List of validation errors (from PpdsValidationException).
    /// </summary>
    [JsonPropertyName("validationErrors")]
    public List<RpcValidationError>? ValidationErrors { get; set; }

    /// <summary>
    /// The type of resource that was not found (from PpdsNotFoundException).
    /// </summary>
    [JsonPropertyName("resourceType")]
    public string? ResourceType { get; set; }

    /// <summary>
    /// The identifier of the resource that was not found (from PpdsNotFoundException).
    /// </summary>
    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; set; }
}

/// <summary>
/// A single validation error for RPC responses.
/// </summary>
public class RpcValidationError
{
    /// <summary>The field that failed validation.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    /// <summary>The validation error message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// Extended error data for DML safety violations.
/// Includes flags that the TypeScript client can use for programmatic flow control
/// (e.g., showing a confirmation dialog or blocking execution outright).
/// </summary>
public sealed class DmlSafetyErrorData : RpcErrorData
{
    /// <summary>Whether the DML operation is blocked outright (e.g., DELETE without WHERE).</summary>
    [JsonPropertyName("dmlBlocked")]
    public bool DmlBlocked { get; init; }

    /// <summary>Whether the DML operation requires user confirmation before execution.</summary>
    [JsonPropertyName("dmlConfirmationRequired")]
    public bool DmlConfirmationRequired { get; init; }
}
