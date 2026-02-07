# TUI Foundation Refactoring

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-02-05
**Code:** [src/PPDS.Cli/Tui/](../src/PPDS.Cli/Tui/)

---

## Overview

Foundational refactoring of the TUI layer before scaling out to multiple screens (Plugin Traces, Plugin Registration, Solutions, Environment Dashboard, Data Migration). Introduces multi-environment session support, a screen base class, tab infrastructure, a splash screen, and consolidates duplicated patterns.

### Goals

- **Multi-Environment Sessions**: Multiple screens can hold live connections to different environments simultaneously
- **Screen Base Class**: Eliminate boilerplate that every new screen would copy from SqlQueryScreen
- **Tab Navigation**: Users can have SQL-DEV, SQL-PROD, and Migration-QA open concurrently
- **Splash Screen**: Branded startup experience while session initializes
- **Pattern Consolidation**: Extract repeated fire-and-forget, service caching, and cancellation patterns

### Non-Goals

- New screen implementations (Plugin Traces, Solutions, etc.) — those follow after this work
- Terminal.Gui v2 migration
- VS Code extension changes
- PTY-based E2E testing

---

## Architecture

### Current

```
TuiShell (single screen stack)
    └─ Screen ──▶ InteractiveSession ──▶ ServiceProvider (1 per session)
                                             └─ ConnectionPool
```

### After

```
TuiShell
    └─ TabManager
         ├─ Tab 1: [SQL Screen ──▶ env: DEV]  ──┐
         ├─ Tab 2: [SQL Screen ──▶ env: PROD] ──┼──▶ InteractiveSession
         └─ Tab 3: [Migration  ──▶ env: QA]   ──┘        │
                                                     ConcurrentDictionary<url, ServiceProvider>
                                                          ├─ SP (DEV)  ──▶ Pool
                                                          ├─ SP (PROD) ──▶ Pool
                                                          └─ SP (QA)   ──▶ Pool
```

### Components

| Component | Change | Responsibility |
|-----------|--------|----------------|
| `InteractiveSession` | Modified | Cache multiple ServiceProviders keyed by URL |
| `TuiScreenBase` | **New** | Abstract base implementing ITuiScreen boilerplate |
| `TabManager` | **New** | Manages tab lifecycle, switching, tab bar rendering |
| `TabBar` | **New** | View component rendering tab strip with environment colors |
| `SplashScreen` | **New** | Branded startup view shown during initialization |
| `AsyncHelper` | **New** | FireAndForget extension method with error reporting |
| `TuiShell` | Modified | Delegates to TabManager instead of screen stack |
| `SqlQueryScreen` | Modified | Inherits TuiScreenBase, removes duplicated boilerplate |

### Dependencies

- Depends on: [tui.md](./tui.md) for existing TUI patterns
- Depends on: [architecture.md](./architecture.md) for Application Services pattern
- Depends on: [connection-pooling.md](./connection-pooling.md) for multi-pool management

---

## Specification

### Step 1: AsyncHelper — Fire-and-Forget Consolidation

**Problem:** The `#pragma disable PPDS013` / `ContinueWith(t => { if (t.IsFaulted) ... })` pattern is copy-pasted ~10 times with slight variations across TuiShell and SqlQueryScreen.

**Solution:** Static extension method on `ITuiErrorService`:

```csharp
// src/PPDS.Cli/Tui/Infrastructure/AsyncHelper.cs
internal static class AsyncHelper
{
    public static void FireAndForget(
        this ITuiErrorService errorService,
        Task task,
        string context,
        [CallerMemberName] string? caller = null)
    {
#pragma warning disable PPDS013
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                errorService.ReportError(
                    $"Background operation failed",
                    t.Exception,
                    $"{context} (from {caller})");
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }
}
```

**Usage (before):**
```csharp
#pragma warning disable PPDS013
_ = SetActiveProfileAsync(profile).ContinueWith(t =>
{
    if (t.IsFaulted)
    {
        _errorService.ReportError("Failed to switch profile", t.Exception, "SwitchProfile");
    }
}, TaskScheduler.Default);
#pragma warning restore PPDS013
```

**Usage (after):**
```csharp
_errorService.FireAndForget(SetActiveProfileAsync(profile), "SwitchProfile");
```

