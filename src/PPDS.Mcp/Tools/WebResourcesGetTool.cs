using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets web resource content for viewing or analysis.
/// </summary>
[McpServerToolType]
public sealed class WebResourcesGetTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebResourcesGetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public WebResourcesGetTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Gets web resource content for viewing or analysis.
    /// </summary>
    /// <param name="id">The web resource ID (GUID).</param>
    /// <param name="published">If true, gets published content; if false, gets unpublished (draft) content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Web resource content and metadata.</returns>
    [McpServerTool(Name = "ppds_web_resources_get")]
    [Description("Get web resource content for viewing or analysis. Returns decoded text content for text types (JS, HTML, CSS, XML, etc.) and metadata-only for binary types (PNG, JPG, GIF). Use the id from ppds_web_resources_list.")]
    public async Task<WebResourceGetResult> ExecuteAsync(
        [Description("The web resource ID (GUID) from ppds_web_resources_list")]
        string id,
        [Description("If true, gets published content; if false, gets unpublished/draft content (default false)")]
        bool published = false,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(id), id)).ConfigureAwait(false);

        if (!Guid.TryParse(id, out var resourceId))
        {
            throw new ArgumentException($"Invalid web resource ID: '{id}'. Must be a valid GUID.");
        }
        var service = serviceProvider.GetRequiredService<IWebResourceService>();

        var content = await service.GetContentAsync(resourceId, published, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Web resource '{id}' not found.");

        var info = new WebResourceInfo(
            content.Id, content.Name, null, content.WebResourceType,
            false, null, null, null, content.ModifiedOn);

        return new WebResourceGetResult
        {
            Id = content.Id.ToString(),
            Name = content.Name,
            Type = content.WebResourceType,
            TypeName = info.TypeName,
            IsTextType = info.IsTextType,
            Content = info.IsTextType ? content.Content : null,
            ModifiedOn = content.ModifiedOn?.ToString("o"),
            Note = info.IsTextType ? null : "Binary web resource content cannot be displayed. Use the Maker Portal to view this resource."
        };
    }
}

/// <summary>
/// Result of the web_resources_get tool.
/// </summary>
public sealed class WebResourceGetResult
{
    /// <summary>
    /// Web resource ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// Logical name of the web resource.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Numeric type code.
    /// </summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    /// <summary>
    /// Human-readable type name.
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    /// <summary>
    /// Whether this is a text-based type.
    /// </summary>
    [JsonPropertyName("isTextType")]
    public bool IsTextType { get; set; }

    /// <summary>
    /// Decoded text content (null for binary types).
    /// </summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>
    /// When the resource was last modified (ISO 8601).
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    /// <summary>
    /// Informational note (e.g., for binary types that can't be displayed).
    /// </summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
