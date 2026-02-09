using PPDS.Auth.Credentials;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;

namespace PPDS.Cli.Tui;

/// <summary>
/// The main shell for the TUI application. Provides persistent menu bar, status bar,
/// and a content area where screens are swapped.
/// </summary>
internal sealed class TuiShell : Window, ITuiStateCapture<TuiShellState>
{
    private readonly string? _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession _session;
    private readonly ITuiThemeService _themeService;
    private readonly ITuiErrorService _errorService;
    private readonly IHotkeyRegistry _hotkeyRegistry;
    private readonly List<IDisposable> _hotkeyRegistrations = new();

    private readonly TuiStatusBar _statusBar;
    private readonly TuiStatusLine _statusLine;
    private readonly FrameView _contentArea;
    private readonly TabManager _tabManager;
    private readonly TabBar _tabBar;
    private PpdsMenuBar? _menuBar;

    private readonly Task _initializationTask;

    private ITuiScreen? _currentScreen;
    private SplashView? _splashView;

    public TuiShell(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session, Task initializationTask)
    {
        _profileName = profileName;
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;
        _initializationTask = initializationTask;
        _themeService = session.GetThemeService();
        _errorService = session.GetErrorService();
        _hotkeyRegistry = session.GetHotkeyRegistry();

        Title = "PPDS - Power Platform Developer Suite";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = TuiColorPalette.Default;

        // Tab manager and tab bar
        _tabManager = new TabManager(_themeService);
        _tabBar = new TabBar(_tabManager, _themeService);

        // Content area where screens are displayed
        _contentArea = new FrameView("Main Menu")
        {
            X = 0,
            Y = 2, // Below menu bar + tab bar
            Width = Dim.Fill(),
            Height = Dim.Fill() - 3, // Leave room for tab bar, status line and status bar
            ColorScheme = TuiColorPalette.Default
        };

        // Wire tab manager to swap content when active tab changes
        _tabManager.ActiveTabChanged += OnActiveTabChanged;
        _tabBar.NewTabClicked += NavigateToSqlQuery;

        // Status line for contextual messages (above status bar)
        _statusLine = new TuiStatusLine();

        // Interactive status bar with profile/environment selectors
        _statusBar = new TuiStatusBar(_session);
        _statusBar.ProfileClicked += OnStatusBarProfileClicked;
        _statusBar.EnvironmentClicked += OnStatusBarEnvironmentClicked;
        _statusBar.EnvironmentConfigureRequested += OnStatusBarEnvironmentConfigureRequested;

        // Subscribe to error events
        _errorService.ErrorOccurred += OnErrorOccurred;
        _session.ConfigChanged += OnConfigChanged;

        // Build initial menu bar
        RebuildMenuBar();

        // Show splash screen during initialization
        ShowSplash();

        Add(_tabBar, _contentArea, _statusBar, _statusLine);

        // Register global hotkeys
        RegisterGlobalHotkeys();

        // Load initial profile info
        LoadProfileInfoAsync();

        // Wire session initialization to splash ready state
        WireInitializationToSplash();
    }

    /// <summary>
    /// Opens a screen in a new tab and makes it active.
    /// Activation is handled by <see cref="OnActiveTabChanged"/> via the TabManager event.
    /// </summary>
    public void NavigateTo(ITuiScreen screen)
    {
        // Hide splash if still showing
        HideSplash();

        // Add screen as a new tab — AddTab fires ActiveTabChanged,
        // which calls OnActiveTabChanged to handle activation.
        var envUrl = (screen as TuiScreenBase)?.EnvironmentUrl ?? _session.CurrentEnvironmentUrl;
        var envName = _session.CurrentEnvironmentDisplayName;
        _tabManager.AddTab(screen, envUrl, envName);
    }

    private void OnScreenCloseRequested()
    {
        NavigateBack();
    }

    private void OnScreenMenuStateChanged()
    {
        RebuildMenuBar();
    }

    /// <summary>
    /// Closes the active tab. If no tabs remain, shows splash home.
    /// </summary>
    public void NavigateBack()
    {
        // Confirm if screen has unsaved work
        if (_currentScreen?.HasUnsavedWork == true)
        {
            var result = MessageBox.Query(
                "Close Tab",
                "This tab has unsaved changes. Close anyway?",
                "Close", "Cancel");

            if (result != 0)
                return;
        }

        // Deactivate current screen (TabManager.CloseTab will dispose it)
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
            _currentScreen = null;
        }

