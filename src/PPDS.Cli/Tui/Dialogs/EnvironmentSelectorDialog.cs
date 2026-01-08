using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for selecting from discovered Dataverse environments.
/// </summary>
internal sealed class EnvironmentSelectorDialog : Dialog
{
    private readonly IEnvironmentService _environmentService;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly ListView _listView;
    private readonly Label _statusLabel;
    private readonly TextField _urlField;
    private readonly Button _selectButton;

    private IReadOnlyList<EnvironmentSummary> _environments = Array.Empty<EnvironmentSummary>();
    private EnvironmentSummary? _selectedEnvironment;
    private bool _useManualUrl;
    private string? _manualUrl;

    /// <summary>
    /// Gets the selected environment, or null if cancelled.
    /// </summary>
    public EnvironmentSummary? SelectedEnvironment => _selectedEnvironment;

    /// <summary>
    /// Gets whether a manual URL was entered instead of selecting from the list.
    /// </summary>
    public bool UseManualUrl => _useManualUrl;

    /// <summary>
    /// Gets the manually entered URL, if any.
    /// </summary>
    public string? ManualUrl => _manualUrl;

    /// <summary>
    /// Creates a new environment selector dialog.
    /// </summary>
    public EnvironmentSelectorDialog(
        IEnvironmentService environmentService,
        Action<DeviceCodeInfo>? deviceCodeCallback = null) : base("Select Environment")
    {
        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _deviceCodeCallback = deviceCodeCallback;

        Width = 70;
        Height = 20;
        ColorScheme = TuiColorPalette.Default;

        // Environment list
        var listFrame = new FrameView("Discovered Environments")
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = 10
        };

        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        _listView.SelectedItemChanged += OnSelectedItemChanged;
        _listView.OpenSelectedItem += OnItemActivated;
        listFrame.Add(_listView);

        // Manual URL entry
        var urlLabel = new Label("Or enter URL directly:")
        {
            X = 1,
            Y = Pos.Bottom(listFrame) + 1,
            Width = 22
        };

        _urlField = new TextField
        {
            X = Pos.Right(urlLabel) + 1,
            Y = Pos.Bottom(listFrame) + 1,
            Width = Dim.Fill() - 3,
            Text = string.Empty
        };
        _urlField.TextChanged += OnUrlChanged;

        // Status label
        _statusLabel = new Label("Loading environments...")
        {
            X = 1,
            Y = Pos.Bottom(_urlField) + 1,
            Width = Dim.Fill() - 2,
            Height = 1
        };

        // Buttons
        _selectButton = new Button("_Select")
        {
            X = Pos.Center() - 15,
            Y = Pos.AnchorEnd(1)
        };
        _selectButton.Clicked += OnSelectClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 5,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(listFrame, urlLabel, _urlField, _statusLabel, _selectButton, cancelButton);

        // Discover environments asynchronously (fire-and-forget with error handling)
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = DiscoverEnvironmentsAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _statusLabel.Text = $"Error: {t.Exception.InnerException?.Message ?? t.Exception.Message}";
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task DiscoverEnvironmentsAsync()
    {
        try
        {
            _environments = await _environmentService.DiscoverEnvironmentsAsync(_deviceCodeCallback);
            UpdateListView();
        }
        catch (PpdsException ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = ex.UserMessage;
                _listView.SetSource(new List<string> { "(Enter URL manually)" });
            });
        }
    }

    private void UpdateListView()
    {
        Application.MainLoop?.Invoke(() =>
        {
            var items = new List<string>();

            foreach (var env in _environments)
            {
                var typeText = env.Type != null ? $"[{env.Type}]" : "";
                var regionText = env.Region != null ? $"({env.Region})" : "";
                items.Add($"{env.DisplayName} {typeText} {regionText}");
            }

            if (items.Count == 0)
            {
                items.Add("(No environments found)");
            }

            _listView.SetSource(items);
            _statusLabel.Text = $"Found {_environments.Count} environment(s)";
        });
    }

    private void OnSelectedItemChanged(ListViewItemEventArgs args)
    {
        // Clear URL field when selecting from list
        if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _environments.Count)
        {
            if (!string.IsNullOrEmpty(_urlField.Text?.ToString()))
            {
                // Don't clear if user is typing a URL
            }
            else
            {
                var env = _environments[_listView.SelectedItem];
                _statusLabel.Text = env.Url;
            }
        }
    }

    private void OnUrlChanged(NStack.ustring obj)
    {
        var url = _urlField.Text?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(url))
        {
            _statusLabel.Text = "Will connect to URL directly";
        }
        else if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _environments.Count)
        {
            var env = _environments[_listView.SelectedItem];
            _statusLabel.Text = env.Url;
        }
    }

    private void OnItemActivated(ListViewItemEventArgs args)
    {
        OnSelectClicked();
    }

    private void OnSelectClicked()
    {
        var url = _urlField.Text?.ToString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(url))
        {
            // Use manual URL
            _useManualUrl = true;
            _manualUrl = url.Trim();
            _selectedEnvironment = null;
            Application.RequestStop();
        }
        else if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _environments.Count)
        {
            // Use selected environment
            _selectedEnvironment = _environments[_listView.SelectedItem];
            _useManualUrl = false;
            _manualUrl = null;
            Application.RequestStop();
        }
        else
        {
            _statusLabel.Text = "Please select an environment or enter a URL";
        }
    }
}
