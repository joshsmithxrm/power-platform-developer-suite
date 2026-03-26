using System;
using System.Linq;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Migration.Models;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// Modal dialog that displays an execution plan summary before import.
/// Shows tier ordering, deferred field count, state transition count,
/// and M2M relationship count. The user can approve or cancel.
/// </summary>
internal sealed class ExecutionPlanPreviewDialog : Dialog, ITuiStateCapture<ExecutionPlanPreviewDialogState>
{
    private readonly ExecutionPlan _plan;
    private readonly int _stateTransitionCount;

    /// <summary>
    /// Gets whether the user approved the plan (clicked OK).
    /// </summary>
    public bool IsApproved { get; private set; }

    /// <summary>
    /// Creates a new execution plan preview dialog.
    /// </summary>
    /// <param name="plan">The execution plan to display.</param>
    /// <param name="stateTransitionCount">Number of state transitions to process.</param>
    public ExecutionPlanPreviewDialog(ExecutionPlan plan, int stateTransitionCount = 0)
        : base("Execution Plan Preview")
    {
        ArgumentNullException.ThrowIfNull(plan);

        _plan = plan;
        _stateTransitionCount = stateTransitionCount;

        Width = Dim.Percent(70);
        Height = Dim.Percent(70);

        var y = 0;

        // Summary section
        var summaryLabel = new Label
        {
            X = 1,
            Y = y++,
            Text = $"Tiers: {plan.TierCount}    Deferred fields: {plan.DeferredFieldCount}    " +
                   $"State transitions: {stateTransitionCount}    M2M relationships: {plan.ManyToManyRelationships.Count}"
        };
        Add(summaryLabel);

        y++; // blank line

        // Tier details
        var tierHeaderLabel = new Label
        {
            X = 1,
            Y = y++,
            Text = "--- Tier Ordering ---"
        };
        Add(tierHeaderLabel);

        foreach (var tier in plan.Tiers)
        {
            var entities = string.Join(", ", tier.Entities);
            var circular = tier.HasCircularReferences ? " [circular]" : string.Empty;
            var tierLabel = new Label
            {
                X = 1,
                Y = y++,
                Width = Dim.Fill(1),
                Text = $"  Tier {tier.TierNumber}: {entities}{circular}"
            };
            Add(tierLabel);
        }

        // Deferred fields
        if (plan.DeferredFieldCount > 0)
        {
            y++; // blank line
            var deferredHeaderLabel = new Label
            {
                X = 1,
                Y = y++,
                Text = "--- Deferred Fields ---"
            };
            Add(deferredHeaderLabel);

            foreach (var (entity, fields) in plan.DeferredFields)
            {
                var fieldList = string.Join(", ", fields);
                var deferredLabel = new Label
                {
                    X = 1,
                    Y = y++,
                    Width = Dim.Fill(1),
                    Text = $"  {entity}: {fieldList}"
                };
                Add(deferredLabel);
            }
        }

        // M2M relationships
        if (plan.ManyToManyRelationships.Count > 0)
        {
            y++; // blank line
            var m2mHeaderLabel = new Label
            {
                X = 1,
                Y = y++,
                Text = "--- Many-to-Many Relationships ---"
            };
            Add(m2mHeaderLabel);

            foreach (var rel in plan.ManyToManyRelationships)
            {
                var relLabel = new Label
                {
                    X = 1,
                    Y = y++,
                    Width = Dim.Fill(1),
                    Text = $"  {rel.Name}: {rel.Entity1} <-> {rel.Entity2}"
                };
                Add(relLabel);
            }
        }

        // Buttons
        var okButton = new Button("_Proceed", is_default: true);
        okButton.Clicked += () =>
        {
            IsApproved = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel");
        cancelButton.Clicked += () =>
        {
            IsApproved = false;
            Application.RequestStop();
        };

        AddButton(okButton);
        AddButton(cancelButton);
    }

    public ExecutionPlanPreviewDialogState CaptureState() => new(
        TierCount: _plan.TierCount,
        EntityCount: _plan.Tiers.Sum(t => t.Entities.Count),
        DeferredFieldCount: _plan.DeferredFieldCount,
        StateTransitionCount: _stateTransitionCount,
        ManyToManyRelationshipCount: _plan.ManyToManyRelationships.Count,
        IsApproved: IsApproved);
}
