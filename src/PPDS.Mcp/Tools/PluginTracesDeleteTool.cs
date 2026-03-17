using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that deletes plugin trace logs.
/// </summary>
[McpServerToolType]
public sealed class PluginTracesDeleteTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTracesDeleteTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginTracesDeleteTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Deletes plugin trace logs by specific IDs or by age threshold.
    /// </summary>
    /// <param name="ids">Trace IDs to delete (array of GUID strings).</param>
    /// <param name="olderThanDays">Delete all traces older than this many days.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with count of deleted traces.</returns>
    [McpServerTool(Name = "ppds_plugin_traces_delete")]
    [Description("Delete plugin trace logs. Provide either specific IDs for targeted deletion, or olderThanDays for bulk cleanup. Exactly one parameter must be specified.")]
    public async Task<PluginTracesDeleteResult> ExecuteAsync(
        [Description("Trace IDs to delete (array of GUID strings from ppds_plugin_traces_list)")]
        string[]? ids = null,
        [Description("Delete all traces older than this many days (minimum 1). Use for bulk cleanup.")]
        int? olderThanDays = null,
        CancellationToken cancellationToken = default)
    {
        if (ids == null && olderThanDays == null)
        {
            return new PluginTracesDeleteResult
            {
                Error = "At least one parameter is required: provide 'ids' for targeted deletion or 'olderThanDays' for bulk cleanup."
            };
        }

        if (ids != null && olderThanDays != null)
        {
            return new PluginTracesDeleteResult
            {
                Error = "Provide only one of 'ids' or 'olderThanDays', not both."
            };
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

        if (ids != null)
        {
            return await DeleteByIdsAsync(traceService, ids, cancellationToken).ConfigureAwait(false);
        }

        return await DeleteOlderThanAsync(traceService, olderThanDays!.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PluginTracesDeleteResult> DeleteByIdsAsync(
        IPluginTraceService traceService,
        string[] ids,
        CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return new PluginTracesDeleteResult
            {
                Error = "The 'ids' array must contain at least one trace ID."
            };
        }

        var guids = new List<Guid>(ids.Length);
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return new PluginTracesDeleteResult
                {
                    Error = $"Invalid trace ID format: '{id}'. Expected a GUID."
                };
            }

            guids.Add(guid);
        }

        var deletedCount = await traceService.DeleteByIdsAsync(guids, progress: null, cancellationToken).ConfigureAwait(false);

        return new PluginTracesDeleteResult
        {
            DeletedCount = deletedCount
        };
    }

    private static async Task<PluginTracesDeleteResult> DeleteOlderThanAsync(
        IPluginTraceService traceService,
        int olderThanDays,
        CancellationToken cancellationToken)
    {
        if (olderThanDays < 1)
        {
            return new PluginTracesDeleteResult
            {
                Error = "The 'olderThanDays' parameter must be at least 1."
            };
        }

        var olderThan = TimeSpan.FromDays(olderThanDays);
        var deletedCount = await traceService.DeleteOlderThanAsync(olderThan, progress: null, cancellationToken).ConfigureAwait(false);

        return new PluginTracesDeleteResult
        {
            DeletedCount = deletedCount
        };
    }
}

/// <summary>
/// Result of the plugin_traces_delete tool.
/// </summary>
public sealed class PluginTracesDeleteResult
{
    /// <summary>
    /// Number of traces deleted.
    /// </summary>
    [JsonPropertyName("deletedCount")]
    public int DeletedCount { get; set; }

    /// <summary>
    /// Error message if the operation failed validation.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}
