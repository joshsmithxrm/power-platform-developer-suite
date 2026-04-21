using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that creates a relationship between two Dataverse tables.
/// </summary>
[McpServerToolType]
public sealed class MetadataCreateRelationshipTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCreateRelationshipTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataCreateRelationshipTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Creates a relationship between two Dataverse tables.
    /// </summary>
    /// <param name="solution">The unique name of the solution to add the relationship to.</param>
    /// <param name="referencedEntity">The logical name of the referenced (parent / 'one' side) entity.</param>
    /// <param name="referencingEntity">The logical name of the referencing (child / 'many' side) entity.</param>
    /// <param name="schemaName">The schema name for the relationship.</param>
    /// <param name="lookupSchemaName">The schema name of the lookup column created on the referencing entity (required for oneToMany).</param>
    /// <param name="lookupDisplayName">The display name of the lookup column (required for oneToMany).</param>
    /// <param name="relationshipType">The type of relationship: 'oneToMany' or 'manyToMany'.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the schema name and metadata ID of the created relationship.</returns>
    [McpServerTool(Name = "ppds_metadata_create_relationship")]
    [Description("Creates a relationship (one-to-many or many-to-many) between two Dataverse tables. For one-to-many, a lookup column is created on the referencing entity. Supports dry-run mode.")]
    public async Task<MetadataCreateRelationshipResult> ExecuteAsync(
        [Description("Unique name of the solution to add the relationship to.")] string solution,
        [Description("Logical name of the referenced (parent / 'one' side) entity.")] string referencedEntity,
        [Description("Logical name of the referencing (child / 'many' side) entity.")] string referencingEntity,
        [Description("Schema name for the relationship.")] string schemaName,
        [Description("Relationship type: 'oneToMany' or 'manyToMany'.")] string relationshipType,
        [Description("Schema name of the lookup column on the referencing entity (required for oneToMany, ignored for manyToMany).")] string? lookupSchemaName = null,
        [Description("Display name of the lookup column (required for oneToMany, ignored for manyToMany).")] string? lookupDisplayName = null,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Context.IsReadOnly)
                throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

            await using var serviceProvider = await CreateScopeAsync(cancellationToken,
                (nameof(solution), solution),
                (nameof(referencedEntity), referencedEntity),
                (nameof(referencingEntity), referencingEntity),
                (nameof(schemaName), schemaName),
                (nameof(relationshipType), relationshipType)).ConfigureAwait(false);

            var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            CreateRelationshipResult result;

            if (string.Equals(relationshipType, "manyToMany", StringComparison.OrdinalIgnoreCase))
            {
                result = await service.CreateManyToManyAsync(new CreateManyToManyRequest
                {
                    SolutionUniqueName = solution,
                    Entity1LogicalName = referencedEntity,
                    Entity2LogicalName = referencingEntity,
                    SchemaName = schemaName,
                    DryRun = dryRun
                }, ct: cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(relationshipType, "oneToMany", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(lookupSchemaName))
                    throw new ArgumentException("lookupSchemaName is required for oneToMany relationships.", nameof(lookupSchemaName));
                if (string.IsNullOrWhiteSpace(lookupDisplayName))
                    throw new ArgumentException("lookupDisplayName is required for oneToMany relationships.", nameof(lookupDisplayName));

                result = await service.CreateOneToManyAsync(new CreateOneToManyRequest
                {
                    SolutionUniqueName = solution,
                    ReferencedEntity = referencedEntity,
                    ReferencingEntity = referencingEntity,
                    SchemaName = schemaName,
                    LookupSchemaName = lookupSchemaName,
                    LookupDisplayName = lookupDisplayName,
                    DryRun = dryRun
                }, ct: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentException(
                    $"Invalid relationship type '{relationshipType}'. Valid types: 'oneToMany', 'manyToMany'.",
                    nameof(relationshipType));
            }

            return new MetadataCreateRelationshipResult
            {
                SchemaName = result.SchemaName,
                MetadataId = result.MetadataId == Guid.Empty ? null : result.MetadataId.ToString(),
                WasDryRun = result.WasDryRun
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
/// Result of the metadata_create_relationship tool.
/// </summary>
public sealed class MetadataCreateRelationshipResult
{
    /// <summary>The schema name of the created relationship.</summary>
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    /// <summary>The metadata identifier of the created relationship.</summary>
    [JsonPropertyName("metadataId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataId { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
