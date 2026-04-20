using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Services.Solutions;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets components for a Dataverse solution.
/// </summary>
[McpServerToolType]
public sealed class SolutionsComponentsTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SolutionsComponentsTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public SolutionsComponentsTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Gets the components of a specific solution.
    /// </summary>
    /// <param name="solutionId">The solution ID (GUID).</param>
    /// <param name="componentType">Optional filter by component type code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Solution components grouped by type.</returns>
    [McpServerTool(Name = "ppds_solutions_components")]
    [Description("Get components of a Dataverse solution. Returns entities, workflows, plugins, and other components. Use the solution id from ppds_solutions_list. Optionally filter by component type code.")]
    public async Task<SolutionsComponentsResult> ExecuteAsync(
        [Description("The solution ID (GUID) from ppds_solutions_list")]
        string solutionId,
        [Description("Optional component type code to filter by (e.g., 1 = Entity, 26 = View, 29 = Workflow)")]
        int? componentType = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(solutionId, out var id))
        {
            throw new ArgumentException($"Invalid solution ID: '{solutionId}'. Must be a valid GUID.");
        }

        await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(solutionId), solutionId)).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<ISolutionService>();

        var components = await service.GetComponentsAsync(
            id,
            componentType: componentType,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new SolutionsComponentsResult
        {
            SolutionId = solutionId,
            TotalCount = components.Count,
            Components = components.Select(c => new ComponentDto
            {
                Id = c.Id.ToString(),
                ObjectId = c.ObjectId.ToString(),
                ComponentType = c.ComponentType,
                ComponentTypeName = c.ComponentTypeName,
                DisplayName = c.DisplayName,
                LogicalName = c.LogicalName,
                SchemaName = c.SchemaName,
                IsMetadata = c.IsMetadata
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the solutions_components tool.
/// </summary>
public sealed class SolutionsComponentsResult
{
    /// <summary>
    /// The solution ID that was queried.
    /// </summary>
    [JsonPropertyName("solutionId")]
    public string SolutionId { get; set; } = "";

    /// <summary>
    /// Total number of components.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>
    /// List of solution components.
    /// </summary>
    [JsonPropertyName("components")]
    public List<ComponentDto> Components { get; set; } = [];
}

/// <summary>
/// A solution component.
/// </summary>
public sealed class ComponentDto
{
    /// <summary>
    /// Component record ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// The object ID of the component (e.g., entity ID, workflow ID).
    /// </summary>
    [JsonPropertyName("objectId")]
    public string ObjectId { get; set; } = "";

    /// <summary>
    /// Component type code.
    /// </summary>
    [JsonPropertyName("componentType")]
    public int ComponentType { get; set; }

    /// <summary>
    /// Human-readable component type name.
    /// </summary>
    [JsonPropertyName("componentTypeName")]
    public string ComponentTypeName { get; set; } = "";

    /// <summary>
    /// Display name of the component.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Logical name of the component.
    /// </summary>
    [JsonPropertyName("logicalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalName { get; set; }

    /// <summary>
    /// Schema name of the component.
    /// </summary>
    [JsonPropertyName("schemaName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaName { get; set; }

    /// <summary>
    /// Whether this is a metadata component.
    /// </summary>
    [JsonPropertyName("isMetadata")]
    public bool IsMetadata { get; set; }
}
