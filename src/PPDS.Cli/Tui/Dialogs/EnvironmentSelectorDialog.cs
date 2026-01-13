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
    private readonly TextField _filterField;
    private readonly ListView _listView;
    private readonly Label _statusLabel;
    private readonly TuiSpinner _spinner;
    private readonly TextField _urlField;
    private readonly Button _selectButton;

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

        Width = 70;
        Height = 22;

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
            X = Pos.Center() - 20,
            Y = Pos.AnchorEnd(1)
        };
        _selectButton.Clicked += OnSelectClicked;

        var detailsButton = new Button("De_tails")
        {
            X = Pos.Center() - 5,
            Y = Pos.AnchorEnd(1)
        };
        detailsButton.Clicked += OnDetailsClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 10,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(filterLabel, _filterField, listFrame, urlLabel, _urlField, _spinner, _statusLabel, _selectButton, detailsButton, cancelButton);

        // Defer loading until dialog is visible to ensure spinner renders
        Loaded += () =>
        {
            _spinner.Start("Loading environments...");

            // Discover environments asynchronously (fire-and-forget with error handling)
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
            _ = DiscoverEnvironmentsAsync().ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Application.MainLoop?.Invoke(() =>
                    {
                        _spinner.Stop();
                        _statusLabel.Text = $"Error: {t.Exception.InnerException?.Message ?? t.Exception.Message}";
                        _statusLabel.Visible = true;
                    });
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
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
        return (env.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (env.Url?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (env.Type?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
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
                var typeText = env.Type != null ? $"[{env.Type}]" : "";
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
        // Show URL of selected environment (only if not typing a manual URL)
        if (_listView.SelectedItem >= 0
            && _listView.SelectedItem < _filteredEnvironments.Count
            && string.IsNullOrEmpty(_urlField.Text?.ToString()))
        {
            var env = _filteredEnvironments[_listView.SelectedItem];
            _statusLabel.Text = env.Url;
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

    private static EnvironmentType DetectEnvironmentType(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return EnvironmentType.Unknown;

        return type.ToLowerInvariant() switch
        {
            "production" => EnvironmentType.Production,
            "sandbox" => EnvironmentType.Sandbox,
            "developer" or "development" => EnvironmentType.Development,
            "trial" => EnvironmentType.Trial,
            _ => EnvironmentType.Unknown
        };
    }
}
