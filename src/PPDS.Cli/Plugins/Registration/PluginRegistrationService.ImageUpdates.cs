using System.ServiceModel;
using System.Runtime.ExceptionServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Plugins.Registration;

public sealed partial class PluginRegistrationService
{
    private static readonly string[] ImageUpdateContextFields =
        ["sdkmessageprocessingstepid", "name", "imagetype", "entityalias", "messagepropertyname", "attributes", "description"];

    private async Task UpdateImageConditionallyAsync(Entity patch, CancellationToken cancellationToken)
    {
        Entity target;
        IPooledClient writer;
        try
        {
            await ValidateImageUpdateCapabilitiesAsync(cancellationToken);
            Entity snapshot;
            await using (var reader = await _pool.GetClientAsync(cancellationToken: cancellationToken))
            {
                snapshot = await RetrieveAsync(patch.LogicalName, patch.Id,
                    new ColumnSet([.. ImageUpdateContextFields, "versionnumber"]), reader, cancellationToken);
            }

            target = CreateImageUpdateContext(snapshot, patch.Id);
            foreach (var (field, value) in patch.Attributes) target[field] = value;

            // This lease alone opts out of SDK transient retries. A stale or uncertain
            // mutation must not be silently reissued or converted to an unconditional write.
            writer = await _pool.GetClientAsync(
                new DataverseClientOptions { MaxRetryCount = 0 }, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(ExceptionMapper.Map(ex).Code,
                "Image update preparation could not be completed. No image update was dispatched.", ex);
        }

        await DispatchConditionalImageUpdateAsync(target, writer, cancellationToken);
    }

    private static async Task DispatchConditionalImageUpdateAsync(Entity target, IPooledClient writer, CancellationToken cancellationToken)
    {
        OrganizationResponse? response = null;
        Exception? dispatchFailure = null;
        Exception? disposalFailure = null;
        try
        {
            response = await ExecuteAsync(new UpdateRequest
            {
                Target = target,
                ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches
            }, writer, cancellationToken);
        }
        catch (Exception ex) { dispatchFailure = ex; }

        // Returning a lease must not replace the actual mutation outcome. Classify
        // only the Execute result, retaining any cleanup failure as secondary evidence.
        try { await writer.DisposeAsync(); }
        catch (Exception ex) { disposalFailure = ex; }

        if (dispatchFailure is OperationCanceledException)
        {
            if (disposalFailure is not null) dispatchFailure.Data["ImageUpdateClientDisposalFailure"] = disposalFailure;
            ExceptionDispatchInfo.Capture(dispatchFailure).Throw();
        }
        if (dispatchFailure is not null)
        {
            var cause = disposalFailure is null ? dispatchFailure : new AggregateException(dispatchFailure, disposalFailure);
            if (dispatchFailure is FaultException<OrganizationServiceFault> fault)
            {
                if (fault.Detail.ErrorCode == -2147088254)
                    throw new PpdsException(ErrorCodes.Plugin.ImageUpdateConflict,
                        "The image changed after it was read. The conditional update was rejected; review its current values before submitting another update.", cause);
                if (fault.Detail.ErrorCode is -2147088253 or -2147088243)
                    throw new PpdsException(ErrorCodes.Plugin.FieldCapabilityUnknown,
                        "Dataverse did not accept the image row-version condition. PPDS will not retry without that condition.", cause);
            }
            throw new PpdsException(ExceptionMapper.Map(dispatchFailure).Code,
                "The conditional image update failed and its outcome is unverified. No automatic mutation retry was attempted; verify the current image before another update.", cause);
        }
        if (response is not UpdateResponse)
            throw new PpdsException(ErrorCodes.Plugin.VerificationFailed,
                "Dataverse did not return an image update acknowledgement. The outcome is uncertain; PPDS will not retry automatically.", disposalFailure);
        if (disposalFailure is not null)
            throw new PpdsException(ErrorCodes.Operation.PartialFailure,
                "Dataverse acknowledged the image update, but returning the connection failed. Do not repeat the update solely because of this cleanup failure.", disposalFailure);
    }

    private async Task ValidateImageUpdateCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var response = await ExecuteAsync(new RetrieveEntityRequest
        {
            LogicalName = SdkMessageProcessingStepImage.EntityLogicalName,
            EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
            RetrieveAsIfPublished = true
        }, client, cancellationToken);
        if (response is not RetrieveEntityResponse metadataResponse ||
            metadataResponse.EntityMetadata is not { } metadata ||
            metadata.LogicalName != SdkMessageProcessingStepImage.EntityLogicalName ||
            metadata.IsOptimisticConcurrencyEnabled is not true)
            throw new PpdsException(ErrorCodes.Plugin.FieldCapabilityUnknown,
                "Image metadata does not establish optimistic concurrency support. No image update was dispatched.");

        foreach (var field in ImageUpdateContextFields)
        {
            var attribute = metadata.Attributes?.SingleOrDefault(a => a.LogicalName == field);
            if (attribute?.IsValidForRead is not true || attribute.IsValidForUpdate is null)
                throw new PpdsException(ErrorCodes.Plugin.FieldCapabilityUnknown,
                    $"Image metadata does not establish read/update capability for '{field}'. No image update was dispatched.");
            if (attribute.IsValidForUpdate is false)
                throw new PpdsException(ErrorCodes.Plugin.FieldNotWritable,
                    $"Image metadata does not permit updating '{field}'. No image update was dispatched.");
        }
    }

    private static Entity CreateImageUpdateContext(Entity snapshot, Guid imageId)
    {
        if (snapshot is null || snapshot.LogicalName != SdkMessageProcessingStepImage.EntityLogicalName ||
            snapshot.Id != imageId || string.IsNullOrWhiteSpace(snapshot.RowVersion))
            throw IncompleteImageUpdateContext();

        var target = new Entity(snapshot.LogicalName, imageId) { RowVersion = snapshot.RowVersion };
        foreach (var field in ImageUpdateContextFields)
        {
            snapshot.Attributes.TryGetValue(field, out var value);
            target[field] = field switch
            {
                "sdkmessageprocessingstepid" when value is EntityReference parent &&
                    parent.LogicalName == SdkMessageProcessingStep.EntityLogicalName && parent.Id != Guid.Empty
                    => new EntityReference(parent.LogicalName, parent.Id),
                "imagetype" when value is OptionSetValue type && type.Value is >= 0 and <= 2
                    => new OptionSetValue(type.Value),
                "name" or "entityalias" or "messagepropertyname" when value is string text && !string.IsNullOrWhiteSpace(text)
                    => text,
                // SDK Retrieve omits null attributes. Capability was checked and these
                // nullable columns were explicitly requested; absence represents null.
                "attributes" or "description" when value is null or string => value,
                _ => throw IncompleteImageUpdateContext()
            };
        }
        return target;
    }

    private static PpdsException IncompleteImageUpdateContext() => new(ErrorCodes.Plugin.FieldCapabilityUnknown,
        "The image's current registration context or genuine SDK row version is unavailable. No image update was dispatched.");
}
