using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for changing the plugin trace logging level.
/// </summary>
internal sealed class TraceLevelDialog : TuiDialog
{
    private readonly RadioGroup _levelGroup;
    private readonly Label _warningLabel;
    private bool _applied;

    /// <summary>
    /// Gets the selected trace level, or null if the dialog was cancelled.
    /// </summary>
    public PluginTraceLogSetting? SelectedLevel => _applied ? MapSelectedLevel() : null;

    /// <summary>
    /// Creates a new trace level dialog.
    /// </summary>
    /// <param name="currentLevel">The current trace logging level to pre-select.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public TraceLevelDialog(PluginTraceLogSetting currentLevel, InteractiveSession? session = null)
        : base("Plugin Trace Level", session)
    {
        Width = 50;
        Height = 12;

        Add(new Label("Select trace logging level:")
        {
            X = 1,
            Y = 1
        });

        _levelGroup = new RadioGroup(new NStack.ustring[] { "Off", "Exception", "All" })
        {
            X = 3,
            Y = 3
        };

        // Pre-select current level
        _levelGroup.SelectedItem = currentLevel switch
        {
            PluginTraceLogSetting.Off => 0,
            PluginTraceLogSetting.Exception => 1,
            PluginTraceLogSetting.All => 2,
            _ => 0
        };

        _warningLabel = new Label("Warning: 'All' can generate significant log volume")
        {
            X = 1,
            Y = 6,
            Width = Dim.Fill(2),
            Visible = currentLevel == PluginTraceLogSetting.All,
            ColorScheme = TuiColorPalette.Error
        };

        _levelGroup.SelectedItemChanged += args =>
        {
            _warningLabel.Visible = args.SelectedItem == 2;
        };

        var applyButton = new Button("_Apply")
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1)
        };
        applyButton.Clicked += () =>
        {
            _applied = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () =>
        {
            _applied = false;
            Application.RequestStop();
        };

        Add(_levelGroup, _warningLabel, applyButton, cancelButton);
    }

    private PluginTraceLogSetting MapSelectedLevel()
    {
        return _levelGroup.SelectedItem switch
        {
            0 => PluginTraceLogSetting.Off,
            1 => PluginTraceLogSetting.Exception,
            2 => PluginTraceLogSetting.All,
            _ => PluginTraceLogSetting.Off
        };
    }
}
