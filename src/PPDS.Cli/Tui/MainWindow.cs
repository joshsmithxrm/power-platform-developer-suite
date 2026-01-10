using PPDS.Auth.Credentials;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using Terminal.Gui;

namespace PPDS.Cli.Tui;

/// <summary>
/// Main window containing the menu and navigation for the TUI application.
/// </summary>
internal sealed class MainWindow : Window
{
    private string? _profileName;
    private string? _environmentName;
    private string? _environmentUrl;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession _session;
    private readonly ITuiThemeService _themeService;
    private readonly Label _statusLabel;

    public MainWindow(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session)
    {
        _profileName = profileName;
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;
        _themeService = session.GetThemeService();

        Title = "PPDS - Power Platform Developer Suite";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Apply dark theme to the main window
        ColorScheme = TuiColorPalette.Default;

        // Status bar at bottom - starts with default/unknown color, updated when environment loads
        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = TuiColorPalette.StatusBar_Default
        };

        SetupMenu();
        ShowMainMenu();
        Add(_statusLabel);

        // Load initial profile info
        LoadProfileInfoAsync();
    }

    private void SetupMenu()
    {
        // Disabled menu items - show "(Coming Soon)" and do nothing when clicked
        var dataMigrationItem = new MenuItem("_Data Migration (Coming Soon)", "", null, shortcut: Key.Null);
        var solutionsItem = new MenuItem("_Solutions (Coming Soon)", "", null);
        var pluginTracesItem = new MenuItem("_Plugin Traces (Coming Soon)", "", null);

        var menu = new MenuBar(new MenuBarItem[]
        {
            new("_File", new MenuItem[]
            {
                new("_SQL Query", "Run SQL queries against Dataverse", () => NavigateToSqlQuery(), shortcut: Key.F2),
                dataMigrationItem,
                new("", "", () => {}, null, null, Key.Null), // Separator
                new("Switch _Profile...", "Select a different authentication profile", () => ShowProfileSelector()),
                new("Switch _Environment...", "Select a different environment", () => ShowEnvironmentSelector()),
                new("", "", () => {}, null, null, Key.Null), // Separator
                new("_Quit", "Exit the application", () => RequestStop(), shortcut: Key.CtrlMask | Key.Q)
            }),
            new("_Tools", new MenuItem[]
            {
                solutionsItem,
                pluginTracesItem,
            }),
            new("_Help", new MenuItem[]
            {
                new("_About", "About PPDS", () => ShowAbout()),
                new("_Keyboard Shortcuts", "Show keyboard shortcuts", () => ShowKeyboardShortcuts(), shortcut: Key.F1),
            })
        });

        Add(menu);
    }

    private void ShowMainMenu()
    {
        var container = new FrameView("Main Menu")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1, // Leave room for status bar
            ColorScheme = TuiColorPalette.Default
        };

        var label = new Label("Welcome to PPDS Interactive Mode\n\nSelect an option from the menu or use keyboard shortcuts:")
        {
            X = 2,
            Y = 1,
            Width = Dim.Fill() - 4,
            Height = 3
        };

        var buttonSql = new Button("SQL Query (F2)")
        {
            X = 2,
            Y = 5
        };
        buttonSql.Clicked += () => NavigateToSqlQuery();

        var buttonData = new Button("Data Migration (Coming Soon)")
        {
            X = 2,
            Y = 7,
            Enabled = false
        };

        var buttonQuit = new Button("Quit (Ctrl+Q)")
        {
            X = 2,
            Y = 10
        };
        buttonQuit.Clicked += () => RequestStop();

        container.Add(label, buttonSql, buttonData, buttonQuit);
        Add(container);

        // Global keyboard shortcuts
        KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.F1:
                    ShowKeyboardShortcuts();
                    e.Handled = true;
                    break;
                case Key.F2:
                    NavigateToSqlQuery();
                    e.Handled = true;
                    break;
                // F3 disabled - Data Migration coming soon
            }
        };
    }

    private void LoadProfileInfoAsync()
    {
        // Fire-and-forget with error handling
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = LoadProfileInfoInternalAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    UpdateStatus("Error loading profile info");
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task LoadProfileInfoInternalAsync()
    {
        var store = _session.GetProfileStore();
        var collection = await store.LoadAsync(CancellationToken.None);
        var profile = collection.ActiveProfile;

        Application.MainLoop?.Invoke(() =>
        {
            if (profile != null)
            {
                _profileName = profile.DisplayIdentifier;
                _environmentName = profile.Environment?.DisplayName;
                _environmentUrl = profile.Environment?.Url;
            }
            UpdateStatus();
        });
    }

    private void UpdateStatus(string? error = null)
    {
        if (error != null)
        {
            _statusLabel.Text = $" {error}";
            _statusLabel.ColorScheme = TuiColorPalette.Error;
            return;
        }

        // Detect environment type and update color scheme
        var envType = _themeService.DetectEnvironmentType(_environmentUrl);
        _statusLabel.ColorScheme = _themeService.GetStatusBarScheme(envType);

        // Build status text with environment type label
        var profilePart = _profileName != null ? $"Profile: {_profileName}" : "No profile";
        var envPart = _environmentName != null ? $"Environment: {_environmentName}" : "No environment";

        // Add environment type label if detected (e.g., "[PROD]", "[DEV]", "[SANDBOX]")
        var envLabel = _themeService.GetEnvironmentLabel(envType);
        var labelPart = !string.IsNullOrEmpty(envLabel) ? $" [{envLabel}]" : "";

        _statusLabel.Text = $" {profilePart} | {envPart}{labelPart}";
    }

    private void ShowProfileSelector()
    {
        var service = _session.GetProfileService();
        var dialog = new ProfileSelectorDialog(service);

        Application.Run(dialog);

        if (dialog.SelectedProfile != null)
        {
            // Update active profile and refresh
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = SetActiveProfileAsync(dialog.SelectedProfile).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.MainLoop?.Invoke(() =>
                    {
                        MessageBox.ErrorQuery("Error", t.Exception?.InnerException?.Message ?? "Failed to switch profile", "OK");
                    });
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
        else if (dialog.CreateNewSelected)
        {
            ShowProfileCreation();
        }
    }

    private async Task SetActiveProfileAsync(ProfileSummary profile)
    {
        // Note: TUI profile switching is session-only (ADR-0018)
        // We don't update the global active profile in profiles.json
        // Use 'ppds auth select' to change the global default

        // Update local state only
        _profileName = profile.DisplayIdentifier;
        _environmentName = profile.EnvironmentName;
        _environmentUrl = profile.EnvironmentUrl;

        // Switch session to new profile - this invalidates the old pool and re-warms with new credentials
        await _session.SetActiveProfileAsync(
            profile.DisplayIdentifier,
            profile.EnvironmentUrl,
            profile.EnvironmentName);

        Application.MainLoop?.Invoke(() =>
        {
            UpdateStatus();
        });
    }

    private void ShowProfileCreation()
    {
        var profileService = _session.GetProfileService();
        var envService = _session.GetEnvironmentService();

        var dialog = new ProfileCreationDialog(profileService, envService, _deviceCodeCallback);
        Application.Run(dialog);

        if (dialog.CreatedProfile != null)
        {
            // Update to use the newly created profile
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = SetActiveProfileAsync(dialog.CreatedProfile).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.MainLoop?.Invoke(() =>
                    {
                        MessageBox.ErrorQuery("Error", t.Exception?.InnerException?.Message ?? "Failed to switch to new profile", "OK");
                    });
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
    }

    private void ShowEnvironmentSelector()
    {
        if (_profileName == null)
        {
            MessageBox.ErrorQuery("No Profile", "Please select a profile first before choosing an environment.", "OK");
            return;
        }

        var service = _session.GetEnvironmentService();
        var dialog = new EnvironmentSelectorDialog(service, _deviceCodeCallback);

        Application.Run(dialog);

        if (dialog.SelectedEnvironment != null || dialog.UseManualUrl)
        {
            var url = dialog.UseManualUrl ? dialog.ManualUrl : dialog.SelectedEnvironment?.Url;
            var name = dialog.UseManualUrl ? dialog.ManualUrl : dialog.SelectedEnvironment?.DisplayName;

            if (url != null)
            {
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
                _ = SetEnvironmentAsync(url, name).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Application.MainLoop?.Invoke(() =>
                        {
                            MessageBox.ErrorQuery("Error", t.Exception?.InnerException?.Message ?? "Failed to set environment", "OK");
                        });
                    }
                }, TaskScheduler.Default);
#pragma warning restore PPDS013
            }
        }
    }

    private async Task SetEnvironmentAsync(string url, string? displayName)
    {
        // Note: TUI environment switching is session-only (ADR-0018)
        // We don't persist to profiles.json - use 'ppds env select' for that
        // Validation happens when pool connects; errors surface to user

        // Update local state only
        _environmentUrl = url;
        _environmentName = displayName;

        // Update session - invalidates old pool, sets new URL, fires EnvironmentChanged
        await _session.SetEnvironmentAsync(url, displayName);

        Application.MainLoop?.Invoke(() =>
        {
            UpdateStatus();
        });
    }

    private void NavigateToSqlQuery()
    {
        var sqlScreen = new SqlQueryScreen(_profileName, _deviceCodeCallback, _session);
        Application.Run(sqlScreen);
    }

    private void ShowAbout()
    {
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "Unknown";
        MessageBox.Query("About PPDS",
            $"Power Platform Developer Suite\n\nVersion: {version}\n\nA multi-interface platform for Dataverse development.\n\nhttps://github.com/joshsmithxrm/power-platform-developer-suite",
            "OK");
    }

    private void ShowKeyboardShortcuts()
    {
        MessageBox.Query("Keyboard Shortcuts",
            "Global Shortcuts:\n" +
            "  F2       - SQL Query\n" +
            "  Ctrl+Q   - Quit\n\n" +
            "Table Navigation:\n" +
            "  Arrows   - Navigate cells\n" +
            "  PgUp/Dn  - Page up/down\n" +
            "  Home/End - First/last row\n" +
            "  /        - Filter results\n" +
            "  Ctrl+C   - Copy cell\n" +
            "  Ctrl+E   - Export\n",
            "OK");
    }
}
