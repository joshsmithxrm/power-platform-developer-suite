# TUI

**Status:** Implemented
**Last Updated:** 2026-03-25
**Code:** [src/PPDS.Cli/Tui/](../src/PPDS.Cli/Tui/)
**Surfaces:** TUI

---

## Overview

The PPDS TUI provides an interactive terminal interface for Power Platform development. Built on Terminal.Gui 1.19+, it offers a visual environment for SQL queries, profile management, and environment switching. The TUI shares Application Services with CLI and RPC, ensuring consistent behavior across all interfaces.

### Goals

- **Interactive Exploration**: Visual query builder with real-time results, pagination, and filtering
- **Environment Awareness**: Color-coded status bar distinguishes production (red) from sandbox (yellow) from development (green)
- **Autonomous Testability**: State capture interfaces enable automated testing without visual inspection

### Non-Goals

- Individual dialog documentation (patterns documented here)
- CLI command implementation (covered in [cli.md](./cli.md))
- Application Service internals (covered in [architecture.md](./architecture.md))

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      PpdsApplication                         │
│              (Entry point, Terminal.Gui init)                │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                        TuiShell                              │
│          (Navigation, menu bar, hotkey registry)             │
└───────────────────────────┬─────────────────────────────────┘
                            │
        ┌───────────────────┼───────────────────┐
        ▼                   ▼                   ▼
