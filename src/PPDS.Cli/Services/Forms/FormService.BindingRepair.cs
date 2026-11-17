using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;

namespace PPDS.Cli.Services.Forms;

public sealed partial class FormService
{
    public async Task<RepairBindingsResult> RepairBindingsAsync(RepairBindingsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        if (request.DryRun && (request.Publish || request.SolutionUniqueName is not null))
            throw new PpdsValidationException("DryRun", "A dry run cannot be combined with publication or solution association (--publish / --solution).");

        // A dry run plans only: like MCP draft dry runs it needs no acknowledgement and makes no request that writes.
        // A target it was given must still match, so a dry run never silently reports on a different environment.
        if (request.DryRun)
            RequireMatchingTarget(request.ConfirmEnvironment);
        else
            await _confirmation.EnsureConfirmedAsync("forms mutation", request.ConfirmEnvironment, ct).ConfigureAwait(false);

        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            var (entityLogicalName, formXmlStr, formId, resolvedName, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            // Same metadata read as the other form writers (add-field, reorder, move): published column metadata.
            var attributes = await _metadataService.GetAttributesAsync(entityLogicalName, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            if (attributes.Count == 0)
                throw new PpdsException(FormErrorCodes.ColumnNotFound,
                    $"No columns were returned for table '{entityLogicalName}'; bindings cannot be checked. Nothing was written.");
            var columnNames = attributes.Select(a => a.LogicalName).ToArray();

            var plan = FormXmlEditor.PlanBindingRepair(formXmlStr!, columnNames);
            // Validated once, on every path, so a dry run surfaces what the write would reject.
            FormXmlValidator.Validate(XDocument.Parse(plan.RepairedXml));
            ct.ThrowIfCancellationRequested();

            if (request.DryRun)
            {
                reporter?.ReportPhase("Planned", $"{plan.Repaired.Count} binding(s) would be repaired; nothing was written.");
                return new(formId, resolvedName, true, plan.Repaired, plan.Unchanged, false, false, false, false);
            }

            var verified = false;
            if (plan.Repaired.Count > 0)
            {
                reporter?.ReportPhase("Writing", $"Repairing {plan.Repaired.Count} binding(s)");
                ct.ThrowIfCancellationRequested();
                // The stored text with only the repaired attribute values replaced, sent verbatim.
                await SubmitFormXmlAsync(plan.RepairedXml, formId, ct);

                reporter?.ReportPhase("Verifying", "Reading the unpublished form back");
                await VerifyRepairAsync(request, columnNames, ct);
                verified = true;
            }
            else
            {
                reporter?.ReportPhase("Unchanged", "No control binding differs from its column's logical name only by case.");
            }

            ct.ThrowIfCancellationRequested();
            // Explicit solution/publication requests keep their established effects, also when nothing was repaired.
            await ApplySideEffectsAsync(formId, entityLogicalName, request.SolutionUniqueName, request.Publish, ct);

            return new(formId, resolvedName, false, plan.Repaired, plan.Unchanged, plan.Repaired.Count > 0, verified,
                request.SolutionUniqueName is not null, request.Publish);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"Failed to repair bindings on form '{request.FormName}': {ex.Message}", ex);
        }
    }

    private void RequireMatchingTarget(string? confirmEnvironment)
    {
        if (confirmEnvironment is null) return;
        var target = _confirmation.Context.EnvironmentUrl;
        if (string.IsNullOrWhiteSpace(target))
            throw new PpdsException(EnvironmentConfirmationErrorCodes.Unresolved,
                "A resolved environment is required to check the supplied acknowledgement.");
        var normalized = EnvironmentConfirmationService.NormalizeUrl(target);
        if (!string.Equals(normalized, EnvironmentConfirmationService.NormalizeUrl(confirmEnvironment), StringComparison.Ordinal))
            throw new PpdsException(EnvironmentConfirmationErrorCodes.TargetMismatch,
                $"The acknowledgement does not match the resolved target '{normalized}'. Nothing was read or written.");
    }

    /// <summary>
    /// Reads the unpublished form back after the update and fails when any control binding still differs from its column's
    /// logical name only by case. Runs before solution association and publication, so neither follows an unconfirmed repair.
    /// </summary>
    private async Task VerifyRepairAsync(RepairBindingsRequest request, IReadOnlyCollection<string> columnNames, CancellationToken ct)
    {
        IReadOnlyList<RepairedBinding> remaining;
        try
        {
            var (_, readBack, _, _, _, _, _) = await FetchFormRecordAsync(request.EntityLogicalName, request.FormName,
                requireMainForm: true, ct, requireCustomizable: true);
            remaining = FormXmlEditor.PlanBindingRepair(readBack ?? string.Empty, columnNames).Repaired;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.RepairNotVerified,
                $"The repaired form XML was submitted, but the form could not be read back to confirm it ({ex.Message}). " +
                "No solution association or publication was attempted; rerun with --dry-run to see the form's current state.", ex);
        }

        if (remaining.Count > 0)
            throw new PpdsException(FormErrorCodes.RepairNotVerified,
                $"The repaired form XML was submitted, but reading the form back still shows {remaining.Count} case-variant binding(s): " +
                $"{string.Join(", ", remaining.Select(r => r.OldBinding))}. No solution association or publication was attempted.");
    }
}
