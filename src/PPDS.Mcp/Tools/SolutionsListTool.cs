using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists Dataverse solutions.
/// </summary>
[McpServerToolType]
public sealed class SolutionsListTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="SolutionsListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public SolutionsListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Lists Dataverse solutions for the current environment.
    /// </summary>
    /// <param name="filter">Optional filter by solution name.</param>
    /// <param name="includeManaged">Include managed solutions (default false).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of solution summaries.</returns>
    [McpServerTool(Name = "ppds_solutions_list")]
    [Description("List Dataverse solutions for the current environment. Shows solution name, version, publisher, and managed status. Use includeManaged to also show managed (system) solutions.")]
    public async Task<SolutionsListResult> ExecuteAsync(
        [Description("Optional filter by solution name (friendly name or unique name)")]
        string? filter = null,
        [Description("Include managed solutions in the results (default false)")]
        bool includeManaged = false,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<ISolutionService>();

        var result = await service.ListAsync(
            filter: filter,
            includeManaged: includeManaged,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new SolutionsListResult
        {
            TotalCount = result.TotalCount,
            Solutions = result.Items.Select(s => new SolutionSummary
            {
                Id = s.Id.ToString(),
                UniqueName = s.UniqueName,
                FriendlyName = s.FriendlyName,
                Version = s.Version,
                IsManaged = s.IsManaged,
                PublisherName = s.PublisherName,
                ModifiedOn = s.ModifiedOn?.ToString("o"),
                InstalledOn = s.InstalledOn?.ToString("o")
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the solutions_list tool.
/// </summary>
public sealed class SolutionsListResult
{
    /// <summary>
    /// List of solution summaries.
    /// </summary>
    [JsonPropertyName("solutions")]
    public List<SolutionSummary> Solutions { get; set; } = [];

    /// <summary>Total count of records matching the query.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Summary information about a Dataverse solution.
/// </summary>
public sealed class SolutionSummary
{
    /// <summary>
    /// Solution ID (use with ppds_solutions_components for details).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// Solution unique name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// Solution display name.
    /// </summary>
    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    /// <summary>
    /// Solution version string.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// Whether the solution is managed.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Publisher name.
    /// </summary>
    [JsonPropertyName("publisherName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublisherName { get; set; }

    /// <summary>
    /// When the solution was last modified (ISO 8601).
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    /// <summary>
    /// When the solution was installed (ISO 8601).
    /// </summary>
    [JsonPropertyName("installedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledOn { get; set; }
}