┌───────────────┐   ┌───────────────┐   ┌───────────────┐
│  ITuiScreen   │   │  TuiDialog    │   │TuiStatusBar   │
│ (Content area)│   │ (Modal popup) │   │ (Bottom bar)  │
└───────┬───────┘   └───────────────┘   └───────────────┘
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│                   InteractiveSession                         │
│        (Connection pool lifecycle, service provider)         │
└───────────────────────────┬─────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                    Application Services                      │
│        (ISqlQueryService, IProfileService, etc.)             │
└─────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PpdsApplication` | Entry point; initializes Terminal.Gui and creates InteractiveSession |
| `TuiShell` | Main window with menu bar, navigation stack, hotkey registry |
| `ITuiScreen` | Interface for content views (SqlQueryScreen, etc.) |
| `TuiDialog` | Base class for modal dialogs with escape handling |
| `InteractiveSession` | Manages connection pool lifecycle across all screens |
| `IHotkeyRegistry` | Context-aware keyboard shortcut management |
| `ITuiThemeService` | Environment detection and color scheme selection |
| `ITuiErrorService` | Thread-safe error collection with event notification |

### Presenter/Handler Extraction Pattern

When a TUI file exceeds ~500 lines, split it into a UI shell and an extracted logic class. The UI shell keeps Terminal.Gui layout and lifecycle; the extracted class owns business logic, state, and validation with zero Terminal.Gui dependencies.

```
┌─────────────────────┐         events          ┌─────────────────────┐
│     UI Shell         │◀────────────────────────│   Presenter/Handler │
│  (Terminal.Gui)      │                         │   (pure C#)         │
│                      │─────── method calls ───▶│                     │
│  - View declarations │                         │  - State management │
│  - Layout            │                         │  - Validation       │
│  - Event wiring      │                         │  - Business logic   │
│  - CaptureState()    │                         │  - Events           │
└──────────────────────┘                         └─────────────────────┘
```

**UI shell keeps:** View field declarations, Terminal.Gui layout/positioning, `Application.MainLoop.Invoke()` marshaling, `CaptureState()` (reads both presenter state and UI state), dialog launching, keyboard event wiring to presenter methods.

**Extracted class gets:** State fields, validation rules, request/response building, async orchestration, events for status changes and results. Constructor takes `InteractiveSession` or equivalent dependencies — same DI the shell had.

**Connection pattern:** The extracted class raises events; the UI shell subscribes and updates views. This mirrors the existing `ITuiErrorService` pattern.

**Naming convention:**
- Screens → `{Name}Presenter` (e.g., `SqlQueryPresenter`)
- Dialogs → `{Name}FormBuilder` (e.g., `AuthMethodFormBuilder`)
- Views → `{Name}Handler` (e.g., `TableKeyboardHandler`)

**File placement:** Same directory as the UI shell. `SqlQueryScreen.cs` and `SqlQueryPresenter.cs` live side by side in `Tui/Screens/`.

**Testability:** Extracted classes are testable with `TuiUnit` trait — no `Application.Init()` required. This is the primary motivation for the pattern.

### Dependencies

- Depends on: [architecture.md](./architecture.md) for Application Services pattern
- Depends on: [authentication.md](./authentication.md) for profile management
- Depends on: [connection-pooling.md](./connection-pooling.md) for Dataverse access

---

## Specification

### Core Requirements

1. TUI launches when `ppds` is invoked without arguments
2. All screens must implement `ITuiScreen` for consistent navigation
3. UI updates from background threads must marshal to main loop via `Application.MainLoop.Invoke()`
4. Connection pool is created once per session and reused across screens
5. Dialogs must inherit from `TuiDialog` for consistent styling and hotkey handling
6. Files exceeding ~500 lines should be split using the Presenter/Handler Extraction Pattern
7. Keyboard shortcuts must follow the three-tier convention (see below)

### Primary Flows

**Application Startup:**

1. **Initialize**: `PpdsApplication.RunAsync()` creates `ProfileStore` and `InteractiveSession`
2. **Pre-Auth Check**: If no active profile, show `PreAuthenticationDialog`
3. **Create Shell**: `TuiShell` creates main window with menu bar
4. **Show Main Menu**: Display `MainMenuScreen` or last active screen
5. **Run Loop**: `Application.Run()` starts Terminal.Gui event loop

**Screen Navigation:**

1. **Navigate**: `TuiShell.NavigateTo(screen)` pushes current screen to stack
2. **Deactivate**: Call `OnDeactivating()` on current screen, unregister hotkeys
3. **Activate**: Call `OnActivated(hotkeyRegistry)` on new screen
4. **Update Menu**: Rebuild menu bar with screen-specific items
5. **Back**: `NavigateBack()` pops stack or returns to main menu

**Query Execution:**

1. **Enter SQL**: User types query in `SqlQueryScreen` text area
2. **Execute**: Ctrl+Enter triggers `ISqlQueryService.ExecuteAsync()`
3. **Display**: Results populate `QueryResultsTableView` with pagination
4. **Filter**: `/` shows filter bar; Enter applies filter to DataView
5. **Export**: File > Export writes results to file via `IExportService`

### Keyboard Shortcut Convention

Three tiers prevent accidental triggers while maintaining speed:

| Context | Modifier | Rationale | Examples |
|---------|----------|-----------|---------|
| Dialogs with text input | `Alt+Letter` | Prevents accidental triggers while typing | `Alt+A` Authenticate |
| List/table views (no text input) | Single letter | Faster navigation, no text input conflict | `/` filter, `q` quit |
| Global shortcuts | `Ctrl+Letter` or `F-key` | Available everywhere, won't conflict with text input | `Ctrl+E` export, `F5` execute |

**Rules:**
- `F-key` alternatives must exist for all `Ctrl+Shift` combos (Linux terminals cannot distinguish `Ctrl+T` from `Ctrl+Shift+T`)
- Single-letter shortcuts must check `HasFocus` — only fire when the non-text-input view has focus
- `Escape` follows context: closes filter → returns to query editor → does nothing (use `Ctrl+W` to close tab)

### Constraints

- Never create new `ServiceClient` per request; use `InteractiveSession.ServiceProvider`
- Never block main thread; use `Task.Run()` for long operations
- Never update UI from background thread; always use `Application.MainLoop.Invoke()`

### Session State

The [`InteractiveSession`](../src/PPDS.Cli/Tui/InteractiveSession.cs) manages:

- `CurrentEnvironmentUrl` - Active Dataverse environment
- `CurrentProfileName` - Active authentication profile
- `ServiceProvider` - Lazy-initialized, reused across queries
- `EnvironmentChanged` / `ProfileChanged` events for UI updates

---

## Core Types

### ITuiScreen

Content views that can be displayed in the shell ([`ITuiScreen.cs:10-58`](../src/PPDS.Cli/Tui/Screens/ITuiScreen.cs#L10-L58)):

```csharp
public interface ITuiScreen : IDisposable
{
    View Content { get; }
    string Title { get; }
    MenuBarItem[]? ScreenMenuItems { get; }
    Action? ExportAction { get; }
    event Action? CloseRequested;
    event Action? MenuStateChanged;
    void OnActivated(IHotkeyRegistry hotkeyRegistry);
    void OnDeactivating();
}
```

Screens provide content and optional menu items. The shell subscribes to events for navigation and menu rebuilding.

### TuiDialog

Base class for modal dialogs ([`TuiDialog.cs:17-60`](../src/PPDS.Cli/Tui/Dialogs/TuiDialog.cs#L17-L60)):

```csharp
public abstract class TuiDialog : Dialog
{
    protected TuiDialog(string title, InteractiveSession? session = null);
    protected virtual void OnEscapePressed();  // Override for custom close behavior
}
```

The base class applies consistent color scheme, registers Escape key handling, and integrates with the hotkey registry to block screen-scope hotkeys while the dialog is open.

**Dialog implementations (14 dialogs):** AboutDialog, EnvironmentSelectorDialog, ProfileCreationDialog, QueryHistoryDialog, ErrorDetailsDialog, ExportDialog, FetchXmlPreviewDialog, KeyboardShortcutsDialog, PreAuthenticationDialog, ProfileDetailsDialog, ProfileSelectorDialog, ClearAllProfilesDialog, EnvironmentDetailsDialog, ReAuthenticationDialog.

### IHotkeyRegistry

Context-aware keyboard shortcut management ([`HotkeyRegistry.cs:46-101`](../src/PPDS.Cli/Tui/Infrastructure/HotkeyRegistry.cs#L46-L101)):

```csharp
public interface IHotkeyRegistry
{
    IDisposable Register(Key key, HotkeyScope scope, string description, Action handler, object? owner = null);
    bool TryHandle(KeyEvent keyEvent);
    IReadOnlyList<HotkeyBinding> GetAllBindings();
    IReadOnlyList<HotkeyBinding> GetContextBindings();
    void SetActiveScreen(object? screen);
    void SetActiveDialog(object? dialog);
    void SetMenuBar(MenuBar? menuBar);
    void SuppressNextAltMenuFocus();
    object? ActiveScreen { get; }
}
```

`HotkeyBinding` record: `Key`, `Scope`, `Description`, `Handler`, `Owner`.

Three scope layers control hotkey priority:
- `Global` - Works everywhere; closes dialogs first
- `Screen` - Only when screen is active (no dialog open)
- `Dialog` - Only when specific dialog is open

### ITuiThemeService

Environment detection and color scheme selection ([`ITuiThemeService.cs:8-48`](../src/PPDS.Cli/Tui/Infrastructure/ITuiThemeService.cs#L8-L48)):

```csharp
public interface ITuiThemeService
{
    EnvironmentType DetectEnvironmentType(string? environmentUrl);
    ColorScheme GetStatusBarScheme(EnvironmentType envType);
    string GetEnvironmentLabel(EnvironmentType envType);
}
```

Environment types with their status bar colors:
- `Production` - Red (high caution)
- `Sandbox` - Yellow (moderate caution)
- `Development` - Green (safe)
- `Trial` - Cyan (temporary)
- `Unknown` - Gray (neutral)

### ITuiErrorService

Thread-safe error collection ([`ITuiErrorService.cs:7-42`](../src/PPDS.Cli/Tui/Infrastructure/ITuiErrorService.cs#L7-L42)):

```csharp
public interface ITuiErrorService
{
    void ReportError(string message, Exception? ex = null, string? context = null);
    IReadOnlyList<TuiError> RecentErrors { get; }
    TuiError? LatestError { get; }
    void ClearErrors();
    event Action<TuiError>? ErrorOccurred;
    string GetLogFilePath();
}
```

The service stores up to 20 recent errors (newest first) and fires events for real-time notification. Errors are also logged to `~/.ppds/tui-debug.log`.

### ITuiStateCapture

Generic interface for autonomous testing ([`ITuiStateCapture.cs:13-20`](../src/PPDS.Cli/Tui/Testing/ITuiStateCapture.cs#L13-L20)):

```csharp
public interface ITuiStateCapture<out TState>
{
    TState CaptureState();  // Snapshot current state for assertions
}
```

Components implementing this interface enable automated testing without visual inspection. See Testing section for details.

### AuthMethodFormBuilder

Extracted from `ProfileCreationDialog` — owns auth method logic with no Terminal.Gui dependencies ([`AuthMethodFormBuilder.cs`](../src/PPDS.Cli/Tui/Dialogs/AuthMethodFormBuilder.cs)):

```csharp
internal sealed class AuthMethodFormBuilder
{
    IReadOnlyList<(string Label, AuthMethod Method)> GetAvailableMethods();
    AuthFieldVisibility GetFieldVisibility(AuthMethod method);
    ValidationResult Validate(AuthMethod method, AuthFormValues values);
    ProfileCreateRequest BuildRequest(AuthMethod method, AuthFormValues values);
}
```

`AuthFormValues` is a plain record holding form field strings. `AuthFieldVisibility` indicates which fields are visible per auth method. `ValidationResult` contains success/failure with error message.

### SqlQueryPresenter

Extracted from `SqlQueryScreen` — owns query execution, pagination, and history with no Terminal.Gui dependencies ([`SqlQueryPresenter.cs`](../src/PPDS.Cli/Tui/Screens/SqlQueryPresenter.cs)):

```csharp
internal sealed class SqlQueryPresenter
{
    string? LastSql { get; }
    bool IsExecuting { get; }
    bool UseTdsEndpoint { get; set; }

    Task ExecuteAsync(string sql, string environmentUrl, CancellationToken ct);
    Task LoadMoreAsync(string environmentUrl, CancellationToken ct);
    Task<string> TranspileSqlAsync(string sql, string environmentUrl, CancellationToken ct);
    Task<QueryPlanDescription> ExplainAsync(string sql, string environmentUrl, CancellationToken ct);
    void ConfirmDml();
    void Cancel();

    event Action<StreamingChunkArgs> ChunkReceived;
    event Action<QueryCompletedArgs> QueryCompleted;
    event Action<QueryFailedArgs> QueryFailed;
    event Action<string> StatusChanged;
}
```

Takes `InteractiveSession` in constructor. The screen subscribes to events and marshals UI updates via `Application.MainLoop.Invoke()`.

### TableKeyboardHandler

Extracted from `QueryResultsTableView` — owns keyboard shortcut logic with `ITableViewState` abstraction ([`TableKeyboardHandler.cs`](../src/PPDS.Cli/Tui/Views/TableKeyboardHandler.cs)):

```csharp
internal interface ITableViewState
{
    int SelectedRow { get; }
    int RowOffset { get; }
    int VisibleHeight { get; }
    bool HasFocus { get; }
    DataTable? Table { get; }
}

internal sealed class TableKeyboardHandler
{
    TableKeyboardHandler(ITableViewState state, Func<string?> getEnvironmentUrl);

    KeyHandleResult HandleKey(Key key);
    CopyResult CopySelection(bool invertHeaders);
    string? GetRecordUrl();
    void ToggleGuidColumns();
}
```

`KeyHandleResult` contains the action taken and a status message for the UI to display. `QueryResultsTableView` implements `ITableViewState` and delegates keyboard events to the handler.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `TuiError` | Any exception in TUI code | Shown in status line; F12 opens details |
| `PpdsAuthException` | Authentication expired | Re-authentication dialog with retry |
| `PpdsThrottleException` | Dataverse throttling | Automatic retry via connection pool |

### Error Flow

1. **Catch**: Screen or dialog catches exception
2. **Report**: Call `ITuiErrorService.ReportError(message, exception, context)`
3. **Display**: Status line shows brief message: `"Error: {BriefSummary} (F12 for details)"`
4. **Details**: F12 opens `ErrorDetailsDialog` with full stack trace

### TuiError Record

Error representation ([`TuiError.cs:12-102`](../src/PPDS.Cli/Tui/Infrastructure/TuiError.cs#L12-L102)):

```csharp
public record TuiError(
    DateTimeOffset Timestamp,
    string Message,
    string? Context,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace);
```

Key methods:
- `FromException(ex, context)` - Creates error with unwrapped AggregateException
- `BriefSummary` - Truncated to 60 chars for status bar
- `GetFullDetails()` - Formatted display with all fields

### Recovery Strategies

- **Authentication errors**: Show re-authentication dialog with pre-filled profile
- **Throttling**: Pool automatically waits and retries
- **Connection errors**: Display error with retry button
- **Validation errors**: Show inline field validation messages

---

## Design Decisions

### Why State Capture for Testing?

**Context:** Terminal.Gui 1.19 doesn't provide good testing APIs. The internal `FakeDriver` is undocumented and unreliable. Manual testing requires users to run `ppds`, perform actions, and share debug logs.

**Decision:** Introduce `ITuiStateCapture<TState>` interface for autonomous testing without visual inspection. Components expose their state as immutable records for assertions.

**Implementation:**
- 20+ state record types in [`Testing/States/`](../src/PPDS.Cli/Tui/Testing/States/)
- All dialogs and key screens implement the interface
- Tests create component, invoke actions, capture state, assert properties

**Test results:**
| Scenario | Result |
|----------|--------|
| Manual feedback loop | Hours (user runs, reports, developer debugs) |
| State capture tests | Seconds (automated, deterministic) |

**Alternatives considered:**
- Terminal.Gui FakeDriver: Rejected - internal/undocumented, unreliable
- Screenshot comparison: Rejected - brittle, environment-dependent
- No automated testing: Rejected - blocks autonomous iteration

**Consequences:**
- Positive: Claude can iterate on TUI bugs without user intervention
- Positive: Fast, deterministic tests run in CI
- Negative: Must maintain state capture alongside UI code
- Negative: Tests don't validate actual rendering

### Why IServiceProviderFactory Abstraction?

**Context:** `InteractiveSession` created dependencies inline, making it untestable. Mocking required modifying production code paths.

**Decision:** Inject `IServiceProviderFactory?` with default fallback to `ProfileBasedServiceProviderFactory`.

**Test mocks created:**
- `MockServiceProviderFactory` - Logs creation calls, returns fakes
- `FakeSqlQueryService` - Returns configurable results
- `FakeQueryHistoryService` - In-memory storage
- `FakeExportService` - Tracks export operations
- `TempProfileStore` - Isolated profile storage with temp directory

**Consequences:**
- Positive: Session lifecycle fully testable
- Positive: Fake services enable deterministic test scenarios
- Negative: Additional DI complexity in `InteractiveSession`

### Why Three-Layer Hotkey Scope?

**Context:** Global hotkeys could fire while dialogs were open, causing state corruption. Screen-specific hotkeys interfered with dialog input.

**Decision:** Three scope layers with priority: Dialog > Screen > Global.

**Key design insight:** Global handlers that would open dialogs must defer execution. If a dialog is already open, the global handler only closes the dialog—user must press again to trigger action. This prevents overlapping `Application.Run()` calls that corrupt Terminal.Gui's `ConsoleDriver` state.

**Implementation** ([`HotkeyRegistry.cs:248-291`](../src/PPDS.Cli/Tui/Infrastructure/HotkeyRegistry.cs#L248-L291)):
```csharp
// Only ONE global handler can be pending at a time
// Close dialog first, then defer actual action to next loop iteration
Application.MainLoop?.Invoke(() => handler());
```

**Consequences:**
- Positive: No conflicting hotkeys between contexts
- Positive: Dialogs get first priority for keyboard input
- Negative: Global actions may require two keypresses when dialog is open

### Why Environment-Aware Theming?

**Context:** Users accidentally modified production data because environments weren't visually distinguished.

**Decision:** Detect environment type from URL and apply color-coded status bar.

**Detection logic** ([`TuiThemeService.cs:19-104`](../src/PPDS.Cli/Tui/Infrastructure/TuiThemeService.cs#L19-L104)):
- Production: `.crm.dynamics.com` (no region number)
- Sandbox: `.crm\d+.dynamics.com` (with region number)
- Development: Keywords in URL (dev, test, qa, uat)
- Trial: Keywords (trial, demo, preview)

**Color assignments:**
| Environment | Foreground | Background | Rationale |
|-------------|------------|------------|-----------|
| Production | White | Red | Maximum warning |
| Sandbox | Black | Yellow | Moderate caution |
| Development | White | Green | Safe to experiment |
| Trial | Black | Cyan | Informational |

**Consequences:**
- Positive: Immediate visual feedback prevents accidents
- Positive: Consistent with traffic light mental model
- Negative: URL heuristics may misclassify custom domains

### Why Event-Based Presenter Extraction?

**Context:** Three TUI files exceeded 500 lines with entangled UI and business logic. `SqlQueryScreen` (1,161 lines) was the worst — query execution, pagination state, history, and plan caching mixed with Terminal.Gui view management. This blocked unit testing (tests need `Application.Init()` for any Terminal.Gui type).

**Decision:** Extract business logic into Presenter/Handler classes that raise events. UI shells subscribe and update views.

**Alternatives considered:**
- Interface-based ViewModel (MVVM): Rejected — Terminal.Gui 1.x has no data binding infrastructure, would require building a mini-framework
- Static helper extraction: Rejected — state management stays in the screen, less testable
- Leave as-is: Rejected — blocks autonomous testing, files continue to grow

**Consequences:**
- Positive: Extracted classes fully testable with `TuiUnit` (no `Application.Init()`)
- Positive: Clear separation makes future changes safer
- Positive: Event pattern matches existing `ITuiErrorService` convention
- Negative: Two files per component instead of one
- Negative: Event subscriptions add indirection

### Why Blue Background Rule?

**Context:** Terminal.Gui's default schemes use white-on-blue, which is unreadable on many terminals.

**Decision:** When background is Cyan, BrightCyan, Blue, or BrightBlue, foreground MUST be Black. No exceptions.

**Implementation** ([`TuiColorPalette.cs:18-20`](../src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs#L18-L20)):
```csharp
// Blue Background Rule:
// When background is Cyan, BrightCyan, Blue, or BrightBlue,
// foreground MUST be Black. No exceptions.
```

Validation method `ValidateBlueBackgroundRule()` returns violations for unit tests.

**Consequences:**
- Positive: Consistent readability across terminals
- Negative: Limited color palette for selection highlights

---

## Extension Points

### Adding a New Screen

1. **Create screen class** in `src/PPDS.Cli/Tui/Screens/{Name}Screen.cs`
2. **Implement `ITuiScreen`:**

```csharp
public sealed class MyScreen : ITuiScreen, ITuiStateCapture<MyScreenState>
{
    public View Content { get; }
    public string Title => "My Screen";
    public MenuBarItem[]? ScreenMenuItems => null;
    public Action? ExportAction => null;

    public event Action? CloseRequested;
    public event Action? MenuStateChanged;

    public void OnActivated(IHotkeyRegistry hotkeyRegistry)
    {
        // Register screen-scope hotkeys
    }

    public void OnDeactivating()
    {
        // Cleanup
    }

    public MyScreenState CaptureState() => new(/* properties */);

    public void Dispose() { /* cleanup */ }
}
```

3. **Create state record** in `Testing/States/MyScreenState.cs`
4. **Add navigation** from main menu or another screen

### Adding a New Dialog

1. **Create dialog class** in `src/PPDS.Cli/Tui/Dialogs/{Name}Dialog.cs`
2. **Inherit from `TuiDialog`:**

```csharp
internal sealed class MyDialog : TuiDialog, ITuiStateCapture<MyDialogState>
{
    public MyDialog(InteractiveSession? session = null)
        : base("Dialog Title", session)
    {
        // Add views
    }

    protected override void OnEscapePressed()
    {
        // Custom close logic or call base
        base.OnEscapePressed();
    }

    public MyDialogState CaptureState() => new(/* properties */);
}
```

3. **Create state record** in `Testing/States/MyDialogState.cs`

### Adding a Hotkey

1. **In screen's `OnActivated()`:**

```csharp
public void OnActivated(IHotkeyRegistry hotkeyRegistry)
{
    _hotkeyRegistration = hotkeyRegistry.Register(
        Key.F5,
        HotkeyScope.Screen,
        "Refresh data",
        () => RefreshData());
}
```

2. **Dispose registration in `Dispose()`:**

```csharp
public void Dispose()
{
    _hotkeyRegistration?.Dispose();
}
```

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `defaultProfile` | string | No | null | Profile to use on startup |
| `defaultEnvironment` | string | No | null | Environment URL to use on startup |

Settings stored in `~/.ppds/settings.json`.

---

## Testing

### Test Categories

| Category | Filter | Purpose |
|----------|--------|---------|
| `TuiUnit` | `--filter Category=TuiUnit` | Session lifecycle, state capture, services |
| `TuiIntegration` | `--filter Category=TuiIntegration` | Full flows with FakeXrmEasy |

### Test Responsibility Matrix

TUI tests focus on presentation layer. Service logic is tested at CLI/service layer.

| Concern | Tested By | Why |
|---------|-----------|-----|
| Query execution logic | CLI/Service tests | Shared `ISqlQueryService` |
| Session state management | TuiUnit | TUI-specific state |
| Screen rendering | Manual/E2E | Visual verification |
| Keyboard navigation | Manual/E2E | TUI-specific behavior |
| Error dialog display | TuiUnit + state capture | Presentation of errors |

### Test Patterns

**State Capture Testing:**

```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void AboutDialog_CapturesState_WithVersion()
{
    var dialog = new AboutDialog();

    var state = dialog.CaptureState();

    Assert.NotNull(state.Version);
    Assert.Contains("PPDS", state.Title);
}
```

**Session Lifecycle Testing:**

```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public async Task Session_ReusesProvider_ForSameEnvironment()
{
    var factory = new MockServiceProviderFactory();
    var session = new InteractiveSession(factory);

    await session.EnsureInitializedAsync("env1");
    await session.EnsureInitializedAsync("env1");

    Assert.Single(factory.CreationLog);  // Only one provider created
}
```

**Error Service Testing:**

```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void ErrorService_StoresRecent_NewestFirst()
{
    var service = new TuiErrorService();

    service.ReportError("Error 1");
    service.ReportError("Error 2");

    Assert.Equal("Error 2", service.RecentErrors[0].Message);
}
```

### Test Infrastructure

| Mock | Purpose | File |
|------|---------|------|
| `MockServiceProviderFactory` | Tracks provider creation, injects fakes | [`Mocks/MockServiceProviderFactory.cs`](../tests/PPDS.Cli.Tests/Mocks/MockServiceProviderFactory.cs) |
| `FakeSqlQueryService` | Returns configurable query results | [`Mocks/FakeSqlQueryService.cs`](../tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs) |
| `FakeQueryHistoryService` | In-memory history storage | [`Mocks/FakeQueryHistoryService.cs`](../tests/PPDS.Cli.Tests/Mocks/FakeQueryHistoryService.cs) |
| `FakeExportService` | Tracks export operations | [`Mocks/FakeExportService.cs`](../tests/PPDS.Cli.Tests/Mocks/FakeExportService.cs) |
| `TempProfileStore` | Isolated profile storage | [`Mocks/TempProfileStore.cs`](../tests/PPDS.Cli.Tests/Mocks/TempProfileStore.cs) |

---

## Acceptance Criteria — Presenter/Handler Extraction (#434–#437)

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `AuthMethodFormBuilder.GetAvailableMethods()` returns platform-aware list (CertificateStore only on Windows) | `AuthMethodFormBuilderTests.GetAvailableMethods_ExcludesCertStore_OnNonWindows` | 🔲 |
| AC-02 | `AuthMethodFormBuilder.GetFieldVisibility(method)` returns correct visible fields for each of 6 auth methods | `AuthMethodFormBuilderTests.GetFieldVisibility_ClientSecret_ShowsSecretField` | 🔲 |
| AC-03 | `AuthMethodFormBuilder.Validate(method, values)` returns error for missing required fields per auth method | `AuthMethodFormBuilderTests.Validate_ClientSecret_MissingAppId_ReturnsError` | 🔲 |
| AC-04 | `AuthMethodFormBuilder.BuildRequest(method, values)` produces correct `ProfileCreateRequest` with method-specific fields | `AuthMethodFormBuilderTests.BuildRequest_CertificateFile_SetsCertFields` | 🔲 |
| AC-05 | `ProfileCreationDialog` delegates validation and request building to `AuthMethodFormBuilder`; existing behavior unchanged | `ProfileCreationDialogTests.CaptureState_ReflectsSelectedMethod` | 🔲 |
| AC-06 | `SqlQueryPresenter.ExecuteAsync()` raises `ChunkReceived` events for each streaming chunk | `SqlQueryPresenterTests.ExecuteAsync_RaisesChunkReceived_PerChunk` | 🔲 |
| AC-07 | `SqlQueryPresenter.ExecuteAsync()` raises `QueryCompleted` with row count, elapsed ms, and execution mode | `SqlQueryPresenterTests.ExecuteAsync_RaisesQueryCompleted_WithStats` | 🔲 |
| AC-08 | `SqlQueryPresenter.ExecuteAsync()` raises `QueryFailed` with appropriate error type for auth, DML confirmation, cancellation, and general errors | `SqlQueryPresenterTests.ExecuteAsync_AuthError_RaisesQueryFailed_WithAuthType` | 🔲 |
| AC-09 | `SqlQueryPresenter.LoadMoreAsync()` updates pagination state and raises `ChunkReceived` | `SqlQueryPresenterTests.LoadMore_UpdatesPagingCookie` | 🔲 |
| AC-10 | `SqlQueryPresenter` saves to history after successful execution | `SqlQueryPresenterTests.ExecuteAsync_SavesHistory` | 🔲 |
| AC-11 | `SqlQueryPresenter` caches execution plan after successful execution | `SqlQueryPresenterTests.ExecuteAsync_CachesExecutionPlan` | 🔲 |
| AC-12 | `SqlQueryPresenter.ConfirmDml()` sets confirmation flag; next execution sends `IsConfirmed=true` | `SqlQueryPresenterTests.ConfirmDml_NextExecute_SendsConfirmed` | 🔲 |
| AC-13 | `SqlQueryScreen` delegates query execution to `SqlQueryPresenter`; subscribes to events to update UI; existing behavior unchanged | Manual + existing `SqlQueryScreenTests` pass | 🔲 |
| AC-14 | `TableKeyboardHandler.HandleKey(Ctrl+C)` returns copy result via `ITableViewState` without Terminal.Gui dependency | `TableKeyboardHandlerTests.HandleKey_CtrlC_ReturnsCopyResult` | 🔲 |
| AC-15 | `TableKeyboardHandler.GetRecordUrl()` builds correct Dynamics 365 URL from environment URL, entity name, and primary key | `TableKeyboardHandlerTests.GetRecordUrl_BuildsCorrectUrl` | 🔲 |
| AC-16 | `TableKeyboardHandler.ToggleGuidColumns()` toggles internal state; caller re-applies column sizing | `TableKeyboardHandlerTests.ToggleGuidColumns_TogglesState` | 🔲 |
| AC-17 | `ITableViewState` interface abstracts `TableView` read-only properties (SelectedRow, RowOffset, VisibleHeight, HasFocus, Table) | `TableKeyboardHandlerTests` uses mock `ITableViewState` | 🔲 |
| AC-18 | `QueryResultsTableView` delegates keyboard handling to `TableKeyboardHandler`; existing behavior unchanged | Manual + `npm run tui:test` pass | 🔲 |
| AC-19 | Keyboard shortcut convention documented in `specs/tui.md` with three-tier table | This spec (self-documenting) | 🔲 |
| AC-20 | CLAUDE.md contains one-liner pointing to keyboard convention in `specs/tui.md` | Grep check | 🔲 |
| AC-21 | All 3 extracted files (`AuthMethodFormBuilder`, `SqlQueryPresenter`, `TableKeyboardHandler`) conform to keyboard shortcut convention | Audit in implementation | 🔲 |
| AC-22 | All existing TUI tests pass unchanged: `dotnet test --filter "Category=TuiUnit"` and `npm run tui:test` | CI gate | 🔲 |

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services that TUI delegates to
- [cli.md](./cli.md) - CLI launched when arguments provided; TUI when no arguments
- [authentication.md](./authentication.md) - Profile management dialogs use these services
- [connection-pooling.md](./connection-pooling.md) - InteractiveSession manages pool lifecycle
- [query.md](./query.md) - SqlQueryScreen uses query execution services
- [mcp.md](./mcp.md) - MCP server shares Application Services with TUI

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-25 | Added Presenter/Handler extraction pattern, keyboard shortcut convention, ACs for #434–#437 |
| 2026-03-18 | Added Surfaces frontmatter, Changelog per spec governance |

## Roadmap

- PTY-based E2E tests for full visual verification
- Vim-style keybindings option
- Multi-environment dashboard view
- Query result visualization (charts)
