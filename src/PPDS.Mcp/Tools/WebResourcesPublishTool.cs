using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Services.WebResources;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that publishes specific web resources to make changes live.
/// </summary>
[McpServerToolType]
public sealed class WebResourcesPublishTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebResourcesPublishTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public WebResourcesPublishTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Publishes specific web resources to make changes live.
    /// </summary>
    /// <param name="ids">Array of web resource IDs (GUIDs) to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with count of published resources.</returns>
    [McpServerTool(Name = "ppds_web_resources_publish")]
    [Description("Publish specific web resources to make changes live. After updating web resource content, you must publish for changes to take effect. Provide one or more web resource IDs.")]
    public async Task<WebResourcesPublishResult> ExecuteAsync(
        [Description("Array of web resource IDs (GUIDs) to publish")]
        string[] ids,
        CancellationToken cancellationToken = default)
    {
        if (Context.IsReadOnly)
        {
            throw new InvalidOperationException(
                "Cannot publish web resources: this MCP session is read-only.");
        }

        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Length == 0)
        {
            throw new ArgumentException("At least one web resource ID is required.");
        }

        var parsedIds = new List<Guid>(ids.Length);
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var parsed))
            {
                throw new ArgumentException($"Invalid web resource ID: '{id}'. Must be a valid GUID.");
            }
            parsedIds.Add(parsed);
        }

        await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(ids), ids)).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IWebResourceService>();

        var count = await service.PublishAsync(parsedIds, cancellationToken).ConfigureAwait(false);

        return new WebResourcesPublishResult
        {
            PublishedCount = count,
            Message = $"Successfully published {count} web resource{(count != 1 ? "s" : "")}."
        };
    }
}

/// <summary>
/// Result of the web_resources_publish tool.
/// </summary>
public sealed class WebResourcesPublishResult
{
    /// <summary>
    /// Number of web resources that were published.
    /// </summary>
    [JsonPropertyName("publishedCount")]
    public int PublishedCount { get; set; }

    /// <summary>
    /// Human-readable result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
