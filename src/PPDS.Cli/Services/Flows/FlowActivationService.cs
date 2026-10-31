using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.Environment;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Generated;

namespace PPDS.Cli.Services.Flows;

/// <summary>Plans and executes solution cloud-flow state transitions with honest partial outcomes.</summary>
public sealed class FlowActivationService : IFlowActivationService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IFlowService _flows;
    private readonly IConnectionReferenceService _references;
    private readonly IEnvironmentConfirmationService _confirmation;
    private readonly ILogger<FlowActivationService> _logger;
    private readonly TimeProvider _time;

    /// <summary>Creates an activation coordinator using the same target-bound readers and pool.</summary>
    public FlowActivationService(IDataverseConnectionPool pool, IFlowService flows,
        IConnectionReferenceService references, IEnvironmentConfirmationService confirmation,
        ILogger<FlowActivationService> logger, TimeProvider? timeProvider = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _flows = flows ?? throw new ArgumentNullException(nameof(flows));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<FlowActivationResult> PlanAsync(FlowActivationRequest request,
        IProgressReporter? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateRequest(request);
            progress ??= NullProgressReporter.Instance;
            progress.ReportPhase("Planning", "Resolving flow selection and connection references");
            if (request.Solution is not null)
                await RequireSolutionAsync(request.Solution, cancellationToken);

            IReadOnlyList<FlowInfo> selected;
            if (request.All)
                selected = (await _flows.ListAsync(request.Solution, includeClassic: true,
                    cancellationToken: cancellationToken)).Items.DistinctBy(f => f.Id).OrderBy(f => f.Id).ToList();
            else
            {
                var flow = request.Id is { } id
                    ? await _flows.GetByIdAsync(id, cancellationToken)
                    : await _flows.GetAsync(request.Name!, cancellationToken);
                if (flow is null)
                    throw new PpdsNotFoundException("Cloud flow", request.Id?.ToString() ?? request.Name!);
                if (!IsEligible(flow))
                    throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                        "Only modern cloud-flow definitions (category 5, type 1) can be enabled or disabled.");
                if (!await IsSolutionMemberAsync(flow.Id, request.Solution, cancellationToken))
                    throw new PpdsException("Flow.SolutionMembershipRequired",
                        request.Solution is null ? "The flow must be a solution component."
                        : $"Flow '{flow.Id}' is not a component of solution '{request.Solution}'.");
                selected = [flow];
            }

            var eligible = selected.Where(IsEligible).OrderBy(f => f.Id).ToList();
            var excluded = selected.Where(f => !IsEligible(f)).Select(f =>
                new ExcludedFlow(f.Id, f.UniqueName, (int)f.Category, f.DefinitionType,
                    "Only modern cloud-flow definitions (category 5, type 1) are eligible.")).ToList();
            var items = new List<FlowActivationItem>();
            var overLimit = eligible.Count > request.MaxFlows;
            foreach (var flow in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(overLimit ? BasicItem(flow, request.TargetState)
                    : await InspectAsync(flow, request.TargetState, cancellationToken));
                progress.ReportProgress(new ProgressSnapshot
                {
                    CurrentItem = items.Count, TotalItems = eligible.Count,
                    CurrentEntity = flow.UniqueName, StatusMessage = "Checking activation preconditions"
                });
            }

            var result = new FlowActivationResult
            {
                EnvironmentUrl = _confirmation.Context.EnvironmentUrl,
                Solution = request.Solution, RequestedState = request.TargetState,
                DryRun = true, PlanHash = string.Empty, Flows = items, Excluded = excluded,
                ErrorCode = overLimit ? "Flow.BatchLimitExceeded"
                    : items.Any(i => i.Status == "blocked") ? "Flow.PreflightBlocked" : null,
                Message = overLimit
                    ? $"Selected {eligible.Count} eligible flows, exceeding --max-flows {request.MaxFlows}. No writes were made; raise the explicit limit or narrow the selection."
                    : items.Any(i => i.Status == "blocked")
                        ? "Activation preflight is blocked. Inspect per-flow diagnostics and run 'ppds connection-references analyze'." : null
            };
            return result with { PlanHash = HashPlan(result, request) };
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException("Flow.PreflightFailed", "Could not inspect flow activation preconditions.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<FlowActivationResult> ExecuteAsync(FlowActivationRequest request,
        IProgressReporter? progress = null, CancellationToken cancellationToken = default)
    {
        progress ??= NullProgressReporter.Instance;
        // This entry point is mutating even if the fresh selection happens to be unchanged.
        if (_confirmation.Context.IsReadOnly)
            throw new PpdsException("Flow.ReadOnly", "Flow state changes are not allowed in a read-only session. Use dry run.");
        var plan = await PlanAsync(request, progress, cancellationToken);
        var items = plan.Flows.Select(f => f.Status == "would-change" ? f with { Status = "not-attempted" } : f).ToList();
        var result = plan with { DryRun = false, Flows = items };
        if (plan.ErrorCode is not null) return result;
        if (request.ExpectedPlanHash is not null &&
            !string.Equals(request.ExpectedPlanHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
            return result with { ErrorCode = "Flow.PlanChanged", Message = "The target or plan changed. Review a fresh dry run before executing." };

        var hasChanges = items.Any(f => f.Status == "not-attempted");
        if (hasChanges || request.ConfirmEnvironment is not null)
            await _confirmation.EnsureConfirmedAsync("flows.state-change", request.ConfirmEnvironment, cancellationToken);
        if (!hasChanges) return result;

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Status == "unchanged") continue;
            var planned = plan.Flows[index];
            var submitted = false;
            var writeAccepted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.ReportPhase("Changing flow state", planned.UniqueName);
                var current = await _flows.GetByIdAsync(planned.Id, cancellationToken)
                    ?? throw new PpdsNotFoundException("Cloud flow", planned.Id.ToString());
                if (!IsEligible(current) || !await IsSolutionMemberAsync(current.Id, request.Solution, cancellationToken))
                    throw new PpdsException("Flow.PreconditionChanged", "Flow eligibility or solution membership changed after planning.");
                var inspected = await InspectAsync(current, request.TargetState, cancellationToken);
                if (inspected.Status == "unchanged")
                {
                    items[index] = inspected;
                    continue;
                }
                if (inspected.Status == "blocked" || HashItem(inspected) != HashItem(planned))
                    throw new PpdsException("Flow.PreconditionChanged", inspected.Message
                        ?? "Flow state or connection bindings changed after planning. Review a fresh dry run.");

                // Acquire before marking submitted: cancellation while waiting for a lease cannot have written.
                await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // State transitions must stop and verify individually; bulk submission would
                    // violate the batch's fail-stop contract and obscure uncertain trigger effects.
                    var update = new Entity(Workflow.EntityLogicalName, planned.Id)
                    {
                        [Workflow.Fields.StateCode] = new OptionSetValue((int)request.TargetState),
                        [Workflow.Fields.StatusCode] = new OptionSetValue(request.TargetState == FlowState.Activated ? 2 : 1)
                    };
                    submitted = true;
                    await client.UpdateAsync(update, cancellationToken);
                    writeAccepted = true;
                }
                items[index] = await VerifyAsync(planned, request, cancellationToken);
                if (items[index].Status != "succeeded") break;
                progress.ReportProgress(new ProgressSnapshot
                {
                    CurrentItem = index + 1, TotalItems = items.Count,
                    CurrentEntity = planned.UniqueName, StatusMessage = "Requested state verified"
                });
            }
            catch (OperationCanceledException)
            {
                items[index] = planned with
                {
                    Status = submitted ? "unknown" : "cancelled",
                    Phase = writeAccepted ? "verification" : submitted ? "submission" : "precondition",
                    ErrorCode = "Flow.Cancelled",
                    Message = submitted ? "Cancelled after submission; inspect the flow before retrying."
                        : "Cancelled before submitting this flow's change."
                };
                break;
            }
            catch (Exception ex)
            {
                var knownRejection = submitted && !writeAccepted &&
                    ex is System.ServiceModel.FaultException<OrganizationServiceFault>;
                var uncertain = submitted && !knownRejection;
                var wrapped = ex as PpdsException ?? new PpdsException(
                    uncertain ? "Flow.OutcomeUnknown" : "Flow.StateChangeFailed",
                    knownRejection ? $"Dataverse rejected the state change: {ex.Message}"
                        : "Could not complete the flow state change. Inspect the flow before retrying.", ex);
                items[index] = planned with
                {
                    Status = uncertain ? "unknown" : "failed",
                    Phase = writeAccepted ? "verification" : submitted ? "submission" : "precondition",
                    ErrorCode = wrapped.ErrorCode, Message = wrapped.UserMessage
                };
                _logger.LogWarning("Flow {FlowId} stopped in phase {Phase} with code {Code}",
                    planned.Id, items[index].Phase, wrapped.ErrorCode);
                break;
            }
        }
        return result;
    }

    internal static void ValidateRequest(FlowActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selectorCount = (request.Id.HasValue ? 1 : 0) + (request.Name is not null ? 1 : 0) + (request.All ? 1 : 0);
        if (selectorCount != 1 || request.Id == Guid.Empty || request.Name is { } name && string.IsNullOrWhiteSpace(name))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "Specify exactly one of --id, --name, or --all.");
        if (request.All && string.IsNullOrWhiteSpace(request.Solution) ||
            request.Solution is not null && string.IsNullOrWhiteSpace(request.Solution))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "--all requires a solution unique name.");
        if (request.TargetState is not (FlowState.Draft or FlowState.Activated))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "Requested state must be Draft or Activated.");
        if (request.MaxFlows < 1 || request.VerificationTimeout < TimeSpan.FromSeconds(1) ||
            request.VerificationTimeout > TimeSpan.FromHours(1))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "--max-flows must be positive and --timeout must be between 1 and 3600 seconds.");
        if (request.ExpectedPlanHash is not null &&
            (request.ExpectedPlanHash.Length != 64 || !request.ExpectedPlanHash.All(Uri.IsHexDigit)))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "--expected-plan-hash must be the 64-character hash from dry run.");
    }

    private async Task<FlowActivationItem> InspectAsync(FlowInfo flow, FlowState target, CancellationToken ct)
    {
        var item = BasicItem(flow, target);
        if (flow.State is not (FlowState.Draft or FlowState.Activated or FlowState.Suspended))
            return item with { Status = "blocked", ErrorCode = "Flow.StateUnverified",
                Message = "The current flow state is missing or unsupported. No transition can be planned safely." };
        if (item.Status == "unchanged" || target == FlowState.Draft) return item;
        if (!flow.ConnectionReferencesVerified)
            return item with { Status = "blocked", ErrorCode = "Flow.DependenciesUnverified",
                Message = flow.ConnectionReferenceParseError ?? "Flow dependency definition could not be verified." };
        var references = new List<FlowReferenceDiagnostic>();
        foreach (var name in flow.ConnectionReferenceLogicalNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            ConnectionReferenceInfo? reference;
            try { reference = await _references.GetAsync(name, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                references.Add(new FlowReferenceDiagnostic(name, "unreadable"));
                continue;
            }
            references.Add(new FlowReferenceDiagnostic(name,
                reference is null ? "missing" : reference.IsActive is null ? "state-unknown"
                : reference.IsActive == false ? "inactive" : !reference.IsBound ? "unbound" : "bound",
                reference?.ConnectionId, reference?.ConnectorId));
        }
        var blocked = references.Any(r => r.Status != "bound");
        return item with { References = references, Status = blocked ? "blocked" : item.Status,
            ErrorCode = references.Any(r => r.Status == "unreadable") ? "Flow.DependenciesUnverified"
                : blocked ? "Flow.ConnectionReferenceBlocked" : null,
            Message = blocked ? "Required connection references are missing, inactive, unbound, or unverified. Run 'ppds connection-references analyze'." : null };
    }

    private async Task<FlowActivationItem> VerifyAsync(FlowActivationItem planned,
        FlowActivationRequest request, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        FlowState? observed = null;
        using var deadline = new CancellationTokenSource(request.VerificationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var flow = await _flows.GetByIdAsync(planned.Id, linked.Token);
                observed = flow?.State;
                linked.Token.ThrowIfCancellationRequested();
                if (observed == request.TargetState)
                    return planned with { Status = "succeeded", Phase = "verified", ObservedState = observed };
                var remaining = request.VerificationTimeout - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), _time, linked.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested) { }
        return planned with { Status = "unknown", Phase = "verification", ObservedState = observed,
            ErrorCode = "Flow.VerificationTimeout",
            Message = $"Requested state was not verified within {request.VerificationTimeout.TotalSeconds:g} seconds. Inspect before retrying." };
    }

    private async Task RequireSolutionAsync(string uniqueName, CancellationToken ct)
    {
        var query = new QueryExpression(Solution.EntityLogicalName)
            { ColumnSet = new ColumnSet(Solution.Fields.SolutionId), TopCount = 2 };
        query.Criteria.AddCondition(Solution.Fields.UniqueName, ConditionOperator.Equal, uniqueName);
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        var result = await client.RetrieveMultipleAsync(query, ct);
        if (result.Entities.Count == 0) throw new PpdsNotFoundException("Solution", uniqueName);
        if (result.Entities.Count != 1)
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, $"Solution name '{uniqueName}' is ambiguous.");
    }

    private async Task<bool> IsSolutionMemberAsync(Guid id, string? solution, CancellationToken ct)
    {
        var query = new QueryExpression(SolutionComponent.EntityLogicalName)
            { ColumnSet = new ColumnSet(SolutionComponent.Fields.ObjectId), TopCount = 1 };
        query.Criteria.AddCondition(SolutionComponent.Fields.ObjectId, ConditionOperator.Equal, id);
        query.Criteria.AddCondition(SolutionComponent.Fields.ComponentType, ConditionOperator.Equal, 29);
        if (solution is not null)
            query.AddLink(Solution.EntityLogicalName, SolutionComponent.Fields.SolutionId, Solution.Fields.SolutionId)
                .LinkCriteria.AddCondition(Solution.Fields.UniqueName, ConditionOperator.Equal, solution);
        await using var client = await _pool.GetClientAsync(cancellationToken: ct);
        return (await client.RetrieveMultipleAsync(query, ct)).Entities.Count > 0;
    }

    private static bool IsEligible(FlowInfo flow) => flow.Category == FlowCategory.ModernFlow && flow.DefinitionType == 1;

    private static FlowActivationItem BasicItem(FlowInfo flow, FlowState target) => new()
    {
        Id = flow.Id, UniqueName = flow.UniqueName, InitialState = flow.State,
        RequestedState = target, ObservedState = flow.State,
        Status = flow.State == target ? "unchanged" : "would-change"
    };

    private string HashPlan(FlowActivationResult result, FlowActivationRequest request) => Hash(new
    {
        result.EnvironmentUrl, _confirmation.Context.ProfileIdentity, request.Id, request.Name,
        request.All, request.Solution, request.TargetState,
        Flows = result.Flows.Select(f => HashItem(f)), result.Excluded
    });

    private static string HashItem(FlowActivationItem item) => Hash(new
    {
        item.Id, item.UniqueName, item.InitialState, item.RequestedState, item.References, item.ErrorCode
    });

    private static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
