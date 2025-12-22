using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Schema
{
    /// <summary>
    /// Generates migration schemas from Dataverse metadata.
    /// </summary>
    public class DataverseSchemaGenerator : ISchemaGenerator
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ILogger<DataverseSchemaGenerator>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseSchemaGenerator"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="logger">Optional logger.</param>
        public DataverseSchemaGenerator(
            IDataverseConnectionPool connectionPool,
            ILogger<DataverseSchemaGenerator>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<EntityInfo>> GetAvailableEntitiesAsync(
            CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("Retrieving available entities from Dataverse");

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveAllEntitiesResponse)await client.ExecuteAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var entities = response.EntityMetadata
                .Where(e => e.IsIntersect != true) // Exclude intersect entities
                .Select(e => new EntityInfo
                {
                    LogicalName = e.LogicalName,
                    DisplayName = e.DisplayName?.UserLocalizedLabel?.Label ?? e.LogicalName,
                    ObjectTypeCode = e.ObjectTypeCode ?? 0,
                    IsCustomEntity = e.IsCustomEntity ?? false
                })
                .OrderBy(e => e.LogicalName)
                .ToList();

            _logger?.LogInformation("Found {Count} entities", entities.Count);

            return entities;
        }

        /// <inheritdoc />
        public async Task<MigrationSchema> GenerateAsync(
            IEnumerable<string> entityLogicalNames,
            SchemaGeneratorOptions? options = null,
            IProgressReporter? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new SchemaGeneratorOptions();
            var entityNames = entityLogicalNames.ToList();

            _logger?.LogInformation("Generating schema for {Count} entities", entityNames.Count);

            progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Generating schema for {entityNames.Count} entities..."
            });

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var entitySchemas = new List<EntitySchema>();
            var entitySet = new HashSet<string>(entityNames, StringComparer.OrdinalIgnoreCase);

            foreach (var entityName in entityNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Entity = entityName,
                    Message = $"Retrieving metadata for {entityName}..."
                });

                var entitySchema = await GenerateEntitySchemaAsync(
                    client, entityName, entitySet, options, cancellationToken).ConfigureAwait(false);

                if (entitySchema != null)
                {
                    entitySchemas.Add(entitySchema);
                }
            }

            _logger?.LogInformation("Generated schema with {Count} entities", entitySchemas.Count);

            return new MigrationSchema
            {
                Version = "1.0",
                GeneratedAt = DateTime.UtcNow,
                Entities = entitySchemas
            };
        }

        private async Task<EntitySchema?> GenerateEntitySchemaAsync(
            IOrganizationServiceAsync2 client,
            string entityName,
            HashSet<string> includedEntities,
            SchemaGeneratorOptions options,
            CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Retrieving metadata for entity {Entity}", entityName);

            var request = new RetrieveEntityRequest
            {
                LogicalName = entityName,
                EntityFilters = EntityFilters.Attributes | EntityFilters.Relationships,
                RetrieveAsIfPublished = false
            };

            RetrieveEntityResponse response;
            try
            {
                response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to retrieve metadata for entity {Entity}", entityName);
                return null;
            }

            var metadata = response.EntityMetadata;

            // Generate fields
            var fields = GenerateFields(metadata, options);

            // Generate relationships
            var relationships = options.IncludeRelationships
                ? GenerateRelationships(metadata, includedEntities)
                : Array.Empty<RelationshipSchema>();

            return new EntitySchema
            {
                LogicalName = metadata.LogicalName,
                DisplayName = metadata.DisplayName?.UserLocalizedLabel?.Label ?? metadata.LogicalName,
                ObjectTypeCode = metadata.ObjectTypeCode,
                PrimaryIdField = metadata.PrimaryIdAttribute ?? $"{metadata.LogicalName}id",
                PrimaryNameField = metadata.PrimaryNameAttribute ?? "name",
                DisablePlugins = options.DisablePluginsByDefault,
                Fields = fields.ToList(),
                Relationships = relationships.ToList()
            };
        }

        private IEnumerable<FieldSchema> GenerateFields(EntityMetadata metadata, SchemaGeneratorOptions options)
        {
            if (metadata.Attributes == null)
            {
                yield break;
            }

            foreach (var attr in metadata.Attributes)
            {
                // Skip if not valid for read
                if (attr.IsValidForRead != true)
                {
                    continue;
                }

                var isPrimaryKey = attr.LogicalName == metadata.PrimaryIdAttribute;

                // Apply attribute filtering (primary key is always included)
                if (!options.ShouldIncludeAttribute(attr.LogicalName, isPrimaryKey))
                {
                    continue;
                }

                // Skip system fields unless requested
                if (!options.IncludeSystemFields && IsSystemField(attr.LogicalName))
                {
                    continue;
                }

                // Skip non-custom fields if only custom requested
                if (options.CustomFieldsOnly && attr.IsCustomAttribute != true)
                {
                    continue;
                }

                var fieldType = GetFieldType(attr);
                var lookupTargets = GetLookupTargets(attr);

                yield return new FieldSchema
                {
                    LogicalName = attr.LogicalName,
                    DisplayName = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName,
                    Type = fieldType,
                    LookupEntity = lookupTargets,
                    IsCustomField = attr.IsCustomAttribute ?? false,
                    IsRequired = attr.RequiredLevel?.Value == AttributeRequiredLevel.ApplicationRequired ||
                                 attr.RequiredLevel?.Value == AttributeRequiredLevel.SystemRequired,
                    IsPrimaryKey = isPrimaryKey
                };
            }
        }

        private static string GetFieldType(AttributeMetadata attr)
        {
            return attr.AttributeType switch
            {
                AttributeTypeCode.BigInt => "bigint",
                AttributeTypeCode.Boolean => "boolean",
                AttributeTypeCode.CalendarRules => "calendarrules",
                AttributeTypeCode.Customer => "entityreference",
                AttributeTypeCode.DateTime => "datetime",
                AttributeTypeCode.Decimal => "decimal",
                AttributeTypeCode.Double => "float",
                AttributeTypeCode.Integer => "integer",
                AttributeTypeCode.Lookup => "entityreference",
                AttributeTypeCode.Memo => "memo",
                AttributeTypeCode.Money => "money",
                AttributeTypeCode.Owner => "owner",
                AttributeTypeCode.PartyList => "partylist",
                AttributeTypeCode.Picklist => "picklist",
                AttributeTypeCode.State => "state",
                AttributeTypeCode.Status => "status",
                AttributeTypeCode.String => "string",
                AttributeTypeCode.Uniqueidentifier => "guid",
                AttributeTypeCode.Virtual => "virtual",
                AttributeTypeCode.ManagedProperty => "managedproperty",
                AttributeTypeCode.EntityName => "entityname",
                _ => "string"
            };
        }

        private static string? GetLookupTargets(AttributeMetadata attr)
        {
            if (attr is not LookupAttributeMetadata lookupAttr)
            {
                return null;
            }

            var targets = lookupAttr.Targets;
            if (targets == null || targets.Length == 0)
            {
                return "*"; // Unknown/unbounded lookup
            }

            // CMT format: pipe-delimited for polymorphic lookups
            return string.Join("|", targets);
        }

        private IEnumerable<RelationshipSchema> GenerateRelationships(
            EntityMetadata metadata,
            HashSet<string> includedEntities)
        {
            // One-to-Many relationships (where this entity is referenced)
            if (metadata.OneToManyRelationships != null)
            {
                foreach (var rel in metadata.OneToManyRelationships)
                {
                    // Only include if related entity is in our set
                    if (!includedEntities.Contains(rel.ReferencingEntity))
                    {
                        continue;
                    }

                    yield return new RelationshipSchema
                    {
                        Name = rel.SchemaName,
                        IsManyToMany = false,
                        Entity1 = rel.ReferencingEntity,
                        Entity1Attribute = rel.ReferencingAttribute,
                        Entity2 = rel.ReferencedEntity,
                        Entity2Attribute = rel.ReferencedAttribute
                    };
                }
            }

            // Many-to-Many relationships
            if (metadata.ManyToManyRelationships != null)
            {
                foreach (var rel in metadata.ManyToManyRelationships)
                {
                    // Determine the "other" entity and correct attributes
                    // The relationship must be relative to the current entity (source)
                    string sourceEntity, targetEntity, sourceAttribute, targetAttribute;
                    if (rel.Entity1LogicalName.Equals(metadata.LogicalName, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceEntity = rel.Entity1LogicalName;
                        sourceAttribute = rel.Entity1IntersectAttribute;
                        targetEntity = rel.Entity2LogicalName;
                        targetAttribute = rel.Entity2IntersectAttribute;
                    }
                    else
                    {
                        sourceEntity = rel.Entity2LogicalName;
                        sourceAttribute = rel.Entity2IntersectAttribute;
                        targetEntity = rel.Entity1LogicalName;
                        targetAttribute = rel.Entity1IntersectAttribute;
                    }

                    // Only include if target entity is in our set
                    if (!includedEntities.Contains(targetEntity))
                    {
                        continue;
                    }

                    // Only emit from one side to avoid duplicates
                    if (string.Compare(metadata.LogicalName, targetEntity, StringComparison.OrdinalIgnoreCase) > 0)
                    {
                        continue;
                    }

                    // Check for reflexive relationship (self-referencing M2M)
                    var isReflexive = sourceEntity.Equals(targetEntity, StringComparison.OrdinalIgnoreCase);

                    yield return new RelationshipSchema
                    {
                        Name = rel.SchemaName,
                        IsManyToMany = true,
                        IsReflexive = isReflexive,
                        Entity1 = sourceEntity,
                        Entity1Attribute = sourceAttribute,
                        Entity2 = targetEntity,
                        Entity2Attribute = targetAttribute,
                        IntersectEntity = rel.IntersectEntityName,
                        TargetEntityPrimaryKey = targetAttribute
                    };
                }
            }
        }

        private static bool IsSystemField(string fieldName)
        {
            // Common system fields that are usually not migrated
            return fieldName switch
            {
                "createdon" => true,
                "createdby" => true,
                "createdonbehalfby" => true,
                "modifiedon" => true,
                "modifiedby" => true,
                "modifiedonbehalfby" => true,
                "versionnumber" => true,
                "timezoneruleversionnumber" => true,
                "utcconversiontimezonecode" => true,
                "overriddencreatedon" => true,
                "importsequencenumber" => true,
                "owningbusinessunit" => true,
                "owningteam" => true,
                "owninguser" => true,
                _ => false
            };
        }
    }
}
