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

        // ── Phase 1: plan. Nothing has been written; failures keep their own classification. ──
        Guid formId;
        string resolvedName, entityLogicalName;
        string[] columnNames;
        BindingRepairPlan plan;
        try
        {
            reporter?.ReportPhase("Retrieving", $"Fetching form '{request.FormName}'");
            string? formXmlStr;
            (entityLogicalName, formXmlStr, formId, resolvedName, _, _, _) =
                await FetchFormRecordAsync(request.EntityLogicalName, request.FormName, requireMainForm: true, ct, requireCustomizable: true);

            // Same metadata read as the other form writers (add-field, reorder, move): RetrieveAsIfPublished = false.
            var attributes = await _metadataService.GetAttributesAsync(entityLogicalName, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            if (attributes.Count == 0)
                throw new PpdsException(FormErrorCodes.ColumnNotFound,
                    $"No columns were returned for table '{entityLogicalName}'; bindings cannot be checked. Nothing was written.");
            columnNames = attributes.Select(a => a.LogicalName).ToArray();

            plan = FormXmlEditor.PlanBindingRepair(formXmlStr!, columnNames);
            // Validated once, on every path, so a dry run surfaces what the write would reject.
            FormXmlValidator.Validate(XDocument.Parse(plan.RepairedXml));
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(Classify(ex, ct, FormErrorCodes.RepairFailed),
                $"Could not plan the binding repair on form '{request.FormName}': {ex.Message} Nothing was written.", ex);
        }

        if (request.DryRun)
        {
            reporter?.ReportPhase("Planned", $"{plan.Repaired.Count} binding(s) would be repaired; nothing was written.");
            return new(formId, resolvedName, true, plan.Repaired, plan.Unchanged, false, false, false, false);
        }

        // ── Phase 2: submit and verify. ──
        IReadOnlyList<RepairedBinding> readBackRepairable = [];
        if (plan.Repaired.Count > 0)
        {
            reporter?.ReportPhase("Writing", $"Repairing {plan.Repaired.Count} binding(s)");
            ct.ThrowIfCancellationRequested(); // last point at which a cancellation leaves the form untouched
            try
            {
                // The stored text with only the repaired attribute values replaced, sent verbatim.
                await SubmitFormXmlAsync(plan.RepairedXml, formId, ct);
            }
            catch (Exception ex)
            {
                throw new PpdsException(Classify(ex, ct, FormErrorCodes.RepairWriteOutcomeUnknown),
                    $"The update of form '{resolvedName}' did not complete and its outcome is unknown: it may or may not have been applied " +
                    $"({ex.Message}). No solution association or publication was attempted; run 'ppds forms repair-bindings --dry-run' " +
                    "to see the form's current state.", ex);
            }

            reporter?.ReportPhase("Verifying", "Reading the unpublished form back");
            readBackRepairable = await VerifyRepairAsync(entityLogicalName, formId, resolvedName, plan, columnNames, ct);
        }
        else
        {
            reporter?.ReportPhase("Unchanged", "No control binding needed repair.");
        }

        // ── Phase 3: explicit side effects, each reported with the state already reached. ──
        var state = plan.Repaired.Count > 0
            ? $"{plan.Repaired.Count} binding(s) on form '{resolvedName}' were written and verified by reading the form back"
            : $"No binding on form '{resolvedName}' needed repair and nothing was written";
        await RunSideEffectAsync(request.SolutionUniqueName is not null, "solution association", FormErrorCodes.SolutionAssociationFailed, state,
            request.Publish ? " Publication was not attempted." : "",
            () => ApplySideEffectsAsync(formId, entityLogicalName, request.SolutionUniqueName, false, ct), ct);
        await RunSideEffectAsync(request.Publish, "publication", FormErrorCodes.PublicationFailed, state, "",
            () => ApplySideEffectsAsync(formId, entityLogicalName, null, true, ct), ct);

        return new(formId, resolvedName, false, plan.Repaired, plan.Unchanged, plan.Repaired.Count > 0, plan.Repaired.Count > 0,
            request.SolutionUniqueName is not null, request.Publish) { ReadBackRepairable = readBackRepairable };
    }

    private static async Task RunSideEffectAsync(bool requested, string step, string fallbackCode, string state, string tail,
        Func<Task> action, CancellationToken ct)
    {
        if (!requested) return;
        try
        {
            ct.ThrowIfCancellationRequested();
            await action();
        }
        catch (Exception ex)
        {
            var what = ex is OperationCanceledException && ct.IsCancellationRequested ? "was cancelled" : $"failed: {ex.Message}";
            throw new PpdsException(Classify(ex, ct, fallbackCode), $"{state}; the {step} then {what}.{tail}", ex);
        }
    }

    /// <summary>
    /// The error code for a failure: a <see cref="PpdsException"/> keeps its code; a user cancellation is
    /// <c>Operation.Cancelled</c>; a cancellation the caller did not request is a timeout; other exceptions keep the
    /// shared mapping (timeout, connection, throttling, authentication) and fall back to <paramref name="fallback"/>.
    /// </summary>
    private static string Classify(Exception ex, CancellationToken ct, string fallback) => ex switch
    {
        PpdsException ppds => ppds.ErrorCode,
        OperationCanceledException when ct.IsCancellationRequested => ErrorCodes.Operation.Cancelled,
        OperationCanceledException => ErrorCodes.Connection.Timeout,
        _ => ExceptionMapper.Map(ex).Code is var code && code != ErrorCodes.Operation.Internal ? code : fallback
    };

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
    /// Reads the unpublished form back by ID after the update and checks positively that every control this run rewrote is at
    /// its position with the same id and the new binding. Any failure, including cancellation, is
    /// <see cref="FormErrorCodes.RepairNotVerified"/> stating that the update was submitted; it runs before solution
    /// association and publication. Returns bindings the read-back shows as repairable that this run did not rewrite.
    /// </summary>
    private async Task<IReadOnlyList<RepairedBinding>> VerifyRepairAsync(string entityLogicalName, Guid formId, string formName,
        BindingRepairPlan plan, IReadOnlyCollection<string> columnNames, CancellationToken ct)
    {
        const string Next = " No solution association or publication was attempted; run 'ppds forms repair-bindings --dry-run' to see the form's current state.";
        string readBack;
        IReadOnlyList<LayoutBinding> bindings;
        try
        {
            var (_, xml, _, _, _, _, _) = await FetchFormRecordAsync(entityLogicalName, formId.ToString("D"),
                requireMainForm: true, ct, requireCustomizable: false);
            readBack = xml ?? string.Empty;
            bindings = FormXmlEditor.ReadLayoutBindings(readBack);
        }
        catch (Exception ex)
        {
            var what = ex is OperationCanceledException && ct.IsCancellationRequested
                ? "reading it back was cancelled"
                : $"the form could not be read back ({ex.Message})";
            throw new PpdsException(FormErrorCodes.RepairNotVerified,
                $"The repaired form XML of '{formName}' was submitted, but {what}, so the repair is not verified.{Next}", ex);
        }

        var problems = new List<string>();
        for (var i = 0; i < plan.Repaired.Count; i++)
        {
            var expected = plan.Repaired[i];
            var index = plan.RepairedLayoutIndexes[i];
            var found = index < bindings.Count && string.Equals(bindings[index].ControlId, expected.ControlId, StringComparison.Ordinal)
                ? bindings[index] : null;
            if (found is null)
                problems.Add($"control '{expected.ControlId ?? "(no id)"}' ({expected.Location}) is missing");
            else if (!string.Equals(found.Binding, expected.NewBinding, StringComparison.Ordinal))
                problems.Add($"control '{expected.ControlId ?? "(no id)"}' ({expected.Location}) is bound to '{found.Binding}', not '{expected.NewBinding}'");
        }
        if (problems.Count > 0)
            throw new PpdsException(FormErrorCodes.RepairNotVerified,
                $"The repaired form XML of '{formName}' was submitted, but the form read back does not show {problems.Count} of the " +
                $"{plan.Repaired.Count} rewritten binding(s): {string.Join("; ", problems)}.{Next}");

        try
        {
            return FormXmlEditor.PlanBindingRepair(readBack, columnNames).Repaired;
        }
        catch (PpdsException)
        {
            // The rewritten bindings are verified; a read-back the planner cannot scan only loses the follow-up report.
            return [];
        }
    }
}
