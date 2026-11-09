using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

public partial class DataverseMetadataAuthoringService
{
    private async Task<AttributeMetadata> ReadColumnForUpdateAsync(RetrieveAttributeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Retain the existing direct column-read acquisition classification.
        await using var client = await _connectionPool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var response = await client.ExecuteAsync(request, ct).ConfigureAwait(false);
            return (response as RetrieveAttributeResponse)?.AttributeMetadata
                ?? throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, "Could not read the column metadata.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
        {
            throw NormalizeColumnLabelSdkFailure(ex, "Could not read the column metadata.");
        }
    }

    private static bool IsFiniteLabelOnlyColumn(AttributeMetadata attribute)
    {
        var type = attribute.GetType();
        return type == typeof(StateAttributeMetadata) || type == typeof(StatusAttributeMetadata)
            || type == typeof(UniqueIdentifierAttributeMetadata)
            || (type == typeof(AttributeMetadata) && attribute.IsPrimaryId == false &&
                (attribute.AttributeType == AttributeTypeCode.Uniqueidentifier ||
                 AttributeTypeDisplayName.UniqueidentifierType.Equals(attribute.AttributeTypeName)));
    }

    private async Task UpdateFiniteColumnLabelsAsync(
        UpdateColumnRequest request, AttributeMetadata source, IMetadataAuthoringProgressReporter? reporter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateFiniteColumnLabels(request, source);

        if (source is UniqueIdentifierAttributeMetadata && source.IsPrimaryId == true)
            await ValidateConcretePrimaryIdLabelsAsync(request, source, ct).ConfigureAwait(false);

        // Construct the exact reviewed SDK type. Do not carry retrieved translations,
        // option values/defaults, managed properties or unrelated column settings.
        AttributeMetadata fresh = source.GetType() == typeof(StateAttributeMetadata) ? new StateAttributeMetadata()
            : source.GetType() == typeof(StatusAttributeMetadata) ? new StatusAttributeMetadata()
            : source.GetType() == typeof(UniqueIdentifierAttributeMetadata) ? new UniqueIdentifierAttributeMetadata()
            : new AttributeMetadata();
        fresh.LogicalName = source.LogicalName;
        fresh.SchemaName = source.SchemaName;
        fresh.MetadataId = source.MetadataId;
        if (request.DisplayName != null) fresh.DisplayName = new Label(request.DisplayName, 1033);
        if (request.Description != null) fresh.Description = new Label(request.Description, 1033);

        reporter?.ReportPhase("Updating column", source.LogicalName);
        ct.ThrowIfCancellationRequested();
        await ExecuteColumnLabelRequestAsync(new UpdateAttributeRequest
        {
            EntityName = source.EntityLogicalName,
            Attribute = fresh,
            MergeLabels = true,
            SolutionUniqueName = request.SolutionUniqueName
        }, ct).ConfigureAwait(false);

        // The SDK acknowledged the update. Invalidate after returning its lease,
        // including when cancellation arrived with the acknowledgement; never replay.
        _cacheProvider?.InvalidateEntity(source.EntityLogicalName);
        ct.ThrowIfCancellationRequested();
        if (request.Publish)
        {
            try
            {
                await PublishEntityInternalAsync(source.EntityLogicalName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
            {
                throw NormalizeColumnLabelSdkFailure(ex, "Could not publish the column label update.");
            }
        }

        ct.ThrowIfCancellationRequested();
        reporter?.ReportInfo(request.Publish
            ? $"Column '{source.LogicalName}' updated on '{source.EntityLogicalName}' and published."
            : $"Column '{source.LogicalName}' updated on '{source.EntityLogicalName}'. Changes must be published to take effect.");
        ct.ThrowIfCancellationRequested();
        _logger?.LogInformation("Updated column {Column} on {Entity}", source.LogicalName, source.EntityLogicalName);
    }

    private static void ValidateFiniteColumnLabels(UpdateColumnRequest request, AttributeMetadata source)
    {
        if (request.DisplayName == null && request.Description == null)
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "A column label update requires a display name or description.");
        if (request is not
            {
                RequiredLevel: null, MaxLength: null, MinValue: null, MaxValue: null, Precision: null,
                Format: null, IsAuditEnabled: null, IsSecured: null, IsValidForAdvancedFind: null, AutoNumberFormat: null
            })
            throw new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
                "This column update supports only the display name and description. Remove the other column settings.");

        if (!IsColumnLabelIdentifier(source.EntityLogicalName) ||
            !string.Equals(source.EntityLogicalName, request.EntityLogicalName, StringComparison.OrdinalIgnoreCase))
            throw ColumnLabelIdentityError("The column metadata does not identify the requested table.", "EntityLogicalName");
        if (!IsColumnLabelIdentifier(source.LogicalName) ||
            !string.Equals(source.LogicalName, request.ColumnLogicalName, StringComparison.OrdinalIgnoreCase))
            throw ColumnLabelIdentityError("The column metadata does not identify the requested column.", "ColumnLogicalName");
        if (!IsColumnLabelIdentifier(source.SchemaName) ||
            !string.Equals(source.SchemaName, source.LogicalName, StringComparison.OrdinalIgnoreCase))
            throw ColumnLabelIdentityError("The column metadata does not contain a valid canonical schema name.", "SchemaName");
        if (!source.MetadataId.HasValue || source.MetadataId.Value == Guid.Empty)
            throw ColumnLabelIdentityError("The column metadata does not contain a nonempty metadata ID.", "MetadataId");

        var type = source.GetType();
        var expectedCode = type == typeof(StateAttributeMetadata) ? AttributeTypeCode.State
            : type == typeof(StatusAttributeMetadata) ? AttributeTypeCode.Status : AttributeTypeCode.Uniqueidentifier;
        var expectedName = type == typeof(StateAttributeMetadata) ? AttributeTypeDisplayName.StateType
            : type == typeof(StatusAttributeMetadata) ? AttributeTypeDisplayName.StatusType : AttributeTypeDisplayName.UniqueidentifierType;
        if (source.AttributeType != expectedCode || !expectedName.Equals(source.AttributeTypeName) ||
            (expectedCode == AttributeTypeCode.Uniqueidentifier && !source.IsPrimaryId.HasValue))
            throw ColumnLabelIdentityError("The column metadata does not contain a consistent reviewed column type.", "ColumnType");

        if (source.IsCustomizable?.Value == false)
            throw ColumnLabelIdentityError("The column's customization metadata denies this label update.", "IsCustomizable");
        if (request.DisplayName != null && source.IsRenameable?.Value == false)
            throw ColumnLabelIdentityError("The column's rename metadata denies a display name update.", "IsRenameable");
        // Unknown managed editability remains server-authoritative. Neither the
        // IsManaged flag nor record-data IsValidForUpdate is a metadata permission.
    }

