using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Verifies the requested invocation source after an acknowledged direct step update.</summary>
internal static class PluginStepUpdateVerificationService
{
    internal static async Task VerifyInvocationSourceAsync(IDataverseConnectionPool pool, Guid stepId,
        int expected, CancellationToken cancellationToken)
    {
        Entity? stored;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);
            stored = await client.RetrieveAsync(SdkMessageProcessingStep.EntityLogicalName, stepId,
                new ColumnSet(SdkMessageProcessingStep.Fields.InvocationSource), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex)
        {
            throw new OperationCanceledException(
                "Invocation-source verification was cancelled after Dataverse acknowledged the step update; " +
                "persistence remains unverified. No retry or rollback was attempted.", ex, ex.CancellationToken);
        }
        catch (Exception ex)
        {
            throw Unverified(stepId, expected, ex);
        }

        if (stored is null || stored.Id != stepId ||
            stored.LogicalName != SdkMessageProcessingStep.EntityLogicalName ||
            !stored.Attributes.TryGetValue(SdkMessageProcessingStep.Fields.InvocationSource, out var value) ||
            value is not OptionSetValue actual || actual.Value is < -1 or > 1)
            throw Unverified(stepId, expected);

        if (actual.Value != expected)
            throw new PpdsException(ErrorCodes.Plugin.UpdateNotPersisted,
                $"Dataverse acknowledged the update to step '{stepId}', but the fresh invocationSource " +
                $"read returned {actual.Value} instead of {expected}. Other requested fields are not verified. " +
                "No retry or rollback was attempted.",
                new Dictionary<string, object>
                {
                    ["stepId"] = stepId, ["field"] = "invocationSource",
                    ["expected"] = expected, ["observed"] = actual.Value,
                    ["writeOutcome"] = "acknowledged", ["verification"] = "mismatch"
                });
    }

    private static PpdsException Unverified(Guid stepId, int expected, Exception? inner = null) =>
        new(ErrorCodes.Plugin.VerificationFailed,
            $"Dataverse acknowledged the update to step '{stepId}', but invocationSource persistence " +
            "could not be verified. Other requested fields are not verified. No retry or rollback was attempted.",
            new Dictionary<string, object>
            {
                ["stepId"] = stepId, ["field"] = "invocationSource", ["expected"] = expected,
                ["writeOutcome"] = "acknowledged", ["verification"] = "unverified"
            }, inner);
}
