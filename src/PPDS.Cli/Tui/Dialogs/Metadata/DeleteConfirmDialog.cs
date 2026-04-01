using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Simple confirmation dialog for delete operations, with optional dependency summary.
/// </summary>
internal sealed class DeleteConfirmDialog : TuiDialog
{
    private bool _confirmed;

    /// <summary>
    /// Gets whether the user confirmed the deletion.
    /// </summary>
    public bool Confirmed => _confirmed;

    public DeleteConfirmDialog(string itemType, string itemName, string? dependencySummary = null, InteractiveSession? session = null)
        : base($"Delete {itemType}", session)
    {
        Width = 60;
        Height = dependencySummary != null ? 12 : 8;

        int y = 1;
        Add(new Label($"Are you sure you want to delete {itemType.ToLowerInvariant()}:") { X = 1, Y = y });
        y++;
        Add(new Label(itemName) { X = 3, Y = y });

        if (dependencySummary != null)
        {
            y += 2;
            Add(new Label("Dependencies:") { X = 1, Y = y });
            y++;
            Add(new Label(dependencySummary) { X = 3, Y = y, Width = Dim.Fill(2) });
        }

        var yesButton = new Button("_Yes") { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1) };
        yesButton.Clicked += () =>
        {
            _confirmed = true;
            Application.RequestStop();
        };

        var noButton = new Button("_No") { X = Pos.Center() + 3, Y = Pos.AnchorEnd(1) };
        noButton.Clicked += () => Application.RequestStop();

        Add(yesButton, noButton);
    }
}