**Files:**
- Create: `src/PPDS.Cli/Tui/Infrastructure/AsyncHelper.cs`
- Modify: `TuiShell.cs` — replace all 5 fire-and-forget instances
- Modify: `SqlQueryScreen.cs` — replace fire-and-forget instances
- Modify: `PpdsApplication.cs` — replace session init fire-and-forget

**Tests:**
```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public async Task FireAndForget_ReportsError_OnFaultedTask()
{
    var errorService = new TuiErrorService();
    var faultedTask = Task.FromException(new InvalidOperationException("test"));

    errorService.FireAndForget(faultedTask, "TestContext");

    // Allow continuation to run
    await Task.Delay(50);
    Assert.Single(errorService.RecentErrors);
    Assert.Contains("TestContext", errorService.RecentErrors[0].Context);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void FireAndForget_DoesNotThrow_OnSuccessfulTask()
{
    var errorService = new TuiErrorService();
    errorService.FireAndForget(Task.CompletedTask, "TestContext");
    Assert.Empty(errorService.RecentErrors);
}
```

---

### Step 2: Cache Local Services in InteractiveSession

**Problem:** `GetProfileService()`, `GetEnvironmentService()`, `GetThemeService()`, and `GetQueryHistoryService()` return new instances on every call. Two callers can hold different instances with different in-memory state.

**Solution:** Apply the same lazy-singleton pattern already used by `GetErrorService()` and `GetHotkeyRegistry()`.

**Changes to `InteractiveSession.cs`:**

```csharp
// Add fields
private IProfileService? _profileService;
private IEnvironmentService? _environmentService;
private ITuiThemeService? _themeService;
private IQueryHistoryService? _queryHistoryService;

// Change methods
public IProfileService GetProfileService()
{
    return _profileService ??= new ProfileService(_profileStore, NullLogger<ProfileService>.Instance);
}

public IEnvironmentService GetEnvironmentService()
{
    return _environmentService ??= new EnvironmentService(_profileStore, NullLogger<EnvironmentService>.Instance);
}

public ITuiThemeService GetThemeService()
{
    return _themeService ??= new TuiThemeService();
}

public IQueryHistoryService GetQueryHistoryService()
{
    return _queryHistoryService ??= new QueryHistoryService(NullLogger<QueryHistoryService>.Instance);
}
```

**Also add `IExportService` to session** (currently instantiated directly in SqlQueryScreen):

```csharp
private IExportService? _exportService;

public IExportService GetExportService()
{
    return _exportService ??= new ExportService(NullLogger<ExportService>.Instance);
}
```

**Files:**
- Modify: `InteractiveSession.cs` — add cached fields and change getters
- Modify: `SqlQueryScreen.cs` — replace `new ExportService(...)` with `_session.GetExportService()`

**Tests:**
```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void GetProfileService_ReturnsSameInstance()
{
    var session = CreateTestSession();
    var first = session.GetProfileService();
    var second = session.GetProfileService();
    Assert.Same(first, second);
}
```

---

### Step 3: Multi-Environment ServiceProvider Caching

**Problem:** `InteractiveSession.GetServiceProviderAsync()` disposes the previous `ServiceProvider` when the environment URL changes. This prevents concurrent multi-environment workflows.

**Solution:** Replace single `_serviceProvider` + `_currentEnvironmentUrl` with a `ConcurrentDictionary<string, ServiceProvider>` keyed by environment URL. Multiple screens can hold connections to different environments simultaneously.

**Changes to `InteractiveSession.cs`:**

```csharp
// Replace:
private ServiceProvider? _serviceProvider;
private string? _currentEnvironmentUrl;

// With:
private readonly ConcurrentDictionary<string, ServiceProvider> _providers = new();
```

**Modified `GetServiceProviderAsync`:**

