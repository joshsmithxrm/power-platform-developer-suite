using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.Flows;

/// <summary>Plans and executes bounded, verified cloud-flow state changes.</summary>
public interface IFlowActivationService
{
    /// <summary>Builds a write-free plan, including dependency diagnostics.</summary>
    Task<FlowActivationResult> PlanAsync(FlowActivationRequest request,
        IProgressReporter? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Executes a fresh plan, stopping on the first uncertain or failed operation.</summary>
    Task<FlowActivationResult> ExecuteAsync(FlowActivationRequest request,
        IProgressReporter? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Exact flow selection and requested transition.</summary>
public sealed record FlowActivationRequest
{
    /// <summary>Single workflow ID.</summary>
    public Guid? Id { get; init; }
    /// <summary>Single schema/unique name, never a display-name match.</summary>
    public string? Name { get; init; }
    /// <summary>Select all eligible definitions in the named solution.</summary>
    public bool All { get; init; }
    /// <summary>Solution unique name or optional single-flow membership constraint.</summary>
    public string? Solution { get; init; }
    /// <summary>Draft (off) or Activated (on).</summary>
    public FlowState TargetState { get; init; }
    /// <summary>Maximum eligible flows; exceeding this fails without truncation.</summary>
    public int MaxFlows { get; init; } = 100;
    /// <summary>Bounded readback verification deadline per changed flow.</summary>
    public TimeSpan VerificationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Optional hash returned by an earlier dry run.</summary>
    public string? ExpectedPlanHash { get; init; }
    /// <summary>Explicit acknowledgement of the resolved environment URL.</summary>
    public string? ConfirmEnvironment { get; init; }
}

/// <summary>Safe connection-reference diagnostics, excluding credentials and client data.</summary>
public sealed record FlowReferenceDiagnostic(string LogicalName, string Status,
    string? ConnectionId = null, string? ConnectorId = null);

/// <summary>An entry deliberately excluded from a solution batch.</summary>
public sealed record ExcludedFlow(Guid Id, string UniqueName, int Category, int? DefinitionType, string Reason);

/// <summary>Outcome of one eligible flow, including uncertain submissions.</summary>
public sealed record FlowActivationItem
{
    /// <summary>Workflow ID.</summary>
    public required Guid Id { get; init; }
    /// <summary>Schema name.</summary>
    public required string UniqueName { get; init; }
    /// <summary>State at planning time.</summary>
    public required FlowState InitialState { get; init; }
    /// <summary>Requested terminal state.</summary>
    public required FlowState RequestedState { get; init; }
    /// <summary>Most recently observed state, if available.</summary>
    public FlowState? ObservedState { get; init; }
    /// <summary>Machine-readable outcome.</summary>
    public required string Status { get; init; }
    /// <summary>Last phase reached.</summary>
    public string Phase { get; init; } = "preflight";
    /// <summary>Structured failure code, if any.</summary>
    public string? ErrorCode { get; init; }
    /// <summary>Safe actionable failure detail.</summary>
    public string? Message { get; init; }
    /// <summary>Binding existence diagnostics, not a runtime health assertion.</summary>
    public IReadOnlyList<FlowReferenceDiagnostic> References { get; init; } = [];
}

/// <summary>Complete plan or execution result; retained on partial failure.</summary>
public sealed record FlowActivationResult
{
    /// <summary>Actual captured environment.</summary>
    public required string EnvironmentUrl { get; init; }
    /// <summary>Requested solution, if any.</summary>
    public string? Solution { get; init; }
    /// <summary>Whether no writes were requested.</summary>
    public bool DryRun { get; init; }
    /// <summary>Requested state.</summary>
    public FlowState RequestedState { get; init; }
    /// <summary>Deterministic fingerprint of the fresh plan.</summary>
    public required string PlanHash { get; init; }
    /// <summary>All eligible entries, including not-attempted outcomes.</summary>
    public required IReadOnlyList<FlowActivationItem> Flows { get; init; }
    /// <summary>Entries excluded from mutation, with reasons.</summary>
    public required IReadOnlyList<ExcludedFlow> Excluded { get; init; }
    /// <summary>Preflight/execution-level error code.</summary>
    public string? ErrorCode { get; init; }
    /// <summary>Preflight/execution-level error detail.</summary>
    public string? Message { get; init; }
    /// <summary>Selection count before eligibility filtering.</summary>
    public int TotalCount => Flows.Count + Excluded.Count;
    /// <summary>Eligible count; never silently capped.</summary>
    public int EligibleCount => Flows.Count;
    /// <summary>Excluded count.</summary>
    public int ExcludedCount => Excluded.Count;
    /// <summary>Counts by machine-readable outcome.</summary>
    public IReadOnlyDictionary<string, int> Summary => Flows.GroupBy(f => f.Status)
        .ToDictionary(g => g.Key, g => g.Count());
    /// <summary>True only when all requested work is complete or a dry run is unblocked.</summary>
    public bool Success => ErrorCode is null && Flows.All(f =>
        f.Status is "succeeded" or "unchanged" || (DryRun && f.Status == "would-change"));
    /// <summary>Limits of preflight diagnostics.</summary>
    public string[] Warnings { get; init; } =
    [
        "Connection bindings do not verify credentials, permissions, licensing, DLP, child-flow dependencies, or runtime connector health.",
        "State changes are not atomic; stopping or disabling does not undo runs already triggered."
    ];
}
