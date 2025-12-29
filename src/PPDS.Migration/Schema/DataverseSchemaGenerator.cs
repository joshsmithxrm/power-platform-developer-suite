using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
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
            catch (FaultException ex)
            {
                _logger?.LogWarning(ex, "Dataverse fault retrieving metadata for entity {Entity}: {Message}",
                    entityName, ex.Message);
                return null;
            }
            catch (TimeoutException ex)
            {
                _logger?.LogWarning(ex, "Timeout retrieving metadata for entity {Entity}", entityName);
                return null;
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Network error retrieving metadata for entity {Entity}: {Message}",
                    entityName, ex.Message);
                return null;
            }

            var metadata = response.EntityMetadata;

            // Generate fields
            var fields = GenerateFields(metadata, options);

            // Generate relationships (always included for dependency analysis and M2M support)
            var relationships = GenerateRelationships(metadata, includedEntities);

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

                var isValidForCreate = attr.IsValidForCreate ?? false;
                var isValidForUpdate = attr.IsValidForUpdate ?? false;

                // Always skip fields that are never writable (not valid for create AND not valid for update)
                // These fields (like versionnumber) can never be imported, so no point exporting them
                if (!isValidForCreate && !isValidForUpdate)
                {
                    _logger?.LogDebug("Skipping never-writable field {Field} on entity {Entity}",
                        attr.LogicalName, metadata.LogicalName);
                    continue;
                }

                var isPrimaryKey = attr.LogicalName == metadata.PrimaryIdAttribute;

                // Apply attribute filtering (primary key is always included)
                if (!options.ShouldIncludeAttribute(attr.LogicalName, isPrimaryKey))
                {
                    continue;
                }

                // Skip non-custom fields if only custom requested
                if (options.CustomFieldsOnly && attr.IsCustomAttribute != true)
                {
                    continue;
                }

                // Determine if field should be included based on metadata-driven filtering
                if (!ShouldIncludeField(attr, isPrimaryKey, options.IncludeAuditFields))
                {
                    _logger?.LogDebug("Skipping non-customizable field {Field} on entity {Entity}",
                        attr.LogicalName, metadata.LogicalName);
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
                    IsPrimaryKey = isPrimaryKey,
                    IsValidForCreate = isValidForCreate,
                    IsValidForUpdate = isValidForUpdate
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

        /// <summary>
        /// Determines if a field should be included based on metadata-driven filtering.
        /// Uses IsCustomAttribute and IsCustomizable as primary filters, with exceptions for
        /// known useful non-customizable fields (BPF, images) and audit fields.
        /// </summary>
        private static bool ShouldIncludeField(AttributeMetadata attr, bool isPrimaryKey, bool includeAuditFields)
        {
            // Primary key is always included
            if (isPrimaryKey)
            {
                return true;
            }

            // Custom fields are always included
            if (attr.IsCustomAttribute == true)
            {
                return true;
            }

            // Handle Virtual attributes specially - only include Image and MultiSelectPicklist
            if (attr.AttributeType == AttributeTypeCode.Virtual)
            {
                return attr is ImageAttributeMetadata or MultiSelectPicklistAttributeMetadata;
            }

            // Exclude system bookkeeping fields (customizable but not migration-relevant)
            if (IsNonMigratableSystemField(attr.LogicalName))
            {
                return false;
            }

            // Customizable system fields are included (statecode, statuscode, most lookups, etc.)
            if (attr.IsCustomizable?.Value == true)
            {
                return true;
            }

            // Audit fields are included only if explicitly requested
            if (IsAuditField(attr.LogicalName))
            {
                return includeAuditFields;
            }

            // BPF and image reference fields are non-customizable but useful
            if (IsBpfOrImageField(attr.LogicalName))
            {
                return true;
            }

            // All other non-customizable system fields are excluded
            // (owningbusinessunit, owningteam, owninguser, etc.)
            return false;
        }

        /// <summary>
        /// System bookkeeping fields that are marked IsCustomizable=true but serve no purpose in data migration.
        /// These exist on every entity and contain system-managed values, not business data.
        /// </summary>
        private static bool IsNonMigratableSystemField(string fieldName)
        {
            return fieldName is
                "timezoneruleversionnumber" or
                "utcconversiontimezonecode" or
                "importsequencenumber";
        }

        /// <summary>
        /// Audit fields track who created/modified records and when.
        /// These are excluded by default but can be included with --include-audit-fields.
        /// </summary>
        private static bool IsAuditField(string fieldName)
        {
            return fieldName is
                "createdon" or
                "createdby" or
                "createdonbehalfby" or
                "modifiedon" or
                "modifiedby" or
                "modifiedonbehalfby" or
                "overriddencreatedon";
        }

        /// <summary>
        /// BPF (Business Process Flow) and image reference fields are non-customizable but commonly needed.
        /// </summary>
        private static bool IsBpfOrImageField(string fieldName)
        {
            return fieldName is "processid" or "stageid" or "entityimageid";
        }
    }
}