```csharp
public async Task<ServiceProvider> GetServiceProviderAsync(
    string environmentUrl,
    CancellationToken cancellationToken = default)
{
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Fast path: provider already cached
    if (_providers.TryGetValue(environmentUrl, out var existing))
    {
        return existing;
    }

    // Slow path: create new provider (serialized per URL via SemaphoreSlim)
    await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        // Double-check after acquiring lock
        if (_providers.TryGetValue(environmentUrl, out existing))
        {
            return existing;
        }

        var provider = await _serviceProviderFactory.CreateAsync(
            string.IsNullOrEmpty(_profileName) ? null : _profileName,
            environmentUrl,
            _deviceCodeCallback,
            _beforeInteractiveAuth,
            cancellationToken).ConfigureAwait(false);

        _providers[environmentUrl] = provider;
        return provider;
    }
    finally
    {
        _lock.Release();
    }
}
```

**Modified `InvalidateAsync`** — invalidates a specific URL or all:

```csharp
public async Task InvalidateAsync(
    string? environmentUrl = null,
    [CallerMemberName] string? caller = null)
{
    await _lock.WaitAsync().ConfigureAwait(false);
    try
    {
        if (environmentUrl != null)
        {
            if (_providers.TryRemove(environmentUrl, out var provider))
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            // Invalidate all
            foreach (var kvp in _providers)
            {
                await kvp.Value.DisposeAsync().ConfigureAwait(false);
            }
            _providers.Clear();
        }
    }
    finally
    {
        _lock.Release();
    }
}
```

**Modified `SetActiveProfileAsync`** — invalidate all providers (credentials changed):

```csharp
// Profile change invalidates ALL cached providers since credentials differ
await InvalidateAsync().ConfigureAwait(false);
```

**Modified `SetEnvironmentAsync`** — no longer invalidates (just updates current URL for status bar):

```csharp
// No invalidation needed — provider cache is keyed by URL
// The old environment's provider stays cached for any screens still using it
_currentEnvironmentUrl = environmentUrl;
_currentEnvironmentDisplayName = displayName;
```

**Modified `DisposeAsync`** — dispose all cached providers with timeout:

```csharp
foreach (var kvp in _providers)
{
    try
    {
        var disposeTask = kvp.Value.DisposeAsync().AsTask();
        await Task.WhenAny(disposeTask, Task.Delay(DisposeTimeout)).ConfigureAwait(false);
    }
    catch { /* log and continue */ }
}
_providers.Clear();
```

**Files:**
- Modify: `InteractiveSession.cs` — replace single provider with dictionary
- Modify: `InteractiveSessionLifecycleTests.cs` — update tests for multi-provider behavior

**Tests:**
```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public async Task GetServiceProvider_CachesByUrl_IndependentProviders()
{
    var factory = new MockServiceProviderFactory();
    var session = CreateTestSession(factory);

    var providerA = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
    var providerB = await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");
    var providerA2 = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");

    Assert.Same(providerA, providerA2);       // Same URL reuses provider
    Assert.NotSame(providerA, providerB);      // Different URL gets new provider
    Assert.Equal(2, factory.CreationLog.Count); // Only 2 created
}

[Fact]
[Trait("Category", "TuiUnit")]
public async Task SetActiveProfile_InvalidatesAllProviders()
{
    var factory = new MockServiceProviderFactory();
    var session = CreateTestSession(factory);

    await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
    await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");
    await session.SetActiveProfileAsync("newprofile", "https://dev.crm.dynamics.com");

    // Next call should create a fresh provider
    await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
    Assert.Equal(3, factory.CreationLog.Count); // 2 initial + 1 after invalidation
}
```

---

### Step 4: TuiScreenBase — Abstract Screen Base Class

**Problem:** Every new screen must duplicate: content View setup, hotkey registration list + cleanup, session/errorService refs, dispose pattern, environment subscription, IHotkeyRegistry management. SqlQueryScreen is 888 lines, much of it boilerplate.

**Solution:** Abstract base class implementing common ITuiScreen patterns.

