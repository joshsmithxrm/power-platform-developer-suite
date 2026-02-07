using System.Reflection;
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
    private MenuBar? _menuBar;

    private ITuiScreen? _currentScreen;
    private View? _mainMenuContent;
    private SplashView? _splashView;
    private bool _hasError;
    private DateTime _lastMenuClickTime = DateTime.MinValue;
    private const int MenuClickDebounceMs = 150;

    public TuiShell(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session)
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
        ColorScheme = TuiColorPalette.Default;

        // Tab manager and tab bar
        _tabManager = new TabManager(_themeService);
        _tabBar = new TabBar(_tabManager);

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

        // Status line for contextual messages (above status bar)
        _statusLine = new TuiStatusLine();

        // Interactive status bar with profile/environment selectors
        _statusBar = new TuiStatusBar(_session);
        _statusBar.ProfileClicked += OnStatusBarProfileClicked;
        _statusBar.EnvironmentClicked += OnStatusBarEnvironmentClicked;

        // Subscribe to error events
        _errorService.ErrorOccurred += OnErrorOccurred;

        // Build initial menu bar
        RebuildMenuBar();

        // Show splash screen during initialization
        ShowSplash();

        Add(_tabBar, _contentArea, _statusBar, _statusLine);

        // Register global hotkeys
        RegisterGlobalHotkeys();

        // Load initial profile info
        LoadProfileInfoAsync();
    }

    /// <summary>
    /// Opens a screen in a new tab and makes it active.
    /// </summary>
    public void NavigateTo(ITuiScreen screen)
    {
        // Clear splash/main menu if still showing
        ClearSplashAndMainMenu();

        // Deactivate current screen (if any)
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
        }

        // Add screen as a new tab
        var envUrl = (screen as TuiScreenBase)?.EnvironmentUrl ?? _session.CurrentEnvironmentUrl;
        var envName = _session.CurrentEnvironmentDisplayName;
        _tabManager.AddTab(screen, envUrl, envName);

        // Activate the new screen
        _currentScreen = screen;
        _currentScreen.CloseRequested += OnScreenCloseRequested;
        _currentScreen.MenuStateChanged += OnScreenMenuStateChanged;
        _hotkeyRegistry.SetActiveScreen(screen);
        _contentArea.Title = screen.Title;
        _contentArea.Add(screen.Content);
        screen.OnActivated(_hotkeyRegistry);

        // Rebuild menu bar with screen-specific items
        RebuildMenuBar();

        // Set focus to content
        screen.Content.SetFocus();
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
    /// Closes the active tab. If no tabs remain, shows main menu.
    /// </summary>
    public void NavigateBack()
    {
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

        // OnActiveTabChanged will handle activating the next tab or showing main menu
    }

    private void OnActiveTabChanged()
    {
        // Deactivate current screen if any
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
            _currentScreen = null;
        }

        var activeTab = _tabManager.ActiveTab;
        if (activeTab == null)
        {
            // No tabs remain - show main menu
            ShowMainMenu();
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

        RebuildMenuBar();
        _currentScreen.Content.SetFocus();
    }

    private void ClearSplashAndMainMenu()
    {
        if (_splashView != null)
        {
            _contentArea.Remove(_splashView);
            _splashView = null;
        }
        if (_mainMenuContent != null)
        {
            _contentArea.Remove(_mainMenuContent);
            _mainMenuContent = null;
        }
    }

    private void ShowSplash()
    {
        _currentScreen = null;
        _hotkeyRegistry.SetActiveScreen(null);
        _contentArea.Title = "PPDS";

        _splashView = new SplashView();
        _contentArea.Add(_splashView);

        RebuildMenuBar();
    }

    private void ShowMainMenu()
    {
        ClearSplashAndMainMenu();

        _currentScreen = null;
        _hotkeyRegistry.SetActiveScreen(null);
        _contentArea.Title = "Main Menu";

        _mainMenuContent = CreateMainMenuContent();
        _contentArea.Add(_mainMenuContent);

        RebuildMenuBar();
    }

    private View CreateMainMenuContent()
    {
        var container = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var label = new Label("Welcome to PPDS Interactive Mode\n\nSelect an option from the menu or use keyboard shortcuts:")
        {
            X = 2,
            Y = 1,
            Width = Dim.Fill() - 4,
            Height = 3
        };

        var buttonSql = new Button("SQL Query")
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

        container.Add(label, buttonSql, buttonData);
        return container;
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
                CloseMenuBar();
                RequestStop();
            })
        }));

        // Screen-specific menus (inserted between File and Tools)
        if (_currentScreen?.ScreenMenuItems != null)
        {
            menuItems.AddRange(_currentScreen.ScreenMenuItems);
        }

        // Disabled menu items (no underscore hotkeys - they work globally in Terminal.Gui)
        var dataMigrationItem = new MenuItem("Data Migration (Coming Soon)", "", null, shortcut: Key.Null);
        var solutionsItem = new MenuItem("Solutions (Coming Soon)", "", null);
        var pluginTracesItem = new MenuItem("Plugin Traces (Coming Soon)", "", null);

        // Tools menu (always present) - contains all workspaces/tools
        menuItems.Add(new MenuBarItem("_Tools", new MenuItem[]
        {
            new("SQL Query", "Run SQL queries against Dataverse", () => NavigateToSqlQuery()),
            dataMigrationItem,
            new("", "", () => {}, null, null, Key.Null), // Separator
            solutionsItem,
            pluginTracesItem,
        }));

        // Help menu (always present)
        menuItems.Add(new MenuBarItem("_Help", new MenuItem[]
        {
            new("About", "About PPDS", () => ShowAbout()),
            new("Keyboard Shortcuts", "Show keyboard shortcuts (F1)", () => ShowKeyboardShortcuts()),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new("Error Log", "View recent errors and debug log (F12)", () => ShowErrorDetails()),
        }));

        _menuBar = new MenuBar(menuItems.ToArray());
        _menuBar.ColorScheme = TuiColorPalette.MenuBar;

        // Register MenuBar with HotkeyRegistry for Alt key suppression
        _hotkeyRegistry.SetMenuBar(_menuBar);

        // Add debounce handler to prevent double-click flicker
        _menuBar.MouseClick += (e) =>
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

        Add(_menuBar);
    }

    /// <summary>
    /// Closes the menu bar dropdown using reflection to call Terminal.Gui's internal CloseAllMenus().
    /// This prevents state corruption when quitting while menu is open.
    /// </summary>
    private void CloseMenuBar()
    {
        if (_menuBar == null) return;

        try
        {
            var closeMethod = typeof(MenuBar).GetMethod(
                "CloseAllMenus",
                BindingFlags.NonPublic | BindingFlags.Instance);
            closeMethod?.Invoke(_menuBar, null);
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Failed to close menu: {ex.Message}");
        }
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
    }

    private void NavigateToSqlQuery()
    {
        // Clear splash or main menu content
        ClearSplashAndMainMenu();

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

            if (url != null)
            {
                _errorService.FireAndForget(SetEnvironmentAsync(url, name), "SetEnvironment");
            }
        }
    }

    private void ShowProfileCreation()
    {
        var profileService = _session.GetProfileService();
        var envService = _session.GetEnvironmentService();

        using var dialog = new ProfileCreationDialog(profileService, envService, _deviceCodeCallback);
        Application.Run(dialog);

        if (dialog.CreatedProfile != null)
        {
            var envUrl = dialog.SelectedEnvironmentUrl ?? dialog.CreatedProfile.EnvironmentUrl;
            var envName = dialog.SelectedEnvironmentName ?? dialog.CreatedProfile.EnvironmentName;

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
            var profiles = await profileService.GetProfilesAsync();
            var active = profiles.FirstOrDefault(p => p.IsActive);

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
        var collection = await store.LoadAsync(CancellationToken.None);
        var profile = collection.ActiveProfile;

        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
        });
    }

    private void ShowAbout()
    {
        using var dialog = new AboutDialog();
        Application.Run(dialog);
    }

    private void ShowKeyboardShortcuts()
    {
        using var dialog = new KeyboardShortcutsDialog();
        Application.Run(dialog);
    }

    private void OnErrorOccurred(TuiError error)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _hasError = true;
            _statusLine.SetMessage($"Error: {error.BriefSummary} (F12 for details)");
        });
    }

    private void ShowErrorDetails()
    {
        using var dialog = new ErrorDetailsDialog(_errorService);
        Application.Run(dialog);

        _hasError = false;
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
            HasErrors: _hasError,
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

            // Unsubscribe from tab manager events before disposing
            _tabManager.ActiveTabChanged -= OnActiveTabChanged;
            _tabManager.Dispose();
            _tabBar.Dispose();

            _errorService.ErrorOccurred -= OnErrorOccurred;
        }

        base.Dispose(disposing);
    }
}
