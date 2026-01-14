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
    private string? _environmentName;
    private string? _environmentUrl;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession _session;
    private readonly ITuiThemeService _themeService;
    private readonly ITuiErrorService _errorService;
    private readonly IHotkeyRegistry _hotkeyRegistry;
    private readonly List<IDisposable> _hotkeyRegistrations = new();

    private readonly TuiStatusBar _statusBar;
    private readonly TuiStatusLine _statusLine;
    private readonly FrameView _contentArea;
    private MenuBar? _menuBar;

    private readonly Stack<ITuiScreen> _screenStack = new();
    private ITuiScreen? _currentScreen;
    private View? _mainMenuContent;
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

        // Content area where screens are displayed
        _contentArea = new FrameView("Main Menu")
        {
            X = 0,
            Y = 1, // Below menu bar
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2, // Leave room for status line and status bar
            ColorScheme = TuiColorPalette.Default
        };

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

        // Show main menu content
        ShowMainMenu();

        Add(_contentArea, _statusBar, _statusLine);

        // Register global hotkeys
        RegisterGlobalHotkeys();

        // Load initial profile info
        LoadProfileInfoAsync();
    }

    /// <summary>
    /// Navigates to a screen, pushing the current screen onto the stack.
    /// </summary>
    public void NavigateTo(ITuiScreen screen)
    {
        // Deactivate current screen (if any) and push to stack
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
            _screenStack.Push(_currentScreen);
        }
        else if (_mainMenuContent != null)
        {
            // Clear main menu content
            _contentArea.Remove(_mainMenuContent);
            _mainMenuContent = null;
        }

        // Activate new screen
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
    /// Navigates back to the previous screen, or to the main menu if no previous screen.
    /// </summary>
    public void NavigateBack()
    {
        // Deactivate and dispose current screen
        if (_currentScreen != null)
        {
            _currentScreen.CloseRequested -= OnScreenCloseRequested;
            _currentScreen.MenuStateChanged -= OnScreenMenuStateChanged;
            _currentScreen.OnDeactivating();
            _contentArea.Remove(_currentScreen.Content);
            _currentScreen.Dispose();
            _currentScreen = null;
        }

        if (_screenStack.Count == 0)
        {
            // No previous screen - show main menu
            ShowMainMenu();
            return;
        }

        // Pop and activate previous screen
        _currentScreen = _screenStack.Pop();
        _currentScreen.CloseRequested += OnScreenCloseRequested;
        _currentScreen.MenuStateChanged += OnScreenMenuStateChanged;
        _hotkeyRegistry.SetActiveScreen(_currentScreen);
        _contentArea.Title = _currentScreen.Title;
        _contentArea.Add(_currentScreen.Content);
        _currentScreen.OnActivated(_hotkeyRegistry);

        RebuildMenuBar();
        _currentScreen.Content.SetFocus();
    }

    private void ShowMainMenu()
    {
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
            Key.F12,
            HotkeyScope.Global,
            "Error log",
            ShowErrorDetails));
    }

    private void NavigateToSqlQuery()
    {
        // Prevent navigation if already on SQL Query screen
        if (_currentScreen is SqlQueryScreen)
            return;

        // Clear current content
        if (_mainMenuContent != null)
        {
            _contentArea.Remove(_mainMenuContent);
            _mainMenuContent = null;
        }

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
        if (_hasError)
        {
            ShowErrorDetails();
        }
        else
        {
            ShowEnvironmentSelector();
        }
    }

    private void ShowProfileSelector()
    {
        var service = _session.GetProfileService();
        using var dialog = new ProfileSelectorDialog(service, _session);

        Application.Run(dialog);

        if (dialog.SelectedProfile != null)
        {
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

    private async Task SetActiveProfileAsync(ProfileSummary profile)
    {
        _environmentName = profile.EnvironmentName;
        _environmentUrl = profile.EnvironmentUrl;

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
        _environmentName = environmentName;
        _environmentUrl = environmentUrl;

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
        _environmentUrl = url;
        _environmentName = displayName;

        await _session.SetEnvironmentAsync(url, displayName);

        Application.MainLoop?.Invoke(() =>
        {
            _statusBar.Refresh();
        });
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
                    _environmentName = active.EnvironmentName;
                    _environmentUrl = active.EnvironmentUrl;
                }
                else
                {
                    _environmentName = null;
                    _environmentUrl = null;
                }
                _statusBar.Refresh();
            });
        });
#pragma warning restore PPDS013
    }

    private void LoadProfileInfoAsync()
    {
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
                _environmentName = profile.Environment?.DisplayName;
                _environmentUrl = profile.Environment?.Url;
            }
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
            ScreenStackDepth: _screenStack.Count,
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

            // Dispose current screen
            _currentScreen?.OnDeactivating();
            _currentScreen?.Dispose();
            _currentScreen = null;

            // Dispose stacked screens
            while (_screenStack.Count > 0)
            {
                try
                {
                    var screen = _screenStack.Pop();
                    try
                    {
                        screen.Dispose();
                    }
                    catch
                    {
                        // Continue disposing remaining screens even if one fails
                    }
                }
                catch
                {
                    // Continue attempting to dispose remaining screens even if Pop fails
                }
            }

            _errorService.ErrorOccurred -= OnErrorOccurred;
        }

        base.Dispose(disposing);
    }
}
