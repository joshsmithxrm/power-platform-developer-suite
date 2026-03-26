namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ExecutionPlanPreviewDialog for testing.
/// </summary>
/// <param name="TierCount">Number of tiers in the execution plan.</param>
/// <param name="EntityCount">Number of entities in the execution plan.</param>
/// <param name="DeferredFieldCount">Number of deferred fields.</param>
/// <param name="StateTransitionCount">Number of state transitions.</param>
/// <param name="ManyToManyRelationshipCount">Number of many-to-many relationships.</param>
/// <param name="IsApproved">Whether the plan was approved.</param>
public sealed record ExecutionPlanPreviewDialogState(
    int TierCount,
    int EntityCount,
    int DeferredFieldCount,
    int StateTransitionCount,
    int ManyToManyRelationshipCount,
    bool IsApproved);
