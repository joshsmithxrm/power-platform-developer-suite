using PPDS.Cli.Tui.Testing.States;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class ExecutionPlanPreviewDialogStateTests
{
    [Fact]
    public void CapturesPlanSummary()
    {
        var state = new ExecutionPlanPreviewDialogState(
            TierCount: 3,
            EntityCount: 8,
            DeferredFieldCount: 2,
            StateTransitionCount: 5,
            ManyToManyRelationshipCount: 1,
            IsApproved: false);

        Assert.Equal(3, state.TierCount);
        Assert.Equal(8, state.EntityCount);
        Assert.Equal(2, state.DeferredFieldCount);
        Assert.Equal(5, state.StateTransitionCount);
        Assert.Equal(1, state.ManyToManyRelationshipCount);
        Assert.False(state.IsApproved);
    }

    [Fact]
    public void CapturesApprovedState()
    {
        var state = new ExecutionPlanPreviewDialogState(
            TierCount: 1,
            EntityCount: 2,
            DeferredFieldCount: 0,
            StateTransitionCount: 0,
            ManyToManyRelationshipCount: 0,
            IsApproved: true);

        Assert.True(state.IsApproved);
        Assert.Equal(1, state.TierCount);
    }

    [Fact]
    public void RecordEquality()
    {
        var state1 = new ExecutionPlanPreviewDialogState(3, 8, 2, 5, 1, false);
        var state2 = new ExecutionPlanPreviewDialogState(3, 8, 2, 5, 1, false);

        Assert.Equal(state1, state2);
    }
}
