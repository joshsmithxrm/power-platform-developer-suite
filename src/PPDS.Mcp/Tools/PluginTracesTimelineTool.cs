using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that builds a plugin execution timeline.
/// </summary>
[McpServerToolType]
public sealed class PluginTracesTimelineTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTracesTimelineTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginTracesTimelineTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Builds an execution timeline from plugin traces with the same correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID from a trace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hierarchical timeline showing plugin execution order and nesting.</returns>
    [McpServerTool(Name = "ppds_plugin_traces_timeline")]
    [Description("Build an execution timeline showing all plugin executions for a single transaction. Use the correlationId from ppds_plugin_traces_list to see how plugins chain together and identify performance bottlenecks.")]
    public async Task<PluginTimelineResult> ExecuteAsync(
        [Description("Correlation ID (GUID) from a trace - groups all traces from the same transaction")]
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("The 'correlationId' parameter is required.", nameof(correlationId));
        }

        if (!Guid.TryParse(correlationId, out var id))
        {
            throw new ArgumentException($"Invalid correlation ID format: '{correlationId}'. Expected a GUID.", nameof(correlationId));
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

        var timeline = await traceService.BuildTimelineAsync(id, cancellationToken).ConfigureAwait(false);

        return new PluginTimelineResult
        {
            CorrelationId = id,
            Nodes = timeline.Select(MapNode).ToList(),
            TotalNodes = CountNodes(timeline)
        };
    }

    private static TimelineNodeDto MapNode(TimelineNode node)
    {
        return new TimelineNodeDto
        {
            Id = node.Trace.Id,
            TypeName = node.Trace.TypeName,
            MessageName = node.Trace.MessageName,
            PrimaryEntity = node.Trace.PrimaryEntity,
            Mode = node.Trace.Mode.ToString(),
            Depth = node.Trace.Depth,
            DurationMs = node.Trace.DurationMs,
            HasException = node.Trace.HasException,
            HierarchyDepth = node.HierarchyDepth,
            OffsetPercent = node.OffsetPercent,
            WidthPercent = node.WidthPercent,
            Children = node.Children.Select(MapNode).ToList()
        };
    }

    private static int CountNodes(IEnumerable<TimelineNode> nodes)
    {
        return nodes.Sum(n => 1 + CountNodes(n.Children));
    }
}

/// <summary>
/// Result of the plugin_traces_timeline tool.
/// </summary>
public sealed class PluginTimelineResult
{
    /// <summary>
    /// Correlation ID for this timeline.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Root timeline nodes (nested structure).
    /// </summary>
    [JsonPropertyName("nodes")]
    public List<TimelineNodeDto> Nodes { get; set; } = [];

    /// <summary>
    /// Total number of nodes in the timeline.
    /// </summary>
    [JsonPropertyName("totalNodes")]
    public int TotalNodes { get; set; }
}

/// <summary>
/// A node in the execution timeline.
/// </summary>
public sealed class TimelineNodeDto
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
    /// Execution depth (1 = top level).
    /// </summary>
    [JsonPropertyName("depth")]
    public int Depth { get; set; }

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
    /// Depth in the hierarchy (0 = root).
    /// </summary>
    [JsonPropertyName("hierarchyDepth")]
    public int HierarchyDepth { get; set; }

    /// <summary>
    /// Offset percentage for timeline visualization.
    /// </summary>
    [JsonPropertyName("offsetPercent")]
    public double OffsetPercent { get; set; }

    /// <summary>
    /// Width percentage for timeline visualization.
    /// </summary>
    [JsonPropertyName("widthPercent")]
    public double WidthPercent { get; set; }

    /// <summary>
    /// Child nodes (nested plugin calls).
    /// </summary>
    [JsonPropertyName("children")]
    public List<TimelineNodeDto> Children { get; set; } = [];
}