```csharp
// src/PPDS.Cli/Tui/Screens/TuiScreenBase.cs
internal abstract class TuiScreenBase : ITuiScreen
{
    protected readonly InteractiveSession Session;
    protected readonly ITuiErrorService ErrorService;
    private readonly List<IDisposable> _hotkeyRegistrations = new();
    private readonly CancellationTokenSource _screenCts = new();
    private IHotkeyRegistry? _hotkeyRegistry;
    private bool _disposed;

    public View Content { get; }
    public abstract string Title { get; }
    public virtual MenuBarItem[]? ScreenMenuItems => null;
    public virtual Action? ExportAction => null;

    public event Action? CloseRequested;
    public event Action? MenuStateChanged;

    /// <summary>The environment URL this screen is bound to.</summary>
    public string? EnvironmentUrl { get; protected set; }

    /// <summary>Cancellation token that fires when the screen is closed/disposed.</summary>
    protected CancellationToken ScreenCancellation => _screenCts.Token;

    protected TuiScreenBase(InteractiveSession session, string? environmentUrl = null)
    {
        Session = session;
        ErrorService = session.GetErrorService();
        EnvironmentUrl = environmentUrl ?? session.CurrentEnvironmentUrl;

        Content = new View
        {
            X = 0, Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = TuiColorPalette.Default
        };
    }

    public void OnActivated(IHotkeyRegistry hotkeyRegistry)
    {
        _hotkeyRegistry = hotkeyRegistry;
        RegisterHotkeys(hotkeyRegistry);
    }

    public void OnDeactivating()
    {
        foreach (var reg in _hotkeyRegistrations)
            reg.Dispose();
        _hotkeyRegistrations.Clear();
        _hotkeyRegistry = null;
    }

    /// <summary>Override to register screen-scope hotkeys.</summary>
    protected abstract void RegisterHotkeys(IHotkeyRegistry registry);

    /// <summary>Helper to register a hotkey and auto-track for cleanup.</summary>
    protected void RegisterHotkey(IHotkeyRegistry registry, Key key, string description, Action handler)
    {
        _hotkeyRegistrations.Add(
            registry.Register(key, HotkeyScope.Screen, description, handler, owner: this));
    }

    /// <summary>Raises CloseRequested event.</summary>
    protected void RequestClose() => CloseRequested?.Invoke();

    /// <summary>Raises MenuStateChanged event.</summary>
    protected void NotifyMenuChanged() => MenuStateChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _screenCts.Cancel();
        _screenCts.Dispose();
        OnDeactivating();
        OnDispose();
        Content.Dispose();
    }

    /// <summary>Override for screen-specific cleanup.</summary>
    protected virtual void OnDispose() { }
}
```

**Migration of SqlQueryScreen:**

SqlQueryScreen changes from implementing ITuiScreen directly to inheriting TuiScreenBase. Remove:
- Manual `_content` View creation (~5 lines)
- Manual `_hotkeyRegistrations` list + `OnDeactivating()` cleanup (~15 lines)
- Manual `_session` and `_errorService` fields (~4 lines)
- Manual `_disposed` flag and disposal pattern (~10 lines)
- Manual `CancellationToken.None` → use `ScreenCancellation`

Keep: All screen-specific logic (query execution, results, filter, etc.)

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/TuiScreenBase.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/TuiScreenBaseState.cs` (if needed)
- Modify: `SqlQueryScreen.cs` — inherit from TuiScreenBase
- Modify: `ITuiScreenContractTests.cs` — verify base class implements interface

**Tests:**
```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void TuiScreenBase_DisposeCancelsToken()
{
    var session = CreateTestSession();
    var screen = new TestScreen(session);
    var token = screen.ScreenCancellation;

    Assert.False(token.IsCancellationRequested);
    screen.Dispose();
    Assert.True(token.IsCancellationRequested);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void TuiScreenBase_OnDeactivating_CleansUpHotkeys()
{
    var session = CreateTestSession();
    var screen = new TestScreen(session);
    var registry = new HotkeyRegistry();

    screen.OnActivated(registry);
    Assert.NotEmpty(registry.GetAllBindings());

    screen.OnDeactivating();
    Assert.Empty(registry.GetAllBindings());
}

// Test implementation
private sealed class TestScreen : TuiScreenBase
{
    public override string Title => "Test";
    public TestScreen(InteractiveSession session) : base(session) { }
    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.F5, "Refresh", () => { });
    }
}
```

---

### Step 5: Splash Screen

**Problem:** The TUI starts with a blank main menu while `InteractiveSession.InitializeAsync()` runs in the background. No branding, no indication of what's happening.

**Solution:** A splash screen shown during startup that displays the PPDS brand and initialization status, then transitions to the main menu (or directly to a tab) when ready.

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│                                                          │
│           ██████  ██████  ██████  ███████                │
│           ██   ██ ██   ██ ██   ██ ██                     │
│           ██████  ██████  ██   ██ ███████                │
│           ██      ██      ██   ██      ██                │
│           ██      ██      ██████  ███████                │
│                                                          │
│            Power Platform Developer Suite                 │
│                                                          │
│                  ⠋ Connecting...                          │
│                                                          │
│                    v1.0.0-beta.12                         │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

**Implementation:**

```csharp
// src/PPDS.Cli/Tui/Views/SplashView.cs
internal sealed class SplashView : View, ITuiStateCapture<SplashViewState>
{
    private readonly TuiSpinner _spinner;
    private readonly Label _statusLabel;
    private readonly Label _versionLabel;

