using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for filtering plugin trace logs by type, message, entity, mode, and duration.
/// </summary>
internal sealed class PluginTraceFilterDialog : TuiDialog
{
    private readonly TextField _typeNameField;
    private readonly TextField _messageNameField;
    private readonly TextField _primaryEntityField;
    private readonly RadioGroup _modeGroup;
    private readonly CheckBox _hasExceptionCheckBox;
    private readonly TextField _minDurationField;
    private bool _applied;

    /// <summary>
    /// Gets the built filter, or null if the dialog was cancelled.
    /// </summary>
    public PluginTraceFilter? Filter => _applied ? BuildFilter() : null;

    /// <summary>
    /// Creates a new plugin trace filter dialog.
    /// </summary>
    /// <param name="existingFilter">Optional existing filter to pre-populate fields.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public PluginTraceFilterDialog(PluginTraceFilter? existingFilter = null, InteractiveSession? session = null)
        : base("Filter Traces", session)
    {
        Width = Dim.Percent(60);
        Height = 20;

        const int labelWidth = 16;
        int row = 1;

        // Type Name
        Add(new Label("Type Name:")
        {
            X = 1,
            Y = row,
            Width = labelWidth
        });
        _typeNameField = new TextField(existingFilter?.TypeName ?? "")
        {
            X = labelWidth + 1,
            Y = row,
            Width = Dim.Fill(2),
            ColorScheme = TuiColorPalette.TextInput
        };
        Add(_typeNameField);
        row++;

        // Message Name
        Add(new Label("Message Name:")
        {
            X = 1,
            Y = row,
            Width = labelWidth
        });
        _messageNameField = new TextField(existingFilter?.MessageName ?? "")
        {
            X = labelWidth + 1,
            Y = row,
            Width = Dim.Fill(2),
            ColorScheme = TuiColorPalette.TextInput
        };
        Add(_messageNameField);
        row++;

        // Primary Entity
        Add(new Label("Primary Entity:")
        {
            X = 1,
            Y = row,
            Width = labelWidth
        });
        _primaryEntityField = new TextField(existingFilter?.PrimaryEntity ?? "")
        {
            X = labelWidth + 1,
            Y = row,
            Width = Dim.Fill(2),
            ColorScheme = TuiColorPalette.TextInput
        };
        Add(_primaryEntityField);
        row += 2;

        // Mode
        Add(new Label("Mode:")
        {
            X = 1,
            Y = row,
            Width = labelWidth
        });
        _modeGroup = new RadioGroup(new NStack.ustring[] { "All", "Synchronous", "Asynchronous" })
        {
            X = labelWidth + 1,
            Y = row,
            DisplayMode = DisplayModeLayout.Horizontal
        };
        // Pre-select mode
        if (existingFilter?.Mode == PluginTraceMode.Synchronous)
            _modeGroup.SelectedItem = 1;
        else if (existingFilter?.Mode == PluginTraceMode.Asynchronous)
            _modeGroup.SelectedItem = 2;
        else
            _modeGroup.SelectedItem = 0;
        Add(_modeGroup);
        row += 2;

        // Has Exception
        _hasExceptionCheckBox = new CheckBox("Errors only", existingFilter?.HasException == true)
        {
            X = 1,
            Y = row
        };
        Add(_hasExceptionCheckBox);
        row += 2;

        // Min Duration
        Add(new Label("Min Duration (ms):")
        {
            X = 1,
            Y = row,
            Width = labelWidth + 2
        });
        _minDurationField = new TextField(existingFilter?.MinDurationMs?.ToString() ?? "")
        {
            X = labelWidth + 3,
            Y = row,
            Width = 10,
            ColorScheme = TuiColorPalette.TextInput
        };
        Add(_minDurationField);
        row += 2;

        // Buttons
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

        Add(applyButton, cancelButton);
    }

    private PluginTraceFilter? BuildFilter()
    {
        var typeName = _typeNameField.Text?.ToString()?.Trim();
        var messageName = _messageNameField.Text?.ToString()?.Trim();
        var primaryEntity = _primaryEntityField.Text?.ToString()?.Trim();

        PluginTraceMode? mode = _modeGroup.SelectedItem switch
        {
            1 => PluginTraceMode.Synchronous,
            2 => PluginTraceMode.Asynchronous,
            _ => null
        };

        bool? hasException = _hasExceptionCheckBox.Checked ? true : null;

        int? minDuration = null;
        var minDurText = _minDurationField.Text?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(minDurText) && int.TryParse(minDurText, out var parsed))
        {
            minDuration = parsed;
        }

        // Return null filter if nothing was specified
        if (string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(messageName) &&
            string.IsNullOrEmpty(primaryEntity) && mode == null &&
            hasException == null && minDuration == null)
        {
            return null;
        }

        return new PluginTraceFilter
        {
            TypeName = string.IsNullOrEmpty(typeName) ? null : typeName,
            MessageName = string.IsNullOrEmpty(messageName) ? null : messageName,
            PrimaryEntity = string.IsNullOrEmpty(primaryEntity) ? null : primaryEntity,
            Mode = mode,
            HasException = hasException,
            MinDurationMs = minDuration
        };
    }
}