        var activeIndex = _tabManager.ActiveIndex;
        if (activeIndex >= 0)
        {
            _tabManager.CloseTab(activeIndex);
        }

        // OnActiveTabChanged will handle activating the next tab or showing splash home
    }

    private void OnActiveTabChanged()
    {
        var activeTab = _tabManager.ActiveTab;

        // Deactivate current screen if any
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
            _currentScreen = null;
        }

        if (activeTab == null)
        {
            // No tabs remain - show splash home
            ShowSplashHome();
            return;
        }

        // Activate the new tab's screen
        _currentScreen = activeTab.Screen;
        _currentScreen.CloseRequested += OnScreenCloseRequested;
        _currentScreen.MenuStateChanged += OnScreenMenuStateChanged;
        _hotkeyRegistry.SetActiveScreen(_currentScreen);
        _contentArea.Title = _currentScreen.Title;
        _contentArea.Add(_currentScreen.Content);
        _currentScreen.OnActivated(_hotkeyRegistry);

        // Sync status bar to reflect this tab's environment
        _session.UpdateDisplayedEnvironment(activeTab.EnvironmentUrl, activeTab.EnvironmentDisplayName);

        RebuildMenuBar();
        _currentScreen.Content.SetFocus();
    }

    private void HideSplash()
    {
        if (_splashView != null)
            _splashView.Visible = false;
    }

    private void ShowSplash()
    {
        _currentScreen = null;
        _hotkeyRegistry.SetActiveScreen(null);
        _contentArea.Title = "PPDS";

        _splashView = new SplashView();
        _contentArea.Add(_splashView);

        // Start spinner animation (safe — guarded by Application.Driver != null inside)
        _splashView.StartSpinner();

        RebuildMenuBar();
    }

    private void ShowSplashHome()
    {
        _currentScreen = null;
        _hotkeyRegistry.SetActiveScreen(null);
        _contentArea.Title = "PPDS";

        if (_splashView != null)
            _splashView.Visible = true;

        RebuildMenuBar();
    }

    private void RebuildMenuBar()
    {
        // Remove old menu bar
        if (_menuBar != null)
        {
            Remove(_menuBar);
            _menuBar = null;
        }

        var menuItems = new List<MenuBarItem>();

        // Export menu item - enabled only when current screen supports export
        var exportAction = _currentScreen?.ExportAction;
        var exportItem = new MenuItem(
            "Export",
            "Export data (Ctrl+E)",
            exportAction != null ? () => exportAction() : (Action?)null,
            shortcut: Key.Null);

        // File menu (always present)
        // Note: Keep underscore on MenuBarItem (_File) for Alt+F to open menu.
        // Remove underscores from MenuItems - they create global Alt+letter hotkeys in Terminal.Gui.
        menuItems.Add(new MenuBarItem("_File", new MenuItem[]
        {
            exportItem,
            new("", "", () => {}, null, null, Key.Null), // Separator
            new("Exit", "Exit the application", () =>
            {
                // Close menu before stopping to prevent Terminal.Gui state corruption
                _menuBar?.CloseMenu();
                RequestStop();
            })
        }));

        // Screen-specific menus (inserted between File and Tools)
        if (_currentScreen?.ScreenMenuItems != null)
        {
            menuItems.AddRange(_currentScreen.ScreenMenuItems);
        }

        // Tools menu (always present) - contains all workspaces/tools
        var hasEnvironment = _session.CurrentEnvironmentUrl != null;
        menuItems.Add(new MenuBarItem("_Tools", new MenuItem[]
        {
            new("SQL Query", "Run SQL queries against Dataverse", () => NavigateToSqlQuery()),
            new("Configure Environment...", "Configure label, type, and color",
                hasEnvironment ? () => ShowEnvironmentConfig() : (Action?)null),
        }));

        // Help menu (always present)
        menuItems.Add(new MenuBarItem("_Help", new MenuItem[]
        {
            new("About", "About PPDS", () => ShowAbout()),
            new("Keyboard Shortcuts", "Show keyboard shortcuts (F1)", () => ShowKeyboardShortcuts()),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new("Error Log", "View recent errors and debug log (F12)", () => ShowErrorDetails()),
        }));

        _menuBar = new PpdsMenuBar(menuItems.ToArray());
        _menuBar.ColorScheme = TuiColorPalette.MenuBar;

        // Register menu-open check with HotkeyRegistry for letter-blocking
        _hotkeyRegistry.SetMenuOpenCheck(() => _menuBar?.IsMenuOpen == true);

        Add(_menuBar);
    }

    private void RegisterGlobalHotkeys()
    {
        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.AltMask | Key.P,
            HotkeyScope.Global,
            "Switch profile",
            ShowProfileSelector));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.AltMask | Key.E,
            HotkeyScope.Global,
            "Switch environment",
            ShowEnvironmentSelector));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.F1,
            HotkeyScope.Global,
            "Keyboard shortcuts",
            ShowKeyboardShortcuts));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.F12,
            HotkeyScope.Global,
            "Error log",
            ShowErrorDetails));

        // Tab management hotkeys
        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.T,
            HotkeyScope.Global,
            "New tab",
            NavigateToSqlQuery));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.W,
            HotkeyScope.Global,
            "Close tab",
            () => { if (_tabManager.ActiveIndex >= 0) NavigateBack(); }));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.Tab,
            HotkeyScope.Global,
            "Next tab",
            () => _tabManager.ActivateNext()));

        // Alternative tab cycling for terminals that don't support Ctrl+Tab
        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.PageDown,
            HotkeyScope.Global,
            "Next tab",
            () => _tabManager.ActivateNext()));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.PageUp,
            HotkeyScope.Global,
            "Previous tab",
            () => _tabManager.ActivatePrevious()));

        // Direct tab selection: Alt+1 through Alt+9
        for (var i = 0; i < 9; i++)
        {
            var index = i;
            _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
                Key.AltMask | (Key)('1' + i),
                HotkeyScope.Global,
                $"Tab {i + 1}",
                () => _tabManager.ActivateTab(index)));
        }
    }

    private void NavigateToSqlQuery()
    {
        // Clear splash or main menu content
        HideSplash();

        // Show centered loading message in content area
        var loadingLabel = new Label("Loading SQL Query...")
        {
            X = Pos.Center(),
            Y = Pos.Center()
        };
        _contentArea.Add(loadingLabel);
        _contentArea.Title = "Loading";

        // Force UI to show loading state before blocking work
        Application.Refresh();

        // Use AddIdle to allow the UI refresh to complete, then create screen
        Application.MainLoop?.AddIdle(() =>
        {
            // Remove loading label
            _contentArea.Remove(loadingLabel);

            // Create and navigate to SQL screen (this is the slow part)
            var sqlScreen = new SqlQueryScreen(_deviceCodeCallback, _session);
            NavigateTo(sqlScreen);

            return false; // Don't repeat idle callback
        });
    }

    /// <summary>
    /// Opens a new SQL Query tab bound to a specific environment.
    /// </summary>
    private void NavigateToSqlQueryOnEnvironment(string environmentUrl, string? displayName)
    {
        HideSplash();

        var loadingLabel = new Label("Loading SQL Query...")
        {
            X = Pos.Center(),
            Y = Pos.Center()
        };
        _contentArea.Add(loadingLabel);
        _contentArea.Title = "Loading";

        Application.Refresh();

        Application.MainLoop?.AddIdle(() =>
        {
            _contentArea.Remove(loadingLabel);

            var sqlScreen = new SqlQueryScreen(_deviceCodeCallback, _session, environmentUrl, displayName);
            NavigateTo(sqlScreen);

            return false;
        });
    }

    private void OnStatusBarProfileClicked()
    {
        ShowProfileSelector();
    }

    private void OnStatusBarEnvironmentClicked()
    {
        ShowEnvironmentSelector();
    }

    private void ShowProfileSelector()
    {
        var service = _session.GetProfileService();
        using var dialog = new ProfileSelectorDialog(service, _session);

        Application.Run(dialog);

        if (dialog.SelectedProfile != null)
        {
            // Profile change invalidates all connections — close all tabs
            CloseAllTabs();
            _errorService.FireAndForget(SetActiveProfileAsync(dialog.SelectedProfile), "SwitchProfile");
        }
        else if (dialog.CreateNewSelected)
        {
            ShowProfileCreation();
        }
        else if (dialog.ProfileWasDeleted)
        {
            RefreshProfileState();
        }
    }

    /// <summary>
    /// Deactivates the current screen and closes all tabs.
    /// OnActiveTabChanged will show the splash home when no tabs remain.
    /// </summary>
    private void CloseAllTabs()
    {
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
            _currentScreen = null;
        }

        _tabManager.CloseAllTabs();
    }

    private void ShowEnvironmentSelector()
    {
        if (_session.CurrentProfileName == null)
        {
            MessageBox.ErrorQuery("No Profile", "Please select a profile first before choosing an environment.", "OK");
            return;
        }

        var service = _session.GetEnvironmentService();
        using var dialog = new EnvironmentSelectorDialog(service, _deviceCodeCallback, _session);

        Application.Run(dialog);

        if (dialog.SelectedEnvironment != null || dialog.UseManualUrl)
        {
            var url = dialog.UseManualUrl ? dialog.ManualUrl : dialog.SelectedEnvironment?.Url;
            var name = dialog.UseManualUrl ? dialog.ManualUrl : dialog.SelectedEnvironment?.DisplayName;
            var discoveredType = dialog.SelectedEnvironment?.Type;

            if (url != null)
            {
                // Prompt to configure if this environment is new/unconfigured
                PromptToConfigureIfNeeded(url, name, discoveredType);

                // Persist selection as profile default
                _errorService.FireAndForget(SetEnvironmentAsync(url, name), "SetEnvironment");

                // If tabs are open, switch to existing tab on that env or open a new one
                if (_tabManager.TabCount > 0)
                {
                    var existingTab = _tabManager.FindTabByEnvironment(url);
                    if (existingTab >= 0)
                    {
                        _tabManager.ActivateTab(existingTab);
                    }
                    else
                    {
                        NavigateToSqlQueryOnEnvironment(url, name);
                    }
                }
            }
        }
    }

    private void OnStatusBarEnvironmentConfigureRequested()
    {
        ShowEnvironmentConfig();
    }

    private void ShowEnvironmentConfig()
    {
        var url = _session.CurrentEnvironmentUrl;
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.ErrorQuery("No Environment", "Please connect to an environment first.", "OK");
            return;
        }

        var displayName = _session.CurrentEnvironmentDisplayName;
        using var dialog = new EnvironmentConfigDialog(_session, url, displayName);
        Application.Run(dialog);

        if (dialog.ConfigChanged)
        {
            _session.NotifyConfigChanged();
        }
    }

    /// <summary>
    /// Prompts the user to configure an environment if it has no saved configuration.
    /// </summary>
    private void PromptToConfigureIfNeeded(string url, string? displayName, string? discoveredType)
    {
        try
        {
#pragma warning disable PPDS012 // Sync-over-async: Terminal.Gui event handler (cached store)
            var config = _session.EnvironmentConfigService
                .GetConfigAsync(url).GetAwaiter().GetResult();
#pragma warning restore PPDS012

            if (config != null) return; // Already configured
        }
        catch
        {
            return; // Can't check — don't nag
        }

        var result = MessageBox.Query(
            "Configure Environment",
            "This environment isn't configured yet.\nSet a label and color for easy identification?",
            "Yes", "Skip");

        if (result == 0)
        {
            using var dialog = new EnvironmentConfigDialog(_session, url, displayName, discoveredType);
            Application.Run(dialog);

            if (dialog.ConfigChanged)
            {
                _session.NotifyConfigChanged();
            }
        }
    }

    private void ShowProfileCreation()
    {
        var profileService = _session.GetProfileService();
        var envService = _session.GetEnvironmentService();

        using var dialog = new ProfileCreationDialog(profileService, envService, _deviceCodeCallback, _session);
        Application.Run(dialog);

        if (dialog.CreatedProfile != null)
        {
            // New profile = new credentials — close all tabs
            CloseAllTabs();

            var envUrl = dialog.SelectedEnvironmentUrl ?? dialog.CreatedProfile.EnvironmentUrl;
            var envName = dialog.SelectedEnvironmentName ?? dialog.CreatedProfile.EnvironmentName;

            // Prompt to configure the environment if unconfigured
            if (envUrl != null)
            {
                PromptToConfigureIfNeeded(envUrl, envName, discoveredType: null);
            }

            _errorService.FireAndForget(
                SetActiveProfileWithEnvironmentAsync(dialog.CreatedProfile, envUrl, envName),
                "ProfileCreation");
        }
    }

    private async Task SetActiveProfileAsync(ProfileSummary profile)
    {
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
        await _session.SetActiveProfileAsync(
            profile.DisplayIdentifier,
            environmentUrl,
            environmentName);

        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
        });
    }

    private async Task SetEnvironmentAsync(string url, string? displayName)
    {
        await _session.SetEnvironmentAsync(url, displayName);

        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
        });
    }

    private void RefreshProfileState()
    {
        _errorService.FireAndForget(Task.Run(async () =>
        {
            var profileService = _session.GetProfileService();
            await profileService.GetProfilesAsync();

            Application.MainLoop?.Invoke(() =>
            {
                _statusBar.Refresh();
            });
        }), "RefreshProfileState");
    }

    private void LoadProfileInfoAsync()
    {
        _errorService.FireAndForget(LoadProfileInfoInternalAsync(), "LoadProfileInfo");
    }

    private async Task LoadProfileInfoInternalAsync()
    {
        var store = _session.GetProfileStore();
        await store.LoadAsync(CancellationToken.None);

        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
        });
    }

    /// <summary>
    /// Wires the session initialization task to splash screen state transitions.
    /// When initialization completes, marks splash as ready. Splash stays as home screen.
    /// </summary>
    private void WireInitializationToSplash()
    {
        _errorService.FireAndForget(WireInitializationToSplashAsync(), "SplashTransition");
    }

    private async Task WireInitializationToSplashAsync()
    {
        try
        {
            await _initializationTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Initialization failed — show error on splash
            TuiDebugLog.Log($"Session initialization failed: {ex.Message}");
            Application.MainLoop?.Invoke(() =>
            {
                _splashView?.SetStatus($"Init error: {ex.Message}");
            });
            // Brief pause so user can see the error
            await Task.Delay(2000).ConfigureAwait(false);
        }

        // Mark splash as ready — it stays as home screen
        Application.MainLoop?.Invoke(() =>
        {
            _splashView?.SetReady();
        });
    }

    private void ShowAbout()
    {
        using var dialog = new AboutDialog();
        Application.Run(dialog);
    }

    private void ShowKeyboardShortcuts()
    {
        using var dialog = new KeyboardShortcutsDialog(_hotkeyRegistry, _session);
        Application.Run(dialog);
    }

    private void OnConfigChanged()
    {
        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
            _tabManager.RefreshTabColors();
        });
    }

    private void OnErrorOccurred(TuiError error)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _statusLine.SetMessage($"Error: {error.BriefSummary} (F12 for details)");
        });
    }

    private void ShowErrorDetails()
    {
        using var dialog = new ErrorDetailsDialog(_errorService);
        Application.Run(dialog);

        _statusLine.ClearMessage();
    }

    /// <inheritdoc />
    public TuiShellState CaptureState()
    {
        var menuBarItems = new List<string>();
        if (_menuBar != null)
        {
            foreach (var item in _menuBar.Menus)
            {
                menuBarItems.Add(item.Title?.ToString()?.Replace("_", "") ?? string.Empty);
            }
        }

        return new TuiShellState(
            Title: Title?.ToString() ?? string.Empty,
            MenuBarItems: menuBarItems,
            StatusBar: _statusBar.CaptureState(),
            CurrentScreenTitle: _currentScreen?.Title,
            CurrentScreenType: _currentScreen?.GetType().Name,
            IsMainMenuVisible: _currentScreen == null,
            TabCount: _tabManager.TabCount,
            HasErrors: _errorService.RecentErrors.Count > 0,
            ErrorCount: _errorService.RecentErrors.Count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unregister global hotkeys
            foreach (var registration in _hotkeyRegistrations)
            {
                registration.Dispose();
            }
            _hotkeyRegistrations.Clear();

            // Deactivate current screen (TabManager.Dispose will handle disposal)
            if (_currentScreen != null)
            {
                _currentScreen.CloseRequested -= OnScreenCloseRequested;
                _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
                _currentScreen.OnDeactivating();
                _currentScreen = null;
            }

            // Unsubscribe from tab events before disposing
            _tabManager.ActiveTabChanged -= OnActiveTabChanged;
            _tabBar.NewTabClicked -= NavigateToSqlQuery;
            _tabManager.Dispose();
            _tabBar.Dispose();

            _statusBar.ProfileClicked -= OnStatusBarProfileClicked;
            _statusBar.EnvironmentClicked -= OnStatusBarEnvironmentClicked;
            _statusBar.EnvironmentConfigureRequested -= OnStatusBarEnvironmentConfigureRequested;
            _errorService.ErrorOccurred -= OnErrorOccurred;
            _session.ConfigChanged -= OnConfigChanged;
        }

        base.Dispose(disposing);
    }
}
