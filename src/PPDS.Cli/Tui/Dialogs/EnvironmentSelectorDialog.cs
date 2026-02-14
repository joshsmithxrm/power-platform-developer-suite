using PPDS.Auth.Profiles;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for selecting from discovered Dataverse environments.
/// </summary>
internal sealed class EnvironmentSelectorDialog : TuiDialog, ITuiStateCapture<EnvironmentSelectorDialogState>
{
    private readonly IEnvironmentService _environmentService;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession? _session;
    private readonly ITuiErrorService? _errorService;
    private readonly TextField _filterField;
    private readonly ListView _listView;
    private readonly Label _statusLabel;
    private readonly TuiSpinner _spinner;
    private readonly TextField _urlField;
    private readonly Button _selectButton;
    private readonly Label _previewUrl;
    private readonly Label _previewType;
    private readonly Label _previewColor;
    private readonly Label _previewStatus;
    private readonly IEnvironmentConfigService? _configService;

    private IReadOnlyList<EnvironmentSummary> _allEnvironments = Array.Empty<EnvironmentSummary>();
    private IReadOnlyList<EnvironmentSummary> _filteredEnvironments = Array.Empty<EnvironmentSummary>();
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
    /// <param name="environmentService">The environment service for discovery.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="session">Optional session for showing environment details.</param>
    public EnvironmentSelectorDialog(
        IEnvironmentService environmentService,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        InteractiveSession? session = null) : base("Select Environment", session)
    {
        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;
        _errorService = session?.GetErrorService();
        _configService = session?.EnvironmentConfigService;

        Width = 72;
        Height = 28;

        // Filter field
        var filterLabel = new Label("Filter:")
        {
            X = 1,
            Y = 1
        };

        _filterField = new TextField
        {
            X = Pos.Right(filterLabel) + 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            ColorScheme = TuiColorPalette.TextInput
        };
        _filterField.TextChanged += OnFilterChanged;

        // Environment list
        var listFrame = new FrameView("Discovered Environments")
        {
            X = 1,
            Y = 3,
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

        // Preview panel (shows details of selected environment)
        var previewFrame = new FrameView("Environment Details")
        {
            X = 1,
            Y = Pos.Bottom(listFrame),
            Width = Dim.Fill() - 2,
            Height = 5
        };

        _previewUrl = new Label("Select an environment to see details")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill()
        };
        _previewType = new Label(string.Empty)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill()
        };
        _previewColor = new Label(string.Empty)
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill()
        };
        _previewStatus = new Label(string.Empty)
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill()
        };
        previewFrame.Add(_previewUrl, _previewType, _previewColor, _previewStatus);

        // Manual URL entry
        var urlLabel = new Label("Or enter URL directly:")
        {
            X = 1,
            Y = Pos.Bottom(previewFrame) + 1,
            Width = 22
        };

        _urlField = new TextField
        {
            X = Pos.Right(urlLabel) + 1,
            Y = Pos.Bottom(previewFrame) + 1,
            Width = Dim.Fill() - 3,
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };
        _urlField.TextChanged += OnUrlChanged;

        // Enter on URL field triggers select
        _urlField.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                OnSelectClicked();
                args.Handled = true;
            }
        };

        // Spinner for loading animation
        _spinner = new TuiSpinner
        {
            X = 1,
            Y = Pos.Bottom(_urlField) + 1,
            Width = Dim.Fill() - 2,
            Height = 1
        };

        // Status label (shown after spinner stops)
        _statusLabel = new Label(string.Empty)
        {
            X = 1,
            Y = Pos.Bottom(_urlField) + 1,
            Width = Dim.Fill() - 2,
            Height = 1,
            Visible = false
        };

        // Buttons
        _selectButton = new Button("_Select")
        {
            X = Pos.Center() - 25,
            Y = Pos.AnchorEnd(1)
        };
        _selectButton.Clicked += OnSelectClicked;

        var detailsButton = new Button("De_tails")
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1)
        };
        detailsButton.Clicked += OnDetailsClicked;

        var configButton = new Button("Con_figure")
        {
            X = Pos.Center() + 5,
            Y = Pos.AnchorEnd(1)
        };
        configButton.Clicked += OnConfigureClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 18,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(filterLabel, _filterField, listFrame, previewFrame, urlLabel, _urlField, _spinner, _statusLabel, _selectButton, detailsButton, configButton, cancelButton);

        // Defer loading until dialog is visible to ensure spinner renders
        Loaded += () =>
        {
            _spinner.Start("Loading environments...");

            _errorService?.FireAndForget(DiscoverEnvironmentsAsync(), "DiscoverEnvironments");
        };
    }

    private async Task DiscoverEnvironmentsAsync()
    {
        try
        {
            _allEnvironments = await _environmentService.DiscoverEnvironmentsAsync(_deviceCodeCallback);
            ApplyFilter();
        }
        catch (PpdsException ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _spinner.Stop();
                _statusLabel.Text = ex.UserMessage;
                _statusLabel.Visible = true;
                _listView.SetSource(new List<string> { "(Enter URL manually)" });
            });
        }
    }

    private void OnFilterChanged(NStack.ustring obj)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filterText = _filterField.Text?.ToString()?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(filterText))
        {
            _filteredEnvironments = _allEnvironments;
        }
        else
        {
            _filteredEnvironments = _allEnvironments
                .Where(env => MatchesFilter(env, filterText))
                .ToList();
        }

        UpdateListView();
    }

    private static bool MatchesFilter(EnvironmentSummary env, string filter)
    {
        var matchesDisplayName = env.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false;
        var matchesUrl = env.Url?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false;
        var matchesType = env.Type?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false;

        return matchesDisplayName || matchesUrl || matchesType;
    }

    private void UpdateListView()
    {
        Application.MainLoop?.Invoke(() =>
        {
            // Stop spinner when we have results
            _spinner.Stop();
            _statusLabel.Visible = true;

            var items = new List<string>();

            foreach (var env in _filteredEnvironments)
            {
                var resolvedType = ResolveDisplayType(env);
                var typeText = resolvedType != null ? $"[{resolvedType}]" : "";
                var regionText = env.Region != null ? $"({env.Region})" : "";
                items.Add($"{env.DisplayName} {typeText} {regionText}");
            }

            if (items.Count == 0)
            {
                items.Add("(No environments found)");
            }

            _listView.SetSource(items);

            if (_allEnvironments.Count == _filteredEnvironments.Count)
            {
                _statusLabel.Text = $"Found {_allEnvironments.Count} environment(s)";
            }
            else
            {
                _statusLabel.Text = $"Showing {_filteredEnvironments.Count} of {_allEnvironments.Count} environments";
            }
        });
    }

    private void OnSelectedItemChanged(ListViewItemEventArgs args)
    {
        if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count)
        {
            var env = _filteredEnvironments[_listView.SelectedItem];
            UpdatePreviewPanel(env);

            // Also update status label if not typing a manual URL
            if (string.IsNullOrEmpty(_urlField.Text?.ToString()))
            {
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
        else if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count)
        {
            var env = _filteredEnvironments[_listView.SelectedItem];
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
        else if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count)
        {
            // Use selected environment
            _selectedEnvironment = _filteredEnvironments[_listView.SelectedItem];
            _useManualUrl = false;
            _manualUrl = null;
            Application.RequestStop();
        }
        else
        {
            _statusLabel.Text = "Please select an environment or enter a URL";
        }
    }

    private void OnDetailsClicked()
    {
        if (_session == null)
        {
            MessageBox.Query("Details", "Environment details require session context.", "OK");
            return;
        }

        // Get URL from manual entry or selected environment
        var url = _urlField.Text?.ToString()?.Trim();
        string? displayName = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count)
            {
                var env = _filteredEnvironments[_listView.SelectedItem];
                url = env.Url;
                displayName = env.DisplayName;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Query("No Environment", "Please select an environment or enter a URL first.", "OK");
            return;
        }

        using var dialog = new EnvironmentDetailsDialog(_session, url, displayName);
        Application.Run(dialog);
    }

    private void OnConfigureClicked()
    {
        if (_session == null)
        {
            MessageBox.Query("Configure", "Configuration requires session context.", "OK");
            return;
        }

        string? url = null;
        string? displayName = null;

        var manualUrl = _urlField.Text?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(manualUrl))
        {
            url = manualUrl;
        }
        else if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count)
        {
            var env = _filteredEnvironments[_listView.SelectedItem];
            url = env.Url;
            displayName = env.DisplayName;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Query("No Environment", "Please select an environment or enter a URL first.", "OK");
            return;
        }

        using var dialog = new EnvironmentConfigDialog(_session, url, displayName);
        Application.Run(dialog);

        if (dialog.ConfigChanged)
        {
            _session.NotifyConfigChanged();
            // Refresh list to show updated resolved labels
            UpdateListView();
            // Refresh preview for current selection
            if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count)
            {
                UpdatePreviewPanel(_filteredEnvironments[_listView.SelectedItem]);
            }
        }
    }

    /// <inheritdoc />
    public EnvironmentSelectorDialogState CaptureState()
    {
        var items = _filteredEnvironments.Select(e => new EnvironmentListItem(
            DisplayName: e.DisplayName ?? "(unknown)",
            Url: e.Url ?? string.Empty,
            EnvironmentType: DetectEnvironmentType(e.Type))).ToList();

        var selectedUrl = _listView.SelectedItem >= 0 && _listView.SelectedItem < _filteredEnvironments.Count
            ? _filteredEnvironments[_listView.SelectedItem].Url
            : null;

        return new EnvironmentSelectorDialogState(
            Title: Title?.ToString() ?? string.Empty,
            Environments: items,
            SelectedIndex: _listView.SelectedItem,
            SelectedEnvironmentUrl: selectedUrl,
            IsLoading: _spinner.Visible,
            HasDetailsButton: _session != null,
            ErrorMessage: null);
    }

    private void UpdatePreviewPanel(EnvironmentSummary env)
    {
        _previewUrl.Text = env.Url ?? "(unknown URL)";

        if (_configService != null && env.Url != null)
        {
            try
            {
#pragma warning disable PPDS012 // Sync-over-async: Terminal.Gui event handler (cached store)
                var config = _configService.GetConfigAsync(env.Url).GetAwaiter().GetResult();
                var resolvedType = _configService.ResolveTypeAsync(env.Url, env.Type).GetAwaiter().GetResult();
                var resolvedColor = _configService.ResolveColorAsync(env.Url).GetAwaiter().GetResult();
                var resolvedLabel = _configService.ResolveLabelAsync(env.Url).GetAwaiter().GetResult();
#pragma warning restore PPDS012

                var labelText = !string.IsNullOrWhiteSpace(resolvedLabel) ? resolvedLabel : "(not set)";
                var typeDisplay = resolvedType != EnvironmentType.Unknown ? resolvedType.ToString() : "(not set)";
                _previewType.Text = $"Type: {typeDisplay}  |  Label: {labelText}";
                _previewColor.Text = $"Color: {resolvedColor}  |  Region: {env.Region ?? "(unknown)"}";
                _previewStatus.Text = config != null ? "Configured" : "Not configured \u2014 use Configure to set up";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                TuiDebugLog.Log($"EnvironmentSelectorDialog preview failed: {ex.Message}");
                _previewType.Text = $"Type: {env.Type ?? "(unknown)"}";
                _previewColor.Text = $"Region: {env.Region ?? "(unknown)"}";
                _previewStatus.Text = string.Empty;
            }
        }
        else
        {
            _previewType.Text = $"Type: {env.Type ?? "(unknown)"}";
            _previewColor.Text = $"Region: {env.Region ?? "(unknown)"}";
            _previewStatus.Text = string.Empty;
        }
    }

    /// <summary>
    /// Resolves the display type for an environment using config service if available,
    /// falling back to the Discovery API type.
    /// </summary>
    private string? ResolveDisplayType(EnvironmentSummary env)
    {
        if (_configService == null || env.Url == null)
            return env.Type;

        try
        {
#pragma warning disable PPDS012 // Sync-over-async: Terminal.Gui event handler (cached store)
            var resolved = _configService.ResolveTypeAsync(env.Url, env.Type).GetAwaiter().GetResult();
#pragma warning restore PPDS012
            return resolved != EnvironmentType.Unknown ? resolved.ToString() : env.Type;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            TuiDebugLog.Log($"EnvironmentSelectorDialog resolve type failed: {ex.Message}");
            return env.Type;
        }
    }

    private static EnvironmentType DetectEnvironmentType(string? type)
        => EnvironmentConfigService.ParseDiscoveryType(type);
}