    public void SetStatus(string message);  // "Connecting...", "Loading profile...", etc.
    public void SetReady();                 // Stops spinner, shows "Ready"
    public SplashViewState CaptureState();
}
```

**Integration with PpdsApplication/TuiShell:**

1. TuiShell shows `SplashView` as initial content (instead of main menu)
2. `InteractiveSession.InitializeAsync()` reports progress via callback
3. On completion, splash transitions to main menu with fade (or immediate swap)
4. If profile has environment configured, splash shows "Connecting to {env}..."
5. If no profile, splash shows "No profile configured — press Alt+P to get started"

**Files:**
- Create: `src/PPDS.Cli/Tui/Views/SplashView.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/SplashViewState.cs`
- Modify: `TuiShell.cs` — show splash on startup instead of main menu

**State record:**
```csharp
public sealed record SplashViewState(
    string StatusMessage,
    bool IsReady,
    string? Version,
    bool SpinnerActive);
```

**Tests:**
```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void SplashView_InitialState_ShowsConnecting()
{
    var splash = new SplashView();
    var state = splash.CaptureState();

    Assert.False(state.IsReady);
    Assert.True(state.SpinnerActive);
    Assert.NotNull(state.Version);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void SplashView_SetReady_StopsSpinner()
{
    var splash = new SplashView();
    splash.SetReady();
    var state = splash.CaptureState();

    Assert.True(state.IsReady);
    Assert.False(state.SpinnerActive);
}
```

---

### Step 6: Tab Infrastructure

**Problem:** TuiShell uses a stack-based navigation model (push/pop). Only one screen is visible at a time. Users need concurrent multi-environment workflows: SQL against DEV while migrating QA while querying PROD.

**Context:** Terminal.Gui 1.19 does not include a TabView control. The environment dashboard spec already documents this constraint and uses RadioGroup + hotkeys as a workaround.

**Solution:** Custom tab bar View + TabManager that coordinates tab lifecycle.

**Tab Bar UI:**
```
┌─[1: SQL DEV]─[2: SQL PROD]─[3: Migration QA]─[+]────────┐
│                                                            │
│  (active tab's screen content)                             │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

- Each tab label is color-coded by environment type (green=dev, red=prod, yellow=sandbox)
- `[+]` button opens new tab dialog
- Active tab is highlighted
- Tabs show `[x]` close button on hover/focus

**Components:**

```csharp
// src/PPDS.Cli/Tui/Infrastructure/TabManager.cs
internal sealed class TabManager
{
    private readonly List<TabInfo> _tabs = new();
    private int _activeIndex = -1;

    public event Action? TabsChanged;       // Tab list changed (add/remove)
    public event Action? ActiveTabChanged;   // Active tab switched

    public void AddTab(ITuiScreen screen, string? environmentUrl);
    public void CloseTab(int index);
    public void ActivateTab(int index);
    public void ActivateNext();              // Ctrl+Tab
    public void ActivatePrevious();          // Ctrl+Shift+Tab

    public TabInfo? ActiveTab { get; }
    public IReadOnlyList<TabInfo> Tabs { get; }
}

public sealed record TabInfo(
    ITuiScreen Screen,
    string? EnvironmentUrl,
    string? EnvironmentDisplayName,
    EnvironmentType EnvironmentType);
```

```csharp
// src/PPDS.Cli/Tui/Views/TabBar.cs
internal sealed class TabBar : View, ITuiStateCapture<TabBarState>
{
    private readonly TabManager _tabManager;
    private readonly ITuiThemeService _themeService;

    // Renders horizontal row of tab labels
    // Each label colored by environment type
    // Click to switch, [x] to close, [+] to add
}
```

**Keyboard Shortcuts:**
| Key | Action |
|-----|--------|
| `Ctrl+T` | New tab (shows screen/environment picker) |
| `Ctrl+W` | Close active tab |
| `Ctrl+Tab` | Next tab |
| `Ctrl+Shift+Tab` | Previous tab |
| `Ctrl+1-9` | Switch to tab by number |

**TuiShell Integration:**

TuiShell changes:
- Remove `_screenStack` — replaced by `TabManager`
- Add `TabBar` below menu bar (Y=1)
- Content area moves down (Y=2 when tabs visible)
- `NavigateTo()` becomes `AddTab()` or opens in current tab
- Status bar reflects active tab's environment
- Main menu shows when no tabs are open

**Screen-Tab Relationship:**

Each tab owns one screen instance. The screen's `EnvironmentUrl` (from TuiScreenBase) determines which cached ServiceProvider it uses. When the user opens "New SQL Query" from the Tools menu:

1. Show environment picker dialog (or use current environment)
2. Create `new SqlQueryScreen(session, environmentUrl)`
3. `tabManager.AddTab(screen, environmentUrl)`
4. Tab bar renders with environment-colored label

**Files:**
- Create: `src/PPDS.Cli/Tui/Infrastructure/TabManager.cs`
- Create: `src/PPDS.Cli/Tui/Views/TabBar.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/TabBarState.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/TabManagerState.cs`
- Modify: `TuiShell.cs` — replace screen stack with TabManager
- Modify: `TuiShell.cs` — add TabBar to layout

**State records:**
```csharp
public sealed record TabBarState(
    int TabCount,
    int ActiveIndex,
    IReadOnlyList<TabSummary> Tabs);

public sealed record TabSummary(
    string ScreenType,
    string Title,
    string? EnvironmentUrl,
    EnvironmentType EnvironmentType,
    bool IsActive);
```

**Tests:**
```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void TabManager_AddTab_SetsAsActive()
{
    var manager = new TabManager();
    var screen = new TestScreen(CreateTestSession());

    manager.AddTab(screen, "https://dev.crm.dynamics.com");

    Assert.Equal(1, manager.Tabs.Count);
    Assert.Equal(0, manager.ActiveTab?.Index);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void TabManager_CloseTab_ActivatesAdjacent()
{
    var manager = new TabManager();
    manager.AddTab(new TestScreen(CreateTestSession()), "https://dev.crm.dynamics.com");
    manager.AddTab(new TestScreen(CreateTestSession()), "https://prod.crm.dynamics.com");
    manager.ActivateTab(0);

    manager.CloseTab(0);

    Assert.Equal(1, manager.Tabs.Count);
    Assert.NotNull(manager.ActiveTab);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void TabManager_MultipleEnvironments_IndependentScreens()
{
    var manager = new TabManager();
    var screenDev = new TestScreen(CreateTestSession());
    var screenProd = new TestScreen(CreateTestSession());

    manager.AddTab(screenDev, "https://dev.crm.dynamics.com");
    manager.AddTab(screenProd, "https://prod.crm.dynamics.com");

    Assert.Equal("https://dev.crm.dynamics.com", manager.Tabs[0].EnvironmentUrl);
    Assert.Equal("https://prod.crm.dynamics.com", manager.Tabs[1].EnvironmentUrl);
}
```

---

### Step 7: Remove Duplicate Environment State from TuiShell

**Problem:** `TuiShell` tracks `_environmentName` and `_environmentUrl` separately from `InteractiveSession`. They can drift out of sync (e.g., `RefreshProfileState` updates shell state without going through session).

**Solution:** After tabs are in place, TuiShell reads environment info from the active tab (which reads from its screen's `EnvironmentUrl`). Remove `_environmentName` and `_environmentUrl` fields from TuiShell entirely.

The status bar reads from `tabManager.ActiveTab?.EnvironmentUrl` and uses `ITuiThemeService` to determine colors. The `_hasError` flag that gates Alt+E behavior should be scoped to the status line, not the shell — errors shouldn't block environment switching.

**Files:**
- Modify: `TuiShell.cs` — remove `_environmentName`, `_environmentUrl`, `_hasError`
- Modify: `TuiStatusBar.cs` — accept environment info from active tab context
- Modify: `TuiShell.cs` — Alt+E always opens environment selector; errors shown via F12 only

---

## Implementation Order

| Step | What | Risk | Dependencies |
|------|------|------|-------------|
| 1 | AsyncHelper | Low | None |
| 2 | Cache local services | Low | None |
| 3 | Multi-env session | Medium | None (but step 6 uses it) |
| 4 | TuiScreenBase | Medium | Step 1 (uses FireAndForget) |
| 5 | Splash screen | Low | None |
| 6 | Tab infrastructure | High | Steps 3, 4 |
| 7 | Remove duplicate state | Low | Step 6 |

Steps 1, 2, 3, and 5 are independent and can be done in parallel.
Step 4 depends on step 1.
Step 6 depends on steps 3 and 4.
Step 7 depends on step 6.

---

## Design Decisions

### Why Multi-Provider Cache Instead of Session-Per-Tab?

**Context:** Could create separate `InteractiveSession` per tab, or share one session with multiple providers.

**Decision:** Shared session with `ConcurrentDictionary<string, ServiceProvider>`.

**Alternatives considered:**
- Session-per-tab: Rejected — duplicates profile management, error service, and theme service unnecessarily. Auth state (MSAL token cache) is process-global anyway.
- Single provider with URL switching: Rejected — this is the current design and doesn't support concurrent environments.

**Consequences:**
- Positive: Shared infrastructure (error service, profiles, hotkeys) across all tabs
- Positive: Connection pools stay warm when switching between tabs
- Negative: Profile switch invalidates ALL providers (acceptable — credentials change)

### Why Custom Tab Bar Instead of Terminal.Gui v2 Migration?

**Context:** Terminal.Gui v2 has a built-in TabView but requires significant API migration.

**Decision:** Build a simple custom tab bar on Terminal.Gui 1.19.

**Alternatives considered:**
- Terminal.Gui v2 migration: Rejected — different API surface, would delay all screen work. Can migrate later.
- tmux-style splits: Rejected — complex to build, unfamiliar UX for non-terminal-power-users
- No tabs (defer to VS Code): Rejected — terminal users need multi-environment too

**Consequences:**
- Positive: Ships faster, no framework migration risk
- Positive: Custom control tailored to our needs (environment colors, etc.)
- Negative: Must maintain custom tab rendering code

### Why Splash Screen?

**Context:** Cold start requires authentication and connection pool warmup. Users see an empty main menu during this time.

**Decision:** Branded splash with progress indication.

**Consequences:**
- Positive: Professional first impression
- Positive: User knows something is happening during auth
- Positive: Can show actionable status ("No profile — press Alt+P")

---

## Testing

### Acceptance Criteria

- [ ] All existing 49 TuiUnit tests continue to pass
- [ ] AsyncHelper tests verify error propagation and success paths
- [ ] Service caching tests verify same-instance behavior
- [ ] Multi-provider tests verify concurrent environments with independent providers
- [ ] TuiScreenBase tests verify lifecycle (hotkey cleanup, cancellation, dispose)
- [ ] Splash view tests verify state transitions
- [ ] TabManager tests verify add/close/switch/multi-environment
- [ ] SqlQueryScreen still works after TuiScreenBase migration
- [ ] Build succeeds with zero errors across net8.0, net9.0, net10.0

### Test Commands

```bash
# TUI unit tests
dotnet test tests/PPDS.Cli.Tests --filter "Category=TuiUnit" --verbosity quiet

# Full build
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo
```

---

## Related Specs

- [tui.md](./tui.md) — Existing TUI spec (will be updated after this work)
- [architecture.md](./architecture.md) — Application Services pattern
- [connection-pooling.md](./connection-pooling.md) — Pool management
- [tui-plugin-traces.md](./tui-plugin-traces.md) — First screen to use new foundation

---

## Roadmap

After this foundation work:
- Plugin Traces screen (uses TuiScreenBase + tabs)
- Plugin Registration screen
- Solutions screen
- Environment Dashboard screen
- Data Migration screen
- Update tui.md spec to reflect tab architecture
