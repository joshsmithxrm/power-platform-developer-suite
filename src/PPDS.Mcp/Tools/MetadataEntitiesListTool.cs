using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists all entities (tables) in a Dataverse environment.
/// </summary>
[McpServerToolType]
public sealed class MetadataEntitiesListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataEntitiesListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataEntitiesListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists all entities (tables) in the connected Dataverse environment.
    /// </summary>
    /// <param name="customOnly">If true, only return custom entities.</param>
    /// <param name="filter">Optional filter pattern for entity logical names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of entity summaries.</returns>
    [McpServerTool(Name = "ppds_metadata_entities")]
    [Description("Lists all entities (tables) in the connected Dataverse environment. Returns entity logical names, display names, schema names, and ownership type. Use this to discover available entities before querying with ppds_metadata_entity for full details.")]
    public async Task<MetadataEntitiesResult> ExecuteAsync(
        [Description("If true, only return custom entities (not system entities).")] bool customOnly = false,
        [Description("Optional filter pattern to match entity logical names. Supports * wildcard (e.g., 'account*', '*custom*').")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken)
            .ConfigureAwait(false);

        var metadataService = serviceProvider
            .GetRequiredService<IMetadataService>();

        var entities = await metadataService
            .GetEntitiesAsync(customOnly, filter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new MetadataEntitiesResult
        {
            Entities = entities.Select(e => new EntityListItem
            {
                LogicalName = e.LogicalName,
                DisplayName = e.DisplayName,
                SchemaName = e.SchemaName,
                IsCustomEntity = e.IsCustomEntity,
                IsManaged = e.IsManaged,
                OwnershipType = e.OwnershipType,
                Description = e.Description
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the metadata_entities tool.
/// </summary>
public sealed class MetadataEntitiesResult
{
    /// <summary>
    /// Total number of entities returned.
    /// </summary>
    [JsonPropertyName("entityCount")]
    public int EntityCount => Entities.Count;

    /// <summary>
    /// List of entity summaries.
    /// </summary>
    [JsonPropertyName("entities")]
    public List<EntityListItem> Entities { get; set; } = [];
}

/// <summary>
/// Summary information about an entity for list views.
/// </summary>
public sealed class EntityListItem
{
    /// <summary>
    /// Entity logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>
    /// Whether this is a custom entity.
    /// </summary>
    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    /// <summary>
    /// Whether this entity is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Ownership type (UserOwned, OrganizationOwned, None).
    /// </summary>
    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    /// <summary>
    /// Entity description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
