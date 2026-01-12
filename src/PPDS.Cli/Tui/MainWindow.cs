using PPDS.Auth.Credentials;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using PPDS.Cli.Tui.Views;
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
    private readonly ITuiErrorService _errorService;
    private readonly TuiStatusBar _statusBar;
    private readonly IHotkeyRegistry _hotkeyRegistry;
    private readonly List<IDisposable> _hotkeyRegistrations = new();
    private bool _hasError;
    private DateTime _lastMenuClickTime = DateTime.MinValue;
    private const int MenuClickDebounceMs = 150;

    public MainWindow(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session)
    {
        _profileName = profileName;
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;
        _themeService = session.GetThemeService();
        _errorService = session.GetErrorService();
        _hotkeyRegistry = session.GetHotkeyRegistry();

        Title = "PPDS - Power Platform Developer Suite";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Apply dark theme to the main window
        ColorScheme = TuiColorPalette.Default;

        // Interactive status bar with profile/environment selectors
        _statusBar = new TuiStatusBar(_session);
        _statusBar.ProfileClicked += OnStatusBarProfileClicked;
        _statusBar.EnvironmentClicked += OnStatusBarEnvironmentClicked;

        // Subscribe to error events
        _errorService.ErrorOccurred += OnErrorOccurred;

        SetupMenu();
        ShowMainMenu();
        Add(_statusBar);

        // Register global hotkeys (work from anywhere, even inside dialogs)
        RegisterGlobalHotkeys();

        // Load initial profile info
        LoadProfileInfoAsync();
    }

    private void OnStatusBarProfileClicked()
    {
        ShowProfileSelector();
    }

    private void OnStatusBarEnvironmentClicked()
    {
        if (_hasError)
        {
            ShowErrorDetails();
        }
        else
        {
            ShowEnvironmentSelector();
        }
    }

    private void SetupMenu()
    {
        // Disabled menu items - show "(Coming Soon)" and do nothing when clicked
        // Note: Avoid _P and _E hotkeys - reserved for global profile/environment switching
        var dataMigrationItem = new MenuItem("_Data Migration (Coming Soon)", "", null, shortcut: Key.Null);
        var solutionsItem = new MenuItem("_Solutions (Coming Soon)", "", null);
        var pluginTracesItem = new MenuItem("Plu_gin Traces (Coming Soon)", "", null);

        // Note: Profile/Environment switching moved to interactive status bar at bottom
        var menu = new MenuBar(new MenuBarItem[]
        {
            new("_File", new MenuItem[]
            {
                new("_SQL Query", "Run SQL queries against Dataverse", () => NavigateToSqlQuery(), shortcut: Key.F2),
                dataMigrationItem,
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
                new("", "", () => {}, null, null, Key.Null), // Separator
                new("Error _Log", "View recent errors and debug log", () => ShowErrorDetails(), shortcut: Key.F12),
            })
        });

        // Add debounce handler to prevent double-click flicker on menus
        // Terminal.Gui 1.x can fire multiple events for a single click in some terminals
        menu.MouseClick += (e) =>
        {
            var now = DateTime.UtcNow;
            var timeSinceLastClick = (now - _lastMenuClickTime).TotalMilliseconds;
            if (timeSinceLastClick < MenuClickDebounceMs)
            {
                e.Handled = true;
                return;
            }
            _lastMenuClickTime = now;
        };

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

        // Note: Global keyboard shortcuts are now handled by HotkeyRegistry
        // via Application.RootKeyEvent - see RegisterGlobalHotkeys()
    }

    private void RegisterGlobalHotkeys()
    {
        // Global hotkeys work from anywhere, even inside dialogs
        // Alt+P and Alt+E close the current dialog before opening selector

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.AltMask | Key.P,
            HotkeyScope.Global,
            "Switch profile",
            ShowProfileSelector));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.AltMask | Key.E,
            HotkeyScope.Global,
            "Switch environment",
            () =>
            {
                if (_hasError)
                    ShowErrorDetails();
                else
                    ShowEnvironmentSelector();
            }));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.F1,
            HotkeyScope.Global,
            "Keyboard shortcuts",
            ShowKeyboardShortcuts));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.F2,
            HotkeyScope.Global,
            "SQL Query",
            NavigateToSqlQuery));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.F12,
            HotkeyScope.Global,
            "Error log",
            ShowErrorDetails));
    }

    private void LoadProfileInfoAsync()
    {
        // Fire-and-forget with error handling
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = LoadProfileInfoInternalAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                _errorService.ReportError("Failed to load profile info", t.Exception, "LoadProfileInfo");
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
            // Status bar updates automatically via session events
            _statusBar.Refresh();
        });
    }

    private void ShowEnvironmentDetails()
    {
        if (_environmentUrl == null)
        {
            MessageBox.ErrorQuery("No Environment", "Please select an environment first.", "OK");
            return;
        }

        using var dialog = new EnvironmentDetailsDialog(_session, _environmentUrl, _environmentName);
        Application.Run(dialog);
    }

    private void ShowProfileSelector()
    {
        var service = _session.GetProfileService();
        var dialog = new ProfileSelectorDialog(service, _session);

        Application.Run(dialog);

        if (dialog.SelectedProfile != null)
        {
            // Update active profile and refresh
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = SetActiveProfileAsync(dialog.SelectedProfile).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _errorService.ReportError("Failed to switch profile", t.Exception, "SwitchProfile");
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
        else if (dialog.CreateNewSelected)
        {
            ShowProfileCreation();
        }
        else if (dialog.ProfileWasDeleted)
        {
            // Refresh profile state after deletion
            RefreshProfileState();
        }
    }

    private void RefreshProfileState()
    {
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = Task.Run(async () =>
        {
            var profileService = _session.GetProfileService();
            var profiles = await profileService.GetProfilesAsync();
            var active = profiles.FirstOrDefault(p => p.IsActive);

            Application.MainLoop?.Invoke(() =>
            {
                if (active != null)
                {
                    _profileName = active.DisplayIdentifier;
                    _environmentName = active.EnvironmentName;
                    _environmentUrl = active.EnvironmentUrl;
                }
                else
                {
                    _profileName = null;
                    _environmentName = null;
                    _environmentUrl = null;
                }
                _statusBar.Refresh();
            });
        });
#pragma warning restore PPDS013
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
            _statusBar.Refresh();
        });
    }

    private async Task SetActiveProfileWithEnvironmentAsync(ProfileSummary profile, string? environmentUrl, string? environmentName)
    {
        // Note: TUI profile switching is session-only (ADR-0018)
        // Uses explicitly provided environment instead of profile's stored environment

        // Update local state
        _profileName = profile.DisplayIdentifier;
        _environmentName = environmentName;
        _environmentUrl = environmentUrl;

        // Switch session to new profile with specified environment
        await _session.SetActiveProfileAsync(
            profile.DisplayIdentifier,
            environmentUrl,
            environmentName);

        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
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
            // Use environment selected post-auth if available, otherwise fall back to profile's environment
            var envUrl = dialog.SelectedEnvironmentUrl ?? dialog.CreatedProfile.EnvironmentUrl;
            var envName = dialog.SelectedEnvironmentName ?? dialog.CreatedProfile.EnvironmentName;

            // Update to use the newly created profile with selected environment
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = SetActiveProfileWithEnvironmentAsync(dialog.CreatedProfile, envUrl, envName).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _errorService.ReportError("Failed to switch to new profile", t.Exception, "ProfileCreation");
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
    }

    private void ShowProfileDetails()
    {
        var dialog = new ProfileDetailsDialog(_session);
        Application.Run(dialog);
    }

    private void ShowEnvironmentSelector()
    {
        if (_profileName == null)
        {
            MessageBox.ErrorQuery("No Profile", "Please select a profile first before choosing an environment.", "OK");
            return;
        }

        var service = _session.GetEnvironmentService();
        var dialog = new EnvironmentSelectorDialog(service, _deviceCodeCallback, _session);

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
                        _errorService.ReportError("Failed to set environment", t.Exception, "SetEnvironment");
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
            _statusBar.Refresh();
        });
    }

    private void NavigateToSqlQuery()
    {
        var sqlScreen = new SqlQueryScreen(_profileName, _deviceCodeCallback, _session);
        Application.Run(sqlScreen);
    }

    private void ShowAbout()
    {
        var dialog = new AboutDialog();
        Application.Run(dialog);
    }

    private void ShowKeyboardShortcuts()
    {
        MessageBox.Query("Keyboard Shortcuts",
            "Global Shortcuts (work everywhere):\n" +
            "  Alt+P    - Switch profile\n" +
            "  Alt+E    - Switch environment\n" +
            "  F1       - This help\n" +
            "  F2       - SQL Query\n" +
            "  F12      - Error Log\n" +
            "  Ctrl+Q   - Quit\n\n" +
            "SQL Query Screen:\n" +
            "  Ctrl+Enter - Execute query\n" +
            "  Ctrl+E     - Export results\n" +
            "  Ctrl+H     - Query history\n" +
            "  /          - Filter results\n" +
            "  Esc        - Close filter or exit\n\n" +
            "Menu Navigation:\n" +
            "  Alt+F/T/H  - Open File/Tools/Help menu\n" +
            "  Arrows     - Navigate menu items\n" +
            "  Enter      - Activate selected item\n" +
            "  Esc        - Close menu\n\n" +
            "Table Navigation:\n" +
            "  Arrows     - Navigate cells\n" +
            "  PgUp/Dn    - Page up/down\n" +
            "  Home/End   - First/last row\n" +
            "  Ctrl+C     - Copy cell\n",
            "OK");
    }

    private void OnErrorOccurred(TuiError error)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _hasError = true;
            _statusBar.SetStatusMessage($"Error: {error.BriefSummary} (F12 for details)");
        });
    }

    private void ShowErrorDetails()
    {
        var dialog = new ErrorDetailsDialog(_errorService);
        Application.Run(dialog);

        // Clear error state when dialog closes (user has seen the error)
        _hasError = false;
        _statusBar.ClearStatusMessage();
    }
}
