using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Provides access to Dataverse metadata using the SDK.
/// </summary>
public class DataverseMetadataService : IMetadataService
{
    private readonly IDataverseConnectionPool _connectionPool;
    private readonly ILogger<DataverseMetadataService>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataverseMetadataService"/> class.
    /// </summary>
    /// <param name="connectionPool">The connection pool.</param>
    /// <param name="logger">Optional logger.</param>
    public DataverseMetadataService(
        IDataverseConnectionPool connectionPool,
        ILogger<DataverseMetadataService>? logger = null)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(
        bool customOnly = false,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Retrieving entity list from Dataverse (customOnly={CustomOnly}, filter={Filter})",
            customOnly, filter ?? "(none)");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new RetrieveAllEntitiesRequest
        {
            EntityFilters = EntityFilters.Entity,
            RetrieveAsIfPublished = false
        };

        var response = (RetrieveAllEntitiesResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var filterRegex = CreateFilterRegex(filter);

        var entities = response.EntityMetadata
            .Where(e => e.IsIntersect != true)
            .Where(e => !customOnly || e.IsCustomEntity == true)
            .Where(e => filterRegex == null || filterRegex.IsMatch(e.LogicalName))
            .Select(MapToEntitySummary)
            .OrderBy(e => e.LogicalName)
            .ToList();

        _logger?.LogInformation("Found {Count} entities", entities.Count);

        return entities;
    }

