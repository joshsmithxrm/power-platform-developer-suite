using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using SdkCascadeType = Microsoft.Xrm.Sdk.Metadata.CascadeType;
using SdkCreateOneToManyRequest = Microsoft.Xrm.Sdk.Messages.CreateOneToManyRequest;
using SdkCreateManyToManyRequest = Microsoft.Xrm.Sdk.Messages.CreateManyToManyRequest;
using SdkUpdateRelationshipRequest = Microsoft.Xrm.Sdk.Messages.UpdateRelationshipRequest;
using SdkDeleteRelationshipRequest = Microsoft.Xrm.Sdk.Messages.DeleteRelationshipRequest;
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOptionValueRequest = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;
using SdkUpdateStateValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Dataverse implementation of <see cref="IMetadataAuthoringService"/>.
/// Provides schema CRUD operations against Dataverse via the SDK.
/// </summary>
public class DataverseMetadataAuthoringService : IMetadataAuthoringService
{
    private readonly IDataverseConnectionPool _connectionPool;
    private readonly SchemaValidator _validator;
    private readonly ILogger<DataverseMetadataAuthoringService>? _logger;
    private readonly ICachedMetadataProvider? _cacheProvider;
    private readonly Dictionary<string, string> _publisherPrefixCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="DataverseMetadataAuthoringService"/> class.
    /// </summary>
    public DataverseMetadataAuthoringService(
        IDataverseConnectionPool connectionPool,
        SchemaValidator validator,
        ILogger<DataverseMetadataAuthoringService>? logger = null,
        ICachedMetadataProvider? cacheProvider = null)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger;
        _cacheProvider = cacheProvider;
    }

    #region Tables

    /// <inheritdoc />
    public async Task<CreateTableResult> CreateTableAsync(
        CreateTableRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateTableRequest(request, prefix);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateTable {SchemaName} validated successfully", request.SchemaName);
            return new CreateTableResult
            {
                LogicalName = request.SchemaName.ToLowerInvariant(),
                WasDryRun = true,
                ValidationMessages = []
            };
        }

        reporter?.ReportPhase("Creating table", request.SchemaName);

        var ownershipType = request.OwnershipType?.Equals("UserOwned", StringComparison.OrdinalIgnoreCase) == true
            ? OwnershipTypes.UserOwned
            : OwnershipTypes.OrganizationOwned;

        var primaryAttrSchemaName = request.PrimaryAttributeSchemaName ?? prefix + "_Name";
        var primaryAttrDisplayName = request.PrimaryAttributeDisplayName ?? "Name";
        var primaryAttrMaxLength = request.PrimaryAttributeMaxLength ?? 100;

        var entityMetadata = new EntityMetadata
        {
            SchemaName = request.SchemaName,
            DisplayName = new Label(request.DisplayName, 1033),
            DisplayCollectionName = new Label(request.PluralDisplayName, 1033),
            Description = string.IsNullOrEmpty(request.Description) ? null : new Label(request.Description, 1033),
            OwnershipType = ownershipType,
        };

        if (request.HasActivities.HasValue)
            entityMetadata.HasActivities = request.HasActivities.Value;
        if (request.HasNotes.HasValue)
            entityMetadata.HasNotes = request.HasNotes.Value;
        if (request.IsActivity.HasValue)
            entityMetadata.IsActivity = request.IsActivity.Value;
        if (request.ChangeTrackingEnabled.HasValue)
            entityMetadata.ChangeTrackingEnabled = request.ChangeTrackingEnabled.Value;
        if (request.IsAuditEnabled.HasValue)
            entityMetadata.IsAuditEnabled = new BooleanManagedProperty(request.IsAuditEnabled.Value);
        if (request.IsQuickCreateEnabled.HasValue)
            entityMetadata.IsQuickCreateEnabled = request.IsQuickCreateEnabled.Value;
        if (request.IsDuplicateDetectionEnabled.HasValue)
            entityMetadata.IsDuplicateDetectionEnabled = new BooleanManagedProperty(request.IsDuplicateDetectionEnabled.Value);
        if (request.IsValidForQueue.HasValue)
            entityMetadata.IsValidForQueue = new BooleanManagedProperty(request.IsValidForQueue.Value);
        if (!string.IsNullOrEmpty(request.EntityColor))
            entityMetadata.EntityColor = request.EntityColor;

        var primaryAttribute = new StringAttributeMetadata
        {
            SchemaName = primaryAttrSchemaName,
            DisplayName = new Label(primaryAttrDisplayName, 1033),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
            MaxLength = primaryAttrMaxLength
        };

        var sdkRequest = new CreateEntityRequest
        {
            Entity = entityMetadata,
            PrimaryAttribute = primaryAttribute,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (CreateEntityResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntityList();
        _cacheProvider?.InvalidateEntity(request.SchemaName.ToLowerInvariant());

        reporter?.ReportInfo($"Table '{request.SchemaName}' created successfully.");
        _logger?.LogInformation("Created table {SchemaName} with MetadataId {MetadataId}", request.SchemaName, response.EntityId);

        return new CreateTableResult
        {
            LogicalName = request.SchemaName.ToLowerInvariant(),
            MetadataId = response.EntityId,
            WasDryRun = false,
            ValidationMessages = []
        };
    }

    /// <inheritdoc />
    public async Task UpdateTableAsync(
        UpdateTableRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateTable {Entity} validated successfully", request.EntityLogicalName);
            return;
        }

        reporter?.ReportPhase("Updating table", request.EntityLogicalName);

        var entityMetadata = new EntityMetadata
        {
            LogicalName = request.EntityLogicalName
        };

        if (request.DisplayName != null)
            entityMetadata.DisplayName = new Label(request.DisplayName, 1033);
        if (request.PluralDisplayName != null)
            entityMetadata.DisplayCollectionName = new Label(request.PluralDisplayName, 1033);
        if (request.Description != null)
            entityMetadata.Description = new Label(request.Description, 1033);
        if (request.HasActivities.HasValue)
            entityMetadata.HasActivities = request.HasActivities.Value;
        if (request.HasNotes.HasValue)
            entityMetadata.HasNotes = request.HasNotes.Value;
        if (request.ChangeTrackingEnabled.HasValue)
            entityMetadata.ChangeTrackingEnabled = request.ChangeTrackingEnabled.Value;
        if (request.IsAuditEnabled.HasValue)
            entityMetadata.IsAuditEnabled = new BooleanManagedProperty(request.IsAuditEnabled.Value);
        if (request.IsQuickCreateEnabled.HasValue)
            entityMetadata.IsQuickCreateEnabled = request.IsQuickCreateEnabled.Value;
        if (request.IsDuplicateDetectionEnabled.HasValue)
            entityMetadata.IsDuplicateDetectionEnabled = new BooleanManagedProperty(request.IsDuplicateDetectionEnabled.Value);
        if (request.IsValidForQueue.HasValue)
            entityMetadata.IsValidForQueue = new BooleanManagedProperty(request.IsValidForQueue.Value);
        if (!string.IsNullOrEmpty(request.EntityColor))
            entityMetadata.EntityColor = request.EntityColor;

        var sdkRequest = new UpdateEntityRequest
        {
            Entity = entityMetadata,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntityList();
        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Table '{request.EntityLogicalName}' updated successfully.");
        _logger?.LogInformation("Updated table {Entity}", request.EntityLogicalName);
    }

    /// <inheritdoc />
    public async Task DeleteTableAsync(
        DeleteTableRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: DeleteTable {Entity} validated", request.EntityLogicalName);
            return;
        }

        reporter?.ReportPhase("Deleting table", request.EntityLogicalName);

        var sdkRequest = new DeleteEntityRequest
        {
            LogicalName = request.EntityLogicalName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntityList();
        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Table '{request.EntityLogicalName}' deleted successfully.");
        _logger?.LogInformation("Deleted table {Entity}", request.EntityLogicalName);
    }

    #endregion

    #region Columns

    /// <inheritdoc />
    public async Task<CreateColumnResult> CreateColumnAsync(
        CreateColumnRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateColumnRequest(request, prefix);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateColumn {SchemaName} on {Entity} validated successfully",
                request.SchemaName, request.EntityLogicalName);
            return new CreateColumnResult
            {
                LogicalName = request.SchemaName.ToLowerInvariant(),
                WasDryRun = true,
                ValidationMessages = []
            };
        }

        reporter?.ReportPhase("Creating column", request.SchemaName);

        var attribute = BuildAttributeMetadata(request);

        var sdkRequest = new CreateAttributeRequest
        {
            EntityName = request.EntityLogicalName,
            Attribute = attribute,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (CreateAttributeResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Column '{request.SchemaName}' created on '{request.EntityLogicalName}'.");
        _logger?.LogInformation("Created column {SchemaName} on {Entity} with MetadataId {MetadataId}",
            request.SchemaName, request.EntityLogicalName, response.AttributeId);

        return new CreateColumnResult
        {
            LogicalName = request.SchemaName.ToLowerInvariant(),
            MetadataId = response.AttributeId,
            WasDryRun = false,
            ValidationMessages = []
        };
    }

    /// <inheritdoc />
    public async Task UpdateColumnAsync(
        UpdateColumnRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateColumn {Column} on {Entity} validated",
                request.ColumnLogicalName, request.EntityLogicalName);
            return;
        }

        reporter?.ReportPhase("Retrieving existing column metadata");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);

        // Retrieve existing attribute to get its type
        var retrieveRequest = new RetrieveAttributeRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            LogicalName = request.ColumnLogicalName
        };
        var retrieveResponse = (RetrieveAttributeResponse)await client.ExecuteAsync(retrieveRequest, ct).ConfigureAwait(false);
        var existingAttr = retrieveResponse.AttributeMetadata;

        reporter?.ReportPhase("Updating column", request.ColumnLogicalName);

        if (request.DisplayName != null)
            existingAttr.DisplayName = new Label(request.DisplayName, 1033);
        if (request.Description != null)
            existingAttr.Description = new Label(request.Description, 1033);
        if (request.RequiredLevel != null)
            existingAttr.RequiredLevel = new AttributeRequiredLevelManagedProperty(ParseRequiredLevel(request.RequiredLevel));
        if (request.IsAuditEnabled.HasValue)
            existingAttr.IsAuditEnabled = new BooleanManagedProperty(request.IsAuditEnabled.Value);
        if (request.IsSecured.HasValue)
            existingAttr.IsSecured = request.IsSecured.Value;
        if (request.IsValidForAdvancedFind.HasValue)
            existingAttr.IsValidForAdvancedFind = new BooleanManagedProperty(request.IsValidForAdvancedFind.Value);

        ApplyTypeSpecificUpdates(existingAttr, request);

        var sdkRequest = new UpdateAttributeRequest
        {
            EntityName = request.EntityLogicalName,
            Attribute = existingAttr,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Column '{request.ColumnLogicalName}' updated on '{request.EntityLogicalName}'.");
        _logger?.LogInformation("Updated column {Column} on {Entity}", request.ColumnLogicalName, request.EntityLogicalName);
    }

    /// <inheritdoc />
    public async Task DeleteColumnAsync(
        DeleteColumnRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: DeleteColumn {Column} from {Entity} validated",
                request.ColumnLogicalName, request.EntityLogicalName);
            return;
        }

        reporter?.ReportPhase("Deleting column", request.ColumnLogicalName);

        var sdkRequest = new DeleteAttributeRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            LogicalName = request.ColumnLogicalName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Column '{request.ColumnLogicalName}' deleted from '{request.EntityLogicalName}'.");
        _logger?.LogInformation("Deleted column {Column} from {Entity}", request.ColumnLogicalName, request.EntityLogicalName);
    }

    #endregion

    #region Relationships

    /// <inheritdoc />
    public async Task<CreateRelationshipResult> CreateOneToManyAsync(
        Authoring.CreateOneToManyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateOneToManyRequest(request, prefix);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateOneToMany {SchemaName} validated", request.SchemaName);
            return new CreateRelationshipResult
            {
                SchemaName = request.SchemaName,
                WasDryRun = true,
                ValidationMessages = []
            };
        }

        reporter?.ReportPhase("Creating 1:N relationship", request.SchemaName);

        var relationship = new OneToManyRelationshipMetadata
        {
            SchemaName = request.SchemaName,
            ReferencedEntity = request.ReferencedEntity,
            ReferencingEntity = request.ReferencingEntity,
            CascadeConfiguration = MapCascadeConfiguration(request.CascadeConfiguration)
        };

        if (request.IsHierarchical.HasValue)
            relationship.IsHierarchical = request.IsHierarchical.Value;

        var lookup = new LookupAttributeMetadata
        {
            SchemaName = request.LookupSchemaName,
            DisplayName = new Label(request.LookupDisplayName, 1033)
        };

        var sdkRequest = new SdkCreateOneToManyRequest
        {
            OneToManyRelationship = relationship,
            Lookup = lookup,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (CreateOneToManyResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.ReferencedEntity);
        _cacheProvider?.InvalidateEntity(request.ReferencingEntity);

        reporter?.ReportInfo($"1:N relationship '{request.SchemaName}' created.");
        _logger?.LogInformation("Created 1:N relationship {SchemaName} with MetadataId {MetadataId}",
            request.SchemaName, response.RelationshipId);

        return new CreateRelationshipResult
        {
            SchemaName = request.SchemaName,
            MetadataId = response.RelationshipId,
            WasDryRun = false,
            ValidationMessages = []
        };
    }

    /// <inheritdoc />
    public async Task<CreateRelationshipResult> CreateManyToManyAsync(
        Authoring.CreateManyToManyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateManyToManyRequest(request, prefix);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateManyToMany {SchemaName} validated", request.SchemaName);
            return new CreateRelationshipResult
            {
                SchemaName = request.SchemaName,
                WasDryRun = true,
                ValidationMessages = []
            };
        }

        reporter?.ReportPhase("Creating N:N relationship", request.SchemaName);

        var relationship = new ManyToManyRelationshipMetadata
        {
            SchemaName = request.SchemaName,
            Entity1LogicalName = request.Entity1LogicalName,
            Entity2LogicalName = request.Entity2LogicalName,
            IntersectEntityName = request.IntersectEntitySchemaName
        };

        if (!string.IsNullOrEmpty(request.Entity1NavigationPropertyName))
            relationship.Entity1NavigationPropertyName = request.Entity1NavigationPropertyName;
        if (!string.IsNullOrEmpty(request.Entity2NavigationPropertyName))
            relationship.Entity2NavigationPropertyName = request.Entity2NavigationPropertyName;

        var sdkRequest = new SdkCreateManyToManyRequest
        {
            ManyToManyRelationship = relationship,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (CreateManyToManyResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.Entity1LogicalName);
        _cacheProvider?.InvalidateEntity(request.Entity2LogicalName);

        reporter?.ReportInfo($"N:N relationship '{request.SchemaName}' created.");
        _logger?.LogInformation("Created N:N relationship {SchemaName} with MetadataId {MetadataId}",
            request.SchemaName, response.ManyToManyRelationshipId);

        return new CreateRelationshipResult
        {
            SchemaName = request.SchemaName,
            MetadataId = response.ManyToManyRelationshipId,
            WasDryRun = false,
            ValidationMessages = []
        };
    }

    /// <inheritdoc />
    public async Task UpdateRelationshipAsync(
        Authoring.UpdateRelationshipRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.SchemaName, "SchemaName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateRelationship {SchemaName} validated", request.SchemaName);
            return;
        }

        reporter?.ReportPhase("Retrieving existing relationship");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);

        // Retrieve existing relationship
        var retrieveRequest = new RetrieveRelationshipRequest
        {
            Name = request.SchemaName
        };
        var retrieveResponse = (RetrieveRelationshipResponse)await client.ExecuteAsync(retrieveRequest, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Updating relationship", request.SchemaName);

        var existingRel = retrieveResponse.RelationshipMetadata;

        if (existingRel is OneToManyRelationshipMetadata o2m && request.CascadeConfiguration != null)
        {
            o2m.CascadeConfiguration = MapCascadeConfiguration(request.CascadeConfiguration);
            if (request.IsHierarchical.HasValue)
                o2m.IsHierarchical = request.IsHierarchical.Value;
        }

        var sdkRequest = new SdkUpdateRelationshipRequest
        {
            Relationship = existingRel,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        // UpdateRelationshipRequest doesn't carry entity logical names, so we must
        // invalidate all entity caches to ensure stale relationship data is cleared.
        _cacheProvider?.InvalidateAll();

        reporter?.ReportInfo($"Relationship '{request.SchemaName}' updated.");
        _logger?.LogInformation("Updated relationship {SchemaName}", request.SchemaName);
    }

    /// <inheritdoc />
    public async Task DeleteRelationshipAsync(
        Authoring.DeleteRelationshipRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.SchemaName, "SchemaName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: DeleteRelationship {SchemaName} validated", request.SchemaName);
            return;
        }

        reporter?.ReportPhase("Deleting relationship", request.SchemaName);

        var sdkRequest = new SdkDeleteRelationshipRequest
        {
            Name = request.SchemaName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        // DeleteRelationshipRequest doesn't carry entity logical names, so we must
        // invalidate all entity caches to ensure stale relationship data is cleared.
        _cacheProvider?.InvalidateAll();

        reporter?.ReportInfo($"Relationship '{request.SchemaName}' deleted.");
        _logger?.LogInformation("Deleted relationship {SchemaName}", request.SchemaName);
    }

    #endregion

    #region Choices

    /// <inheritdoc />
    public async Task<CreateChoiceResult> CreateGlobalChoiceAsync(
        CreateGlobalChoiceRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateGlobalChoiceRequest(request, prefix);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateGlobalChoice {SchemaName} validated", request.SchemaName);
            return new CreateChoiceResult
            {
                Name = request.SchemaName,
                WasDryRun = true,
                ValidationMessages = []
            };
        }

        reporter?.ReportPhase("Creating global choice", request.SchemaName);

        var optionSet = new OptionSetMetadata
        {
            Name = request.SchemaName,
            DisplayName = new Label(request.DisplayName, 1033),
            Description = string.IsNullOrEmpty(request.Description) ? null : new Label(request.Description, 1033),
            IsGlobal = true,
            OptionSetType = request.IsMultiSelect ? OptionSetType.Picklist : OptionSetType.Picklist
        };

        if (request.Options != null)
        {
            foreach (var opt in request.Options)
            {
                optionSet.Options.Add(new OptionMetadata(new Label(opt.Label, 1033), opt.Value));
            }
        }

        var sdkRequest = new CreateOptionSetRequest
        {
            OptionSet = optionSet,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (CreateOptionSetResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateGlobalOptionSets();

        reporter?.ReportInfo($"Global choice '{request.SchemaName}' created.");
        _logger?.LogInformation("Created global choice {SchemaName} with MetadataId {MetadataId}",
            request.SchemaName, response.OptionSetId);

        return new CreateChoiceResult
        {
            Name = request.SchemaName,
            MetadataId = response.OptionSetId,
            WasDryRun = false,
            ValidationMessages = []
        };
    }

    /// <inheritdoc />
    public async Task UpdateGlobalChoiceAsync(
        UpdateGlobalChoiceRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.Name, "Name");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateGlobalChoice {Name} validated", request.Name);
            return;
        }

        reporter?.ReportPhase("Retrieving existing choice");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);

        var retrieveRequest = new RetrieveOptionSetRequest { Name = request.Name };
        var retrieveResponse = (RetrieveOptionSetResponse)await client.ExecuteAsync(retrieveRequest, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Updating global choice", request.Name);

        var existingOs = retrieveResponse.OptionSetMetadata;

        if (request.DisplayName != null)
            existingOs.DisplayName = new Label(request.DisplayName, 1033);
        if (request.Description != null)
            existingOs.Description = new Label(request.Description, 1033);

        var sdkRequest = new UpdateOptionSetRequest
        {
            OptionSet = existingOs,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateGlobalOptionSets();

        reporter?.ReportInfo($"Global choice '{request.Name}' updated.");
        _logger?.LogInformation("Updated global choice {Name}", request.Name);
    }

    /// <inheritdoc />
    public async Task DeleteGlobalChoiceAsync(
        DeleteGlobalChoiceRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.Name, "Name");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: DeleteGlobalChoice {Name} validated", request.Name);
            return;
        }

        reporter?.ReportPhase("Deleting global choice", request.Name);

        var sdkRequest = new DeleteOptionSetRequest
        {
            Name = request.Name
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateGlobalOptionSets();

        reporter?.ReportInfo($"Global choice '{request.Name}' deleted.");
        _logger?.LogInformation("Deleted global choice {Name}", request.Name);
    }

    /// <inheritdoc />
    public async Task<int> AddOptionValueAsync(AddOptionValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");
        _validator.ValidateRequiredString(request.Label, "Label");

        var sdkRequest = new InsertOptionValueRequest
        {
            OptionSetName = request.OptionSetName,
            Label = new Label(request.Label, 1033),
            Value = request.Value,
            SolutionUniqueName = request.SolutionUniqueName
        };

        if (!string.IsNullOrEmpty(request.Description))
            sdkRequest.Description = new Label(request.Description, 1033);
        if (!string.IsNullOrEmpty(request.EntityLogicalName))
            sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        if (!string.IsNullOrEmpty(request.AttributeLogicalName))
            sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (InsertOptionValueResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Added option value {Value} to {OptionSet}", response.NewOptionValue, request.OptionSetName);

        return response.NewOptionValue;
    }

    /// <inheritdoc />
    public async Task UpdateOptionValueAsync(Authoring.UpdateOptionValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");
        _validator.ValidateRequiredString(request.Label, "Label");

        var sdkRequest = new SdkUpdateOptionValueRequest
        {
            OptionSetName = request.OptionSetName,
            Value = request.Value,
            Label = new Label(request.Label, 1033),
            SolutionUniqueName = request.SolutionUniqueName
        };

        if (!string.IsNullOrEmpty(request.Description))
            sdkRequest.Description = new Label(request.Description, 1033);
        if (!string.IsNullOrEmpty(request.EntityLogicalName))
            sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        if (!string.IsNullOrEmpty(request.AttributeLogicalName))
            sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Updated option value {Value} in {OptionSet}", request.Value, request.OptionSetName);
    }

    /// <inheritdoc />
    public async Task DeleteOptionValueAsync(Authoring.DeleteOptionValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");

        var sdkRequest = new SdkDeleteOptionValueRequest
        {
            OptionSetName = request.OptionSetName,
            Value = request.Value,
            SolutionUniqueName = request.SolutionUniqueName
        };

        if (!string.IsNullOrEmpty(request.EntityLogicalName))
            sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        if (!string.IsNullOrEmpty(request.AttributeLogicalName))
            sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Deleted option value {Value} from {OptionSet}", request.Value, request.OptionSetName);
    }

    /// <inheritdoc />
    public async Task ReorderOptionsAsync(Authoring.ReorderOptionsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");

        var sdkRequest = new OrderOptionRequest
        {
            OptionSetName = request.OptionSetName,
            Values = request.Order,
            SolutionUniqueName = request.SolutionUniqueName
        };

        if (!string.IsNullOrEmpty(request.EntityLogicalName))
            sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        if (!string.IsNullOrEmpty(request.AttributeLogicalName))
            sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Reordered options in {OptionSet}", request.OptionSetName);
    }

    /// <inheritdoc />
    public async Task UpdateStateValueAsync(Authoring.UpdateStateValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.AttributeLogicalName, "AttributeLogicalName");
        _validator.ValidateRequiredString(request.Label, "Label");

        var sdkRequest = new SdkUpdateStateValueRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            AttributeLogicalName = request.AttributeLogicalName,
            Value = request.Value,
            Label = new Label(request.Label, 1033)
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Updated state value {Value} label to '{Label}' on {Entity}.{Attribute}",
            request.Value, request.Label, request.EntityLogicalName, request.AttributeLogicalName);
    }

    #endregion

    #region Alternate Keys

    /// <inheritdoc />
    public async Task<CreateKeyResult> CreateKeyAsync(
        CreateKeyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateKeyRequest(request, prefix);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateKey {SchemaName} on {Entity} validated",
                request.SchemaName, request.EntityLogicalName);
            return new CreateKeyResult
            {
                SchemaName = request.SchemaName,
                WasDryRun = true,
                ValidationMessages = []
            };
        }

        reporter?.ReportPhase("Creating alternate key", request.SchemaName);

        var entityKey = new EntityKeyMetadata
        {
            SchemaName = request.SchemaName,
            DisplayName = new Label(request.DisplayName, 1033),
            KeyAttributes = request.KeyAttributes
        };

        var sdkRequest = new CreateEntityKeyRequest
        {
            EntityName = request.EntityLogicalName,
            EntityKey = entityKey,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        var response = (CreateEntityKeyResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Alternate key '{request.SchemaName}' created on '{request.EntityLogicalName}'.");
        _logger?.LogInformation("Created alternate key {SchemaName} on {Entity} with MetadataId {MetadataId}",
            request.SchemaName, request.EntityLogicalName, response.EntityKeyId);

        return new CreateKeyResult
        {
            SchemaName = request.SchemaName,
            MetadataId = response.EntityKeyId,
            WasDryRun = false,
            ValidationMessages = []
        };
    }

    /// <inheritdoc />
    public async Task DeleteKeyAsync(
        Authoring.DeleteKeyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.KeyLogicalName, "KeyLogicalName");

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: DeleteKey {Key} from {Entity} validated",
                request.KeyLogicalName, request.EntityLogicalName);
            return;
        }

        reporter?.ReportPhase("Deleting alternate key", request.KeyLogicalName);

        var sdkRequest = new DeleteEntityKeyRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            Name = request.KeyLogicalName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        reporter?.ReportInfo($"Alternate key '{request.KeyLogicalName}' deleted from '{request.EntityLogicalName}'.");
        _logger?.LogInformation("Deleted alternate key {Key} from {Entity}", request.KeyLogicalName, request.EntityLogicalName);
    }

    /// <inheritdoc />
    public async Task ReactivateKeyAsync(Authoring.ReactivateKeyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.KeyLogicalName, "KeyLogicalName");

        var sdkRequest = new ReactivateEntityKeyRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            EntityKeyLogicalName = request.KeyLogicalName
        };

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Reactivated alternate key {Key} on {Entity}", request.KeyLogicalName, request.EntityLogicalName);
    }

    #endregion

    #region Private Helpers

    private async Task<string> ResolvePublisherPrefixAsync(string solutionUniqueName, CancellationToken ct)
    {
        if (_publisherPrefixCache.TryGetValue(solutionUniqueName, out var cached))
        {
            return cached;
        }

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);

        // Query solution for publisher reference
        var solutionQuery = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("publisherid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("uniquename", ConditionOperator.Equal, solutionUniqueName)
                }
            }
        };

        var solutionResult = await client.RetrieveMultipleAsync(solutionQuery, ct).ConfigureAwait(false);

        if (solutionResult.Entities.Count == 0)
        {
            throw new MetadataValidationException(
                MetadataErrorCodes.EntityNotFound,
                $"Solution '{solutionUniqueName}' not found.",
                "SolutionUniqueName");
        }

        var publisherId = solutionResult.Entities[0].GetAttributeValue<EntityReference>("publisherid").Id;

        // Query publisher for customization prefix
        var publisherQuery = new QueryExpression("publisher")
        {
            ColumnSet = new ColumnSet("customizationprefix"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("publisherid", ConditionOperator.Equal, publisherId)
                }
            }
        };

        var publisherResult = await client.RetrieveMultipleAsync(publisherQuery, ct).ConfigureAwait(false);

        if (publisherResult.Entities.Count == 0)
        {
            throw new MetadataValidationException(
                MetadataErrorCodes.EntityNotFound,
                $"Publisher for solution '{solutionUniqueName}' not found.",
                "SolutionUniqueName");
        }

        var prefix = publisherResult.Entities[0].GetAttributeValue<string>("customizationprefix");
        _publisherPrefixCache[solutionUniqueName] = prefix;

        return prefix;
    }

    private static AttributeMetadata BuildAttributeMetadata(CreateColumnRequest request)
    {
        var requiredLevel = ParseRequiredLevel(request.RequiredLevel);

        AttributeMetadata attr = request.ColumnType switch
        {
            SchemaColumnType.String => new StringAttributeMetadata
            {
                MaxLength = request.MaxLength ?? 100,
                FormatName = ParseStringFormat(request.Format),
                AutoNumberFormat = request.AutoNumberFormat
            },
            SchemaColumnType.Memo => new MemoAttributeMetadata
            {
                MaxLength = request.MaxLength ?? 2000
            },
            SchemaColumnType.Integer => new IntegerAttributeMetadata
            {
                MinValue = request.MinValue.HasValue ? (int)request.MinValue.Value : null,
                MaxValue = request.MaxValue.HasValue ? (int)request.MaxValue.Value : null,
                Format = ParseIntegerFormat(request.Format)
            },
            SchemaColumnType.BigInt => new BigIntAttributeMetadata(),
            SchemaColumnType.Decimal => new DecimalAttributeMetadata
            {
                MinValue = request.MinValue.HasValue ? (decimal)request.MinValue.Value : null,
                MaxValue = request.MaxValue.HasValue ? (decimal)request.MaxValue.Value : null,
                Precision = request.Precision
            },
            SchemaColumnType.Double => new DoubleAttributeMetadata
            {
                MinValue = request.MinValue.HasValue ? request.MinValue.Value : null,
                MaxValue = request.MaxValue.HasValue ? request.MaxValue.Value : null,
                Precision = request.Precision
            },
            SchemaColumnType.Money => new MoneyAttributeMetadata
            {
                MinValue = request.MinValue.HasValue ? request.MinValue.Value : null,
                MaxValue = request.MaxValue.HasValue ? request.MaxValue.Value : null,
                Precision = request.Precision,
                ImeMode = ParseImeMode(request.ImeMode)
            },
            SchemaColumnType.Boolean => BuildBooleanAttribute(request),
            SchemaColumnType.DateTime => new DateTimeAttributeMetadata
            {
                DateTimeBehavior = ParseDateTimeBehavior(request.DateTimeBehavior),
                Format = ParseDateTimeFormat(request.Format)
            },
            SchemaColumnType.Choice => BuildChoiceAttribute(request),
            SchemaColumnType.Choices => BuildMultiSelectChoiceAttribute(request),
            SchemaColumnType.Image => new ImageAttributeMetadata
            {
                MaxSizeInKB = request.MaxSizeInKB,
                CanStoreFullImage = request.CanStoreFullImage ?? false
            },
            SchemaColumnType.File => new FileAttributeMetadata
            {
                MaxSizeInKB = request.MaxSizeInKB ?? 32768
            },
            _ => throw new MetadataValidationException(
                MetadataErrorCodes.InvalidConstraint,
                $"Unsupported column type: {request.ColumnType}",
                "ColumnType")
        };

        attr.SchemaName = request.SchemaName;
        attr.DisplayName = new Label(request.DisplayName, 1033);
        attr.RequiredLevel = new AttributeRequiredLevelManagedProperty(requiredLevel);

        if (!string.IsNullOrEmpty(request.Description))
            attr.Description = new Label(request.Description, 1033);
        if (request.IsAuditEnabled.HasValue)
            attr.IsAuditEnabled = new BooleanManagedProperty(request.IsAuditEnabled.Value);
        if (request.IsSecured.HasValue)
            attr.IsSecured = request.IsSecured.Value;
        if (request.IsValidForAdvancedFind.HasValue)
            attr.IsValidForAdvancedFind = new BooleanManagedProperty(request.IsValidForAdvancedFind.Value);

        return attr;
    }

    private static BooleanAttributeMetadata BuildBooleanAttribute(CreateColumnRequest request)
    {
        var trueLabel = request.TrueLabel ?? "Yes";
        var falseLabel = request.FalseLabel ?? "No";

        var attr = new BooleanAttributeMetadata
        {
            OptionSet = new BooleanOptionSetMetadata(
                new OptionMetadata(new Label(trueLabel, 1033), 1),
                new OptionMetadata(new Label(falseLabel, 1033), 0))
        };

        if (request.DefaultValue.HasValue)
            attr.DefaultValue = request.DefaultValue.Value != 0;

        return attr;
    }

    private static PicklistAttributeMetadata BuildChoiceAttribute(CreateColumnRequest request)
    {
        var attr = new PicklistAttributeMetadata();

        if (!string.IsNullOrEmpty(request.OptionSetName))
        {
            // Use existing global option set
            attr.OptionSet = new OptionSetMetadata { IsGlobal = true, Name = request.OptionSetName };
        }
        else if (request.Options != null && request.Options.Length > 0)
        {
            // Create local option set
            var optionSet = new OptionSetMetadata();
            foreach (var opt in request.Options)
            {
                optionSet.Options.Add(new OptionMetadata(new Label(opt.Label, 1033), opt.Value));
            }
            attr.OptionSet = optionSet;
        }

        if (request.DefaultValue.HasValue)
            attr.DefaultFormValue = request.DefaultValue;

        return attr;
    }

    private static MultiSelectPicklistAttributeMetadata BuildMultiSelectChoiceAttribute(CreateColumnRequest request)
    {
        var attr = new MultiSelectPicklistAttributeMetadata();

        if (!string.IsNullOrEmpty(request.OptionSetName))
        {
            attr.OptionSet = new OptionSetMetadata { IsGlobal = true, Name = request.OptionSetName };
        }
        else if (request.Options != null && request.Options.Length > 0)
        {
            var optionSet = new OptionSetMetadata();
            foreach (var opt in request.Options)
            {
                optionSet.Options.Add(new OptionMetadata(new Label(opt.Label, 1033), opt.Value));
            }
            attr.OptionSet = optionSet;
        }

        if (request.DefaultValue.HasValue)
            attr.DefaultFormValue = request.DefaultValue;

        return attr;
    }

    private static void ApplyTypeSpecificUpdates(AttributeMetadata attr, UpdateColumnRequest request)
    {
        switch (attr)
        {
            case StringAttributeMetadata stringAttr:
                if (request.MaxLength.HasValue) stringAttr.MaxLength = request.MaxLength.Value;
                if (request.Format != null) stringAttr.FormatName = ParseStringFormat(request.Format);
                if (request.AutoNumberFormat != null) stringAttr.AutoNumberFormat = request.AutoNumberFormat;
                break;
            case MemoAttributeMetadata memoAttr:
                if (request.MaxLength.HasValue) memoAttr.MaxLength = request.MaxLength.Value;
                break;
            case IntegerAttributeMetadata intAttr:
                if (request.MinValue.HasValue) intAttr.MinValue = (int)request.MinValue.Value;
                if (request.MaxValue.HasValue) intAttr.MaxValue = (int)request.MaxValue.Value;
                if (request.Format != null) intAttr.Format = ParseIntegerFormat(request.Format);
                break;
            case DecimalAttributeMetadata decAttr:
                if (request.MinValue.HasValue) decAttr.MinValue = (decimal)request.MinValue.Value;
                if (request.MaxValue.HasValue) decAttr.MaxValue = (decimal)request.MaxValue.Value;
                if (request.Precision.HasValue) decAttr.Precision = request.Precision.Value;
                break;
            case DoubleAttributeMetadata dblAttr:
                if (request.MinValue.HasValue) dblAttr.MinValue = request.MinValue.Value;
                if (request.MaxValue.HasValue) dblAttr.MaxValue = request.MaxValue.Value;
                if (request.Precision.HasValue) dblAttr.Precision = request.Precision.Value;
                break;
            case MoneyAttributeMetadata moneyAttr:
                if (request.MinValue.HasValue) moneyAttr.MinValue = request.MinValue.Value;
                if (request.MaxValue.HasValue) moneyAttr.MaxValue = request.MaxValue.Value;
                if (request.Precision.HasValue) moneyAttr.Precision = request.Precision.Value;
                break;
        }
    }

    private static CascadeConfiguration MapCascadeConfiguration(CascadeConfigurationDto? dto)
    {
        if (dto == null)
        {
            return new CascadeConfiguration
            {
                Assign = SdkCascadeType.NoCascade,
                Delete = SdkCascadeType.RemoveLink,
                Merge = SdkCascadeType.NoCascade,
                Reparent = SdkCascadeType.NoCascade,
                Share = SdkCascadeType.NoCascade,
                Unshare = SdkCascadeType.NoCascade
            };
        }

        return new CascadeConfiguration
        {
            Assign = MapCascadeType(dto.Assign),
            Delete = MapCascadeType(dto.Delete),
            Merge = MapCascadeType(dto.Merge),
            Reparent = MapCascadeType(dto.Reparent),
            Share = MapCascadeType(dto.Share),
            Unshare = MapCascadeType(dto.Unshare)
        };
    }

    private static SdkCascadeType MapCascadeType(CascadeBehavior? behavior)
    {
        return behavior switch
        {
            CascadeBehavior.Cascade => SdkCascadeType.Cascade,
            CascadeBehavior.Active => SdkCascadeType.Active,
            CascadeBehavior.NoCascade => SdkCascadeType.NoCascade,
            CascadeBehavior.UserOwned => SdkCascadeType.UserOwned,
            CascadeBehavior.RemoveLink => SdkCascadeType.RemoveLink,
            CascadeBehavior.Restrict => SdkCascadeType.Restrict,
            _ => SdkCascadeType.NoCascade
        };
    }

    private static AttributeRequiredLevel ParseRequiredLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return AttributeRequiredLevel.None;

        return level.ToLowerInvariant() switch
        {
            "required" or "applicationrequired" => AttributeRequiredLevel.ApplicationRequired,
            "recommended" => AttributeRequiredLevel.Recommended,
            "systemrequired" => AttributeRequiredLevel.SystemRequired,
            _ => AttributeRequiredLevel.None
        };
    }

    private static StringFormatName? ParseStringFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return StringFormatName.Text;

        return format.ToLowerInvariant() switch
        {
            "email" => StringFormatName.Email,
            "url" => StringFormatName.Url,
            "phone" => StringFormatName.Phone,
            "tickersymbol" => StringFormatName.TickerSymbol,
            "phonetic" => StringFormatName.PhoneticGuide,
            "versionnumber" => StringFormatName.VersionNumber,
            "json" => StringFormatName.Json,
            "richtext" => StringFormatName.RichText,
            _ => StringFormatName.Text
        };
    }

    private static IntegerFormat? ParseIntegerFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return IntegerFormat.None;

        return format.ToLowerInvariant() switch
        {
            "duration" => IntegerFormat.Duration,
            "timezone" => IntegerFormat.TimeZone,
            "language" => IntegerFormat.Language,
            "locale" => IntegerFormat.Locale,
            _ => IntegerFormat.None
        };
    }

    private static ImeMode? ParseImeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return null;

        return mode.ToLowerInvariant() switch
        {
            "auto" => ImeMode.Auto,
            "active" => ImeMode.Active,
            "inactive" => ImeMode.Inactive,
            "disabled" => ImeMode.Disabled,
            _ => null
        };
    }

    private static DateTimeBehavior? ParseDateTimeBehavior(string? behavior)
    {
        if (string.IsNullOrWhiteSpace(behavior))
            return Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.UserLocal;

        return behavior.ToLowerInvariant() switch
        {
            "dateonly" => Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.DateOnly,
            "timezoneindependent" => Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.TimeZoneIndependent,
            _ => Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.UserLocal
        };
    }

    private static DateTimeFormat? ParseDateTimeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return DateTimeFormat.DateAndTime;

        return format.ToLowerInvariant() switch
        {
            "dateonly" => DateTimeFormat.DateOnly,
            _ => DateTimeFormat.DateAndTime
        };
    }

    #endregion
}
