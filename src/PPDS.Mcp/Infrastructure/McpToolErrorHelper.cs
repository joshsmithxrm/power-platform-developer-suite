using System.Text.Json;
using ModelContextProtocol;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Helper for converting PPDS exceptions into structured MCP error content.
/// </summary>
/// <remarks>
/// The ModelContextProtocol SDK (v0.2.0-preview.3) catches tool exceptions and returns:
/// - For <see cref="McpException"/>: uses <c>ex.Message</c> verbatim in the error content.
/// - For other exceptions: returns a generic "An error occurred invoking 'tool'." message.
///
/// By throwing <see cref="McpException"/> with a JSON-encoded structured payload as the message,
/// we surface <see cref="PpdsException.ErrorCode"/>, <see cref="PpdsException.UserMessage"/>,
/// and <see cref="PpdsException.Context"/> to MCP clients so they can distinguish failure modes
/// programmatically. Shakedown finding H7 (2026-04-20).
/// </remarks>
public static class McpToolErrorHelper
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Throws an <see cref="McpException"/> carrying structured PPDS error information.
    /// The message is a JSON object containing <c>errorCode</c>, <c>userMessage</c>, and
    /// optional <c>context</c> so MCP clients can parse structured error details.
    /// </summary>
    /// <param name="ex">The originating <see cref="PpdsException"/>.</param>
    /// <exception cref="McpException">Always thrown.</exception>
    public static void ThrowStructuredError(PpdsException ex)
    {
        var payload = new StructuredErrorPayload
        {
            ErrorCode = ex.ErrorCode,
            UserMessage = ex.UserMessage,
            Context = ex.Context?.ToDictionary(k => k.Key, v => v.Value?.ToString())
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        throw new McpException(json, McpErrorCode.InternalError);
    }

    /// <summary>
    /// Throws an <see cref="McpException"/> carrying a generic error description for
    /// non-PPDS exceptions. Avoids leaking internal exception messages to MCP clients.
    /// A <see cref="MetadataValidationException"/> from the shared metadata authoring service
    /// is reported with its own error code, message and field instead (see
    /// <see cref="ThrowMetadataValidationError"/>).
    /// </summary>
    /// <param name="ex">The originating exception.</param>
    /// <exception cref="McpException">Always thrown.</exception>
    public static void ThrowStructuredError(Exception ex)
    {
        if (ex is MetadataValidationException validation)
            ThrowMetadataValidationError(validation);

        // The environments file holds a value PPDS cannot read: the message names the file, entry and field (#1829).
        if (ex is PPDS.Auth.Profiles.EnvironmentConfigFileException file)
        {
            var filePayload = new StructuredErrorPayload { ErrorCode = file.ErrorCode, UserMessage = file.Message };
            throw new McpException(JsonSerializer.Serialize(filePayload, SerializerOptions), McpErrorCode.InternalError);
        }

        // A statement too deeply nested or too large to process safely (#1952): the message is fixed text
        // that never contains the statement, so it is safe to return.
        if (QueryStack.IsDepthRefusal(ex))
        {
            var depth = QueryStack.AsNestedTooDeeply(ex);
            var depthPayload = new StructuredErrorPayload { ErrorCode = depth.ErrorCode, UserMessage = depth.Message };
            throw new McpException(JsonSerializer.Serialize(depthPayload, SerializerOptions), McpErrorCode.InternalError);
        }

        var payload = new StructuredErrorPayload
        {
            ErrorCode = ErrorCodes.Operation.Internal,
            UserMessage = "An internal error occurred. See server logs for details."
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        throw new McpException(json, McpErrorCode.InternalError);
    }

    /// <summary>
    /// Mirrors the CLI, which reports a metadata validation failure as its error code and message. The message is
    /// service-owned text built from caller-supplied identifiers; the inner exception (for example a Dataverse
    /// fault) is never read, so server diagnostic text stays withheld. The failing request field, when known, is
    /// added as <c>context.field</c>.
    /// </summary>
    private static void ThrowMetadataValidationError(MetadataValidationException ex)
    {
        var payload = new StructuredErrorPayload
        {
            ErrorCode = ex.ErrorCode,
            UserMessage = ex.Message,
            Context = ex.Field is null ? null : new Dictionary<string, string?> { ["field"] = ex.Field }
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        throw new McpException(json, McpErrorCode.InternalError);
    }
}

/// <summary>
/// JSON payload emitted in MCP tool error messages.
/// </summary>
internal sealed class StructuredErrorPayload
{
    /// <summary>Machine-readable error code (e.g. "Solution.ListFailed").</summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = "";

    /// <summary>Human-readable error description safe to display to users.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("userMessage")]
    public string UserMessage { get; init; } = "";

    /// <summary>Additional context key-value pairs, if present.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("context")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Context { get; init; }
}
