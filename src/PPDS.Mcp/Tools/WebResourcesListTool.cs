using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Services.WebResources;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists web resources in a Dataverse environment.
/// </summary>
[McpServerToolType]
public sealed class WebResourcesListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebResourcesListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public WebResourcesListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists web resources in the current environment, optionally filtered by solution.
    /// </summary>
    /// <param name="solutionId">Optional solution ID to filter by (GUID string).</param>
    /// <param name="textOnly">If true, only return text-based web resources (default true).</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of web resource summaries.</returns>
    [McpServerTool(Name = "ppds_web_resources_list")]
    [Description("List web resources in a Dataverse environment, optionally filtered by solution. Returns name, type, managed status, and modification info. Use textOnly=true (default) to exclude binary types like images.")]
    public async Task<WebResourcesListResult> ExecuteAsync(
        [Description("Optional solution ID (GUID) to filter web resources by solution")]
        string? solutionId = null,
        [Description("If true, only return text-based web resources like JS/HTML/CSS (default true)")]
        bool textOnly = true,
        CancellationToken cancellationToken = default)
    {
        Guid? parsedSolutionId = null;
        if (solutionId != null)
        {
            if (!Guid.TryParse(solutionId, out var parsed))
            {
                throw new ArgumentException($"Invalid solution ID: '{solutionId}'. Must be a valid GUID.");
            }
            parsedSolutionId = parsed;
        }

        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IWebResourceService>();

        var result = await service.ListAsync(parsedSolutionId, textOnly, cancellationToken).ConfigureAwait(false);

        return new WebResourcesListResult
        {
            TotalCount = result.TotalCount,
            Resources = result.Items.Select(r => new WebResourceSummary
            {
                Id = r.Id.ToString(),
                Name = r.Name,
                DisplayName = r.DisplayName,
                Type = r.WebResourceType,
                TypeName = r.TypeName,
                IsManaged = r.IsManaged,
                IsTextType = r.IsTextType,
                ModifiedBy = r.ModifiedByName,
                ModifiedOn = r.ModifiedOn?.ToString("o")
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the web_resources_list tool.
/// </summary>
public sealed class WebResourcesListResult
{
    /// <summary>
    /// List of web resource summaries.
    /// </summary>
    [JsonPropertyName("resources")]
    public List<WebResourceSummary> Resources { get; set; } = [];

    /// <summary>Total count of records.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Summary information about a web resource.
/// </summary>
public sealed class WebResourceSummary
{
    /// <summary>
    /// Web resource ID (use with ppds_web_resources_get for content).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// Logical name of the web resource (e.g., "publisher_/scripts/main.js").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Display name of the web resource.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Numeric type code (1=HTML, 2=CSS, 3=JS, 4=XML, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    /// <summary>
    /// Human-readable type name.
    /// </summary>
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    /// <summary>
    /// Whether the web resource is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Whether this is a text-based type that can be viewed/edited.
    /// </summary>
    [JsonPropertyName("isTextType")]
    public bool IsTextType { get; set; }

    /// <summary>
    /// Name of the user who last modified the resource.
    /// </summary>
    [JsonPropertyName("modifiedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedBy { get; set; }

    /// <summary>
    /// When the resource was last modified (ISO 8601).
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}