    /// <inheritdoc />
    public async Task<EntityMetadataDto> GetEntityAsync(
        string logicalName,
        bool includeAttributes = true,
        bool includeRelationships = true,
        bool includeKeys = true,
        bool includePrivileges = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        _logger?.LogInformation("Retrieving entity metadata for {Entity}", logicalName);

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var entityFilters = EntityFilters.Entity;
        if (includeAttributes) entityFilters |= EntityFilters.Attributes;
        if (includeRelationships) entityFilters |= EntityFilters.Relationships;
        if (includePrivileges) entityFilters |= EntityFilters.Privileges;

        var request = new RetrieveEntityRequest
        {
            LogicalName = logicalName,
            EntityFilters = entityFilters,
            RetrieveAsIfPublished = false
        };

        var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var metadata = response.EntityMetadata;

        var result = new EntityMetadataDto
        {
            MetadataId = metadata.MetadataId ?? Guid.Empty,
            LogicalName = metadata.LogicalName,
            DisplayName = GetLocalizedLabel(metadata.DisplayName),
            PluralName = GetLocalizedLabel(metadata.DisplayCollectionName),
            SchemaName = metadata.SchemaName,
            EntitySetName = metadata.EntitySetName,
            PrimaryIdAttribute = metadata.PrimaryIdAttribute,
            PrimaryNameAttribute = metadata.PrimaryNameAttribute,
            PrimaryImageAttribute = metadata.PrimaryImageAttribute,
            ObjectTypeCode = metadata.ObjectTypeCode ?? 0,
            IsCustomEntity = metadata.IsCustomEntity ?? false,
            IsManaged = metadata.IsManaged ?? false,
            OwnershipType = metadata.OwnershipType?.ToString(),
            LogicalCollectionName = metadata.LogicalCollectionName,
            Description = GetLocalizedLabel(metadata.Description),
            IsActivity = metadata.IsActivity ?? false,
            IsActivityParty = metadata.IsActivityParty ?? false,
            HasNotes = metadata.HasNotes ?? false,
            HasActivities = metadata.HasActivities ?? false,
            IsValidForAdvancedFind = metadata.IsValidForAdvancedFind ?? false,
            IsAuditEnabled = metadata.IsAuditEnabled?.Value ?? false,
            ChangeTrackingEnabled = metadata.ChangeTrackingEnabled ?? false,
            IsBusinessProcessEnabled = metadata.IsBusinessProcessEnabled ?? false,
            IsQuickCreateEnabled = metadata.IsQuickCreateEnabled ?? false,
            IsDuplicateDetectionEnabled = metadata.IsDuplicateDetectionEnabled?.Value ?? false,
            IsValidForQueue = metadata.IsValidForQueue?.Value ?? false,
            IsIntersect = metadata.IsIntersect ?? false,
            // Note: CanCreateMultiple/CanUpdateMultiple are not directly exposed by current SDK version.
            // Default to true (optimistic) - runtime detection in TieredImporter handles unsupported entities.
            CanCreateMultiple = true,
            CanUpdateMultiple = true,
            Attributes = includeAttributes ? MapAttributes(metadata).ToList() : [],
            OneToManyRelationships = includeRelationships ? MapOneToManyRelationships(metadata).ToList() : [],
            ManyToOneRelationships = includeRelationships ? MapManyToOneRelationships(metadata).ToList() : [],
            ManyToManyRelationships = includeRelationships ? MapManyToManyRelationships(metadata).ToList() : [],
            Keys = includeKeys ? MapKeys(metadata).ToList() : [],
            Privileges = includePrivileges ? MapPrivileges(metadata).ToList() : []
        };

        _logger?.LogInformation("Retrieved entity {Entity} with {AttrCount} attributes, {RelCount} relationships",
            logicalName, result.Attributes.Count,
            result.OneToManyRelationships.Count + result.ManyToOneRelationships.Count + result.ManyToManyRelationships.Count);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(
        string entityLogicalName,
        string? attributeType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        _logger?.LogInformation("Retrieving attributes for {Entity} (type={Type})",
            entityLogicalName, attributeType ?? "(all)");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new RetrieveEntityRequest
        {
            LogicalName = entityLogicalName,
            EntityFilters = EntityFilters.Attributes,
            RetrieveAsIfPublished = false
        };

        var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var metadata = response.EntityMetadata;
        var attributes = MapAttributes(metadata);

        if (!string.IsNullOrEmpty(attributeType))
        {
            attributes = attributes.Where(a =>
                a.AttributeType.Equals(attributeType, StringComparison.OrdinalIgnoreCase));
        }

        var result = attributes.OrderBy(a => a.LogicalName).ToList();

        _logger?.LogInformation("Found {Count} attributes for {Entity}", result.Count, entityLogicalName);

        return result;
    }

    /// <inheritdoc />
    public async Task<EntityRelationshipsDto> GetRelationshipsAsync(
        string entityLogicalName,
        string? relationshipType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        _logger?.LogInformation("Retrieving relationships for {Entity} (type={Type})",
            entityLogicalName, relationshipType ?? "(all)");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new RetrieveEntityRequest
        {
            LogicalName = entityLogicalName,
            EntityFilters = EntityFilters.Relationships,
            RetrieveAsIfPublished = false
        };

        var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var metadata = response.EntityMetadata;

        var result = new EntityRelationshipsDto
        {
            EntityLogicalName = entityLogicalName,
            OneToMany = ShouldIncludeRelationshipType(relationshipType, "OneToMany")
                ? MapOneToManyRelationships(metadata).OrderBy(r => r.SchemaName).ToList()
                : [],
            ManyToOne = ShouldIncludeRelationshipType(relationshipType, "ManyToOne")
                ? MapManyToOneRelationships(metadata).OrderBy(r => r.SchemaName).ToList()
                : [],
            ManyToMany = ShouldIncludeRelationshipType(relationshipType, "ManyToMany")
                ? MapManyToManyRelationships(metadata).OrderBy(r => r.SchemaName).ToList()
                : []
        };

        _logger?.LogInformation("Found {O2M} 1:N, {M2O} N:1, {M2M} N:N relationships for {Entity}",
            result.OneToMany.Count, result.ManyToOne.Count, result.ManyToMany.Count, entityLogicalName);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OptionSetSummary>> GetGlobalOptionSetsAsync(
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Retrieving global option sets (filter={Filter})", filter ?? "(none)");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new RetrieveAllOptionSetsRequest();

        var response = (RetrieveAllOptionSetsResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var filterRegex = CreateFilterRegex(filter);

        var optionSets = response.OptionSetMetadata
            .Where(os => filterRegex == null || filterRegex.IsMatch(os.Name))
            .Select(MapToOptionSetSummary)
            .OrderBy(os => os.Name)
            .ToList();

        _logger?.LogInformation("Found {Count} global option sets", optionSets.Count);

        return optionSets;
    }

    /// <inheritdoc />
    public async Task<OptionSetMetadataDto> GetOptionSetAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _logger?.LogInformation("Retrieving option set {Name}", name);

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new RetrieveOptionSetRequest
        {
            Name = name
        };

        var response = (RetrieveOptionSetResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var result = MapToOptionSetMetadata(response.OptionSetMetadata);

        _logger?.LogInformation("Retrieved option set {Name} with {Count} options",
            name, result.Options.Count);

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityKeyDto>> GetKeysAsync(
        string entityLogicalName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLogicalName);

        _logger?.LogInformation("Retrieving alternate keys for {Entity}", entityLogicalName);

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var request = new RetrieveEntityRequest
        {
            LogicalName = entityLogicalName,
            EntityFilters = EntityFilters.Entity,
            RetrieveAsIfPublished = false
        };

        var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var metadata = response.EntityMetadata;
        var keys = MapKeys(metadata).OrderBy(k => k.LogicalName).ToList();

        _logger?.LogInformation("Found {Count} alternate keys for {Entity}", keys.Count, entityLogicalName);

        return keys;
    }

    #region Mapping Methods

    private static EntitySummary MapToEntitySummary(EntityMetadata e)
    {
        return new EntitySummary
        {
            LogicalName = e.LogicalName,
            DisplayName = GetLocalizedLabel(e.DisplayName),
            SchemaName = e.SchemaName,
            EntitySetName = e.EntitySetName,
            ObjectTypeCode = e.ObjectTypeCode ?? 0,
            IsCustomEntity = e.IsCustomEntity ?? false,
            IsManaged = e.IsManaged ?? false,
            OwnershipType = e.OwnershipType?.ToString(),
            LogicalCollectionName = e.LogicalCollectionName,
            Description = GetLocalizedLabel(e.Description)
        };
    }

    private static IEnumerable<AttributeMetadataDto> MapAttributes(EntityMetadata metadata)
    {
        if (metadata.Attributes == null)
        {
            yield break;
        }

        foreach (var attr in metadata.Attributes)
        {
            yield return MapAttribute(attr, metadata);
        }
    }

    private static AttributeMetadataDto MapAttribute(AttributeMetadata attr, EntityMetadata entityMetadata)
    {
        // Extract type-specific properties first
        int? maxLength = null;
        decimal? minValue = null;
        decimal? maxValue = null;
        int? precision = null;
        List<string>? targets = null;
        string? optionSetName = null;
        bool isGlobalOptionSet = false;
        List<OptionValueDto>? options = null;
        string? dateTimeBehavior = null;
        string? format = null;
        string? autoNumberFormat = null;

        switch (attr)
        {
            case StringAttributeMetadata stringAttr:
                maxLength = stringAttr.MaxLength;
                format = stringAttr.Format?.ToString();
                autoNumberFormat = stringAttr.AutoNumberFormat;
                break;

            case MemoAttributeMetadata memoAttr:
                maxLength = memoAttr.MaxLength;
                break;

            case IntegerAttributeMetadata intAttr:
                minValue = intAttr.MinValue;
                maxValue = intAttr.MaxValue;
                break;

            case DecimalAttributeMetadata decAttr:
                minValue = decAttr.MinValue;
                maxValue = decAttr.MaxValue;
                precision = decAttr.Precision;
                break;

            case DoubleAttributeMetadata dblAttr:
                minValue = (decimal?)dblAttr.MinValue;
                maxValue = (decimal?)dblAttr.MaxValue;
                precision = dblAttr.Precision;
                break;

            case MoneyAttributeMetadata moneyAttr:
                minValue = (decimal?)moneyAttr.MinValue;
                maxValue = (decimal?)moneyAttr.MaxValue;
                precision = moneyAttr.Precision;
                break;

            case LookupAttributeMetadata lookupAttr:
                targets = lookupAttr.Targets?.ToList();
                break;

            case PicklistAttributeMetadata picklistAttr:
                optionSetName = picklistAttr.OptionSet?.Name;
                isGlobalOptionSet = picklistAttr.OptionSet?.IsGlobal ?? false;
                options = picklistAttr.OptionSet?.Options?.Select(MapOptionValue).ToList();
                break;

            case MultiSelectPicklistAttributeMetadata multiPicklistAttr:
                optionSetName = multiPicklistAttr.OptionSet?.Name;
                isGlobalOptionSet = multiPicklistAttr.OptionSet?.IsGlobal ?? false;
                options = multiPicklistAttr.OptionSet?.Options?.Select(MapOptionValue).ToList();
                break;

            case StateAttributeMetadata stateAttr:
                optionSetName = stateAttr.OptionSet?.Name;
                options = stateAttr.OptionSet?.Options?.Select(MapOptionValue).ToList();
                break;

            case StatusAttributeMetadata statusAttr:
                optionSetName = statusAttr.OptionSet?.Name;
                options = statusAttr.OptionSet?.Options?
                    .OfType<StatusOptionMetadata>()
                    .Select(o => new OptionValueDto
                    {
                        Value = o.Value ?? 0,
                        Label = GetLocalizedLabel(o.Label),
                        Description = GetLocalizedLabel(o.Description),
                        Color = o.Color,
                        State = o.State
                    })
                    .ToList();
                break;

            case DateTimeAttributeMetadata dtAttr:
                dateTimeBehavior = dtAttr.DateTimeBehavior?.Value;
                format = dtAttr.Format?.ToString();
                break;

            case BooleanAttributeMetadata boolAttr:
                optionSetName = boolAttr.OptionSet?.Name;
                if (boolAttr.OptionSet != null)
                {
                    options =
                    [
                        new OptionValueDto
                        {
                            Value = boolAttr.OptionSet.FalseOption?.Value ?? 0,
                            Label = GetLocalizedLabel(boolAttr.OptionSet.FalseOption?.Label),
                            IsDefault = boolAttr.DefaultValue == false
                        },
                        new OptionValueDto
                        {
                            Value = boolAttr.OptionSet.TrueOption?.Value ?? 1,
                            Label = GetLocalizedLabel(boolAttr.OptionSet.TrueOption?.Label),
                            IsDefault = boolAttr.DefaultValue == true
                        }
                    ];
                }
                break;
        }

        return new AttributeMetadataDto
        {
            MetadataId = attr.MetadataId ?? Guid.Empty,
            LogicalName = attr.LogicalName,
            DisplayName = GetLocalizedLabel(attr.DisplayName),
            SchemaName = attr.SchemaName,
            AttributeType = attr.AttributeType?.ToString() ?? "Unknown",
            AttributeTypeName = attr.AttributeTypeName?.Value,
            IsCustomAttribute = attr.IsCustomAttribute ?? false,
            IsManaged = attr.IsManaged ?? false,
            IsPrimaryId = attr.LogicalName == entityMetadata.PrimaryIdAttribute,
            IsPrimaryName = attr.LogicalName == entityMetadata.PrimaryNameAttribute,
            RequiredLevel = attr.RequiredLevel?.Value.ToString(),
            IsValidForCreate = attr.IsValidForCreate ?? false,
            IsValidForUpdate = attr.IsValidForUpdate ?? false,
            IsValidForRead = attr.IsValidForRead ?? false,
            IsSearchable = attr.IsSearchable ?? false,
            IsFilterable = attr.IsFilterable ?? false,
            IsSortable = attr.IsSortableEnabled?.Value ?? false,
            Description = GetLocalizedLabel(attr.Description),
            MaxLength = maxLength,
            MinValue = minValue,
            MaxValue = maxValue,
            Precision = precision,
            Targets = targets,
            OptionSetName = optionSetName,
            IsGlobalOptionSet = isGlobalOptionSet,
            Options = options,
            DateTimeBehavior = dateTimeBehavior,
            Format = format,
            // Calculation and Security
            SourceType = attr.SourceType,
            IsSecured = attr.IsSecured ?? false,
            FormulaDefinition = null, // Not available via SDK metadata API
            AutoNumberFormat = autoNumberFormat,
            // Form and Grid Behavior
            IsValidForForm = attr.IsValidForForm ?? false,
            IsValidForGrid = attr.IsValidForGrid ?? false,
            // Security Capabilities
            CanBeSecuredForRead = attr.CanBeSecuredForRead ?? false,
            CanBeSecuredForCreate = attr.CanBeSecuredForCreate ?? false,
            CanBeSecuredForUpdate = attr.CanBeSecuredForUpdate ?? false,
            // Advanced Properties
            IsRetrievable = attr.IsRetrievable ?? false,
            AttributeOf = attr.AttributeOf,
            IsLogical = attr.IsLogical ?? false,
            IntroducedVersion = attr.IntroducedVersion
        };
    }

    private static OptionValueDto MapOptionValue(OptionMetadata opt)
    {
        return new OptionValueDto
        {
            Value = opt.Value ?? 0,
            Label = GetLocalizedLabel(opt.Label),
            Description = GetLocalizedLabel(opt.Description),
            Color = opt.Color,
            ExternalValue = opt.ExternalValue,
            IsManaged = opt.IsManaged ?? false
        };
    }

    private static IEnumerable<RelationshipMetadataDto> MapOneToManyRelationships(EntityMetadata metadata)
    {
        if (metadata.OneToManyRelationships == null)
        {
            yield break;
        }

        foreach (var rel in metadata.OneToManyRelationships)
        {
            yield return new RelationshipMetadataDto
            {
                MetadataId = rel.MetadataId ?? Guid.Empty,
                SchemaName = rel.SchemaName,
                RelationshipType = "OneToMany",
                ReferencedEntity = rel.ReferencedEntity,
                ReferencedEntityNavigationPropertyName = rel.ReferencedEntityNavigationPropertyName,
                ReferencedAttribute = rel.ReferencedAttribute,
                ReferencingEntity = rel.ReferencingEntity,
                ReferencingEntityNavigationPropertyName = rel.ReferencingEntityNavigationPropertyName,
                ReferencingAttribute = rel.ReferencingAttribute,
                IsCustomRelationship = rel.IsCustomRelationship ?? false,
                IsManaged = rel.IsManaged ?? false,
                IsHierarchical = rel.IsHierarchical ?? false,
                SecurityTypes = rel.SecurityTypes?.ToString(),
                CascadeAssign = rel.CascadeConfiguration?.Assign?.ToString(),
                CascadeDelete = rel.CascadeConfiguration?.Delete?.ToString(),
                CascadeMerge = rel.CascadeConfiguration?.Merge?.ToString(),
                CascadeReparent = rel.CascadeConfiguration?.Reparent?.ToString(),
                CascadeShare = rel.CascadeConfiguration?.Share?.ToString(),
                CascadeUnshare = rel.CascadeConfiguration?.Unshare?.ToString()
            };
        }
    }

    private static IEnumerable<RelationshipMetadataDto> MapManyToOneRelationships(EntityMetadata metadata)
    {
        if (metadata.ManyToOneRelationships == null)
        {
            yield break;
        }

        foreach (var rel in metadata.ManyToOneRelationships)
        {
            yield return new RelationshipMetadataDto
            {
                MetadataId = rel.MetadataId ?? Guid.Empty,
                SchemaName = rel.SchemaName,
                RelationshipType = "ManyToOne",
                ReferencedEntity = rel.ReferencedEntity,
                ReferencedEntityNavigationPropertyName = rel.ReferencedEntityNavigationPropertyName,
                ReferencedAttribute = rel.ReferencedAttribute,
                ReferencingEntity = rel.ReferencingEntity,
                ReferencingEntityNavigationPropertyName = rel.ReferencingEntityNavigationPropertyName,
                ReferencingAttribute = rel.ReferencingAttribute,
                IsCustomRelationship = rel.IsCustomRelationship ?? false,
                IsManaged = rel.IsManaged ?? false,
                IsHierarchical = rel.IsHierarchical ?? false,
                SecurityTypes = rel.SecurityTypes?.ToString(),
                CascadeAssign = rel.CascadeConfiguration?.Assign?.ToString(),
                CascadeDelete = rel.CascadeConfiguration?.Delete?.ToString(),
                CascadeMerge = rel.CascadeConfiguration?.Merge?.ToString(),
                CascadeReparent = rel.CascadeConfiguration?.Reparent?.ToString(),
                CascadeShare = rel.CascadeConfiguration?.Share?.ToString(),
                CascadeUnshare = rel.CascadeConfiguration?.Unshare?.ToString()
            };
        }
    }

    private static IEnumerable<ManyToManyRelationshipDto> MapManyToManyRelationships(EntityMetadata metadata)
    {
        if (metadata.ManyToManyRelationships == null)
        {
            yield break;
        }

        foreach (var rel in metadata.ManyToManyRelationships)
        {
            var isReflexive = rel.Entity1LogicalName.Equals(rel.Entity2LogicalName, StringComparison.OrdinalIgnoreCase);

            yield return new ManyToManyRelationshipDto
            {
                MetadataId = rel.MetadataId ?? Guid.Empty,
                SchemaName = rel.SchemaName,
                IntersectEntityName = rel.IntersectEntityName,
                Entity1LogicalName = rel.Entity1LogicalName,
                Entity1IntersectAttribute = rel.Entity1IntersectAttribute,
                Entity1NavigationPropertyName = rel.Entity1NavigationPropertyName,
                Entity2LogicalName = rel.Entity2LogicalName,
                Entity2IntersectAttribute = rel.Entity2IntersectAttribute,
                Entity2NavigationPropertyName = rel.Entity2NavigationPropertyName,
                IsCustomRelationship = rel.IsCustomRelationship ?? false,
                IsManaged = rel.IsManaged ?? false,
                SecurityTypes = rel.SecurityTypes?.ToString(),
                IsReflexive = isReflexive
            };
        }
    }

    private static IEnumerable<EntityKeyDto> MapKeys(EntityMetadata metadata)
    {
        if (metadata.Keys == null)
        {
            yield break;
        }

        foreach (var key in metadata.Keys)
        {
            yield return new EntityKeyDto
            {
                MetadataId = key.MetadataId ?? Guid.Empty,
                SchemaName = key.SchemaName,
                LogicalName = key.LogicalName,
                DisplayName = GetLocalizedLabel(key.DisplayName),
                KeyAttributes = key.KeyAttributes?.ToList() ?? [],
                IsCustomizable = key.IsCustomizable?.Value ?? false,
                IsManaged = key.IsManaged ?? false,
                EntityKeyIndexStatus = key.EntityKeyIndexStatus.ToString()
            };
        }
    }

    private static IEnumerable<PrivilegeDto> MapPrivileges(EntityMetadata metadata)
    {
        if (metadata.Privileges == null)
        {
            yield break;
        }

        foreach (var priv in metadata.Privileges)
        {
            yield return new PrivilegeDto
            {
                PrivilegeId = priv.PrivilegeId,
                Name = priv.Name,
                PrivilegeType = priv.PrivilegeType.ToString(),
                CanBeLocal = priv.CanBeLocal,
                CanBeDeep = priv.CanBeDeep,
                CanBeGlobal = priv.CanBeGlobal,
                CanBeBasic = priv.CanBeBasic
            };
        }
    }

    private static OptionSetSummary MapToOptionSetSummary(OptionSetMetadataBase os)
    {
        var optionCount = os switch
        {
            OptionSetMetadata osm => osm.Options?.Count ?? 0,
            BooleanOptionSetMetadata _ => 2,
            _ => 0
        };

        return new OptionSetSummary
        {
            MetadataId = os.MetadataId ?? Guid.Empty,
            Name = os.Name,
            DisplayName = GetLocalizedLabel(os.DisplayName),
            OptionSetType = os.OptionSetType?.ToString() ?? "Unknown",
            IsGlobal = os.IsGlobal ?? false,
            IsCustomOptionSet = os.IsCustomOptionSet ?? false,
            IsManaged = os.IsManaged ?? false,
            Description = GetLocalizedLabel(os.Description),
            OptionCount = optionCount
        };
    }

    private static OptionSetMetadataDto MapToOptionSetMetadata(OptionSetMetadataBase os)
    {
        var options = os switch
        {
            OptionSetMetadata osm => osm.Options?.Select(MapOptionValue).ToList() ?? [],
            BooleanOptionSetMetadata bosm =>
            [
                new OptionValueDto
                {
                    Value = bosm.FalseOption?.Value ?? 0,
                    Label = GetLocalizedLabel(bosm.FalseOption?.Label)
                },
                new OptionValueDto
                {
                    Value = bosm.TrueOption?.Value ?? 1,
                    Label = GetLocalizedLabel(bosm.TrueOption?.Label)
                }
            ],
            _ => []
        };

        return new OptionSetMetadataDto
        {
            MetadataId = os.MetadataId ?? Guid.Empty,
            Name = os.Name,
            DisplayName = GetLocalizedLabel(os.DisplayName),
            OptionSetType = os.OptionSetType?.ToString() ?? "Unknown",
            IsGlobal = os.IsGlobal ?? false,
            IsCustomOptionSet = os.IsCustomOptionSet ?? false,
            IsManaged = os.IsManaged ?? false,
            Description = GetLocalizedLabel(os.Description),
            ExternalTypeName = os.ExternalTypeName,
            Options = options
        };
    }

    #endregion

    #region Helper Methods

    private static string GetLocalizedLabel(Label? label)
    {
        return label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels?.FirstOrDefault()?.Label ?? string.Empty;
    }

    private static Regex? CreateFilterRegex(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        string pattern;
        if (filter.Contains('*'))
        {
            // Wildcards present: anchored pattern matching (e.g., 'foo*' = starts with)
            pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*") + "$";
        }
        else
        {
            // No wildcards: contains search (more intuitive default)
            pattern = Regex.Escape(filter);
        }

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static bool ShouldIncludeRelationshipType(string? filter, string type)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return filter.Equals(type, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
