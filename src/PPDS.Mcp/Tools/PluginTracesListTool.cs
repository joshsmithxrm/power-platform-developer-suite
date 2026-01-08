using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists plugin trace logs.
/// </summary>
[McpServerToolType]
public sealed class PluginTracesListTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTracesListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginTracesListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Lists plugin trace logs with optional filtering.
    /// </summary>
    /// <param name="entity">Filter by primary entity name.</param>
    /// <param name="message">Filter by message name (Create, Update, etc.).</param>
    /// <param name="typeName">Filter by plugin type name.</param>
    /// <param name="errorsOnly">Show only traces with exceptions.</param>
    /// <param name="maxRows">Maximum rows to return (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of plugin trace summaries.</returns>
    [McpServerTool(Name = "ppds_plugin_traces_list")]
    [Description("List plugin trace logs from Dataverse. Use this to find plugin execution logs, identify errors, and debug plugin behavior. Requires trace logging to be enabled in the environment.")]
    public async Task<PluginTracesListResult> ExecuteAsync(
        [Description("Filter by entity logical name (e.g., 'account')")]
        string? entity = null,
        [Description("Filter by message name (e.g., 'Create', 'Update')")]
        string? message = null,
        [Description("Filter by plugin type name")]
        string? typeName = null,
        [Description("Show only traces with exceptions")]
        bool errorsOnly = false,
        [Description("Maximum rows to return (default 50, max 500)")]
        int maxRows = 50,
        CancellationToken cancellationToken = default)
    {
        maxRows = Math.Clamp(maxRows, 1, 500);

        var filter = new PluginTraceFilter
        {
            PrimaryEntity = entity,
            MessageName = message,
            TypeName = typeName,
            HasException = errorsOnly ? true : null
        };

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

        var traces = await traceService.ListAsync(filter, maxRows, cancellationToken).ConfigureAwait(false);

        return new PluginTracesListResult
        {
            Traces = traces.Select(t => new PluginTraceSummary
            {
                Id = t.Id,
                TypeName = t.TypeName,
                MessageName = t.MessageName,
                PrimaryEntity = t.PrimaryEntity,
                Mode = t.Mode.ToString(),
                Depth = t.Depth,
                CreatedOn = t.CreatedOn,
                DurationMs = t.DurationMs,
                HasException = t.HasException,
                CorrelationId = t.CorrelationId
            }).ToList(),
            Count = traces.Count
        };
    }
}

/// <summary>
/// Result of the plugin_traces_list tool.
/// </summary>
public sealed class PluginTracesListResult
{
    /// <summary>
    /// List of trace summaries.
    /// </summary>
    [JsonPropertyName("traces")]
    public List<PluginTraceSummary> Traces { get; set; } = [];

    /// <summary>
    /// Number of traces returned.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// Summary information about a plugin trace.
/// </summary>
public sealed class PluginTraceSummary
{
    /// <summary>
    /// Trace ID (use with ppds_plugin_traces_get for details).
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Plugin type name (class name).
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    /// <summary>
    /// Message name (Create, Update, Delete, etc.).
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
    /// Whether the execution threw an exception.
    /// </summary>
    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    /// <summary>
    /// Correlation ID for related traces (use with ppds_plugin_traces_timeline).
    /// </summary>
    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrelationId { get; set; }
}
