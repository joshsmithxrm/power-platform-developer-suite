using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that retrieves detailed entity metadata.
/// </summary>
[McpServerToolType]
public sealed class MetadataEntityTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataEntityTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataEntityTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Gets detailed metadata for a Dataverse entity.
    /// </summary>
    /// <param name="entityName">Entity logical name.</param>
    /// <param name="includeAttributes">Include attribute details (default true).</param>
    /// <param name="includeRelationships">Include relationship details (default false).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Entity metadata including attributes and relationships.</returns>
    [McpServerTool(Name = "ppds_metadata_entity")]
    [Description("Get detailed metadata for a Dataverse entity including attributes, relationships, and keys. Use this to understand entity structure, field types, and how entities relate to each other.")]
    public async Task<EntityMetadataResult> ExecuteAsync(
        [Description("Entity logical name (e.g., 'account', 'contact')")]
        string entityName,
        [Description("Include attribute details (default true)")]
        bool includeAttributes = true,
        [Description("Include relationship details (default false)")]
        bool includeRelationships = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new ArgumentException("The 'entityName' parameter is required.", nameof(entityName));
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var metadataService = serviceProvider.GetRequiredService<IMetadataService>();

        var entity = await metadataService.GetEntityAsync(
            entityName,
            includeAttributes: includeAttributes,
            includeRelationships: includeRelationships,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = new EntityMetadataResult
        {
            LogicalName = entity.LogicalName,
            DisplayName = entity.DisplayName,
            DisplayCollectionName = entity.PluralName,
            Description = entity.Description,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            SchemaName = entity.SchemaName,
            IsCustomEntity = entity.IsCustomEntity,
            IsActivityEntity = entity.IsActivity,
            OwnershipType = entity.OwnershipType
        };

        if (includeAttributes)
        {
            result.Attributes = entity.Attributes.Select(a => new MetadataAttributeInfo
            {
                LogicalName = a.LogicalName,
                DisplayName = a.DisplayName,
                Description = a.Description,
                AttributeType = a.AttributeType,
                SchemaName = a.SchemaName,
                IsCustomAttribute = a.IsCustomAttribute,
                RequiredLevel = a.RequiredLevel,
                MaxLength = a.MaxLength,
                MinValue = a.MinValue,
                MaxValue = a.MaxValue,
                Precision = a.Precision,
                TargetEntities = a.Targets?.ToList()
            }).OrderBy(a => a.LogicalName).ToList();
        }

        if (includeRelationships)
        {
            result.OneToManyRelationships = entity.OneToManyRelationships?.Select(r => new RelationshipInfo
            {
                SchemaName = r.SchemaName,
                ReferencingEntity = r.ReferencingEntity,
                ReferencingAttribute = r.ReferencingAttribute,
                ReferencedEntity = r.ReferencedEntity,
                ReferencedAttribute = r.ReferencedAttribute,
                RelationshipType = "OneToMany"
            }).ToList();

            result.ManyToOneRelationships = entity.ManyToOneRelationships?.Select(r => new RelationshipInfo
            {
                SchemaName = r.SchemaName,
                ReferencingEntity = r.ReferencingEntity,
                ReferencingAttribute = r.ReferencingAttribute,
                ReferencedEntity = r.ReferencedEntity,
                ReferencedAttribute = r.ReferencedAttribute,
                RelationshipType = "ManyToOne"
            }).ToList();

            result.ManyToManyRelationships = entity.ManyToManyRelationships?.Select(r => new ManyToManyRelationshipInfo
            {
                SchemaName = r.SchemaName,
                Entity1LogicalName = r.Entity1LogicalName,
                Entity1Attribute = r.Entity1IntersectAttribute,
                Entity2LogicalName = r.Entity2LogicalName,
                Entity2Attribute = r.Entity2IntersectAttribute,
                IntersectEntityName = r.IntersectEntityName
            }).ToList();
        }

        return result;
    }
}

