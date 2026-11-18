using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;
using PPDS.Dataverse.Resilience;

namespace PPDS.Cli.Services.Forms;

public sealed partial class FormService
{
    // Service-protection fault codes (the pooled client's throttle detector rethrows these faults unchanged).
    private static readonly int[] ServiceProtectionFaultCodes = [-2147015902, -2147015903, -2147015898];

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
                $"Could not plan the binding repair on form '{request.FormName}': {Describe(ex, ct)} Nothing was written.", ex);
        }

        if (request.DryRun)
        {
            reporter?.ReportPhase("Planned", $"{plan.Repaired.Count} binding(s) would be repaired; nothing was written.");
            return new(formId, resolvedName, true, plan.Repaired, plan.Unchanged, false, false, false, false);
        }

        // ── Phase 2: submit and verify. ──
        var repaired = plan.Repaired;
        IReadOnlyList<RepairedBinding> readBackRepairable = [];
        var scanComplete = true;
        if (plan.Repaired.Count > 0)
        {
            reporter?.ReportPhase("Writing", $"Repairing {plan.Repaired.Count} binding(s)");
            ct.ThrowIfCancellationRequested(); // last point at which a cancellation leaves the form untouched
            try
            {
                // The stored text with only the repaired attribute values replaced, sent verbatim.
                await SubmitFormXmlAsync(plan.RepairedXml, formId, ct);
            }
            catch (Exception ex) when (IsServerRejection(ex))
            {
                // The organisation service answered with a fault: this request did not change the form.
                throw new PpdsException(ClassifyRejection(ex),
                    $"Dataverse rejected the update of form '{resolvedName}' ({DescribeFault(ex)}); this request did not change the form. " +
                    "No solution association or publication was attempted.", ex);
            }
            catch (Exception ex)
            {
                throw new PpdsException(Classify(ex, ct, FormErrorCodes.RepairWriteOutcomeUnknown),
                    $"The update of form '{resolvedName}' did not complete and its outcome is unknown: it may or may not have been applied " +
                    $"({Describe(ex, ct)}). No solution association or publication was attempted; run 'ppds forms repair-bindings --dry-run' " +
                    "to see the form's current state.", ex);
            }

            reporter?.ReportPhase("Verifying", "Reading the unpublished form back");
            (repaired, readBackRepairable, scanComplete) = await VerifyRepairAsync(entityLogicalName, formId, resolvedName, plan, columnNames, ct);
        }
        else
        {
            reporter?.ReportPhase("Unchanged", "No control binding needed repair.");
        }

        // ── Phase 3: explicit side effects, each reported with the state already reached. ──
        var completed = new List<string>
        {
            plan.Repaired.Count > 0
                ? $"{plan.Repaired.Count} binding(s) on form '{resolvedName}' were written and verified by reading the form back"
                : $"No binding on form '{resolvedName}' needed repair and nothing was written"
        };
        await RunSideEffectAsync(request.SolutionUniqueName is not null, "solution association", FormErrorCodes.SolutionAssociationFailed,
            completed, request.Publish ? " Publication was not attempted." : "",
            () => ApplySideEffectsAsync(formId, entityLogicalName, request.SolutionUniqueName, false, ct),
            $"the solution association to '{request.SolutionUniqueName}' completed", ct);
        await RunSideEffectAsync(request.Publish, "publication", FormErrorCodes.PublicationFailed, completed, "",
            () => ApplySideEffectsAsync(formId, entityLogicalName, null, true, ct), "the publication completed", ct);

        return new(formId, resolvedName, false, repaired, plan.Unchanged, plan.Repaired.Count > 0, plan.Repaired.Count > 0,
            request.SolutionUniqueName is not null, request.Publish)
        { ReadBackRepairable = readBackRepairable, ReadBackScanComplete = scanComplete };
    }

    private static async Task RunSideEffectAsync(bool requested, string step, string fallbackCode, List<string> completed, string tail,
        Func<Task> action, string completedText, CancellationToken ct)
    {
        if (!requested) return;
        var state = string.Join("; ", completed);
        if (ct.IsCancellationRequested)
            throw new PpdsException(ErrorCodes.Operation.Cancelled, $"{state}; the {step} was cancelled before it was sent.{tail}");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var code = Classify(ex, ct, fallbackCode);
            // A timeout or cancellation after the request was sent leaves that step's outcome unknown, as for the update.
            var unknown = ex is OperationCanceledException or TimeoutException || code == ErrorCodes.Connection.Timeout;
            var what = unknown
                ? $"did not complete and its outcome is unknown ({Describe(ex, ct)})"
                : $"failed: {Describe(ex, ct)}";
            throw new PpdsException(code, $"{state}; the {step} then {what}.{tail}", ex);
        }
        completed.Add(completedText);
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
        FaultException<OrganizationServiceFault> fault when ServiceProtectionFaultCodes.Contains(fault.Detail?.ErrorCode ?? 0)
            => ErrorCodes.Connection.Throttled,
        _ => ExceptionMapper.Map(ex).Code is var code && code != ErrorCodes.Operation.Internal ? code : fallback
    };

    /// <summary>The exception's message, except that a cancellation the caller did not request is described as such.</summary>
    private static string Describe(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && !ct.IsCancellationRequested
            ? "the request was cancelled without a cancellation request from the caller, so it is treated as a timeout"
            : ex.Message;

    /// <summary>The organisation service answered: an organisation-service fault, or an authentication or service-protection
    /// failure the pooled client reports for an answered request.</summary>
    private static bool IsServerRejection(Exception ex)
        => ex is FaultException<OrganizationServiceFault> or DataverseAuthenticationException or ServiceProtectionException;

    private static string ClassifyRejection(Exception ex) => ex switch
    {
        FaultException<OrganizationServiceFault> fault when ServiceProtectionFaultCodes.Contains(fault.Detail?.ErrorCode ?? 0)
            => ErrorCodes.Connection.Throttled,
        FaultException<OrganizationServiceFault> => FormErrorCodes.RepairWriteRejected,
        _ => ExceptionMapper.Map(ex).Code
    };

    private static string DescribeFault(Exception ex) => ex is FaultException<OrganizationServiceFault> fault && fault.Detail is { } detail
        ? $"fault 0x{unchecked((uint)detail.ErrorCode):X8}: {detail.Message ?? fault.Message}"
        : ex.Message;

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
    /// Reads the unpublished form back by ID after the update and checks each control this run rewrote: by its id and container
    /// when the id was present and unique on the layout, otherwise by its position among the layout-bound controls and its
    /// container. Any failure, including cancellation, is <see cref="FormErrorCodes.RepairNotVerified"/> stating that the update
    /// was submitted; it runs before solution association and publication. Returns the verified entries, bindings the read-back
    /// shows as repairable that this run did not rewrite, and whether that scan completed.
    /// </summary>
    private async Task<(IReadOnlyList<RepairedBinding> Verified, IReadOnlyList<RepairedBinding> Repairable, bool ScanComplete)> VerifyRepairAsync(
        string entityLogicalName, Guid formId, string formName, BindingRepairPlan plan, IReadOnlyCollection<string> columnNames, CancellationToken ct)
    {
        const string Next = " No solution association or publication was attempted; run 'ppds forms repair-bindings --dry-run' to see the form's current state.";
        var submitted = $"The repaired form XML of '{formName}' was submitted, but";
        string readBack;
        try
        {
            var (_, xml, _, _, _, _, _) = await FetchFormRecordAsync(entityLogicalName, formId.ToString("D"),
                requireMainForm: true, ct, requireCustomizable: false);
            readBack = xml ?? string.Empty;
        }
        catch (Exception ex)
        {
            var what = ex is OperationCanceledException && ct.IsCancellationRequested
                ? "reading it back was cancelled"
                : $"the form could not be read back ({Describe(ex, ct)})";
            throw new PpdsException(FormErrorCodes.RepairNotVerified, $"{submitted} {what}, so the repair is not verified.{Next}", ex);
        }

        IReadOnlyList<LayoutBinding> bindings;
        try
        {
            bindings = FormXmlEditor.ReadLayoutBindings(readBack);
        }
        catch (Exception ex)
        {
            throw new PpdsException(FormErrorCodes.RepairNotVerified,
                $"{submitted} the form XML read back is empty or could not be parsed ({ex.Message}), so the repair is not verified.{Next}", ex);
        }

        var problems = new List<string>();
        var verified = new List<RepairedBinding>();
        for (var i = 0; i < plan.Repaired.Count; i++)
        {
            var expected = plan.Repaired[i];
            var target = plan.Targets[i];
            var name = $"control '{expected.ControlId ?? "(no id)"}' ({expected.Location})";
            LayoutBinding? found = null;
            var by = BindingVerification.ByPosition;
            var byId = target.IdIsUnique
                ? bindings.Where(b => string.Equals(b.ControlId, expected.ControlId, StringComparison.Ordinal)).ToList()
                : null;
            if (byId is { Count: 0 })
            {
                problems.Add($"{name} is missing");
                continue;
            }
            if (byId is { Count: 1 })
            {
                by = BindingVerification.ById;
                if (byId[0].Container != target.Container)
                {
                    problems.Add($"{name} was found in a different place");
                    continue;
                }
                found = byId[0];
            }
            else
            {
                // Id absent or repeated: match by position among the layout-bound controls, id and container.
                var index = target.LayoutIndex;
                found = index < bindings.Count && string.Equals(bindings[index].ControlId, expected.ControlId, StringComparison.Ordinal)
                    && bindings[index].Container == target.Container ? bindings[index] : null;
                if (found is null)
                {
                    problems.Add($"{name} was not found at its expected position");
                    continue;
                }
            }
            if (!string.Equals(found.Binding, expected.NewBinding, StringComparison.Ordinal))
            {
                problems.Add($"{name} is bound to '{found.Binding}', not '{expected.NewBinding}'");
                continue;
            }
            verified.Add(expected with { VerifiedBy = by });
        }
        if (problems.Count > 0)
            throw new PpdsException(FormErrorCodes.RepairNotVerified,
                $"{submitted} the form read back does not show {problems.Count} of the {plan.Repaired.Count} rewritten binding(s): " +
                $"{string.Join("; ", problems)}.{Next}");

        try
        {
            return (verified, FormXmlEditor.PlanBindingRepair(readBack, columnNames).Repaired, true);
        }
        catch (Exception)
        {
            // The rewritten bindings are verified; only the follow-up scan is lost, and the result says so.
            return (verified, [], false);
        }
    }
}
