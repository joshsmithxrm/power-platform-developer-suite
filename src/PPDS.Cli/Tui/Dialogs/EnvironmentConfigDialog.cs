using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for configuring environment display settings (label, type, color).
/// </summary>
internal sealed class EnvironmentConfigDialog : TuiDialog, ITuiStateCapture<EnvironmentConfigDialogState>
{
    private readonly InteractiveSession _session;
    private readonly string _environmentUrl;
    private readonly TextField _labelField;
    private readonly TextField _typeField;
    private readonly ListView _colorList;
    private readonly EnvironmentColor?[] _colorValues;

    /// <summary>
    /// Gets whether the configuration was changed and saved.
    /// </summary>
    public bool ConfigChanged { get; private set; }

    public EnvironmentConfigDialog(
        InteractiveSession session,
        string environmentUrl,
        string? currentDisplayName = null) : base("Configure Environment", session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _environmentUrl = environmentUrl ?? throw new ArgumentNullException(nameof(environmentUrl));

        Width = 60;
        Height = 18;

        // URL display (read-only)
        var urlLabel = new Label("URL:")
        {
            X = 2,
            Y = 1
        };
        var urlValue = new Label(environmentUrl.Length > 48 ? environmentUrl[..45] + "..." : environmentUrl)
        {
            X = 10,
            Y = 1,
            Width = Dim.Fill() - 3,
            ColorScheme = TuiColorPalette.Focused
        };

        // Label field
        var labelLabel = new Label("Label:")
        {
            X = 2,
            Y = 3
        };
        _labelField = new TextField
        {
            X = 10,
            Y = 3,
            Width = Dim.Fill() - 3,
            ColorScheme = TuiColorPalette.TextInput
        };

        // Type field (free text with hint)
        var typeLabel = new Label("Type:")
        {
            X = 2,
            Y = 5
        };
        _typeField = new TextField
        {
            X = 10,
            Y = 5,
            Width = Dim.Fill() - 3,
            ColorScheme = TuiColorPalette.TextInput
        };
        var typeHint = new Label("(e.g., Production, Sandbox, Dev, UAT)")
        {
            X = 10,
            Y = 6,
            Width = Dim.Fill() - 3,
            ColorScheme = TuiColorPalette.Default
        };

        // Color selection
        var colorLabel = new Label("Color:")
        {
            X = 2,
            Y = 8
        };

        _colorValues = new EnvironmentColor?[] { null }
            .Concat(Enum.GetValues<EnvironmentColor>().Cast<EnvironmentColor?>())
            .ToArray();
        var colorNames = _colorValues
            .Select(c => c?.ToString() ?? "(Use type default)")
            .ToList();

        _colorList = new ListView(colorNames)
        {
            X = 10,
            Y = 8,
            Width = Dim.Fill() - 3,
            Height = 5,
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };

        // Buttons
        var saveButton = new Button("_Save")
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1)
        };
        saveButton.Clicked += OnSaveClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => Application.RequestStop();

        Add(urlLabel, urlValue, labelLabel, _labelField,
            typeLabel, _typeField, typeHint,
            colorLabel, _colorList,
            saveButton, cancelButton);

        // Load existing config after dialog is added to view hierarchy
        Loaded += () => LoadExistingConfig();
    }

    private void LoadExistingConfig()
    {
        try
        {
            // Terminal.Gui UI thread - config store is cached, completes synchronously
#pragma warning disable PPDS012 // Sync-over-async: Terminal.Gui event handler
            var config = _session.EnvironmentConfigService
                .GetConfigAsync(_environmentUrl).GetAwaiter().GetResult();
#pragma warning restore PPDS012

            if (config != null)
            {
                if (config.Label != null)
                    _labelField.Text = config.Label;
                if (config.Type != null)
                    _typeField.Text = config.Type;
                if (config.Color != null)
                {
                    var idx = Array.IndexOf(_colorValues, (EnvironmentColor?)config.Color.Value);
                    if (idx >= 0)
                        _colorList.SelectedItem = idx;
                }
            }
        }
        catch
        {
            // Non-critical: if load fails, dialog starts with empty fields
        }
    }

    private void OnSaveClicked()
    {
        var label = _labelField.Text?.ToString()?.Trim();
        var type = _typeField.Text?.ToString()?.Trim();
        EnvironmentColor? color = null;

        if (_colorList.SelectedItem >= 0 && _colorList.SelectedItem < _colorValues.Length)
        {
            color = _colorValues[_colorList.SelectedItem]; // null if "(Use type default)" selected
        }

        // Only save if at least one field is populated
        if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(type) && color == null)
        {
            Application.RequestStop();
            return;
        }

        try
        {
#pragma warning disable PPDS012 // Sync-over-async: Terminal.Gui event handler
            _session.EnvironmentConfigService.SaveConfigAsync(
                _environmentUrl,
                label: string.IsNullOrEmpty(label) ? null : label,
                type: string.IsNullOrEmpty(type) ? null : type,
                color: color).GetAwaiter().GetResult();
#pragma warning restore PPDS012

            ConfigChanged = true;
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Save Failed", $"Could not save configuration: {ex.Message}", "OK");
            return;
        }

        Application.RequestStop();
    }

    /// <inheritdoc />
    public EnvironmentConfigDialogState CaptureState()
    {
        var selectedColor = _colorList.SelectedItem >= 0 && _colorList.SelectedItem < _colorValues.Length
            ? _colorValues[_colorList.SelectedItem]
            : null;

        return new EnvironmentConfigDialogState(
            Title: Title?.ToString() ?? string.Empty,
            Url: _environmentUrl,
            Label: _labelField.Text?.ToString() ?? string.Empty,
            Type: _typeField.Text?.ToString() ?? string.Empty,
            SelectedColorIndex: _colorList.SelectedItem,
            SelectedColor: selectedColor,
            ConfigChanged: ConfigChanged,
            IsVisible: Visible);
    }
}