/// <summary>
/// Result of the metadata_entity tool.
/// </summary>
public sealed class EntityMetadataResult
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Display name for collections (plural form).
    /// </summary>
    [JsonPropertyName("displayCollectionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayCollectionName { get; set; }

    /// <summary>
    /// Entity description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Primary ID attribute name.
    /// </summary>
    [JsonPropertyName("primaryIdAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryIdAttribute { get; set; }

    /// <summary>
    /// Primary name attribute name.
    /// </summary>
    [JsonPropertyName("primaryNameAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryNameAttribute { get; set; }

    /// <summary>
    /// Schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaName { get; set; }

    /// <summary>
    /// Whether this is a custom entity.
    /// </summary>
    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    /// <summary>
    /// Whether this is an activity entity.
    /// </summary>
    [JsonPropertyName("isActivityEntity")]
    public bool IsActivityEntity { get; set; }

    /// <summary>
    /// Ownership type (UserOwned, OrganizationOwned, None).
    /// </summary>
    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    /// <summary>
    /// List of attributes.
    /// </summary>
    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MetadataAttributeInfo>? Attributes { get; set; }

    /// <summary>
    /// One-to-many relationships (this entity is referenced).
    /// </summary>
    [JsonPropertyName("oneToManyRelationships")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RelationshipInfo>? OneToManyRelationships { get; set; }

    /// <summary>
    /// Many-to-one relationships (this entity references).
    /// </summary>
    [JsonPropertyName("manyToOneRelationships")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RelationshipInfo>? ManyToOneRelationships { get; set; }

    /// <summary>
    /// Many-to-many relationships.
    /// </summary>
    [JsonPropertyName("manyToManyRelationships")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManyToManyRelationshipInfo>? ManyToManyRelationships { get; set; }
}

/// <summary>
/// Attribute metadata information.
/// </summary>
public sealed class MetadataAttributeInfo
{
    /// <summary>
    /// Attribute logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Attribute description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Attribute type.
    /// </summary>
    [JsonPropertyName("attributeType")]
    public string AttributeType { get; set; } = "";

    /// <summary>
    /// Schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaName { get; set; }

    /// <summary>
    /// Whether this is a custom attribute.
    /// </summary>
    [JsonPropertyName("isCustomAttribute")]
    public bool IsCustomAttribute { get; set; }

    /// <summary>
    /// Required level (None, Recommended, Required).
    /// </summary>
    [JsonPropertyName("requiredLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiredLevel { get; set; }

    /// <summary>
    /// Maximum length for string attributes.
    /// </summary>
    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    /// <summary>
    /// Minimum value for numeric attributes.
    /// </summary>
    [JsonPropertyName("minValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MinValue { get; set; }

    /// <summary>
    /// Maximum value for numeric attributes.
    /// </summary>
    [JsonPropertyName("maxValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MaxValue { get; set; }

    /// <summary>
    /// Decimal precision.
    /// </summary>
    [JsonPropertyName("precision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Precision { get; set; }

    /// <summary>
    /// Target entities for lookup attributes.
    /// </summary>
    [JsonPropertyName("targetEntities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? TargetEntities { get; set; }
}

/// <summary>
/// One-to-many or many-to-one relationship information.
/// </summary>
public sealed class RelationshipInfo
{
    /// <summary>
    /// Relationship schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>
    /// Referencing entity (the "many" side).
    /// </summary>
    [JsonPropertyName("referencingEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferencingEntity { get; set; }

    /// <summary>
    /// Referencing attribute (lookup field).
    /// </summary>
    [JsonPropertyName("referencingAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferencingAttribute { get; set; }

    /// <summary>
    /// Referenced entity (the "one" side).
    /// </summary>
    [JsonPropertyName("referencedEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferencedEntity { get; set; }

    /// <summary>
    /// Referenced attribute (primary key).
    /// </summary>
    [JsonPropertyName("referencedAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferencedAttribute { get; set; }

    /// <summary>
    /// Relationship type (OneToMany, ManyToOne).
    /// </summary>
    [JsonPropertyName("relationshipType")]
    public string RelationshipType { get; set; } = "";
}

/// <summary>
/// Many-to-many relationship information.
/// </summary>
public sealed class ManyToManyRelationshipInfo
{
    /// <summary>
    /// Relationship schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>
    /// First entity logical name.
    /// </summary>
    [JsonPropertyName("entity1LogicalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity1LogicalName { get; set; }

    /// <summary>
    /// First entity attribute in intersect.
    /// </summary>
    [JsonPropertyName("entity1Attribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity1Attribute { get; set; }

    /// <summary>
    /// Second entity logical name.
    /// </summary>
    [JsonPropertyName("entity2LogicalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity2LogicalName { get; set; }

    /// <summary>
    /// Second entity attribute in intersect.
    /// </summary>
    [JsonPropertyName("entity2Attribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity2Attribute { get; set; }

    /// <summary>
    /// Intersect entity name.
    /// </summary>
    [JsonPropertyName("intersectEntityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntersectEntityName { get; set; }
}
