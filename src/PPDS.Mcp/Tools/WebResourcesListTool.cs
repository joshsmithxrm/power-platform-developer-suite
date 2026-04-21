using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Infrastructure.Errors;
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
    /// <param name="maxRows">Maximum number of records per page (default 100, max 500).</param>
    /// <param name="nextPageToken">Cursor token from a previous call to fetch the next page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Page of web resource summaries with optional nextPageToken for continuation.</returns>
    [McpServerTool(Name = "ppds_web_resources_list")]
    [Description("List web resources in a Dataverse environment, optionally filtered by solution. Returns name, type, managed status, and modification info. Use textOnly=true (default) to exclude binary types like images. Supports pagination via maxRows and nextPageToken parameters — when more records exist the response includes a nextPageToken to pass on the next call.")]
    public async Task<WebResourcesListResult> ExecuteAsync(
        [Description("Optional solution ID (GUID) to filter web resources by solution")]
        string? solutionId = null,
        [Description("If true, only return text-based web resources like JS/HTML/CSS (default true)")]
        bool textOnly = true,
        [Description("Maximum records per page (default 100, max 500)")]
        int maxRows = 100,
        [Description("Pagination cursor from a previous call's nextPageToken field")]
        string? nextPageToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            maxRows = Math.Clamp(maxRows, 1, 500);

            Guid? parsedSolutionId = null;
            if (solutionId != null)
            {
                if (!Guid.TryParse(solutionId, out var parsed))
                {
                    throw new ArgumentException($"Invalid solution ID: '{solutionId}'. Must be a valid GUID.");
                }
                parsedSolutionId = parsed;
            }

            // Decode offset from cursor token (base-10 integer, base64-encoded).
            int offset = 0;
            if (nextPageToken != null)
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(nextPageToken));
                    offset = int.Parse(decoded);
                }
                catch
                {
                    throw new ArgumentException("Invalid nextPageToken — use the value returned by a previous ppds_web_resources_list call.");
                }
            }

            await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
            var service = serviceProvider.GetRequiredService<IWebResourceService>();

            var result = await service.ListAsync(parsedSolutionId, textOnly, cancellationToken).ConfigureAwait(false);

            // Apply offset + page window (Constitution I4: show X of Y, provide nextPageToken when truncated).
            var page = result.Items.Skip(offset).Take(maxRows).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < result.TotalCount;

            string? outToken = hasMore
                ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(nextOffset.ToString()))
                : null;

            return new WebResourcesListResult
            {
                TotalCount = result.TotalCount,
                ReturnedCount = page.Count,
                Offset = offset,
                NextPageToken = outToken,
                Resources = page.Select(r => new WebResourceSummary
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
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
    }
}

/// <summary>
/// Result of the web_resources_list tool.
/// </summary>
public sealed class WebResourcesListResult
{
    /// <summary>
    /// List of web resource summaries for this page.
    /// </summary>
    [JsonPropertyName("resources")]
    public List<WebResourceSummary> Resources { get; set; } = [];

    /// <summary>Total count of records matching the filter (all pages).</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Number of records returned in this page.</summary>
    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; set; }

    /// <summary>Zero-based offset of the first record in this page.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>
    /// Cursor token to pass as nextPageToken to retrieve the next page.
    /// Null when this is the last page.
    /// </summary>
    [JsonPropertyName("nextPageToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextPageToken { get; set; }
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
