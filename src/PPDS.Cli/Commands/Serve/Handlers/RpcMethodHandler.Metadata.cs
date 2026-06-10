using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Diagnostics;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Authoring = PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Security;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using System.Threading;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;
using PluginAssemblyInfoModel = PPDS.Cli.Plugins.Registration.PluginAssemblyInfo;
using PluginPackageInfoModel = PPDS.Cli.Plugins.Registration.PluginPackageInfo;
using PluginStepInfoModel = PPDS.Cli.Plugins.Registration.PluginStepInfo;
using ConnRefRelationshipType = PPDS.Cli.Services.ConnectionReferences.RelationshipType;
using WebResourceInfoModel = PPDS.Cli.Services.WebResources.WebResourceInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

public partial class RpcMethodHandler
{
    #region Metadata Methods

    /// <summary>
    /// Lists all entities in the environment with summary metadata.
    /// Used by the Metadata Browser panel in VS Code.
    /// </summary>
    [JsonRpcMethod("metadata/entities")]
    public async Task<MetadataEntitiesResponse> MetadataEntitiesAsync(
        string? environmentUrl = null,
        bool includeIntersect = false,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataQueryService>();

            // Fetch entities with requested includeIntersect setting
            var entities = await metadataService.GetEntitiesAsync(includeIntersect: includeIntersect, cancellationToken: ct).ConfigureAwait(false);
            var intersectHiddenCount = 0;

            if (!includeIntersect)
            {
                // Fetch total count (with intersect) to compute how many were hidden
                var allEntities = await metadataService.GetEntitiesAsync(includeIntersect: true, cancellationToken: ct).ConfigureAwait(false);
                intersectHiddenCount = allEntities.Count - entities.Count;
            }

            return new MetadataEntitiesResponse
            {
                Entities = entities.Select(MapEntitySummaryToRpc).ToList(),
                IntersectHiddenCount = intersectHiddenCount,
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all global option sets in the environment.
    /// Used by the Metadata Browser panel CHOICES section in VS Code.
    /// </summary>
    [JsonRpcMethod("metadata/globalOptionSets")]
    public async Task<MetadataGlobalOptionSetsResponse> MetadataGlobalOptionSetsAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataQueryService>();
            var optionSets = await metadataService.GetGlobalOptionSetsAsync(cancellationToken: ct).ConfigureAwait(false);

            return new MetadataGlobalOptionSetsResponse
            {
                OptionSets = optionSets.Select(MapOptionSetSummaryToRpc).ToList(),
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the full details of a specific global option set including all values.
    /// Used when a global choice is selected in the Metadata Browser CHOICES tree.
    /// </summary>
    [JsonRpcMethod("metadata/globalOptionSet")]
    public async Task<MetadataGlobalOptionSetDetailResponse> MetadataGlobalOptionSetAsync(
        string name,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'name' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataQueryService>();
            var optionSet = await metadataService.GetOptionSetAsync(name, ct).ConfigureAwait(false);

            return new MetadataGlobalOptionSetDetailResponse
            {
                OptionSet = MapOptionSetToRpc(optionSet),
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets full metadata for a specific entity including attributes, relationships,
    /// keys, privileges, and optionally global option set values.
    /// Used by the Metadata Browser panel in VS Code.
    /// </summary>
    [JsonRpcMethod("metadata/entity")]
    public async Task<MetadataEntityResponse> MetadataEntityAsync(
        string logicalName,
        bool includeGlobalOptionSets = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'logicalName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataQueryService>();
            var (entity, globalOptionSets) = await metadataService.GetEntityWithGlobalOptionSetsAsync(
                logicalName,
                includeGlobalOptionSets,
                ct);

            return new MetadataEntityResponse
            {
                Entity = MapEntityDetailToRpc(entity, globalOptionSets)
            };
        }, cancellationToken);
    }

    private static MetadataEntitySummaryDto MapEntitySummaryToRpc(EntitySummary e)
    {
        return new MetadataEntitySummaryDto
        {
            LogicalName = e.LogicalName,
            SchemaName = e.SchemaName,
            DisplayName = e.DisplayName,
            IsCustomEntity = e.IsCustomEntity,
            IsManaged = e.IsManaged,
            OwnershipType = e.OwnershipType,
            ObjectTypeCode = e.ObjectTypeCode,
            Description = e.Description
        };
    }

    private static MetadataEntityDetailDto MapEntityDetailToRpc(
        EntityMetadataDto entity,
        IReadOnlyList<OptionSetMetadataDto> globalOptionSets)
    {
        return new MetadataEntityDetailDto
        {
            LogicalName = entity.LogicalName,
            SchemaName = entity.SchemaName,
            DisplayName = entity.DisplayName,
            IsCustomEntity = entity.IsCustomEntity,
            IsManaged = entity.IsManaged,
            OwnershipType = entity.OwnershipType,
            ObjectTypeCode = entity.ObjectTypeCode,
            Description = entity.Description,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            PrimaryImageAttribute = entity.PrimaryImageAttribute,
            EntitySetName = entity.EntitySetName,
            LogicalCollectionName = entity.LogicalCollectionName,
            PluralName = entity.PluralName,
            IsActivity = entity.IsActivity,
            IsActivityParty = entity.IsActivityParty,
            HasNotes = entity.HasNotes,
            HasActivities = entity.HasActivities,
            IsValidForAdvancedFind = entity.IsValidForAdvancedFind,
            IsAuditEnabled = entity.IsAuditEnabled,
            ChangeTrackingEnabled = entity.ChangeTrackingEnabled,
            IsBusinessProcessEnabled = entity.IsBusinessProcessEnabled,
            IsQuickCreateEnabled = entity.IsQuickCreateEnabled,
            IsDuplicateDetectionEnabled = entity.IsDuplicateDetectionEnabled,
            IsValidForQueue = entity.IsValidForQueue,
            IsIntersect = entity.IsIntersect,
            CanCreateMultiple = entity.CanCreateMultiple,
            CanUpdateMultiple = entity.CanUpdateMultiple,
            Attributes = entity.Attributes.Select(MapAttributeToRpc).ToList(),
            OneToManyRelationships = entity.OneToManyRelationships.Select(MapRelationshipToRpc).ToList(),
            ManyToOneRelationships = entity.ManyToOneRelationships.Select(MapRelationshipToRpc).ToList(),
            ManyToManyRelationships = entity.ManyToManyRelationships.Select(MapManyToManyToRpc).ToList(),
            Keys = entity.Keys.Select(MapKeyToRpc).ToList(),
            Privileges = entity.Privileges.Select(MapPrivilegeToRpc).ToList(),
            GlobalOptionSets = globalOptionSets.Select(MapOptionSetToRpc).ToList()
        };
    }

    private static MetadataAttributeDto MapAttributeToRpc(AttributeMetadataDto a)
    {
        return new MetadataAttributeDto
        {
            LogicalName = a.LogicalName,
            DisplayName = a.DisplayName,
            SchemaName = a.SchemaName,
            AttributeType = a.AttributeType,
            AttributeTypeName = a.AttributeTypeName,
            IsPrimaryId = a.IsPrimaryId,
            IsPrimaryName = a.IsPrimaryName,
            IsCustomAttribute = a.IsCustomAttribute,
            RequiredLevel = a.RequiredLevel,
            MaxLength = a.MaxLength,
            MinValue = a.MinValue,
            MaxValue = a.MaxValue,
            Precision = a.Precision,
            Targets = a.Targets,
            OptionSetName = a.OptionSetName,
            IsGlobalOptionSet = a.IsGlobalOptionSet,
            Options = a.Options?.Select(MapOptionValueToRpc).ToList(),
            Format = a.Format,
            DateTimeBehavior = a.DateTimeBehavior,
            SourceType = a.SourceType,
            IsSecured = a.IsSecured,
            Description = a.Description,
            AutoNumberFormat = a.AutoNumberFormat
        };
    }

    private static MetadataRelationshipDto MapRelationshipToRpc(RelationshipMetadataDto r)
    {
        return new MetadataRelationshipDto
        {
            SchemaName = r.SchemaName,
            RelationshipType = r.RelationshipType,
            ReferencedEntity = r.ReferencedEntity,
            ReferencedAttribute = r.ReferencedAttribute,
            ReferencingEntity = r.ReferencingEntity,
            ReferencingAttribute = r.ReferencingAttribute,
            CascadeAssign = r.CascadeAssign,
            CascadeDelete = r.CascadeDelete,
            CascadeMerge = r.CascadeMerge,
            CascadeReparent = r.CascadeReparent,
            CascadeShare = r.CascadeShare,
            CascadeUnshare = r.CascadeUnshare,
            IsHierarchical = r.IsHierarchical
        };
    }

    private static MetadataManyToManyDto MapManyToManyToRpc(ManyToManyRelationshipDto r)
    {
        return new MetadataManyToManyDto
        {
            SchemaName = r.SchemaName,
            Entity1LogicalName = r.Entity1LogicalName,
            Entity1IntersectAttribute = r.Entity1IntersectAttribute,
            Entity2LogicalName = r.Entity2LogicalName,
            Entity2IntersectAttribute = r.Entity2IntersectAttribute,
            IntersectEntityName = r.IntersectEntityName
        };
    }

    private static MetadataKeyDto MapKeyToRpc(EntityKeyDto k)
    {
        return new MetadataKeyDto
        {
            SchemaName = k.SchemaName,
            LogicalName = k.LogicalName,
            DisplayName = k.DisplayName,
            KeyAttributes = k.KeyAttributes,
            EntityKeyIndexStatus = k.EntityKeyIndexStatus,
            IsManaged = k.IsManaged
        };
    }

    private static MetadataPrivilegeDto MapPrivilegeToRpc(PrivilegeDto p)
    {
        return new MetadataPrivilegeDto
        {
            PrivilegeId = p.PrivilegeId,
            Name = p.Name,
            PrivilegeType = p.PrivilegeType,
            CanBeLocal = p.CanBeLocal,
            CanBeDeep = p.CanBeDeep,
            CanBeGlobal = p.CanBeGlobal,
            CanBeBasic = p.CanBeBasic
        };
    }

    private static MetadataOptionSetDto MapOptionSetToRpc(OptionSetMetadataDto os)
    {
        return new MetadataOptionSetDto
        {
            Name = os.Name,
            DisplayName = os.DisplayName,
            OptionSetType = os.OptionSetType,
            IsGlobal = os.IsGlobal,
            IsCustomOptionSet = os.IsCustomOptionSet,
            IsManaged = os.IsManaged,
            Description = os.Description,
            Options = os.Options.Select(MapOptionValueToRpc).ToList()
        };
    }

    private static MetadataGlobalChoiceSummaryDto MapOptionSetSummaryToRpc(OptionSetSummary os)
    {
        return new MetadataGlobalChoiceSummaryDto
        {
            Name = os.Name,
            DisplayName = os.DisplayName,
            OptionSetType = os.OptionSetType,
            IsCustomOptionSet = os.IsCustomOptionSet,
            IsManaged = os.IsManaged,
            OptionCount = os.OptionCount,
            Description = os.Description,
        };
    }

    private static MetadataOptionValueDto MapOptionValueToRpc(OptionValueDto o)
    {
        return new MetadataOptionValueDto
        {
            Value = o.Value,
            Label = o.Label,
            Color = o.Color,
            Description = o.Description
        };
    }

    #endregion
}

public class MetadataEntitiesResponse
{
    [JsonPropertyName("entities")]
    public List<MetadataEntitySummaryDto> Entities { get; set; } = [];

    [JsonPropertyName("intersectHiddenCount")]
    public int IntersectHiddenCount { get; set; }
}

public class MetadataGlobalOptionSetsResponse
{
    [JsonPropertyName("optionSets")]
    public List<MetadataGlobalChoiceSummaryDto> OptionSets { get; set; } = [];
}

public class MetadataGlobalOptionSetDetailResponse
{
    [JsonPropertyName("optionSet")]
    public MetadataOptionSetDto OptionSet { get; set; } = null!;
}

public class MetadataGlobalChoiceSummaryDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("optionSetType")]
    public string OptionSetType { get; set; } = "";

    [JsonPropertyName("isCustomOptionSet")]
    public bool IsCustomOptionSet { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("optionCount")]
    public int OptionCount { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public class MetadataEntitySummaryDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    [JsonPropertyName("objectTypeCode")]
    public int ObjectTypeCode { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public class MetadataEntityResponse
{
    [JsonPropertyName("entity")]
    public MetadataEntityDetailDto Entity { get; set; } = null!;
}

public class MetadataEntityDetailDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    [JsonPropertyName("objectTypeCode")]
    public int ObjectTypeCode { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("primaryIdAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryIdAttribute { get; set; }

    [JsonPropertyName("primaryNameAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryNameAttribute { get; set; }

    [JsonPropertyName("entitySetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntitySetName { get; set; }

    [JsonPropertyName("primaryImageAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryImageAttribute { get; set; }

    [JsonPropertyName("logicalCollectionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalCollectionName { get; set; }

    [JsonPropertyName("pluralName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluralName { get; set; }

    [JsonPropertyName("isActivity")]
    public bool IsActivity { get; set; }

    [JsonPropertyName("isActivityParty")]
    public bool IsActivityParty { get; set; }

    [JsonPropertyName("hasNotes")]
    public bool HasNotes { get; set; }

    [JsonPropertyName("hasActivities")]
    public bool HasActivities { get; set; }

    [JsonPropertyName("isValidForAdvancedFind")]
    public bool IsValidForAdvancedFind { get; set; }

    [JsonPropertyName("isAuditEnabled")]
    public bool IsAuditEnabled { get; set; }

    [JsonPropertyName("changeTrackingEnabled")]
    public bool ChangeTrackingEnabled { get; set; }

    [JsonPropertyName("isBusinessProcessEnabled")]
    public bool IsBusinessProcessEnabled { get; set; }

    [JsonPropertyName("isQuickCreateEnabled")]
    public bool IsQuickCreateEnabled { get; set; }

    [JsonPropertyName("isDuplicateDetectionEnabled")]
    public bool IsDuplicateDetectionEnabled { get; set; }

    [JsonPropertyName("isValidForQueue")]
    public bool IsValidForQueue { get; set; }

    [JsonPropertyName("isIntersect")]
    public bool IsIntersect { get; set; }

    [JsonPropertyName("canCreateMultiple")]
    public bool CanCreateMultiple { get; set; }

    [JsonPropertyName("canUpdateMultiple")]
    public bool CanUpdateMultiple { get; set; }

    [JsonPropertyName("attributes")]
    public List<MetadataAttributeDto> Attributes { get; set; } = [];

    [JsonPropertyName("oneToManyRelationships")]
    public List<MetadataRelationshipDto> OneToManyRelationships { get; set; } = [];

    [JsonPropertyName("manyToOneRelationships")]
    public List<MetadataRelationshipDto> ManyToOneRelationships { get; set; } = [];

    [JsonPropertyName("manyToManyRelationships")]
    public List<MetadataManyToManyDto> ManyToManyRelationships { get; set; } = [];

    [JsonPropertyName("keys")]
    public List<MetadataKeyDto> Keys { get; set; } = [];

    [JsonPropertyName("privileges")]
    public List<MetadataPrivilegeDto> Privileges { get; set; } = [];

    [JsonPropertyName("globalOptionSets")]
    public List<MetadataOptionSetDto> GlobalOptionSets { get; set; } = [];
}

public class MetadataAttributeDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("attributeType")]
    public string AttributeType { get; set; } = "";

    [JsonPropertyName("attributeTypeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttributeTypeName { get; set; }

    [JsonPropertyName("isPrimaryId")]
    public bool IsPrimaryId { get; set; }

    [JsonPropertyName("isPrimaryName")]
    public bool IsPrimaryName { get; set; }

    [JsonPropertyName("isCustomAttribute")]
    public bool IsCustomAttribute { get; set; }

    [JsonPropertyName("requiredLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiredLevel { get; set; }

    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    [JsonPropertyName("minValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MinValue { get; set; }

    [JsonPropertyName("maxValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MaxValue { get; set; }

    [JsonPropertyName("precision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Precision { get; set; }

    [JsonPropertyName("targets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Targets { get; set; }

    [JsonPropertyName("optionSetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OptionSetName { get; set; }

    [JsonPropertyName("isGlobalOptionSet")]
    public bool IsGlobalOptionSet { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MetadataOptionValueDto>? Options { get; set; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    [JsonPropertyName("dateTimeBehavior")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateTimeBehavior { get; set; }

    [JsonPropertyName("sourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceType { get; set; }

    [JsonPropertyName("isSecured")]
    public bool IsSecured { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("autoNumberFormat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoNumberFormat { get; set; }
}

public class MetadataRelationshipDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("relationshipType")]
    public string RelationshipType { get; set; } = "";

    [JsonPropertyName("referencedEntity")]
    public string ReferencedEntity { get; set; } = "";

    [JsonPropertyName("referencedAttribute")]
    public string ReferencedAttribute { get; set; } = "";

    [JsonPropertyName("referencingEntity")]
    public string ReferencingEntity { get; set; } = "";

    [JsonPropertyName("referencingAttribute")]
    public string ReferencingAttribute { get; set; } = "";

    [JsonPropertyName("cascadeAssign")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeAssign { get; set; }

    [JsonPropertyName("cascadeDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeDelete { get; set; }

    [JsonPropertyName("cascadeMerge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeMerge { get; set; }

    [JsonPropertyName("cascadeReparent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeReparent { get; set; }

    [JsonPropertyName("cascadeShare")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeShare { get; set; }

    [JsonPropertyName("cascadeUnshare")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeUnshare { get; set; }

    [JsonPropertyName("isHierarchical")]
    public bool IsHierarchical { get; set; }
}

public class MetadataManyToManyDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("entity1LogicalName")]
    public string Entity1LogicalName { get; set; } = "";

    [JsonPropertyName("entity1IntersectAttribute")]
    public string Entity1IntersectAttribute { get; set; } = "";

    [JsonPropertyName("entity2LogicalName")]
    public string Entity2LogicalName { get; set; } = "";

    [JsonPropertyName("entity2IntersectAttribute")]
    public string Entity2IntersectAttribute { get; set; } = "";

    [JsonPropertyName("intersectEntityName")]
    public string IntersectEntityName { get; set; } = "";
}

public class MetadataKeyDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("keyAttributes")]
    public List<string> KeyAttributes { get; set; } = [];

    [JsonPropertyName("entityKeyIndexStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityKeyIndexStatus { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }
}

public class MetadataPrivilegeDto
{
    [JsonPropertyName("privilegeId")]
    public Guid PrivilegeId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("privilegeType")]
    public string PrivilegeType { get; set; } = "";

    [JsonPropertyName("canBeLocal")]
    public bool CanBeLocal { get; set; }

    [JsonPropertyName("canBeDeep")]
    public bool CanBeDeep { get; set; }

    [JsonPropertyName("canBeGlobal")]
    public bool CanBeGlobal { get; set; }

    [JsonPropertyName("canBeBasic")]
    public bool CanBeBasic { get; set; }
}

public class MetadataOptionSetDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("optionSetType")]
    public string OptionSetType { get; set; } = "";

    [JsonPropertyName("isGlobal")]
    public bool IsGlobal { get; set; }

    [JsonPropertyName("isCustomOptionSet")]
    public bool IsCustomOptionSet { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("options")]
    public List<MetadataOptionValueDto> Options { get; set; } = [];
}

public class MetadataOptionValueDto
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

// ── Connection References DTOs ─────────────────────────────────────────────────
