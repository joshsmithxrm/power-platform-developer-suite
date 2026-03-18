using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for confirming deletion of plugin traces.
/// </summary>
internal sealed class TraceDeleteDialog : TuiDialog
{
    private readonly RadioGroup _modeGroup;
    private readonly TextField _dayCountField;
    private bool _confirmed;

    /// <summary>
    /// Gets the delete result, or null if the dialog was cancelled.
    /// </summary>
    public TraceDeleteResult? Result => _confirmed ? BuildResult() : null;

    /// <summary>
    /// Creates a new trace delete dialog.
    /// </summary>
    /// <param name="selectedCount">Number of currently selected/loaded traces.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public TraceDeleteDialog(int selectedCount, InteractiveSession? session = null)
        : base("Delete Traces", session)
    {
        Width = 60;
        Height = 14;

        Add(new Label("Choose delete mode:")
        {
            X = 1,
            Y = 1
        });

        _modeGroup = new RadioGroup(new NStack.ustring[]
        {
            $"Delete loaded traces ({selectedCount})",
            "Delete traces older than N days"
        })
        {
            X = 3,
            Y = 3
        };

        var dayLabel = new Label("Days:")
        {
            X = 5,
            Y = 6
        };

        _dayCountField = new TextField("30")
        {
            X = 11,
            Y = 6,
            Width = 8,
            Enabled = false,
            ColorScheme = TuiColorPalette.TextInput
        };

        _modeGroup.SelectedItemChanged += args =>
        {
            _dayCountField.Enabled = args.SelectedItem == 1;
        };

        var confirmButton = new Button("_Confirm")
        {
            X = Pos.Center() - 12,
            Y = Pos.AnchorEnd(1)
        };
        confirmButton.Clicked += () =>
        {
            if (_modeGroup.SelectedItem == 1)
            {
                var dayText = _dayCountField.Text?.ToString()?.Trim();
                if (string.IsNullOrEmpty(dayText) || !int.TryParse(dayText, out var days) || days <= 0)
                {
                    MessageBox.ErrorQuery("Invalid Input", "Day count must be a positive number.", "OK");
                    return;
                }
            }

            _confirmed = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () =>
        {
            _confirmed = false;
            Application.RequestStop();
        };

        Add(_modeGroup, dayLabel, _dayCountField, confirmButton, cancelButton);
    }

    private TraceDeleteResult? BuildResult()
    {
        if (_modeGroup.SelectedItem == 0)
        {
            return new TraceDeleteResult(TraceDeleteMode.ByIds, null);
        }

        var dayText = _dayCountField.Text?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(dayText) && int.TryParse(dayText, out var days) && days > 0)
        {
            return new TraceDeleteResult(TraceDeleteMode.ByAge, days);
        }

        // Invalid day count — treat as cancelled
        return null;
    }
}

/// <summary>
/// Result from the trace delete dialog.
/// </summary>
internal sealed record TraceDeleteResult(TraceDeleteMode Mode, int? DayCount);

/// <summary>
/// Delete mode selection.
/// </summary>
internal enum TraceDeleteMode
{
    /// <summary>Delete traces by their IDs (loaded traces).</summary>
    ByIds,

    /// <summary>Delete traces older than a specified age.</summary>
    ByAge
}
