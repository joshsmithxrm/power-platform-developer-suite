using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure.Output;

/// <summary>
/// Writes structured JSON output to stdout.
/// All output is valid JSON with a version field for schema evolution.
/// </summary>
/// <remarks>
/// The JSON output format is designed for machine consumption by the VS Code
/// extension, scripts, and CI/CD pipelines. The schema includes a version
/// field to enable forward compatibility.
/// </remarks>
public sealed class JsonOutputWriter : IOutputWriter
{
    /// <summary>
    /// Current JSON output schema version.
    /// Increment when making breaking changes to output format.
    /// </summary>
    public const string SchemaVersion = "1.0";

    private readonly TextWriter _writer;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <inheritdoc />
    public bool DebugMode { get; }

    /// <inheritdoc />
    public bool IsJsonMode => true;

    /// <summary>
    /// Creates a new JsonOutputWriter.
    /// </summary>
    /// <param name="writer">The text writer for output (defaults to Console.Out).</param>
    /// <param name="debugMode">Whether to include full error details.</param>
    public JsonOutputWriter(TextWriter? writer = null, bool debugMode = false)
    {
        _writer = writer ?? Console.Out;
        DebugMode = debugMode;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public void WriteResult<T>(CommandResult<T> result)
    {
        var output = new JsonResultEnvelope
        {
            Version = SchemaVersion,
            Success = result.Success,
            Data = result.Data,
            Error = result.Error != null ? ToJsonError(result.Error) : null,
            Results = result.Results?.Select(ToJsonItemResult).ToList(),
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        WriteJson(output);
    }

    /// <inheritdoc />
    public void WriteSuccess<T>(T data)
        => WriteSuccess(data, metadata: null);

    // Additive command metadata preserves the existing version-1 data contract.
    internal void WriteSuccess<T>(T data, object? metadata)
    {
        var output = new JsonResultEnvelope
        {
            Version = SchemaVersion,
            Success = true,
            Data = data,
            Metadata = metadata,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        WriteJson(output);
    }

    /// <inheritdoc />
    public void WriteError(StructuredError error)
    {
        var output = new JsonResultEnvelope
        {
            Version = SchemaVersion,
            Success = false,
            Error = ToJsonError(error),
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        WriteJson(output);
    }

    /// <inheritdoc />
    public void WritePartialSuccess<T>(T data, IEnumerable<ItemResult> results)
    {
        var resultsList = results.ToList();
        var output = new JsonResultEnvelope
        {
            Version = SchemaVersion,
            Success = false,
            Data = data,
            Results = resultsList.Select(ToJsonItemResult).ToList(),
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        WriteJson(output);
    }

    /// <inheritdoc />
    public void WriteMessage(string message)
    {
        var output = new JsonResultEnvelope
        {
            Version = SchemaVersion,
            Success = true,
            Data = new { message },
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        WriteJson(output);
    }

    /// <inheritdoc />
    public void WriteWarning(string message)
    {
        var output = new JsonResultEnvelope
        {
            Version = SchemaVersion,
            Success = true,
            Data = new { warning = message },
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        WriteJson(output);
    }

    private JsonErrorDto ToJsonError(StructuredError error)
    {
        return new JsonErrorDto
        {
            Code = error.Code,
            Message = error.Message,
            Details = error.Details,
            Target = error.Target
        };
    }

    private JsonItemResultDto ToJsonItemResult(ItemResult result)
    {
        return new JsonItemResultDto
        {
            Name = result.Name,
            Success = result.Success,
            Data = result.Data,
            Error = result.Error != null ? ToJsonError(result.Error) : null
        };
    }

    private void WriteJson(object data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        _writer.WriteLine(json);
        _writer.Flush();
    }

    // DTOs for JSON serialization
    private sealed class JsonResultEnvelope
    {
        public string? Version { get; set; }
        public bool Success { get; set; }
        public object? Data { get; set; }
        public object? Metadata { get; set; }
        public JsonErrorDto? Error { get; set; }
        public List<JsonItemResultDto>? Results { get; set; }
        public string? Timestamp { get; set; }
    }

    private sealed class JsonErrorDto
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
        public string? Details { get; set; }
        public string? Target { get; set; }
    }

    private sealed class JsonItemResultDto
    {
        public string? Name { get; set; }
        public bool Success { get; set; }
        public object? Data { get; set; }
        public JsonErrorDto? Error { get; set; }
    }
}