    private async Task ValidateConcretePrimaryIdLabelsAsync(UpdateColumnRequest request, AttributeMetadata source, CancellationToken ct)
    {
        var response = await ExecuteColumnLabelRequestAsync(new RetrieveEntityRequest
        {
            LogicalName = source.EntityLogicalName,
            EntityFilters = EntityFilters.Entity
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var table = (response as RetrieveEntityResponse)?.EntityMetadata;
        if (table == null || !IsColumnLabelIdentifier(table.LogicalName) ||
            !string.Equals(table.LogicalName, source.EntityLogicalName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(table.LogicalName, request.EntityLogicalName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(table.PrimaryIdAttribute, source.LogicalName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(table.PrimaryIdAttribute, request.ColumnLogicalName, StringComparison.OrdinalIgnoreCase))
            throw ColumnLabelIdentityError("Table metadata does not identify the requested column as its primary ID.", "ColumnLogicalName");
    }

    private async Task<OrganizationResponse> ExecuteColumnLabelRequestAsync(OrganizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await client.ExecuteAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
        {
            throw NormalizeColumnLabelSdkFailure(ex, "Could not complete the column label operation.");
        }
    }

    private static PpdsException NormalizeColumnLabelSdkFailure(Exception ex, string userMessage)
    {
        // Retain only the existing mapper's classification. Provider message/target
        // stay out of the public exception; the original diagnostic is the cause.
        var (mapped, exitCode) = ExceptionMapper.MapWithExitCode(ex);
        if (exitCode == ExitCodes.AuthError)
            return new PpdsAuthException(mapped.Code, userMessage, ex)
            {
                RequiresReauthentication = ex is PPDS.Dataverse.Resilience.DataverseAuthenticationException { RequiresReauthentication: true }
            };

        var code = exitCode == ExitCodes.ConnectionError ? mapped.Code : MetadataErrorCodes.SdkOperationFailed;
        return new PpdsException(code, userMessage, ex);
    }

    private static MetadataValidationException ColumnLabelIdentityError(string message, string field)
        => new(MetadataErrorCodes.InvalidConstraint, message, field);

    private static bool IsColumnLabelIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || !char.IsAsciiLetter(value[0])) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        return true;
    }
}
