using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets detailed plugin trace information.
/// </summary>
[McpServerToolType]
public sealed class PluginTracesGetTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTracesGetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginTracesGetTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Gets detailed information about a specific plugin trace.
    /// </summary>
    /// <param name="traceId">The trace ID from ppds_plugin_traces_list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Full trace details including message block and exception.</returns>
    [McpServerTool(Name = "ppds_plugin_traces_get")]
    [Description("Get detailed information about a specific plugin trace including the full message block (trace output) and exception details. Use the trace ID from ppds_plugin_traces_list.")]
    public async Task<PluginTraceDetailResult> ExecuteAsync(
        [Description("Trace ID (GUID) from ppds_plugin_traces_list")]
        string traceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new ArgumentException("The 'traceId' parameter is required.", nameof(traceId));
        }

        if (!Guid.TryParse(traceId, out var id))
        {
            throw new ArgumentException($"Invalid trace ID format: '{traceId}'. Expected a GUID.", nameof(traceId));
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

        var trace = await traceService.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (trace == null)
        {
            throw new InvalidOperationException($"Trace with ID '{traceId}' not found.");
        }

        return new PluginTraceDetailResult
        {
            Id = trace.Id,
            TypeName = trace.TypeName,
            MessageName = trace.MessageName,
            PrimaryEntity = trace.PrimaryEntity,
            Mode = trace.Mode.ToString(),
            OperationType = trace.OperationType.ToString(),
            Depth = trace.Depth,
            CreatedOn = trace.CreatedOn,
            DurationMs = trace.DurationMs,
            ConstructorDurationMs = trace.ConstructorDurationMs,
            ExecutionStartTime = trace.ExecutionStartTime,
            HasException = trace.HasException,
            ExceptionDetails = trace.ExceptionDetails,
            MessageBlock = trace.MessageBlock,
            Configuration = trace.Configuration,
            CorrelationId = trace.CorrelationId,
            RequestId = trace.RequestId,
            PluginStepId = trace.PluginStepId
        };
    }
}

/// <summary>
/// Result of the plugin_traces_get tool.
/// </summary>
public sealed class PluginTraceDetailResult
{
    /// <summary>
    /// Trace ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Plugin type name (class name).
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    /// <summary>
    /// Message name (Create, Update, etc.).
    /// </summary>
    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    /// <summary>
    /// Primary entity logical name.
    /// </summary>
    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    /// <summary>
    /// Execution mode (Synchronous/Asynchronous).
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    /// <summary>
    /// Operation type (Plugin/WorkflowActivity).
    /// </summary>
    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = "";

    /// <summary>
    /// Execution depth (1 = top level).
    /// </summary>
    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    /// <summary>
    /// When the trace was created.
    /// </summary>
    [JsonPropertyName("createdOn")]
    public DateTime CreatedOn { get; set; }

    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    /// <summary>
    /// Constructor duration in milliseconds.
    /// </summary>
    [JsonPropertyName("constructorDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConstructorDurationMs { get; set; }

    /// <summary>
    /// Execution start time.
    /// </summary>
    [JsonPropertyName("executionStartTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExecutionStartTime { get; set; }

    /// <summary>
    /// Whether the execution threw an exception.
    /// </summary>
    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    /// <summary>
    /// Full exception details including stack trace.
    /// </summary>
    [JsonPropertyName("exceptionDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionDetails { get; set; }

    /// <summary>
    /// Message block containing trace output from the plugin.
    /// </summary>
    [JsonPropertyName("messageBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageBlock { get; set; }

    /// <summary>
    /// Unsecured configuration.
    /// </summary>
    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    /// <summary>
    /// Correlation ID for related traces.
    /// </summary>
    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Request ID.
    /// </summary>
    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RequestId { get; set; }

    /// <summary>
    /// Plugin step ID.
    /// </summary>
    [JsonPropertyName("pluginStepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PluginStepId { get; set; }
}
