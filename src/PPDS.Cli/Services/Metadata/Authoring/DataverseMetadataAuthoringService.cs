using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using SdkCascadeType = Microsoft.Xrm.Sdk.Metadata.CascadeType;
using SdkCreateOneToManyRequest = Microsoft.Xrm.Sdk.Messages.CreateOneToManyRequest;
using SdkCreateManyToManyRequest = Microsoft.Xrm.Sdk.Messages.CreateManyToManyRequest;
using SdkUpdateRelationshipRequest = Microsoft.Xrm.Sdk.Messages.UpdateRelationshipRequest;
using SdkDeleteRelationshipRequest = Microsoft.Xrm.Sdk.Messages.DeleteRelationshipRequest;
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOptionValueRequest = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;
using SdkUpdateStateValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Dataverse implementation of <see cref="IMetadataAuthoringService"/>.
/// Provides schema CRUD operations against Dataverse via the SDK.
/// </summary>
public partial class DataverseMetadataAuthoringService : IMetadataAuthoringService, IMetadataDeletePreviewService
{
    private readonly IDataverseConnectionPool _connectionPool;
    private readonly SchemaValidator _validator;
    private readonly IShakedownGuard _guard;
    private readonly ILogger<DataverseMetadataAuthoringService>? _logger;
    private readonly ICachedMetadataProvider? _cacheProvider;
    private readonly IOrganizationBaseLanguageProvider _baseLanguage;
    private readonly ConcurrentDictionary<string, string> _publisherPrefixCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _publisherOptionValuePrefixCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="DataverseMetadataAuthoringService"/> class.
    /// </summary>
    /// <param name="baseLanguage">
    /// The environment's base language, which every label is written in. When null, it is read from
    /// <paramref name="connectionPool"/> (organization.languagecode) the first time a label is written, and cached for
    /// this instance.
    /// </param>
    public DataverseMetadataAuthoringService(
        IDataverseConnectionPool connectionPool,
        SchemaValidator validator,
        IShakedownGuard guard,
        ILogger<DataverseMetadataAuthoringService>? logger = null,
        ICachedMetadataProvider? cacheProvider = null,
        IOrganizationBaseLanguageProvider? baseLanguage = null)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger;
        _cacheProvider = cacheProvider;
        _baseLanguage = baseLanguage ?? new OrganizationBaseLanguageProvider(connectionPool);
    }

    /// <summary>The organisation's base language: the LCID every label PPDS writes is authored in.</summary>
    private Task<int> BaseLanguageAsync(CancellationToken ct) => _baseLanguage.GetBaseLanguageAsync(ct);

    #region Tables

    /// <inheritdoc />
    public async Task<CreateTableResult> CreateTableAsync(
        CreateTableRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.table.create");

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
        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);

        var entityMetadata = new EntityMetadata
        {
            SchemaName = request.SchemaName,
            DisplayName = new Label(request.DisplayName, lcid),
            DisplayCollectionName = new Label(request.PluralDisplayName, lcid),
            Description = string.IsNullOrEmpty(request.Description) ? null : new Label(request.Description, lcid),
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
            DisplayName = new Label(primaryAttrDisplayName, lcid),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
            MaxLength = primaryAttrMaxLength
        };

        var sdkRequest = new CreateEntityRequest
        {
            Entity = entityMetadata,
            PrimaryAttribute = primaryAttribute,
            SolutionUniqueName = request.SolutionUniqueName
        };

        CreateEntityResponse response;
        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            response = (CreateEntityResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntityList();
        _cacheProvider?.InvalidateEntity(request.SchemaName.ToLowerInvariant());

        var created = request.SchemaName.ToLowerInvariant();
        reporter?.ReportPhase("Confirming table labels", created);
        await ConfirmLabelsAsync("table", $"Table '{created}' was created.", request.Publish, lcid,
            token => ReadTableForConfirmationAsync(created, token), ct,
            new LabelExpectation<EntityMetadata>("display name", request.DisplayName, t => t.DisplayName),
            new LabelExpectation<EntityMetadata>("plural name", request.PluralDisplayName, t => t.DisplayCollectionName),
            new LabelExpectation<EntityMetadata>("description", string.IsNullOrEmpty(request.Description) ? null : request.Description, t => t.Description)).ConfigureAwait(false);
        var primaryName = primaryAttrSchemaName.ToLowerInvariant();
        await ConfirmLabelsAsync("table", $"Table '{created}' was created.", request.Publish, lcid,
            token => ReadColumnForConfirmationAsync(created, primaryName, token), ct,
            new LabelExpectation<AttributeMetadata>("primary column display name", primaryAttrDisplayName, a => a.DisplayName)).ConfigureAwait(false);

        if (request.Publish)
            await PublishEntityInternalAsync(request.SchemaName.ToLowerInvariant(), ct).ConfigureAwait(false);

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
    public async Task<MetadataLabelWriteResult> UpdateTableAsync(
        UpdateTableRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.table.update");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");

        // A dry-run is input-only (its documented scope, MetadataDryRunScope.Inputs): it reads nothing, so it neither
        // checks that the table exists nor computes notices.
        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateTable {Entity} validated successfully", request.EntityLogicalName);
            return MetadataLabelWriteResult.None;
        }

        // Labels are written in the organisation's base language only. The table's current labels are read (only when
        // a label is requested) so the update can say when the label Dataverse shows the caller is a stored translation
        // the write leaves unchanged; the written labels are then read back.
        var notices = MetadataLabelWriteResult.None;
        var labelsRequested = request.DisplayName != null || request.PluralDisplayName != null || request.Description != null;
        var lcid = 0;
        if (labelsRequested)
        {
            lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
            reporter?.ReportPhase("Retrieving existing table labels", request.EntityLogicalName);
            var existing = await ReadTableLabelsAsync(request.EntityLogicalName, ct).ConfigureAwait(false);
            notices = BaseLanguageNotices(lcid,
                ("DisplayName", "display name", request.DisplayName, existing.DisplayName),
                ("PluralDisplayName", "plural name", request.PluralDisplayName, existing.DisplayCollectionName),
                ("Description", "description", request.Description, existing.Description));
        }

        reporter?.ReportPhase("Updating table", request.EntityLogicalName);

        var entityMetadata = new EntityMetadata
        {
            LogicalName = request.EntityLogicalName
        };

        if (request.DisplayName != null)
            entityMetadata.DisplayName = new Label(request.DisplayName, lcid);
        if (request.PluralDisplayName != null)
            entityMetadata.DisplayCollectionName = new Label(request.PluralDisplayName, lcid);
        if (request.Description != null)
            entityMetadata.Description = new Label(request.Description, lcid);
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

        // Icon fields must be set via UpdateEntityRequest (SDK) — Web API EntityDefinitions PATCH
        // rejects these properties. Pass null to leave an icon unchanged; pass "" to clear it.
        if (request.IconSmallName != null)
            entityMetadata.IconSmallName = request.IconSmallName;
        if (request.IconMediumName != null)
            entityMetadata.IconMediumName = request.IconMediumName;
        if (request.IconVectorName != null)
            entityMetadata.IconVectorName = request.IconVectorName;

        var sdkRequest = new UpdateEntityRequest
        {
            Entity = entityMetadata,
            MergeLabels = true, // The request carries only base-language labels. Preserve other translations.
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntityList();
        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        if (labelsRequested)
        {
            reporter?.ReportPhase("Confirming table labels", request.EntityLogicalName);
            await ConfirmLabelsAsync("table", $"Table '{request.EntityLogicalName}' was updated.", request.Publish, lcid,
                token => ReadTableForConfirmationAsync(request.EntityLogicalName, token), ct,
                new LabelExpectation<EntityMetadata>("display name", request.DisplayName, t => t.DisplayName),
                new LabelExpectation<EntityMetadata>("plural name", request.PluralDisplayName, t => t.DisplayCollectionName),
                new LabelExpectation<EntityMetadata>("description", request.Description, t => t.Description)).ConfigureAwait(false);
        }

        if (request.Publish)
            await PublishEntityInternalAsync(request.EntityLogicalName, ct).ConfigureAwait(false);

        // Issue #1009: keep the message UI-agnostic. Surfaces format their own publish command.
        reporter?.ReportInfo(request.Publish
            ? $"Table '{request.EntityLogicalName}' updated and published."
            : $"Table '{request.EntityLogicalName}' updated. Changes must be published to take effect.");
        _logger?.LogInformation("Updated table {Entity}", request.EntityLogicalName);
        return notices;
    }

    /// <summary>
    /// Reads a table's entity-level metadata (labels) on its own lease, before a label update. Published, like the
    /// column read the column notices use, because list commands show published labels. Checkout failures keep the
    /// shared acquisition classification; SDK failures keep the mapper's timeout/connection/auth codes, exit codes and
    /// re-authentication signal (as the column read does); a response without table metadata stops the update.
    /// </summary>
    private async Task<EntityMetadata> ReadTableLabelsAsync(string entityLogicalName, CancellationToken ct)
    {
        await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
        var message = $"Could not read table '{entityLogicalName}' before updating its labels.";
        try
        {
            var response = await client.ExecuteAsync(new RetrieveEntityRequest
            {
                LogicalName = entityLogicalName,
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            }, ct).ConfigureAwait(false);
            return (response as RetrieveEntityResponse)?.EntityMetadata
                ?? throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
        {
            throw NormalizeColumnLabelSdkFailure(ex, message);
        }
    }

    /// <inheritdoc />
    public async Task DeleteTableAsync(
        DeleteTableRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.table.delete");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");

        await EnsureDeleteAllowedAsync(new MetadataDeleteTarget(MetadataDeleteKind.Table, request.SolutionUniqueName, request.EntityLogicalName), ct).ConfigureAwait(false);

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
        _guard.EnsureCanMutate("metadata.column.create");

        // Text that names no required level or format is refused before anything is read (#1919).
        ValidateCreateColumnText(request);

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateColumnRequest(request, prefix);

        var options = request.Options;
        ValidationMessage[] warnings = [];
        if ((request.ColumnType == SchemaColumnType.Choice || request.ColumnType == SchemaColumnType.Choices)
            && string.IsNullOrEmpty(request.OptionSetName) && options is { Length: > 0 })
            (options, warnings) = await PrepareCreateOptionValuesAsync(options, request.SolutionUniqueName, reporter, ct).ConfigureAwait(false);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateColumn {SchemaName} on {Entity} validated successfully",
                request.SchemaName, request.EntityLogicalName);
            return new CreateColumnResult
            {
                LogicalName = request.SchemaName.ToLowerInvariant(),
                WasDryRun = true,
                ValidationMessages = warnings
            };
        }

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var attribute = BuildAttributeMetadata(request, options, lcid);

        var sdkRequest = new CreateAttributeRequest
        {
            EntityName = request.EntityLogicalName,
            Attribute = attribute,
            SolutionUniqueName = request.SolutionUniqueName
        };

        if (attribute is EnumAttributeMetadata { OptionSet: { IsGlobal: true } reference })
        {
            reporter?.ReportPhase("Resolving global choice", reference.Name);
            reference.Name = await ResolveGlobalChoiceNameAsync(reference.Name, ct).ConfigureAwait(false);
        }

        reporter?.ReportPhase("Creating column", request.SchemaName);
        CreateAttributeResponse response;
        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            response = (CreateAttributeResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);

        var createdColumn = request.SchemaName.ToLowerInvariant();
        reporter?.ReportPhase("Confirming column labels", createdColumn);
        await ConfirmLabelsAsync("column", $"Column '{createdColumn}' was created on '{request.EntityLogicalName}'.", request.Publish, lcid,
            token => ReadColumnForConfirmationAsync(request.EntityLogicalName, createdColumn, token), ct,
            CreatedColumnExpectations(attribute, request).ToArray()).ConfigureAwait(false);

        if (request.Publish)
            await PublishEntityInternalAsync(request.EntityLogicalName, ct).ConfigureAwait(false);

        reporter?.ReportInfo($"Column '{request.SchemaName}' created on '{request.EntityLogicalName}'.");
        _logger?.LogInformation("Created column {SchemaName} on {Entity} with MetadataId {MetadataId}",
            request.SchemaName, request.EntityLogicalName, response.AttributeId);

        return new CreateColumnResult
        {
            LogicalName = request.SchemaName.ToLowerInvariant(),
            MetadataId = response.AttributeId,
            WasDryRun = false,
            ValidationMessages = warnings
        };
    }

    /// <inheritdoc />
    public async Task<MetadataLabelWriteResult> UpdateColumnAsync(
        UpdateColumnRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.column.update");
        ct.ThrowIfCancellationRequested();

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");
        ct.ThrowIfCancellationRequested();

        // Text that names no required level or format is refused before anything is read, dry run included (#1919).
        ValidateGivenRequiredLevel(request.RequiredLevel);
        ValidateGivenFormat(request.Format);

        // A dry-run is input-only (MetadataDryRunScope.Inputs): it reads nothing and computes no notices.
        var columnLabelsRequested = request.DisplayName != null || request.Description != null;
        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateColumn {Column} on {Entity} validated",
                request.ColumnLogicalName, request.EntityLogicalName);
            return MetadataLabelWriteResult.None;
        }

        reporter?.ReportPhase("Retrieving existing column metadata");

        var retrieveRequest = new RetrieveAttributeRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            LogicalName = request.ColumnLogicalName
        };
        var existingAttr = await ReadColumnForUpdateAsync(retrieveRequest, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Keep the existing typed and exact-base primary-ID contracts separate from
        // the finite, identity-checked State/Status/GUID label-only transition.
        var lcid = columnLabelsRequested ? await BaseLanguageAsync(ct).ConfigureAwait(false) : 0;
        var notices = BaseLanguageNotices(lcid,
            ("DisplayName", "display name", request.DisplayName, existingAttr.DisplayName),
            ("Description", "description", request.Description, existingAttr.Description));
        if (IsFiniteLabelOnlyColumn(existingAttr))
        {
            await UpdateFiniteColumnLabelsAsync(request, existingAttr, lcid, reporter, ct).ConfigureAwait(false);
            return notices;
        }

        var primaryIdLabels = existingAttr.GetType() == typeof(AttributeMetadata)
            && existingAttr.AttributeType == AttributeTypeCode.Uniqueidentifier
            && existingAttr.IsPrimaryId == true;
        if (primaryIdLabels)
            await ValidatePrimaryIdLabelUpdateAsync(request, existingAttr, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Updating column", request.ColumnLogicalName);

        // #1009: re-sending the retrieved AttributeMetadata silently drops RequiredLevel
        // changes on the wire. Build a fresh, minimal attribute of the same SDK type
        // and copy only the fields the caller explicitly asked to change.
        var updateAttr = primaryIdLabels ? new AttributeMetadata() : CreateUpdateAttribute(existingAttr);
        // Some SDK constructors set a required level of their own (FileAttributeMetadata sets None); only a level the
        // caller gave is sent (#1919).
        updateAttr.RequiredLevel = null;
        updateAttr.LogicalName = existingAttr.LogicalName;
        updateAttr.SchemaName = existingAttr.SchemaName;

        if (request.DisplayName != null)
            updateAttr.DisplayName = new Label(request.DisplayName, lcid);
        if (request.Description != null)
            updateAttr.Description = new Label(request.Description, lcid);
        if (request.RequiredLevel != null)
            updateAttr.RequiredLevel = new AttributeRequiredLevelManagedProperty(ParseRequiredLevel(request.RequiredLevel));
        if (request.IsAuditEnabled.HasValue)
            updateAttr.IsAuditEnabled = new BooleanManagedProperty(request.IsAuditEnabled.Value);
        if (request.IsSecured.HasValue)
            updateAttr.IsSecured = request.IsSecured.Value;
        if (request.IsValidForAdvancedFind.HasValue)
            updateAttr.IsValidForAdvancedFind = new BooleanManagedProperty(request.IsValidForAdvancedFind.Value);

        ApplyTypeSpecificUpdates(updateAttr, request);

        var sdkRequest = new UpdateAttributeRequest
        {
            EntityName = request.EntityLogicalName,
            Attribute = updateAttr,
            MergeLabels = true, // The request carries only base-language labels. Preserve other translations.
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        await ConfirmColumnLabelsAsync(request, lcid, ct).ConfigureAwait(false);

        if (request.Publish)
            await PublishEntityInternalAsync(request.EntityLogicalName, ct).ConfigureAwait(false);

        // Issue #1009: keep the message UI-agnostic. Surfaces format their own publish command.
        reporter?.ReportInfo(request.Publish
            ? $"Column '{request.ColumnLogicalName}' updated on '{request.EntityLogicalName}' and published."
            : $"Column '{request.ColumnLogicalName}' updated on '{request.EntityLogicalName}'. Changes must be published to take effect.");
        _logger?.LogInformation("Updated column {Column} on {Entity}", request.ColumnLogicalName, request.EntityLogicalName);
        return notices;
    }

    /// <inheritdoc />
    public async Task DeleteColumnAsync(
        DeleteColumnRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.column.delete");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");

        await EnsureDeleteAllowedAsync(new MetadataDeleteTarget(MetadataDeleteKind.Column, request.SolutionUniqueName, request.EntityLogicalName, request.ColumnLogicalName), ct).ConfigureAwait(false);

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
        PPDS.Dataverse.Metadata.Authoring.CreateOneToManyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.relationship.createOneToMany");

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

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var lookup = new LookupAttributeMetadata
        {
            SchemaName = request.LookupSchemaName,
            DisplayName = new Label(request.LookupDisplayName, lcid)
        };

        var sdkRequest = new SdkCreateOneToManyRequest
        {
            OneToManyRelationship = relationship,
            Lookup = lookup,
            SolutionUniqueName = request.SolutionUniqueName
        };

        CreateOneToManyResponse response;
        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            response = (CreateOneToManyResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntity(request.ReferencedEntity);
        _cacheProvider?.InvalidateEntity(request.ReferencingEntity);

        var lookupColumn = request.LookupSchemaName.ToLowerInvariant();
        await ConfirmLabelsAsync("lookup column", $"1:N relationship '{request.SchemaName}' was created.", publishRequested: false, lcid,
            token => ReadColumnForConfirmationAsync(request.ReferencingEntity, lookupColumn, token), ct,
            new LabelExpectation<AttributeMetadata>("lookup display name", request.LookupDisplayName, a => a.DisplayName)).ConfigureAwait(false);

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
        PPDS.Dataverse.Metadata.Authoring.CreateManyToManyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.relationship.createManyToMany");

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        // Default the intersect entity schema name to the relationship schema name when not supplied.
        // Matches the Power Apps Maker / SDK sample convention where both names are the same string
        // (Microsoft Learn: CreateManyToManyRequest). Done before validation so prefix checks see the
        // resolved value and dry-run catches mismatches.
        var intersectSchemaName = string.IsNullOrWhiteSpace(request.IntersectEntitySchemaName)
            ? request.SchemaName
            : request.IntersectEntitySchemaName;

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateManyToManyRequest(request, prefix, intersectSchemaName);

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
            Entity2LogicalName = request.Entity2LogicalName
        };

        if (!string.IsNullOrEmpty(request.Entity1NavigationPropertyName))
            relationship.Entity1NavigationPropertyName = request.Entity1NavigationPropertyName;
        if (!string.IsNullOrEmpty(request.Entity2NavigationPropertyName))
            relationship.Entity2NavigationPropertyName = request.Entity2NavigationPropertyName;

        var sdkRequest = new SdkCreateManyToManyRequest
        {
            ManyToManyRelationship = relationship,
            SolutionUniqueName = request.SolutionUniqueName,
            IntersectEntitySchemaName = intersectSchemaName
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
    public async Task<UpdateRelationshipResult> UpdateRelationshipAsync(
        PPDS.Dataverse.Metadata.Authoring.UpdateRelationshipRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.relationship.update");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.SchemaName, "SchemaName");

        // #1919: an update changes only the settings the caller gave. Each given value is checked against the values
        // Microsoft documents for its action before anything is read.
        var given = GivenCascadeSettings(request.CascadeConfiguration);
        if (given.Count == 0 && !request.IsHierarchical.HasValue)
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                "Nothing to update: give at least one cascade setting (Assign, Delete, Merge, Reparent, Share or Unshare) or the hierarchical flag.",
                "CascadeConfiguration");

        // The dry run reads the relationship too, so that it can show each setting before and after.
        reporter?.ReportPhase("Retrieving existing relationship", request.SchemaName);
        var existingRel = await ReadRelationshipForUpdateAsync(request.SchemaName, ct).ConfigureAwait(false);

        // The definition sent is the one read, so it must be the relationship asked for: a response that names another
        // relationship, or carries no metadata id, is not written back.
        if (!string.Equals(existingRel.SchemaName, request.SchemaName, StringComparison.OrdinalIgnoreCase)
            || existingRel.MetadataId is not { } metadataId || metadataId == Guid.Empty)
            throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                "Dataverse did not return the relationship asked for (another schema name, or no metadata id), so nothing was changed. Check the schema name and try again.");

        if (existingRel is not OneToManyRelationshipMetadata o2m)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "Cascade settings and the hierarchical flag apply only to a one-to-many relationship; this relationship is many-to-many. Nothing was changed.",
                "SchemaName");

        // Every setting not given is sent exactly as read, so it must have been read. A relationship whose current
        // settings are not all known is refused rather than written with a guess.
        var cascade = o2m.CascadeConfiguration;
        var before = new Dictionary<string, CascadeBehavior>(StringComparer.Ordinal);
        foreach (var (name, read, _) in CascadeSettingAccessors)
        {
            var current = cascade == null ? null : read(cascade);
            if (current is not { } currentValue || ToCascadeBehavior(currentValue) is not { } value)
                throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                    "Could not read all six cascade settings of the relationship, so it was not updated: a setting that is not given is sent as read, never guessed. Check the relationship in the maker portal and try again.");
            before[name] = value;
        }
        var hierarchicalBefore = o2m.IsHierarchical;

        // Only the given settings are written, on the object that was read.
        foreach (var (name, _, write) in CascadeSettingAccessors)
            if (given.TryGetValue(name, out var value))
                write(cascade!, ToSdkCascadeType(value));
        if (request.IsHierarchical.HasValue)
            o2m.IsHierarchical = request.IsHierarchical.Value;

        // The result is read back from the configuration that will be sent, not from the request, so it reports what
        // is actually sent.
        var settings = CascadeSettingAccessors.Select(accessor =>
        {
            var sent = accessor.Read(cascade!) is { } raw ? ToCascadeBehavior(raw) : null;
            return new CascadeSettingChange
            {
                Setting = accessor.Name,
                Before = before[accessor.Name],
                After = sent ?? throw new InvalidOperationException("A cascade setting was cleared while it was applied."),
                Requested = given.ContainsKey(accessor.Name)
            };
        }).ToList();

        var hasChanges = settings.Any(s => s.Changed) || hierarchicalBefore != o2m.IsHierarchical;
        var send = !request.DryRun && hasChanges;
        var result = new UpdateRelationshipResult
        {
            SchemaName = o2m.SchemaName ?? request.SchemaName,
            WasDryRun = request.DryRun,
            WasSent = send,
            HasChanges = hasChanges,
            CascadeSettings = settings,
            IsHierarchicalBefore = hierarchicalBefore,
            IsHierarchicalAfter = o2m.IsHierarchical,
            // Microsoft: when Reparent or Share is changed to Cascade None, Dataverse starts the inherited access
            // rights cleanup job ("Create and edit relationships between tables", "Inherited access rights cleanup").
            RemovesInheritedAccess = settings.Any(s => s.Setting is "Reparent" or "Share"
                && s.Changed && s.After == CascadeBehavior.NoCascade)
        };

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateRelationship {SchemaName} computed", request.SchemaName);
            return result;
        }

        // Every value given already equals the current one: nothing is sent, and the result says so.
        if (!send)
        {
            reporter?.ReportInfo($"Relationship '{request.SchemaName}' already has every value given; nothing was sent.");
            _logger?.LogInformation("UpdateRelationship {SchemaName}: nothing to change, nothing sent", request.SchemaName);
            return result;
        }

        reporter?.ReportPhase("Updating relationship", request.SchemaName);

        // The update is safe only because it sends the object it read: UpdateRelationship writes every property of
        // the definition it is given (menu behaviour, labels, navigation names, customizability...), so a definition
        // built here would reset whatever it left out. Do not replace o2m with a new object.
        // Last writer wins: the request has no concurrency option, so a change made to the relationship between the
        // read above and this write is overwritten with the value read.
        var sdkRequest = new SdkUpdateRelationshipRequest
        {
            Relationship = o2m,
            MergeLabels = true, // The labels are sent as read; this keeps any language not in them (#1767).
            SolutionUniqueName = request.SolutionUniqueName
        };

        try
        {
            await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
        {
            throw RelationshipWriteFailure(ex);
        }

        // UpdateRelationshipRequest doesn't carry entity logical names, so we must
        // invalidate all entity caches to ensure stale relationship data is cleared.
        _cacheProvider?.InvalidateAll();

        reporter?.ReportInfo($"Relationship '{request.SchemaName}' updated. Changes must be published to take effect.");
        _logger?.LogInformation("Updated relationship {SchemaName}", request.SchemaName);
        return result;
    }

    /// <summary>
    /// Reads the relationship to update on its own lease, as if published, so that unpublished changes to the settings
    /// the caller did not give are sent back unchanged (#1767). SDK failures keep the mapper's classification; a
    /// response without a relationship stops the update.
    /// </summary>
    private async Task<RelationshipMetadataBase> ReadRelationshipForUpdateAsync(string schemaName, CancellationToken ct)
    {
        const string message = "Could not read the relationship before updating it, so it was not updated. Check the schema name and that your account can read the relationship's definition, then try again.";
        await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await client.ExecuteAsync(new RetrieveRelationshipRequest
            {
                Name = schemaName,
                RetrieveAsIfPublished = true
            }, ct).ConfigureAwait(false);
            return (response as RetrieveRelationshipResponse)?.RelationshipMetadata
                ?? throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, message);
        }
        catch (System.ServiceModel.FaultException<OrganizationServiceFault> fault) when (fault.Detail?.ErrorCode == ObjectDoesNotExistErrorCode)
        {
            // Dataverse's object-not-found code. Which code RetrieveRelationship returns for an unknown name is not
            // documented; this is the code the other metadata reads return (see ObjectDoesNotExistErrorCode).
            throw new PpdsNotFoundException("Relationship", schemaName,
                "No relationship with that schema name was found, so nothing was changed. Check the schema name (it is the relationship's SchemaName, not its display name).",
                fault);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
        {
            throw NormalizeColumnLabelSdkFailure(ex, message);
        }
    }

    /// <summary>
    /// The relationship write failed. A Dataverse fault is a refusal: the single request was not applied, so nothing
    /// was changed, and only Dataverse's typed fault code is shown (its message can carry ids). A timeout or a
    /// cancellation leaves the outcome unknown, and so may a failure with no answer from Dataverse; the message says
    /// so. The mapper's classification (sign-in, connection) is kept.
    /// </summary>
    private static PpdsException RelationshipWriteFailure(Exception ex)
    {
        if (!IsIndeterminate(ex) && FindTypedFault(ex) is not null)
            return new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                AppendDataverseFaultCode("Dataverse refused the relationship update, so nothing was changed.", ex), ex);

        var interruption = IsIndeterminate(ex) ? Interruption(ex) : "failed without an answer from Dataverse";
        return NormalizeColumnLabelSdkFailure(ex,
            $"The relationship update {interruption}, so whether it was applied is unknown. Read the relationship's current settings (a dry run of the same update shows them) before trying again.");
    }

    /// <summary>
    /// The six cascade settings an update can change, in the order of <see cref="CascadeActionRules.Actions"/>. The
    /// values valid for each are in <see cref="CascadeActionRules"/>.
    /// </summary>
    private static readonly (string Name, Func<CascadeConfiguration, SdkCascadeType?> Read, Action<CascadeConfiguration, SdkCascadeType> Write)[] CascadeSettingAccessors =
    [
        ("Assign", c => c.Assign, (c, v) => c.Assign = v),
        ("Delete", c => c.Delete, (c, v) => c.Delete = v),
        ("Merge", c => c.Merge, (c, v) => c.Merge = v),
        ("Reparent", c => c.Reparent, (c, v) => c.Reparent = v),
        ("Share", c => c.Share, (c, v) => c.Share = v),
        ("Unshare", c => c.Unshare, (c, v) => c.Unshare = v),
    ];

    /// <summary>
    /// The cascade settings the caller gave, by name. A value Microsoft does not document for its action is refused
    /// before anything is read.
    /// </summary>
    private static Dictionary<string, CascadeBehavior> GivenCascadeSettings(CascadeConfigurationDto? dto)
    {
        var given = new Dictionary<string, CascadeBehavior>(StringComparer.Ordinal);
        if (dto == null)
            return given;

        foreach (var (name, value) in new (string, CascadeBehavior?)[]
                 {
                     ("Assign", dto.Assign), ("Delete", dto.Delete), ("Merge", dto.Merge),
                     ("Reparent", dto.Reparent), ("Share", dto.Share), ("Unshare", dto.Unshare)
                 })
        {
            if (value is not { } behavior)
                continue;
            if (!CascadeActionRules.IsValid(name, behavior))
                throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                    $"Cascade {name} cannot be {(Enum.IsDefined(behavior) ? behavior.ToString() : "that value")}. Give {CascadeActionRules.Describe(name)}. Nothing was changed.",
                    $"CascadeConfiguration.{name}");
            given[name] = behavior;
        }

        return given;
    }

    /// <summary>
    /// Maps a cascade value to the SDK's. A value that is not a defined member is refused, never written as a
    /// default; the values given are checked before this, so this is the last guard.
    /// </summary>
    private static SdkCascadeType ToSdkCascadeType(CascadeBehavior behavior) => behavior switch
    {
        CascadeBehavior.Cascade => SdkCascadeType.Cascade,
        CascadeBehavior.Active => SdkCascadeType.Active,
        CascadeBehavior.NoCascade => SdkCascadeType.NoCascade,
        CascadeBehavior.UserOwned => SdkCascadeType.UserOwned,
        CascadeBehavior.RemoveLink => SdkCascadeType.RemoveLink,
        CascadeBehavior.Restrict => SdkCascadeType.Restrict,
        _ => throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
            "A cascade value is not one Dataverse accepts. Nothing was changed.", "CascadeConfiguration")
    };

    private static CascadeBehavior? ToCascadeBehavior(SdkCascadeType value) => value switch
    {
        SdkCascadeType.Cascade => CascadeBehavior.Cascade,
        SdkCascadeType.Active => CascadeBehavior.Active,
        SdkCascadeType.NoCascade => CascadeBehavior.NoCascade,
        SdkCascadeType.UserOwned => CascadeBehavior.UserOwned,
        SdkCascadeType.RemoveLink => CascadeBehavior.RemoveLink,
        SdkCascadeType.Restrict => CascadeBehavior.Restrict,
        _ => null
    };

    /// <inheritdoc />
    public async Task DeleteRelationshipAsync(
        PPDS.Dataverse.Metadata.Authoring.DeleteRelationshipRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.relationship.delete");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.SchemaName, "SchemaName");

        await EnsureDeleteAllowedAsync(new MetadataDeleteTarget(MetadataDeleteKind.Relationship, request.SolutionUniqueName, Name: request.SchemaName), ct).ConfigureAwait(false);

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
        _guard.EnsureCanMutate("metadata.globalChoice.create");

        reporter?.ReportPhase("Resolving publisher prefix");
        var prefix = await ResolvePublisherPrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Validating");
        _validator.ValidateCreateGlobalChoiceRequest(request, prefix);
        var (options, warnings) = await PrepareCreateOptionValuesAsync(request.Options ?? [], request.SolutionUniqueName, reporter, ct).ConfigureAwait(false);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: CreateGlobalChoice {SchemaName} validated", request.SchemaName);
            return new CreateChoiceResult
            {
                Name = request.SchemaName,
                WasDryRun = true,
                ValidationMessages = warnings
            };
        }

        reporter?.ReportPhase("Creating global choice", request.SchemaName);

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var optionSet = new OptionSetMetadata
        {
            Name = request.SchemaName,
            DisplayName = new Label(request.DisplayName, lcid),
            Description = string.IsNullOrEmpty(request.Description) ? null : new Label(request.Description, lcid),
            IsGlobal = true,
            OptionSetType = OptionSetType.Picklist // SDK uses Picklist for all choice types; multi-select is determined by the attribute type (MultiSelectPicklistAttributeMetadata), not the option set
        };

        if (request.Options != null)
        {
            foreach (var opt in options)
            {
                optionSet.Options.Add(new OptionMetadata(new Label(opt.Label, lcid), opt.Value));
            }
        }

        var sdkRequest = new CreateOptionSetRequest
        {
            OptionSet = optionSet,
            SolutionUniqueName = request.SolutionUniqueName
        };

        CreateOptionSetResponse response;
        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            response = (CreateOptionSetResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateGlobalOptionSets();

        reporter?.ReportPhase("Confirming global choice labels", request.SchemaName);
        var expectations = new List<LabelExpectation<OptionSetMetadataBase>>
        {
            new("display name", request.DisplayName, c => c.DisplayName),
            new("description", string.IsNullOrEmpty(request.Description) ? null : request.Description, c => c.Description)
        };
        if (request.Options != null)
            foreach (var option in options)
                expectations.Add(new($"option {option.Value} label", option.Label,
                    c => (c as OptionSetMetadata)?.Options.FirstOrDefault(o => o.Value == option.Value)?.Label));
        // Read back by the id the create returned, not by the caller's spelling: the confirmation must not depend on how
        // Dataverse stores the case of the name (a name lookup is case-sensitive, AC-133).
        await ConfirmLabelsAsync("global choice", $"Global choice '{request.SchemaName}' was created.", publishRequested: false, lcid,
            token => response.OptionSetId != Guid.Empty
                ? ReadForConfirmationAsync(new RetrieveOptionSetRequest { MetadataId = response.OptionSetId, RetrieveAsIfPublished = true },
                    r => (r as RetrieveOptionSetResponse)?.OptionSetMetadata, $"global choice '{request.SchemaName}' (id {response.OptionSetId})", token)
                : ReadChoiceForConfirmationAsync(request.SchemaName, token),
            ct, expectations.ToArray()).ConfigureAwait(false);

        reporter?.ReportInfo($"Global choice '{request.SchemaName}' created.");
        _logger?.LogInformation("Created global choice {SchemaName} with MetadataId {MetadataId}",
            request.SchemaName, response.OptionSetId);

        return new CreateChoiceResult
        {
            Name = request.SchemaName,
            MetadataId = response.OptionSetId,
            WasDryRun = false,
            ValidationMessages = warnings
        };
    }

    /// <inheritdoc />
    public async Task<MetadataLabelWriteResult> UpdateGlobalChoiceAsync(
        UpdateGlobalChoiceRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.globalChoice.update");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.Name, "Name");

        // A dry-run is input-only (MetadataDryRunScope.Inputs, globalChoiceNameChecked=false, AC-143): it does not read
        // the choice, so it neither resolves the name nor computes notices.
        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateGlobalChoice {Name} validated", request.Name);
            return MetadataLabelWriteResult.None;
        }

        reporter?.ReportPhase("Retrieving existing choice");

        // #1535: the exact spelling is retrieved first; a case variant resolves to the stored name.
        // The retrieve lease is returned before the update acquires its own. The choice is read as-if-published:
        // UpdateOptionSet re-sends every property, so a published read would revert unpublished edits to the
        // properties this request does not change.
        var (_, retrieveResponse) = await RetrieveGlobalChoiceAsync(request.Name, "Name", retrieveAsIfPublished: true, ct).ConfigureAwait(false);

        reporter?.ReportPhase("Updating global choice", request.Name);

        var existingOs = retrieveResponse.OptionSetMetadata;
        // The base language is read only when a label is about to be written (the unrequested labels are re-sent
        // exactly as read, in every language they have).
        var lcid = request.DisplayName != null || request.Description != null ? await BaseLanguageAsync(ct).ConfigureAwait(false) : 0;
        var notices = BaseLanguageNotices(lcid,
            ("DisplayName", "display name", request.DisplayName, existingOs.DisplayName),
            ("Description", "description", request.Description, existingOs.Description));

        if (request.DisplayName != null)
            existingOs.DisplayName = new Label(request.DisplayName, lcid);
        if (request.Description != null)
            existingOs.Description = new Label(request.Description, lcid);

        // A requested label is written in the base language only; MergeLabels keeps the other languages' text.
        var sdkRequest = new UpdateOptionSetRequest
        {
            OptionSet = existingOs,
            MergeLabels = true,
            SolutionUniqueName = request.SolutionUniqueName
        };

        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateGlobalOptionSets();

        var storedName = existingOs.Name ?? request.Name;
        reporter?.ReportPhase("Confirming global choice labels", storedName);
        await ConfirmLabelsAsync("global choice", $"Global choice '{storedName}' was updated.", publishRequested: false, lcid,
            token => ReadChoiceForConfirmationAsync(storedName, token), ct,
            new LabelExpectation<OptionSetMetadataBase>("display name", request.DisplayName, c => c.DisplayName),
            new LabelExpectation<OptionSetMetadataBase>("description", request.Description, c => c.Description)).ConfigureAwait(false);

        reporter?.ReportInfo($"Global choice '{request.Name}' updated. Changes must be published to take effect.");
        _logger?.LogInformation("Updated global choice {Name}", request.Name);
        return notices;
    }

    /// <inheritdoc />
    public async Task DeleteGlobalChoiceAsync(
        DeleteGlobalChoiceRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.globalChoice.delete");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.Name, "Name");

        // Delete is exact-name only (#1535): a caller that needs case resolution previews first
        // (PreviewDeleteAsync reports TargetName) and deletes that stored name after confirmation.
        await EnsureDeleteAllowedAsync(new MetadataDeleteTarget(MetadataDeleteKind.GlobalChoice, request.SolutionUniqueName, Name: request.Name), ct,
            request.ExpectedMetadataId).ConfigureAwait(false);

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
        _guard.EnsureCanMutate("metadata.option.add");

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");
        _validator.ValidateRequiredString(request.Label, "Label");

        if (request.DryRun)
        {
            // The SDK assigns the value at insert time, so a dry-run can only echo the
            // requested value (0 = would be auto-assigned).
            _logger?.LogInformation("Dry-run: AddOptionValue '{Label}' to {OptionSet} validated", request.Label, request.OptionSetName);
            return request.Value ?? 0;
        }

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        async Task<InsertOptionValueResponse> InsertAsync(string optionSetName)
        {
            var sdkRequest = new InsertOptionValueRequest
            {
                OptionSetName = optionSetName,
                Label = new Label(request.Label, lcid),
                Value = request.Value,
                SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
            };

            if (!string.IsNullOrEmpty(request.Description))
                sdkRequest.Description = new Label(request.Description, lcid);
            if (!string.IsNullOrEmpty(request.EntityLogicalName))
                sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
            if (!string.IsNullOrEmpty(request.AttributeLogicalName))
                sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;
            WithColor(sdkRequest, request.Color);

            await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
            return (InsertOptionValueResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        // #1535: a global choice is inserted under the caller's spelling first; only a server rejection
        // resolves the stored name and retries. Local (entity + attribute) targets are sent unchanged.
        var optionSetName = request.OptionSetName;
        InsertOptionValueResponse response;
        if (IsLocalOptionTarget(request.EntityLogicalName, request.AttributeLogicalName))
            response = await InsertAsync(optionSetName).ConfigureAwait(false);
        else
            (optionSetName, response) = await WithGlobalChoiceNameAsync(optionSetName, "OptionSetName", InsertAsync, null, ct).ConfigureAwait(false);

        // The insert is committed: invalidate first, then confirm the label, description and colour that rode on it.
        InvalidateOptionCache(request.EntityLogicalName, request.AttributeLogicalName);
        await ConfirmOptionWriteAsync(optionSetName, request.EntityLogicalName, request.AttributeLogicalName, response.NewOptionValue, lcid,
            request.Label, string.IsNullOrEmpty(request.Description) ? null : request.Description, request.Color,
            $"Option '{request.Label}' was added with value {response.NewOptionValue}.", publishRequested: false, ct).ConfigureAwait(false);

        _logger?.LogInformation("Added option value {Value} to {OptionSet}", response.NewOptionValue, request.OptionSetName);

        return response.NewOptionValue;
    }

    /// <inheritdoc />
    public async Task<MetadataLabelWriteResult> UpdateOptionValueAsync(PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.option.update");

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");

        if (!request.Value.HasValue && string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --label to identify the target option.", "Value");

        if (request.Value.HasValue && !string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "Value and Label are mutually exclusive; provide exactly one to identify the target option.", "Value");

        EnsureSomethingToUpdate(request.NewLabel, request.Description, request.Color, "a new label, a description or a color");

        // #1170: resolve the target by value or label; the current label is preserved when
        // NewLabel is omitted (e.g. color-only updates), mirroring the local column-option variant.
        var target = await ResolveGlobalOptionAsync(request.OptionSetName, request.Value, request.Label, ct).ConfigureAwait(false);
        var lcid = request.NewLabel != null || request.Description != null ? await BaseLanguageAsync(ct).ConfigureAwait(false) : 0;
        var notices = BaseLanguageNotices(lcid,
            ("NewLabel", "label", request.NewLabel, target.Labels),
            ("Description", "description", request.Description, target.Descriptions));

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateOptionValue {Value} in {OptionSet} validated", target.Value, request.OptionSetName);
            return notices;
        }

        var sdkRequest = new SdkUpdateOptionValueRequest
        {
            OptionSetName = target.OptionSetName,
            Value = target.Value,
            MergeLabels = true,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };
        if (BaseLanguageLabel(request.NewLabel, lcid) is { } globalLabel)
            sdkRequest.Label = globalLabel;

        // An empty description is sent as an empty base-language description (clears it, as optionset update does).
        if (BaseLanguageLabel(request.Description, lcid) is { } globalDescription)
            sdkRequest.Description = globalDescription;
        if (!string.IsNullOrEmpty(request.EntityLogicalName))
            sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        if (!string.IsNullOrEmpty(request.AttributeLogicalName))
            sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;
        WithColor(sdkRequest, request.Color);

        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        // The update is committed: invalidate first, then confirm the label, description and colour that rode on it.
        InvalidateOptionCache(request.EntityLogicalName, request.AttributeLogicalName);
        await ConfirmOptionWriteAsync(target.OptionSetName, request.EntityLogicalName, request.AttributeLogicalName, target.Value, lcid,
            request.NewLabel, request.Description, request.Color,
            $"Option {target.Value} was updated ({Sent(request.NewLabel, request.Description, request.Color)}).", publishRequested: false, ct).ConfigureAwait(false);

        _logger?.LogInformation("Updated option value {Value} in {OptionSet}", target.Value, request.OptionSetName);
        return notices;
    }

    /// <inheritdoc />
    public async Task DeleteOptionValueAsync(PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.option.delete");

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");

        if (!request.Value.HasValue && string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --label to identify the target option.", "Value");

        if (request.Value.HasValue && !string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "Value and Label are mutually exclusive; provide exactly one to identify the target option.", "Value");

        // #1169: resolve the target by value or label, mirroring the local (column) option variant.
        var target = await ResolveGlobalOptionAsync(request.OptionSetName, request.Value, request.Label, ct).ConfigureAwait(false);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: DeleteOptionValue {Value} from {OptionSet} validated", target.Value, request.OptionSetName);
            return;
        }

        var sdkRequest = new SdkDeleteOptionValueRequest
        {
            OptionSetName = target.OptionSetName,
            Value = target.Value,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };

        if (!string.IsNullOrEmpty(request.EntityLogicalName))
            sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        if (!string.IsNullOrEmpty(request.AttributeLogicalName))
            sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);

        InvalidateOptionCache(request.EntityLogicalName, request.AttributeLogicalName);
        _logger?.LogInformation("Deleted option value {Value} from {OptionSet}", target.Value, request.OptionSetName);
    }

    /// <summary>
    /// Retrieves a global option set (as-if-published, so unpublished edits are visible) and
    /// resolves the target option by value or label (#1169). Mirrors <see cref="ResolveColumnOption"/>.
    /// </summary>
    private async Task<(int Value, string Label, string OptionSetName, Label? Labels, Label? Descriptions)> ResolveGlobalOptionAsync(
        string optionSetName, int? value, string? label, CancellationToken ct)
    {
        RetrieveOptionSetResponse retrieveResponse;
        var requestedName = optionSetName;
        try
        {
            // #1535: the exact spelling is retrieved first; a case variant resolves to the stored name.
            (optionSetName, retrieveResponse) = await RetrieveGlobalChoiceAsync(
                requestedName, "OptionSetName", retrieveAsIfPublished: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // R2: cancellation must propagate
        }
        catch (Exception ex) when (ex is not MetadataValidationException and not PpdsNotFoundException)
        {
            throw new PpdsException(MetadataErrorCodes.EntityNotFound,
                $"Could not retrieve option set '{optionSetName}'.", ex);
        }

        if (retrieveResponse.OptionSetMetadata is not OptionSetMetadata optionSet || optionSet.Options == null)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"'{optionSetName}' is not an option set with option values.", "OptionSetName");

        // Value targeting is unambiguous — option values are unique within a set — so the first match is the only match.
        if (value.HasValue)
        {
            foreach (var option in optionSet.Options)
            {
                // Compare the nullable value directly — a null option value must not collapse to 0 and
                // false-match a requested value of 0.
                if (option.Value == value.Value)
                {
                    var matchedLabel = option.Label?.UserLocalizedLabel?.Label
                        ?? option.Label?.LocalizedLabels?.FirstOrDefault()?.Label
                        ?? "";
                    return (value.Value, matchedLabel, optionSetName, option.Label, option.Description);
                }
            }

            throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                $"Option with value {value.Value} not found in option set '{optionSetName}'.",
                "Value");
        }

        // Label targeting: duplicate labels are legal in Dataverse. Collect every match and refuse to act when
        // more than one matches (#1235) — silently picking the first could mutate or delete the wrong option.
        var labelMatches = new List<(int Value, string Label, Label? Labels, Label? Descriptions)>();
        foreach (var option in optionSet.Options)
        {
            // An option without a concrete value can't be a selectable --value target; skip it so a null
            // value never collapses to 0 and collides with a real value-0 option in the match set.
            if (option.Value is not int optionValue)
                continue;
            var optionLabel = option.Label?.UserLocalizedLabel?.Label
                ?? option.Label?.LocalizedLabels?.FirstOrDefault()?.Label
                ?? "";
            if (string.Equals(optionLabel, label, StringComparison.OrdinalIgnoreCase))
                labelMatches.Add((optionValue, optionLabel, option.Label, option.Description));
        }

        // #1531 follow-up: 'optionset <name>' lists published labels, so a draft-resolved label must not act on
        // an option other than the one the listing shows under that label.
        if (labelMatches.Count <= 1)
            await EnsureGlobalOptionLabelAgreesWithPublishedAsync(optionSetName, label!,
                labelMatches.Select(m => m.Value), ct).ConfigureAwait(false);

        if (labelMatches.Count == 0)
            throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                $"Option with label '{label}' not found in option set '{optionSetName}'.",
                "Label");

        if (labelMatches.Count > 1)
            throw new PpdsException(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel,
                $"Label '{label}' matches {labelMatches.Count} options in option set '{optionSetName}' " +
                $"(values: {string.Join(", ", labelMatches.Select(m => m.Value))}). " +
                "Select the target option with --value instead.");

        return (labelMatches[0].Value, labelMatches[0].Label, optionSetName, labelMatches[0].Labels, labelMatches[0].Descriptions);
    }

    /// <inheritdoc />
    public async Task ReorderOptionsAsync(PPDS.Dataverse.Metadata.Authoring.ReorderOptionsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.option.reorder");

        _validator.ValidateRequiredString(request.OptionSetName, "OptionSetName");

        async Task<OrganizationResponse> OrderAsync(string optionSetName)
        {
            var sdkRequest = new OrderOptionRequest
            {
                OptionSetName = optionSetName,
                Values = request.Order,
                SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
            };

            if (!string.IsNullOrEmpty(request.EntityLogicalName))
                sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
            if (!string.IsNullOrEmpty(request.AttributeLogicalName))
                sdkRequest["AttributeLogicalName"] = request.AttributeLogicalName;

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            return await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        // #1535: a global choice is ordered under the caller's spelling first; only a server rejection
        // resolves the stored name and retries. Local (entity + attribute) targets are sent unchanged.
        if (IsLocalOptionTarget(request.EntityLogicalName, request.AttributeLogicalName))
            await OrderAsync(request.OptionSetName).ConfigureAwait(false);
        else
            await WithGlobalChoiceNameAsync(request.OptionSetName, "OptionSetName", OrderAsync, null, ct).ConfigureAwait(false);

        InvalidateOptionCache(request.EntityLogicalName, request.AttributeLogicalName);
        _logger?.LogInformation("Reordered options in {OptionSet}", request.OptionSetName);
    }

    /// <inheritdoc />
    public async Task UpdateStateValueAsync(PPDS.Dataverse.Metadata.Authoring.UpdateStateValueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.stateValue.update");

        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.AttributeLogicalName, "AttributeLogicalName");
        _validator.ValidateRequiredString(request.Label, "Label");

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var sdkRequest = new SdkUpdateStateValueRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            AttributeLogicalName = request.AttributeLogicalName,
            Value = request.Value,
            Label = new Label(request.Label, lcid),
            MergeLabels = true // The request carries only the base-language label. Preserve other translations.
        };

        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        await ConfirmOptionWriteAsync(null, request.EntityLogicalName, request.AttributeLogicalName, request.Value, lcid,
            request.Label, description: null, color: null,
            $"State value {request.Value} on '{request.EntityLogicalName}.{request.AttributeLogicalName}' was updated (label).", publishRequested: false, ct).ConfigureAwait(false);

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
        _guard.EnsureCanMutate("metadata.key.create");

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

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var entityKey = new EntityKeyMetadata
        {
            SchemaName = request.SchemaName,
            DisplayName = new Label(request.DisplayName, lcid),
            KeyAttributes = request.KeyAttributes
        };

        var sdkRequest = new CreateEntityKeyRequest
        {
            EntityName = request.EntityLogicalName,
            EntityKey = entityKey,
            SolutionUniqueName = request.SolutionUniqueName
        };

        CreateEntityKeyResponse response;
        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            response = (CreateEntityKeyResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        var keyName = request.SchemaName.ToLowerInvariant();
        await ConfirmLabelsAsync("alternate key", $"Alternate key '{request.SchemaName}' was created on '{request.EntityLogicalName}'.", publishRequested: false, lcid,
            token => ReadForConfirmationAsync(new RetrieveEntityKeyRequest { EntityLogicalName = request.EntityLogicalName, LogicalName = keyName, RetrieveAsIfPublished = true },
                r => (r as RetrieveEntityKeyResponse)?.EntityKeyMetadata, $"alternate key '{keyName}'", token), ct,
            new LabelExpectation<EntityKeyMetadata>("display name", request.DisplayName, k => k.DisplayName)).ConfigureAwait(false);

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
        PPDS.Dataverse.Metadata.Authoring.DeleteKeyRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.key.delete");

        reporter?.ReportPhase("Validating");
        _validator.ValidateRequiredString(request.SolutionUniqueName, "SolutionUniqueName");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.KeyLogicalName, "KeyLogicalName");

        await EnsureDeleteAllowedAsync(new MetadataDeleteTarget(MetadataDeleteKind.Key, request.SolutionUniqueName, request.EntityLogicalName, request.KeyLogicalName), ct).ConfigureAwait(false);

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
    public async Task ReactivateKeyAsync(PPDS.Dataverse.Metadata.Authoring.ReactivateKeyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.key.reactivate");

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

    #region Status Reasons

    /// <inheritdoc />
    public async Task<int> AddStatusReasonAsync(
        AddStatusReasonRequest request,
        IMetadataAuthoringProgressReporter? reporter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.statusReason.add");

        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.Label, "Label");

        if (!request.Value.HasValue && string.IsNullOrWhiteSpace(request.SolutionUniqueName))
            throw new MetadataValidationException(
                MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --solution to determine the option value.",
                "Value");

        var existingReasons = await ReadStatusReasonsAsync(request.EntityLogicalName, retrieveAsIfPublished: true, ct).ConfigureAwait(false);
        var existingValues = existingReasons.Select(r => r.Value).ToList();

        int? optionPrefix = null;
        if (!string.IsNullOrWhiteSpace(request.SolutionUniqueName))
            optionPrefix = await ResolvePublisherOptionValuePrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        var value = OptionValueDeriver.Derive(request.Value, optionPrefix, existingValues);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: AddStatusReason '{Label}' on '{Entity}' validated (derived value {Value})", request.Label, request.EntityLogicalName, value);
            return value;
        }

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var sdkRequest = new InsertStatusValueRequest
        {
            EntityLogicalName = request.EntityLogicalName,
            AttributeLogicalName = "statuscode",
            StateCode = request.StateCode,
            Value = value,
            Label = new Label(request.Label, lcid),
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };
        WithColor(sdkRequest, request.Color);

        InsertStatusValueResponse response;
        try
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            response = (InsertStatusValueResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
        {
            throw new PpdsException(
                MetadataErrorCodes.SdkOperationFailed,
                IsIndeterminate(ex)
                    ? $"Adding status reason '{request.Label}' to '{request.EntityLogicalName}' {Interruption(ex)}, so whether it was added is unknown. " +
                      $"List the status reasons (ppds metadata entity list-statusreasons {request.EntityLogicalName}) before retrying" +
                      (request.Value.HasValue ? "." : "; a retry could add a second one with a new value.")
                    : AppendDataverseFaultCode($"Failed to add status reason '{request.Label}' to '{request.EntityLogicalName}'.", ex),
                ex);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        await ConfirmOptionWriteAsync(null, request.EntityLogicalName, "statuscode", response.NewOptionValue, lcid, request.Label, description: null, request.Color,
            $"Status reason '{request.Label}' was added with value {response.NewOptionValue}.", request.Publish, ct).ConfigureAwait(false);

        _logger?.LogInformation("Added status reason '{Label}' with value {Value} to '{Entity}'", request.Label, response.NewOptionValue, request.EntityLogicalName);

        if (request.Publish)
            await PublishAfterWriteAsync(request.EntityLogicalName, $"Status reason '{request.Label}' was added with value {response.NewOptionValue}.", ct).ConfigureAwait(false);

        return response.NewOptionValue;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StatusReasonInfo>> ListStatusReasonsAsync(string entityLogicalName, CancellationToken ct = default)
        => ReadStatusReasonsAsync(entityLogicalName, retrieveAsIfPublished: false, ct);

    // Allocation and write-target resolution need draft values (#1531); the public list remains published.
    private async Task<IReadOnlyList<StatusReasonInfo>> ReadStatusReasonsAsync(
        string entityLogicalName, bool retrieveAsIfPublished, CancellationToken ct)
        => (await ReadStatusReasonEntriesAsync(entityLogicalName, retrieveAsIfPublished, ct).ConfigureAwait(false))
            .Select(entry => entry.Info).ToList();

    // Each reason with its full label, for target resolution and the base-language label notice.
    private async Task<IReadOnlyList<(StatusReasonInfo Info, Label? Labels)>> ReadStatusReasonEntriesAsync(
        string entityLogicalName, bool retrieveAsIfPublished, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField, "EntityLogicalName is required.", "EntityLogicalName");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);

        var retrieveRequest = new RetrieveAttributeRequest
        {
            EntityLogicalName = entityLogicalName,
            LogicalName = "statuscode",
            RetrieveAsIfPublished = retrieveAsIfPublished
        };

        OrganizationResponse retrieveResponse;
        try
        {
            retrieveResponse = await client.ExecuteAsync(retrieveRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // R2: cancellation must propagate, not be masked as a not-found error
        }
        catch (Exception ex)
        {
            throw new PpdsException(MetadataErrorCodes.EntityNotFound, $"Could not retrieve statuscode attribute for '{entityLogicalName}'.", ex);
        }

        if (retrieveResponse is not RetrieveAttributeResponse retrieveAttrResponse
            || retrieveAttrResponse.AttributeMetadata is not StatusAttributeMetadata statusAttr
            || statusAttr.OptionSet?.Options == null)
            return Array.Empty<(StatusReasonInfo, Label?)>();

        var result = new List<(StatusReasonInfo Info, Label? Labels)>();
        foreach (var opt in statusAttr.OptionSet.Options)
        {
            if (opt is not StatusOptionMetadata statusOpt)
                continue;

            result.Add((new StatusReasonInfo
            {
                Value = statusOpt.Value ?? 0,
                Label = statusOpt.Label?.UserLocalizedLabel?.Label
                    ?? statusOpt.Label?.LocalizedLabels?.FirstOrDefault()?.Label
                    ?? "",
                StateCode = statusOpt.State ?? 0,
                // Standard Dataverse entities use 0=Active, 1=Inactive; custom multi-state entities (state code >1) are out of scope for #1160
                StateLabel = (statusOpt.State ?? 0) switch { 0 => "Active", 1 => "Inactive", var n => $"State{n}" },
                Color = statusOpt.Color
            }, statusOpt.Label));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<MetadataLabelWriteResult> UpdateStatusReasonAsync(UpdateStatusReasonRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.statusReason.update");

        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");

        if (!request.Value.HasValue && string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(
                MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --label to identify the target status reason.",
                "Value");

        EnsureSomethingToUpdate(request.NewLabel, null, request.Color, "--new-label or --color");

        int targetValue;
        Label? currentLabels;

        // Read the reasons to resolve the target and the label shown to the caller (for the base-language label notice).
        // #1531: resolve the target from draft (as-if-published) metadata so a reason added moments earlier,
        // and its unpublished label, are visible. The public list stays published.
        var statusEntries = await ReadStatusReasonEntriesAsync(request.EntityLogicalName, retrieveAsIfPublished: true, ct).ConfigureAwait(false);
        var statusReasons = statusEntries.Select(entry => entry.Info).ToList();

        if (request.Value.HasValue)
        {
            targetValue = request.Value.Value;
            var valueMatch = statusReasons.FirstOrDefault(r => r.Value == targetValue);
            if (valueMatch == null)
                throw new MetadataValidationException(
                    MetadataErrorCodes.OptionNotFound,
                    $"Status reason with value {targetValue} not found on '{request.EntityLogicalName}'.",
                    "Value");
            currentLabels = statusEntries.First(entry => ReferenceEquals(entry.Info, valueMatch)).Labels;
        }
        else
        {
            // Duplicate status-reason labels are legal in Dataverse. Collect every match and refuse to act
            // when more than one matches (#1235) — silently picking the first could update the wrong reason.
            var labelMatches = statusReasons
                .Where(r => string.Equals(r.Label, request.Label, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (labelMatches.Count <= 1)
                await EnsureStatusReasonLabelAgreesWithPublishedAsync(request.EntityLogicalName, request.Label!,
                    labelMatches.Select(r => r.Value), ct).ConfigureAwait(false);

            if (labelMatches.Count == 0)
                throw new MetadataValidationException(
                    MetadataErrorCodes.OptionNotFound,
                    $"Status reason with label '{request.Label}' not found on '{request.EntityLogicalName}'.",
                    "Label");

            if (labelMatches.Count > 1)
                throw new PpdsException(
                    ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel,
                    $"Label '{request.Label}' matches {labelMatches.Count} status reasons on '{request.EntityLogicalName}' " +
                    $"(values: {string.Join(", ", labelMatches.Select(r => r.Value))}). " +
                    "Select the target status reason with --value instead.");

            targetValue = labelMatches[0].Value;
            currentLabels = statusEntries.First(entry => ReferenceEquals(entry.Info, labelMatches[0])).Labels;
        }

        var lcid = request.NewLabel != null ? await BaseLanguageAsync(ct).ConfigureAwait(false) : 0;
        var notices = BaseLanguageNotices(lcid, ("NewLabel", "label", request.NewLabel, currentLabels));
        var sdkRequest = new SdkUpdateOptionValueRequest
        {
            Value = targetValue,
            MergeLabels = true,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };
        if (BaseLanguageLabel(request.NewLabel, lcid) is { } statusLabel)
            sdkRequest.Label = statusLabel;

        sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        sdkRequest["AttributeLogicalName"] = "statuscode";
        WithColor(sdkRequest, request.Color);

        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                throw new PpdsException(
                    MetadataErrorCodes.SdkOperationFailed,
                    AppendDataverseFaultCode($"Failed to update status reason (value {targetValue}) on '{request.EntityLogicalName}'.", ex),
                    ex);
            }
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        await ConfirmOptionWriteAsync(null, request.EntityLogicalName, "statuscode", targetValue, lcid, request.NewLabel, description: null, request.Color,
            $"Status reason {targetValue} was updated ({Sent(request.NewLabel, null, request.Color)}).", request.Publish, ct).ConfigureAwait(false);

        _logger?.LogInformation("Updated status reason (value {Value}) on '{Entity}'", targetValue, request.EntityLogicalName);

        if (request.Publish)
            await PublishAfterWriteAsync(request.EntityLogicalName, $"Status reason {targetValue} was updated ({Sent(request.NewLabel, null, request.Color)}).", ct).ConfigureAwait(false);
        return notices;
    }

    /// <inheritdoc />
    public async Task RemoveStatusReasonAsync(RemoveStatusReasonRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.statusReason.remove");

        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");

        if (!request.Value.HasValue && string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(
                MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --label to identify the target status reason.",
                "Value");

        // #1531: resolve the target from draft (as-if-published) metadata so an unpublished reason can be removed.
        var removeReasons = await ReadStatusReasonsAsync(request.EntityLogicalName, retrieveAsIfPublished: true, ct).ConfigureAwait(false);

        int targetValue;

        if (request.Value.HasValue)
        {
            targetValue = request.Value.Value;
            if (!removeReasons.Any(r => r.Value == targetValue))
                throw new MetadataValidationException(
                    MetadataErrorCodes.OptionNotFound,
                    $"Status reason with value {targetValue} not found on '{request.EntityLogicalName}'.",
                    "Value");
        }
        else
        {
            // Duplicate status-reason labels are legal in Dataverse. Collect every match and refuse to act
            // when more than one matches (#1235) — silently picking the first could delete the wrong reason.
            var labelMatches = removeReasons
                .Where(r => string.Equals(r.Label, request.Label, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (labelMatches.Count <= 1)
                await EnsureStatusReasonLabelAgreesWithPublishedAsync(request.EntityLogicalName, request.Label!,
                    labelMatches.Select(r => r.Value), ct).ConfigureAwait(false);

            if (labelMatches.Count == 0)
                throw new MetadataValidationException(
                    MetadataErrorCodes.OptionNotFound,
                    $"Status reason with label '{request.Label}' not found on '{request.EntityLogicalName}'.",
                    "Label");

            if (labelMatches.Count > 1)
                throw new PpdsException(
                    ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel,
                    $"Label '{request.Label}' matches {labelMatches.Count} status reasons on '{request.EntityLogicalName}' " +
                    $"(values: {string.Join(", ", labelMatches.Select(r => r.Value))}). " +
                    "Select the target status reason with --value instead.");

            targetValue = labelMatches[0].Value;
        }

        var sdkRequest = new SdkDeleteOptionValueRequest
        {
            Value = targetValue,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };

        sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        sdkRequest["AttributeLogicalName"] = "statuscode";

        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                throw new PpdsException(
                    MetadataErrorCodes.SdkOperationFailed,
                    AppendDataverseFaultCode($"Failed to remove status reason (value {targetValue}) from '{request.EntityLogicalName}'.", ex),
                    ex);
            }
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        _logger?.LogInformation("Removed status reason (value {Value}) from '{Entity}'", targetValue, request.EntityLogicalName);

        if (request.Publish)
            await PublishEntityInternalAsync(request.EntityLogicalName, ct).ConfigureAwait(false);
    }

    #endregion

    #region Local Column Options (#1161)

    /// <inheritdoc />
    public Task<IReadOnlyList<ColumnOptionInfo>> ListColumnOptionsAsync(string entityLogicalName, string columnLogicalName, CancellationToken ct = default)
        => ReadColumnOptionsAsync(entityLogicalName, columnLogicalName, retrieveAsIfPublished: false, ct);

    // Draft-aware reads serve allocation and update/remove target resolution (#1531); the public list stays published.
    private async Task<IReadOnlyList<ColumnOptionInfo>> ReadColumnOptionsAsync(
        string entityLogicalName, string columnLogicalName, bool retrieveAsIfPublished, CancellationToken ct)
        => (await ReadColumnOptionEntriesAsync(entityLogicalName, columnLogicalName, retrieveAsIfPublished, ct).ConfigureAwait(false))
            .Select(entry => entry.Info).ToList();

    // Each option with its full label, for target resolution and the base-language label notice.
    private async Task<IReadOnlyList<(ColumnOptionInfo Info, Label? Labels)>> ReadColumnOptionEntriesAsync(
        string entityLogicalName, string columnLogicalName, bool retrieveAsIfPublished, CancellationToken ct)
    {
        _validator.ValidateRequiredString(entityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(columnLogicalName, "ColumnLogicalName");

        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);

        var retrieveRequest = new RetrieveAttributeRequest
        {
            EntityLogicalName = entityLogicalName,
            LogicalName = columnLogicalName,
            RetrieveAsIfPublished = retrieveAsIfPublished
        };

        OrganizationResponse retrieveResponse;
        try
        {
            retrieveResponse = await client.ExecuteAsync(retrieveRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // R2: cancellation must propagate
        }
        catch (Exception ex)
        {
            throw new PpdsException(MetadataErrorCodes.EntityNotFound,
                $"Could not retrieve attribute '{columnLogicalName}' on '{entityLogicalName}'.", ex);
        }

        var optionSet = (retrieveResponse as RetrieveAttributeResponse)?.AttributeMetadata switch
        {
            PicklistAttributeMetadata p => p.OptionSet,
            MultiSelectPicklistAttributeMetadata m => m.OptionSet,
            _ => null
        };

        if (optionSet?.Options == null)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"Attribute '{columnLogicalName}' on '{entityLogicalName}' is not a Choice/Choices column with a local option set.",
                "ColumnLogicalName");

        var result = new List<(ColumnOptionInfo Info, Label? Labels)>();
        foreach (var opt in optionSet.Options)
        {
            result.Add((new ColumnOptionInfo
            {
                Value = opt.Value ?? 0,
                Label = opt.Label?.UserLocalizedLabel?.Label
                    ?? opt.Label?.LocalizedLabels?.FirstOrDefault()?.Label
                    ?? "",
                Color = opt.Color
            }, opt.Label));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> AddColumnOptionAsync(AddColumnOptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.columnOption.add");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");
        _validator.ValidateRequiredString(request.Label, "Label");

        if (!request.Value.HasValue && string.IsNullOrWhiteSpace(request.SolutionUniqueName))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                "Provide an explicit value or --solution to derive the option value.", "Value");

        var existing = await ReadColumnOptionsAsync(request.EntityLogicalName, request.ColumnLogicalName, retrieveAsIfPublished: true, ct).ConfigureAwait(false);
        var existingValues = existing.Select(o => o.Value).ToList();

        int? optionPrefix = null;
        if (!string.IsNullOrWhiteSpace(request.SolutionUniqueName))
            optionPrefix = await ResolvePublisherOptionValuePrefixAsync(request.SolutionUniqueName, ct).ConfigureAwait(false);

        var value = OptionValueDeriver.Derive(request.Value, optionPrefix, existingValues);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: AddColumnOption '{Label}' to '{Entity}.{Column}' validated (derived value {Value})", request.Label, request.EntityLogicalName, request.ColumnLogicalName, value);
            return value;
        }

        var lcid = await BaseLanguageAsync(ct).ConfigureAwait(false);
        var sdkRequest = new InsertOptionValueRequest
        {
            Label = new Label(request.Label, lcid),
            Value = value,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };
        sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        sdkRequest["AttributeLogicalName"] = request.ColumnLogicalName;
        WithColor(sdkRequest, request.Color);

        InsertOptionValueResponse response;
        try
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            response = (InsertOptionValueResponse)await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
        {
            throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                IsIndeterminate(ex)
                    ? $"Adding option '{request.Label}' to '{request.EntityLogicalName}.{request.ColumnLogicalName}' {Interruption(ex)}, so whether it was added is unknown. " +
                      "Check the column's options in Dataverse before retrying" +
                      (request.Value.HasValue ? "." : "; a retry could add a second one with a new value.")
                    : AppendDataverseFaultCode($"Failed to add option '{request.Label}' to '{request.EntityLogicalName}.{request.ColumnLogicalName}'.", ex), ex);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        await ConfirmOptionWriteAsync(null, request.EntityLogicalName, request.ColumnLogicalName, response.NewOptionValue, lcid, request.Label, description: null, request.Color,
            $"Option '{request.Label}' was added with value {response.NewOptionValue} to '{request.EntityLogicalName}.{request.ColumnLogicalName}'.", request.Publish, ct).ConfigureAwait(false);
        if (request.Publish)
            await PublishAfterWriteAsync(request.EntityLogicalName,
                $"Option '{request.Label}' was added with value {response.NewOptionValue} to '{request.EntityLogicalName}.{request.ColumnLogicalName}'.", ct).ConfigureAwait(false);

        _logger?.LogInformation("Added local option {Value} to {Entity}.{Column}", response.NewOptionValue, request.EntityLogicalName, request.ColumnLogicalName);
        return response.NewOptionValue;
    }

    /// <inheritdoc />
    public async Task<MetadataLabelWriteResult> UpdateColumnOptionAsync(UpdateColumnOptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.columnOption.update");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");

        if (!request.Value.HasValue && string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --label to identify the target option.", "Value");

        EnsureSomethingToUpdate(request.NewLabel, null, request.Color, "--new-label or --color");

        // #1531: resolve the target (and its current label) from draft metadata so an unpublished option is visible.
        var (target, targetLabels) = await ResolveDraftColumnOptionAsync(request.EntityLogicalName, request.ColumnLogicalName, request.Value, request.Label, ct).ConfigureAwait(false);

        var lcid = request.NewLabel != null ? await BaseLanguageAsync(ct).ConfigureAwait(false) : 0;
        var notices = BaseLanguageNotices(lcid, ("NewLabel", "label", request.NewLabel, targetLabels));
        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: UpdateColumnOption (value {Value}) on '{Entity}.{Column}' validated", target.Value, request.EntityLogicalName, request.ColumnLogicalName);
            return notices;
        }

        var sdkRequest = new SdkUpdateOptionValueRequest
        {
            Value = target.Value,
            MergeLabels = true,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };
        if (BaseLanguageLabel(request.NewLabel, lcid) is { } columnLabel)
            sdkRequest.Label = columnLabel;
        sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        sdkRequest["AttributeLogicalName"] = request.ColumnLogicalName;
        WithColor(sdkRequest, request.Color);

        try
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                AppendDataverseFaultCode($"Failed to update option {target.Value} on '{request.EntityLogicalName}.{request.ColumnLogicalName}'.", ex), ex);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        await ConfirmOptionWriteAsync(null, request.EntityLogicalName, request.ColumnLogicalName, target.Value, lcid, request.NewLabel, description: null, request.Color,
            $"Option {target.Value} on '{request.EntityLogicalName}.{request.ColumnLogicalName}' was updated ({Sent(request.NewLabel, null, request.Color)}).", request.Publish, ct).ConfigureAwait(false);
        if (request.Publish)
            await PublishAfterWriteAsync(request.EntityLogicalName,
                $"Option {target.Value} on '{request.EntityLogicalName}.{request.ColumnLogicalName}' was updated ({Sent(request.NewLabel, null, request.Color)}).", ct).ConfigureAwait(false);

        _logger?.LogInformation("Updated local option {Value} on {Entity}.{Column}", target.Value, request.EntityLogicalName, request.ColumnLogicalName);
        return notices;
    }

    /// <inheritdoc />
    public async Task RemoveColumnOptionAsync(RemoveColumnOptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _guard.EnsureCanMutate("metadata.columnOption.remove");
        _validator.ValidateRequiredString(request.EntityLogicalName, "EntityLogicalName");
        _validator.ValidateRequiredString(request.ColumnLogicalName, "ColumnLogicalName");

        if (!request.Value.HasValue && string.IsNullOrEmpty(request.Label))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                "Provide --value or --label to identify the target option.", "Value");

        // #1531: resolve the target from draft metadata so an unpublished option can be removed.
        var (target, _) = await ResolveDraftColumnOptionAsync(request.EntityLogicalName, request.ColumnLogicalName, request.Value, request.Label, ct).ConfigureAwait(false);

        if (request.DryRun)
        {
            _logger?.LogInformation("Dry-run: RemoveColumnOption (value {Value}) from '{Entity}.{Column}' validated", target.Value, request.EntityLogicalName, request.ColumnLogicalName);
            return;
        }

        var sdkRequest = new SdkDeleteOptionValueRequest
        {
            Value = target.Value,
            SolutionUniqueName = OptionSolution(request.SolutionUniqueName)
        };
        sdkRequest["EntityLogicalName"] = request.EntityLogicalName;
        sdkRequest["AttributeLogicalName"] = request.ColumnLogicalName;

        try
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            await client.ExecuteAsync(sdkRequest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                AppendDataverseFaultCode($"Failed to remove option {target.Value} from '{request.EntityLogicalName}.{request.ColumnLogicalName}'.", ex), ex);
        }

        _cacheProvider?.InvalidateEntity(request.EntityLogicalName);
        if (request.Publish)
            await PublishEntityInternalAsync(request.EntityLogicalName, ct).ConfigureAwait(false);

        _logger?.LogInformation("Removed local option {Value} from {Entity}.{Column}", target.Value, request.EntityLogicalName, request.ColumnLogicalName);
    }

    private static ColumnOptionInfo ResolveColumnOption(IReadOnlyList<ColumnOptionInfo> options, int? value, string? label, string entity, string column)
    {
        // Value targeting is unambiguous — option values are unique within a column's option set.
        if (value.HasValue)
        {
            var valueMatch = options.FirstOrDefault(o => o.Value == value.Value);
            if (valueMatch == null)
                throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                    $"Option with value {value.Value} not found on '{entity}.{column}'.",
                    "Value");
            return valueMatch;
        }

        // Label targeting: duplicate labels are legal in Dataverse. Refuse to act on an ambiguous label (#1235) —
        // silently picking the first could mutate or delete the wrong option; the caller must select with --value.
        var labelMatches = options
            .Where(o => string.Equals(o.Label, label, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (labelMatches.Count == 0)
            throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                $"Option with label '{label}' not found on '{entity}.{column}'.",
                "Label");

        if (labelMatches.Count > 1)
            throw new PpdsException(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel,
                $"Label '{label}' matches {labelMatches.Count} options on '{entity}.{column}' " +
                $"(values: {string.Join(", ", labelMatches.Select(m => m.Value))}). " +
                "Select the target option with --value instead.");

        return labelMatches[0];
    }

    #endregion

    #region Private Helpers

    // This acquisition contract is intentionally limited to the corrected metadata workflows.
    // SDK operations and unrelated acquisitions retain their own existing exception contracts.
    private async Task<IPooledClient> GetMetadataClientAsync(CancellationToken ct)
    {
        try
        {
            return await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
        {
            var (error, exitCode) = ExceptionMapper.MapWithExitCode(ex);
            if (exitCode == ExitCodes.AuthError)
                throw new PpdsAuthException(error.Code, "Could not acquire a Dataverse connection.", ex)
                {
                    RequiresReauthentication = ex is DataverseAuthenticationException { RequiresReauthentication: true }
                };

            throw new MetadataConnectionAcquisitionException(error.Code,
                "Could not acquire a Dataverse connection.", exitCode, ex);
        }
    }

    private async Task<string> ResolvePublisherPrefixAsync(string solutionUniqueName, CancellationToken ct)
    {
        if (_publisherPrefixCache.TryGetValue(solutionUniqueName, out var cached))
        {
            return cached;
        }

        Guid publisherId;
        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
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

            var publisherRef = solutionResult.Entities[0].GetAttributeValue<EntityReference>("publisherid")
                ?? throw new MetadataValidationException(MetadataErrorCodes.EntityNotFound,
                    $"Solution '{solutionUniqueName}' has no publisher reference.", "SolutionUniqueName");
            publisherId = publisherRef.Id;
        }

        string prefix;
        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
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

            prefix = publisherResult.Entities[0].GetAttributeValue<string>("customizationprefix");
        }

        _publisherPrefixCache[solutionUniqueName] = prefix;

        return prefix;
    }

    private static AttributeMetadata BuildAttributeMetadata(CreateColumnRequest request, OptionDefinition[]? options, int lcid)
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
            SchemaColumnType.Boolean => BuildBooleanAttribute(request, lcid),
            SchemaColumnType.DateTime => BuildDateTimeAttribute(request),
            SchemaColumnType.Choice => BuildChoiceAttribute(request, options, lcid),
            SchemaColumnType.Choices => BuildMultiSelectChoiceAttribute(request, options, lcid),
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
        attr.DisplayName = new Label(request.DisplayName, lcid);
        attr.RequiredLevel = new AttributeRequiredLevelManagedProperty(requiredLevel);

        if (!string.IsNullOrEmpty(request.Description))
            attr.Description = new Label(request.Description, lcid);
        if (request.IsAuditEnabled.HasValue)
            attr.IsAuditEnabled = new BooleanManagedProperty(request.IsAuditEnabled.Value);
        if (request.IsSecured.HasValue)
            attr.IsSecured = request.IsSecured.Value;
        if (request.IsValidForAdvancedFind.HasValue)
            attr.IsValidForAdvancedFind = new BooleanManagedProperty(request.IsValidForAdvancedFind.Value);

        return attr;
    }

    /// <summary>
    /// Every label a column create sent, for the one read-back: the display name, the description when sent, each local
    /// option's label (Choice/Choices with inline options) and a Boolean column's true and false labels. A column that
    /// uses a global choice sends no option labels (the choice's own labels are not written by this command).
    /// </summary>
    private static IEnumerable<LabelExpectation<AttributeMetadata>> CreatedColumnExpectations(AttributeMetadata sent, CreateColumnRequest request)
    {
        yield return new("display name", request.DisplayName, a => a.DisplayName);
        yield return new("description", string.IsNullOrEmpty(request.Description) ? null : request.Description, a => a.Description);
        if (sent is EnumAttributeMetadata { OptionSet: { IsGlobal: false } local })
            foreach (var option in local.Options.Where(o => o.Value.HasValue))
            {
                var value = option.Value!.Value;
                yield return new($"option {value} label", BaseLabelText(option.Label),
                    a => (a as EnumAttributeMetadata)?.OptionSet?.Options?.FirstOrDefault(o => o.Value == value)?.Label);
            }
        if (sent is BooleanAttributeMetadata { OptionSet: { } flags })
        {
            yield return new("true label", BaseLabelText(flags.TrueOption?.Label), a => (a as BooleanAttributeMetadata)?.OptionSet?.TrueOption?.Label);
            yield return new("false label", BaseLabelText(flags.FalseOption?.Label), a => (a as BooleanAttributeMetadata)?.OptionSet?.FalseOption?.Label);
        }
    }

    /// <summary>The single text of a label this service built (one base-language entry), or null when there is none.</summary>
    private static string? BaseLabelText(Label? label) => label?.LocalizedLabels?.FirstOrDefault()?.Label;

    private static BooleanAttributeMetadata BuildBooleanAttribute(CreateColumnRequest request, int lcid)
    {
        var trueLabel = request.TrueLabel ?? "Yes";
        var falseLabel = request.FalseLabel ?? "No";

        var attr = new BooleanAttributeMetadata
        {
            OptionSet = new BooleanOptionSetMetadata(
                new OptionMetadata(new Label(trueLabel, lcid), 1),
                new OptionMetadata(new Label(falseLabel, lcid), 0))
        };

        if (request.DefaultValue.HasValue)
            attr.DefaultValue = request.DefaultValue.Value != 0;

        return attr;
    }

    private static PicklistAttributeMetadata BuildChoiceAttribute(CreateColumnRequest request, OptionDefinition[]? options, int lcid)
    {
        var attr = new PicklistAttributeMetadata();

        if (!string.IsNullOrEmpty(request.OptionSetName))
        {
            // Use existing global option set
            attr.OptionSet = new OptionSetMetadata { IsGlobal = true, Name = request.OptionSetName };
        }
        else if (options != null && options.Length > 0)
        {
            attr.OptionSet = BuildLocalOptionSet(options, lcid);
        }

        if (request.DefaultValue.HasValue)
            attr.DefaultFormValue = request.DefaultValue;

        return attr;
    }

    /// <summary>Allocates only omitted create values, preserving every caller-owned option.</summary>
    private async Task<(OptionDefinition[] Options, ValidationMessage[] Warnings)> PrepareCreateOptionValuesAsync(
        OptionDefinition[] options, string solution, IMetadataAuthoringProgressReporter? reporter, CancellationToken ct)
    {
        if (options.Length == 0) return (options, []);
        var prefix = await TryResolvePublisherOptionValuePrefixAsync(solution, ct).ConfigureAwait(false);
        if (!prefix.HasValue && options.Any(o => !o.HasExplicitValue))
            throw MissingPublisherOptionValuePrefix(solution);

        var assigned = options.Where(o => o.HasExplicitValue).Select(o => o.Value).ToList();
        var normalized = new OptionDefinition[options.Length];
        var warnings = new List<ValidationMessage>();
        if (!prefix.HasValue)
            warnings.Add(new ValidationMessage { Field = "Options", Rule = "OptionValueRangeUnavailable",
                Message = $"Publisher for solution '{solution}' has no option value prefix; explicit values are preserved, but their publisher range could not be checked." });
        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i];
            var value = option.HasExplicitValue ? option.Value : OptionValueDeriver.Derive(null, prefix, assigned);
            if (!option.HasExplicitValue) assigned.Add(value);
            normalized[i] = new OptionDefinition { Label = option.Label, Value = value, Description = option.Description, Color = option.Color };
            if (option.HasExplicitValue && prefix.HasValue)
            {
                var first = (long)prefix.Value * 10_000;
                var last = first + 9_999;
                if (value < first || value > last)
                    warnings.Add(new ValidationMessage { Field = "Options", Rule = "OptionValueOutsidePublisherRange",
                        Message = $"Explicit option value {value} is outside publisher range {first}–{last} for solution '{solution}'; the explicit value is preserved." });
            }
        }
        foreach (var warning in warnings) reporter?.ReportInfo("Warning: " + warning.Message);
        return (normalized, warnings.ToArray());
    }

    /// <summary>
    /// Dataverse stores the <c>Color</c> parameter verbatim, without validating or normalising it (asserted live for
    /// lower case, three digits, no '#', a colour name, surrounding spaces and invalid text), so the stored value must
    /// equal the request exactly. No input is rejected or rewritten: Dataverse rejects none of these.
    /// </summary>
    private static bool ColorsMatch(string? stored, string requested) => string.Equals(stored, requested, StringComparison.Ordinal);

    /// <summary>Context for a failure after a committed write: whether its cause is an expired sign-in.</summary>
    private static Dictionary<string, object> ReauthenticationContext(Exception ex)
    {
        var context = new Dictionary<string, object>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is PpdsAuthException { RequiresReauthentication: true }
                or PPDS.Dataverse.Resilience.DataverseAuthenticationException { RequiresReauthentication: true })
            {
                context["requiresReauthentication"] = true;
                break;
            }
        }
        return context;
    }

    /// <summary>A write request that timed out or was cancelled may or may not have been applied.</summary>
    private static bool IsIndeterminate(Exception ex) => ex is TimeoutException or OperationCanceledException
        || ex.InnerException is TimeoutException or OperationCanceledException;

    private static string Interruption(Exception ex)
        => ex is OperationCanceledException && ex is not TaskCanceledException ? "was cancelled" : "timed out or was cancelled";

    /// <summary>
    /// Publishes after a committed option write. A failure keeps its own code, exit classification and
    /// re-authentication signal, and its message says what was already written and that it is not published.
    /// </summary>
    private async Task PublishAfterWriteAsync(string entityLogicalName, string written, CancellationToken ct)
    {
        try
        {
            await PublishEntityInternalAsync(entityLogicalName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var (mapped, exit) = ExceptionMapper.MapWithExitCode(ex);
            var reason = ex switch
            {
                OperationCanceledException => "publishing was cancelled",
                PpdsException or MetadataValidationException => "publishing failed: " + ex.Message.TrimEnd('.'),
                _ => "publishing failed"
            };
            var message = $"{written} It is saved but not published: {reason}. Run 'ppds metadata publish {entityLogicalName}' to publish it.";
            if (ex is MetadataValidationException validation)
                throw new MetadataValidationException(validation.ErrorCode, message, validation.Field, ex);
            if (exit == ExitCodes.AuthError)
                throw new PpdsAuthException(mapped.Code, message, ex) { RequiresReauthentication = ReauthenticationContext(ex).Count > 0 };
            throw new PpdsException(mapped.Code, message, ex);
        }
    }

    /// <summary>The cache entries a committed option write makes stale: the table for a local target, else the global choices.</summary>
    private void InvalidateOptionCache(string? entityLogicalName, string? attributeLogicalName)
    {
        if (IsLocalOptionTarget(entityLogicalName, attributeLogicalName))
            _cacheProvider?.InvalidateEntity(entityLogicalName!);
        else
            _cacheProvider?.InvalidateGlobalOptionSets();
    }

    /// <summary>The properties an option update sent, for messages about a committed write.</summary>
    private static string Sent(string? label, string? description, string? color)
        => string.Join(", ", new[] { label != null ? "label" : null, description != null ? "description" : null, !string.IsNullOrEmpty(color) ? "color" : null }
            .Where(part => part != null));

    /// <summary>An option update must change something; otherwise it is refused before any Dataverse call.</summary>
    private static void EnsureSomethingToUpdate(string? newLabel, string? description, string? color, string what)
    {
        if (newLabel == null && description == null && string.IsNullOrEmpty(color))
            throw new MetadataValidationException(MetadataErrorCodes.MissingRequiredField,
                $"Nothing to update: provide {what}.", "NewLabel");
    }

    /// <summary>Reads one option from draft metadata (global choice or entity + attribute), to confirm a write.</summary>
    private Task<OptionMetadata> ReadStoredOptionAsync(
        string? optionSetName,
        string? entityLogicalName,
        string? attributeLogicalName,
        int value,
        CancellationToken ct)
    {
        // Local (column option or status reason) takes precedence: the entity + attribute pair fully
        // identifies a column-scoped option set, even when an OptionSetName is also present.
        if (!string.IsNullOrEmpty(entityLogicalName) && !string.IsNullOrEmpty(attributeLogicalName))
            return ReadLocalOptionAsync(entityLogicalName, attributeLogicalName, value, ct);

        if (!string.IsNullOrEmpty(optionSetName))
            return ReadGlobalOptionAsync(optionSetName, value, ct);

        throw new MetadataValidationException(
            MetadataErrorCodes.MissingRequiredField,
            "Confirming an option write requires either a global choice name or an entity + attribute.",
            "OptionSetName");
    }

    private async Task<OptionMetadata> ReadGlobalOptionAsync(string optionSetName, int value, CancellationToken ct)
    {
        OptionSetMetadata? optionSet;
        try
        {
            // #1531: read as-if-published so an option inserted moments earlier (add-option --color) is found.
            // #1535: the exact spelling is retrieved first; a case variant resolves to the stored name.
            OptionSetMetadataBase? read;
            (optionSetName, read) = await WithGlobalChoiceNameAsync(optionSetName, "OptionSetName", async name =>
            {
                await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
                var response = await client
                    .ExecuteAsync(new RetrieveOptionSetRequest { Name = name, RetrieveAsIfPublished = true }, ct).ConfigureAwait(false);
                return (response as RetrieveOptionSetResponse)?.Results.TryGetValue("OptionSetMetadata", out var metadata) == true
                    ? metadata as OptionSetMetadataBase : null;
            }, metadata => metadata?.Name, ct).ConfigureAwait(false);
            if (read is null)
                throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, $"Dataverse returned no metadata for global choice '{optionSetName}'.");
            // RetrieveOptionSetResponse.OptionSetMetadata is typed as the base; the Options collection lives on
            // the picklist-typed OptionSetMetadata. A boolean choice (no Options) falls through to OptionNotFound.
            optionSet = read as OptionSetMetadata;
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException and not OperationCanceledException)
        {
            throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                $"Failed to read global choice '{optionSetName}' to confirm the write.", ex);
        }

        return optionSet?.Options?.FirstOrDefault(o => o.Value == value)
            ?? throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                $"Option {value} not found in global choice '{optionSetName}'.", "Value");
    }

    private async Task<OptionMetadata> ReadLocalOptionAsync(
        string entityLogicalName, string attributeLogicalName, int value, CancellationToken ct)
    {
        AttributeMetadata? attribute;
        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var retrieveResponse = await client.ExecuteAsync(
                    new RetrieveAttributeRequest
                    {
                        EntityLogicalName = entityLogicalName,
                        LogicalName = attributeLogicalName,
                        // The inserted option and other pending changes may not yet be published.
                        RetrieveAsIfPublished = true
                    },
                    ct).ConfigureAwait(false);
                attribute = (retrieveResponse as RetrieveAttributeResponse)?.Results.TryGetValue("AttributeMetadata", out var metadata) == true
                    ? metadata as AttributeMetadata : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
            {
                throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                    $"Failed to read attribute '{attributeLogicalName}' on '{entityLogicalName}' to confirm the write.", ex);
            }
        }

        if (attribute is null)
            throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                $"Dataverse returned no metadata for attribute '{attributeLogicalName}' on '{entityLogicalName}'.");

        // EnumAttributeMetadata is the base for Picklist / MultiSelectPicklist / State / Status — all expose OptionSet.
        var enumAttribute = attribute as EnumAttributeMetadata
            ?? throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"Attribute '{attributeLogicalName}' on '{entityLogicalName}' is not a choice or status column.", "ColumnLogicalName");

        return enumAttribute.OptionSet?.Options?.FirstOrDefault(o => o.Value == value)
            ?? throw new MetadataValidationException(MetadataErrorCodes.OptionNotFound,
                $"Option {value} not found on '{entityLogicalName}.{attributeLogicalName}'.", "Value");
    }

    /// <summary>Adds the option-value messages' <c>Color</c> parameter to <paramref name="request"/> when a colour is requested.</summary>
    private static void WithColor(OrganizationRequest request, string? color)
    {
        if (!string.IsNullOrEmpty(color))
            request[ColorParameter] = color;
    }

    /// <summary>
    /// The solution for an option-value message. An empty <c>SolutionUniqueName</c> is rejected by Dataverse with a
    /// fault (asserted live for UpdateOptionValue and the other option messages, see "Option colours"), so an empty or
    /// blank name is sent as no solution.
    /// </summary>
    private static string? OptionSolution(string? solutionUniqueName)
        => string.IsNullOrWhiteSpace(solutionUniqueName) ? null : solutionUniqueName;

    /// <summary>The <c>Color</c> parameter of the option-value messages (not exposed as an SDK property).</summary>
    internal const string ColorParameter = "Color";

    /// <summary>
    /// Builds an inline (column-scoped) option set. IsGlobal MUST be explicitly false and
    /// OptionSetType set, or Dataverse rejects column creation with "IsGlobal is not specified" (#1161).
    /// </summary>
    private static OptionSetMetadata BuildLocalOptionSet(OptionDefinition[] options, int lcid)
    {
        var optionSet = new OptionSetMetadata
        {
            IsGlobal = false,
            OptionSetType = OptionSetType.Picklist
        };
        foreach (var opt in options)
        {
            var om = new OptionMetadata(new Label(opt.Label, lcid), opt.Value);
            if (!string.IsNullOrEmpty(opt.Color))
                om.Color = opt.Color;
            optionSet.Options.Add(om);
        }
        return optionSet;
    }

    private static MultiSelectPicklistAttributeMetadata BuildMultiSelectChoiceAttribute(CreateColumnRequest request, OptionDefinition[]? options, int lcid)
    {
        var attr = new MultiSelectPicklistAttributeMetadata();

        if (!string.IsNullOrEmpty(request.OptionSetName))
        {
            attr.OptionSet = new OptionSetMetadata { IsGlobal = true, Name = request.OptionSetName };
        }
        else if (options != null && options.Length > 0)
        {
            attr.OptionSet = BuildLocalOptionSet(options, lcid);
        }

        if (request.DefaultValue.HasValue)
            attr.DefaultFormValue = request.DefaultValue;

        return attr;
    }

    private async Task ValidatePrimaryIdLabelUpdateAsync(
        UpdateColumnRequest request, AttributeMetadata attribute, CancellationToken ct)
    {
        if (request.DisplayName == null && request.Description == null)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "A primary ID update requires a display name or description.");

        if (request is not
            {
                RequiredLevel: null, MaxLength: null, MinValue: null, MaxValue: null, Precision: null,
                Format: null, IsAuditEnabled: null, IsSecured: null, IsValidForAdvancedFind: null, AutoNumberFormat: null
            })
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "A primary ID update supports only the display name and description. Remove the other column settings.");

        // Address GUIDs can also have IsPrimaryId set. Verify the table's actual key before writing.
        string? primaryId;
        await using (var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            try
            {
                var response = await client.ExecuteAsync(new RetrieveEntityRequest
                {
                    LogicalName = request.EntityLogicalName,
                    EntityFilters = EntityFilters.Entity
                }, ct).ConfigureAwait(false);
                primaryId = (response as RetrieveEntityResponse)?.EntityMetadata?.PrimaryIdAttribute;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
            {
                throw new PpdsException(MetadataErrorCodes.SdkOperationFailed,
                    $"Could not retrieve the primary ID of '{request.EntityLogicalName}'.", ex);
            }
        }

        if (!string.Equals(primaryId, request.ColumnLogicalName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(primaryId, attribute.LogicalName, StringComparison.OrdinalIgnoreCase))
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"Table metadata does not identify '{request.ColumnLogicalName}' as the primary ID of '{request.EntityLogicalName}'.",
                nameof(request.ColumnLogicalName));
    }

    private static AttributeMetadata CreateUpdateAttribute(AttributeMetadata existingAttr)
    {
        return existingAttr switch
        {
            StringAttributeMetadata => new StringAttributeMetadata(),
            MemoAttributeMetadata => new MemoAttributeMetadata(),
            IntegerAttributeMetadata => new IntegerAttributeMetadata(),
            BigIntAttributeMetadata => new BigIntAttributeMetadata(),
            DecimalAttributeMetadata => new DecimalAttributeMetadata(),
            DoubleAttributeMetadata => new DoubleAttributeMetadata(),
            MoneyAttributeMetadata => new MoneyAttributeMetadata(),
            BooleanAttributeMetadata => new BooleanAttributeMetadata(),
            DateTimeAttributeMetadata => new DateTimeAttributeMetadata(),
            MultiSelectPicklistAttributeMetadata => new MultiSelectPicklistAttributeMetadata(),
            PicklistAttributeMetadata => new PicklistAttributeMetadata(),
            ImageAttributeMetadata => new ImageAttributeMetadata(),
            FileAttributeMetadata => new FileAttributeMetadata(),
            LookupAttributeMetadata => new LookupAttributeMetadata(),
            _ => throw new MetadataValidationException(
                MetadataErrorCodes.InvalidConstraint,
                $"Unsupported attribute type for update: {existingAttr.GetType().Name}",
                "ColumnType")
        };
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
            case DateTimeAttributeMetadata dtAttr:
                if (request.Format != null) dtAttr.Format = ParseDateTimeFormat(request.Format);
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

    // Create: a setting not given is NoCascade (a separate issue); an undefined value is refused.
    private static SdkCascadeType MapCascadeType(CascadeBehavior? behavior)
        => behavior is { } value ? ToSdkCascadeType(value) : SdkCascadeType.NoCascade;

    // #1919: the text given for a required level or a format must name one of these (any letter case, surrounding
    // white space ignored). Unknown text is refused, never read as a default: a typo must not make a required column
    // optional or set a format nobody asked for.
    private static readonly (string Name, AttributeRequiredLevel Value)[] RequiredLevelNames =
    [
        ("None", AttributeRequiredLevel.None),
        ("Recommended", AttributeRequiredLevel.Recommended),
        ("Required", AttributeRequiredLevel.ApplicationRequired),
        ("ApplicationRequired", AttributeRequiredLevel.ApplicationRequired),
        ("SystemRequired", AttributeRequiredLevel.SystemRequired),
    ];

    private static readonly (string Name, StringFormatName Value)[] StringFormatNames =
    [
        ("Text", StringFormatName.Text), ("Email", StringFormatName.Email), ("Url", StringFormatName.Url),
        ("Phone", StringFormatName.Phone), ("TickerSymbol", StringFormatName.TickerSymbol),
        ("Phonetic", StringFormatName.PhoneticGuide), ("VersionNumber", StringFormatName.VersionNumber),
        ("Json", StringFormatName.Json), ("RichText", StringFormatName.RichText),
    ];

    private static readonly (string Name, IntegerFormat Value)[] IntegerFormatNames =
    [
        ("None", IntegerFormat.None), ("Duration", IntegerFormat.Duration), ("TimeZone", IntegerFormat.TimeZone),
        ("Language", IntegerFormat.Language), ("Locale", IntegerFormat.Locale),
    ];

    private static readonly (string Name, DateTimeFormat Value)[] DateTimeFormatNames =
    [
        ("DateAndTime", DateTimeFormat.DateAndTime), ("DateOnly", DateTimeFormat.DateOnly),
    ];

    private static bool TryName<T>((string Name, T Value)[] names, string? text, out T value)
    {
        var trimmed = text?.Trim();
        foreach (var (name, candidate) in names)
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private static string NameList<T>((string Name, T Value)[] names)
        => string.Join(", ", names[..^1].Select(n => n.Name)) + " or " + names[^1].Name;

    private static MetadataValidationException RequiredLevelNotKnown()
        => new(MetadataErrorCodes.InvalidConstraint,
            $"The required level is not one PPDS knows. Give {NameList(RequiredLevelNames)}. Nothing was changed.",
            "RequiredLevel");

    private static MetadataValidationException FormatNotKnown(string columnType, string names)
        => new(MetadataErrorCodes.InvalidConstraint,
            $"The format is not one a {columnType} column takes. Give {names}. Nothing was changed.",
            "Format");

    /// <summary>A required level that was given (update): any text, blank included, must name a level.</summary>
    private static void ValidateGivenRequiredLevel(string? level)
    {
        if (level != null && !TryName(RequiredLevelNames, level, out _))
            throw RequiredLevelNotKnown();
    }

    /// <summary>
    /// Create: blank text is not given (the default applies); other text must name a level, and a format given for a
    /// String, Integer or DateTime column must name one that type takes. Checked before anything is read.
    /// </summary>
    private static void ValidateCreateColumnText(CreateColumnRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RequiredLevel))
            ValidateGivenRequiredLevel(request.RequiredLevel);
        if (string.IsNullOrWhiteSpace(request.Format))
            return;
        _ = request.ColumnType switch
        {
            SchemaColumnType.String => ParseStringFormat(request.Format),
            SchemaColumnType.Integer => ParseIntegerFormat(request.Format),
            SchemaColumnType.DateTime => (object?)ParseDateTimeFormat(request.Format),
            _ => null
        };
    }

    /// <summary>
    /// Update, before the read: a format given (blank included) must name a format of some column type. After the
    /// read, <see cref="ApplyTypeSpecificUpdates"/> refuses one the column's own type does not take.
    /// </summary>
    private static void ValidateGivenFormat(string? format)
    {
        if (format != null && !TryName(StringFormatNames, format, out _) && !TryName(IntegerFormatNames, format, out _)
            && !TryName(DateTimeFormatNames, format, out _))
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                $"The format is not one any column type takes. String: {NameList(StringFormatNames)}. Integer: {NameList(IntegerFormatNames)}. DateTime: {NameList(DateTimeFormatNames)}. Nothing was changed.",
                "Format");
    }

    private static AttributeRequiredLevel ParseRequiredLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return AttributeRequiredLevel.None;
        return TryName(RequiredLevelNames, level, out var value) ? value : throw RequiredLevelNotKnown();
    }

    private static StringFormatName? ParseStringFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return StringFormatName.Text;
        return TryName(StringFormatNames, format, out var value) ? value : throw FormatNotKnown("String", NameList(StringFormatNames));
    }

    private static IntegerFormat? ParseIntegerFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return IntegerFormat.None;
        return TryName(IntegerFormatNames, format, out var value) ? value : throw FormatNotKnown("Integer", NameList(IntegerFormatNames));
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

    private static DateTimeAttributeMetadata BuildDateTimeAttribute(CreateColumnRequest request)
    {
        var behavior = ParseDateTimeBehavior(request.DateTimeBehavior);
        // DateOnly behavior requires DateOnly format; Dataverse rejects any other combination.
        var format = behavior == Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.DateOnly
            ? DateTimeFormat.DateOnly
            : ParseDateTimeFormat(request.Format);
        return new DateTimeAttributeMetadata { DateTimeBehavior = behavior, Format = format };
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
        return TryName(DateTimeFormatNames, format, out var value) ? value : throw FormatNotKnown("DateTime", NameList(DateTimeFormatNames));
    }

    private async Task<int> ResolvePublisherOptionValuePrefixAsync(string solutionUniqueName, CancellationToken ct)
        => await TryResolvePublisherOptionValuePrefixAsync(solutionUniqueName, ct).ConfigureAwait(false)
            ?? throw MissingPublisherOptionValuePrefix(solutionUniqueName);

    private static MetadataValidationException MissingPublisherOptionValuePrefix(string solutionUniqueName)
        => new(MetadataErrorCodes.MissingRequiredField,
            $"Publisher for solution '{solutionUniqueName}' has no option value prefix configured; pass --value to assign the option value explicitly.",
            "SolutionUniqueName");

    private async Task<int?> TryResolvePublisherOptionValuePrefixAsync(string solutionUniqueName, CancellationToken ct)
    {
        if (_publisherOptionValuePrefixCache.TryGetValue(solutionUniqueName, out var cached))
            return cached;

        // D2: acquire, use, release per query — never hold a client across multiple operations
        Guid publisherId;
        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            var solutionQuery = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet("publisherid"),
                Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, solutionUniqueName) } }
            };
            var solutionResult = await client.RetrieveMultipleAsync(solutionQuery, ct).ConfigureAwait(false);
            if (solutionResult.Entities.Count == 0)
                throw new MetadataValidationException(MetadataErrorCodes.EntityNotFound, $"Solution '{solutionUniqueName}' not found.", "SolutionUniqueName");
            publisherId = (solutionResult.Entities[0].GetAttributeValue<EntityReference>("publisherid")
                ?? throw new MetadataValidationException(MetadataErrorCodes.EntityNotFound,
                    $"Solution '{solutionUniqueName}' has no publisher reference.", "SolutionUniqueName")).Id;
        }

        {
            await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            var publisherQuery = new QueryExpression("publisher")
            {
                ColumnSet = new ColumnSet("customizationoptionvalueprefix"),
                Criteria = { Conditions = { new ConditionExpression("publisherid", ConditionOperator.Equal, publisherId) } }
            };
            var publisherResult = await client.RetrieveMultipleAsync(publisherQuery, ct).ConfigureAwait(false);
            if (publisherResult.Entities.Count == 0)
                throw new MetadataValidationException(MetadataErrorCodes.EntityNotFound, $"Publisher for solution '{solutionUniqueName}' not found.", "SolutionUniqueName");
            var prefix = publisherResult.Entities[0].GetAttributeValue<int?>("customizationoptionvalueprefix");
            if (prefix.HasValue) _publisherOptionValuePrefixCache[solutionUniqueName] = prefix.Value;
            return prefix;
        }
    }

    private async Task PublishEntityInternalAsync(string entityLogicalName, CancellationToken ct)
    {
        var parameterXml = $"<importexportxml><entities><entity>{entityLogicalName}</entity></entities></importexportxml>";
        var publishRequest = new PublishXmlRequest { ParameterXml = parameterXml };

        await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
        await client.ExecuteAsync(publishRequest, ct).ConfigureAwait(false);

        _logger?.LogInformation("Published entity '{Entity}'", entityLogicalName);
    }

    #endregion
}
