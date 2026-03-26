# TUI

**Status:** Implemented
**Last Updated:** 2026-03-26
**Code:** [src/PPDS.Cli/Tui/](../src/PPDS.Cli/Tui/), [src/PPDS.Cli/Infrastructure/](../src/PPDS.Cli/Infrastructure/)
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
| `SqlQueryPresenter` | Query execution orchestration, state, history — no Terminal.Gui deps ([`Tui/Screens/SqlQueryPresenter.cs`](../src/PPDS.Cli/Tui/Screens/SqlQueryPresenter.cs)) |
| `AuthMethodFormModel` | Auth method validation, visibility rules, request building — no Terminal.Gui deps ([`Tui/Dialogs/AuthMethodFormModel.cs`](../src/PPDS.Cli/Tui/Dialogs/AuthMethodFormModel.cs)) |
| `DataverseUrlBuilder` | Centralized URL construction for Dynamics 365 record, Maker, and Power Automate URLs ([`Infrastructure/DataverseUrlBuilder.cs`](../src/PPDS.Cli/Infrastructure/DataverseUrlBuilder.cs)) |

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

## Presenter/Model Extraction Pattern

### Problem

Three TUI files mix untestable Terminal.Gui code with business logic that should be unit-testable:

| File | Lines | Extractable Logic |
|------|-------|-------------------|
| `SqlQueryScreen.cs` | 1,161 | ~500 lines — query execution, state management, history, plans |
| `ProfileCreationDialog.cs` | 667 | ~150 lines — validation, request building, visibility rules |
| `QueryResultsTableView.cs` | 655 | ~60 lines — URL construction (duplicated, moved to shared helper) |

Other large TUI files (`TuiShell` 1,014 lines, `PluginRegistrationScreen` 994 lines, `SyntaxHighlightedTextView` 811 lines) were audited and found cohesive — their size comes from inherently complex UI concerns, not trapped business logic.

### Pattern

```
┌─────────────────────────┐         ┌─────────────────────────┐
│     UI Shell (View)     │         │  Extracted Class         │
│                         │  owns   │  (Presenter/Model)       │
│  - Layout construction  │────────▶│                         │
│  - Terminal.Gui lifecycle│        │  - Business logic        │
│  - View event wiring    │◀───────│  - State management      │
│  - Focus management     │ events │  - Validation            │
│  - Dispose cleanup      │        │  - Data transformation   │
└─────────────────────────┘         └─────────────────────────┘
```

**Rules:**

1. Extracted class has **zero Terminal.Gui dependencies** — no `using Terminal.Gui;`
2. Communication via **C# events and return values** — presenter raises events, shell subscribes
3. Extracted class is a **plain C# class** — testable without Terminal.Gui initialization
4. UI shell becomes a **thin adapter** — translates Terminal.Gui events to presenter calls, presenter events to UI updates
5. All `Application.MainLoop.Invoke()` calls stay in the UI shell

**When NOT to extract:** If a file is large but its logic is inherently Terminal.Gui-coupled (layout, rendering, tree management, custom drawing), extraction would just move UI code to another class with UI dependencies. Size alone is not a reason to extract.

**File placement:** Same directory as source file (`Tui/Screens/SqlQueryPresenter.cs`, `Tui/Dialogs/AuthMethodFormModel.cs`). Shared utilities used by both TUI and CLI go in `src/PPDS.Cli/Infrastructure/` (e.g., `DataverseUrlBuilder`).

### SqlQueryPresenter

Extracted from `SqlQueryScreen` (1,161 → ~450 lines screen + ~500 lines presenter).

**Moves to presenter:**
- `ExecuteAsync` — streaming query orchestration
- `SaveToHistoryAsync`, `CacheExecutionPlanAsync`
- `LoadMoreAsync` — next-page fetching
- `TranspileSqlAsync`, `GetExecutionPlanAsync`
- `ToggleTds`, `ConfirmDml`
- State: `LastSql`, `LastPagingCookie`, `LastPageNumber`, `IsExecuting`, `LastErrorMessage`, `LastExecutionPlan`, `LastExecutionTimeMs`, `UseTdsEndpoint`

**Stays in screen:**
- Layout construction (editor, splitter, filter, results table, status)
- Keyboard handling (editor shortcuts, context navigation, word deletion)
- Filter show/hide, editor resize, splitter drag
- Visual focus indicators, IntelliSense wiring
- State capture, dispose

**Events:**

```csharp
event Action<string> StatusChanged;
event Action<IReadOnlyList<QueryColumn>, string> StreamingColumnsReady;
event Action<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>, IReadOnlyList<QueryColumn>> StreamingRowsReady;
event Action<int, long, QueryExecutionMode?> ExecutionComplete;
event Action<QueryResult> PageLoaded;
event Action<string> ErrorOccurred;
event Action<string> AuthenticationRequired;
event Action<string> DmlConfirmationRequired;
```

**Constructor:** Takes `InteractiveSession` + `environmentUrl`. Uses session's existing service resolution pattern.

### AuthMethodFormModel

Extracted from `ProfileCreationDialog` (667 → ~450 lines dialog + ~200 lines model).

**Moves to model:**
- `AvailableMethods` — platform-aware auth method list (CertificateStore Windows-only)
- `GetVisibleFields(AuthMethod)` → `FieldVisibility` record (which fields to show per method)
- `GetStatusText(AuthMethod)` → hint text per method
- `Validate(AuthMethod, AuthMethodFieldValues)` → error string or null
- `BuildRequest(AuthMethod, AuthMethodFieldValues)` → `ProfileCreateRequest`

**Stays in dialog:**
- Layout construction (all TextFields, RadioGroup, frames, buttons)
- Auth method change handler (calls model, updates field visibility)
- Authenticate click handler (collects field values, calls model.Validate, kicks off auth)
- Async auth flow, device code callback, environment selector
- State capture, dispose

**`AuthMethodFieldValues` record:** Flat record holding all field values as strings. Dialog populates from TextFields, passes to model. No two-way binding.

```csharp
public record AuthMethodFieldValues(
    string? ProfileName, string? EnvironmentUrl,
    string? AppId, string? TenantId, string? ClientSecret,
    string? CertPath, string? CertPassword, string? Thumbprint,
    string? Username, string? Password);
```

### DataverseUrlBuilder

Consolidates duplicated URL construction from 6+ locations into `src/PPDS.Cli/Infrastructure/DataverseUrlBuilder.cs`.

**Record URL** (replaces inline construction in `QueryResultsTableView.GetCurrentRecordUrl` and duplicate in `QueryResultConverter.BuildRecordUrl`):

```csharp
public static string? BuildRecordUrl(string environmentUrl, string entityLogicalName, string? recordId)
```

Pattern: `{baseUrl}/main.aspx?etn={entity}&id={id}&pagetype=entityrecord`

**Maker URL** (replaces duplicate `BuildMakerUrl` methods in `Solutions/UrlCommand`, `Solutions/GetCommand`, `EnvironmentVariables/UrlCommand`, `EnvironmentVariables/GetCommand`, `ImportJobs/UrlCommand`, `ImportJobs/GetCommand`, `EnvironmentSelectorDialog`):

```csharp
public static string BuildMakerUrl(string environmentUrl, string? path = "/solutions")
public static string BuildSolutionMakerUrl(string environmentUrl, Guid solutionId)
public static string BuildEnvironmentVariableMakerUrl(string environmentUrl, Guid definitionId)
public static string BuildImportJobMakerUrl(string environmentUrl, Guid importJobId)
```

Base pattern: `https://make.powerapps.com/environments/Default-{orgName}{path}`

Typed overloads prevent callers from constructing path strings manually. Example: `BuildSolutionMakerUrl(envUrl, solutionId)` produces `https://make.powerapps.com/environments/Default-contoso/solutions/{solutionId}`.

**Entity List URL** (replaces inline in `MetadataExplorerScreen.OpenInMaker`):

```csharp
public static string BuildEntityListUrl(string environmentUrl, string entityLogicalName)
```

Pattern: `{baseUrl}/main.aspx?pagetype=entitylist&etn={entity}`

**Web Resource URL** (replaces `WebResources/UrlCommand.BuildMakerUrl`):

```csharp
public static string BuildWebResourceEditorUrl(string environmentUrl, Guid webResourceId)
```

**Power Automate URL** (replaces inline in `Flows/UrlCommand`):

```csharp
public static string BuildFlowUrl(string environmentId, Guid flowId)
```

Pattern: `https://make.powerautomate.com/environments/{environmentId}/flows/{flowId}/details`

---

## Keyboard Shortcut Convention

### Layers

| Layer | Modifier | Dispatch | Rationale |
|-------|----------|----------|-----------|
| **Global** | Alt+Letter, F1, F12 | `HotkeyRegistry` Global scope | Alt avoids conflict with Ctrl screen shortcuts |
| **Tab management** | Ctrl+T/W/Tab/PgUp/PgDn | `HotkeyRegistry` Global scope | Exception — Ctrl used because Alt+T conflicts with Terminal.Gui menu bar |
| **Screen actions** | Ctrl+Letter | `HotkeyRegistry` Screen scope | Standard app shortcut convention |
| **Screen actions (alt)** | F5–F10 | `HotkeyRegistry` Screen scope | Linux-friendly alternatives for Ctrl+Shift combos |
| **Table context** | Single letter (`/`, `q`) | `Content.KeyPress` with focus guard | Vim-style, only when text input not focused |
| **Table actions** | Ctrl+Letter | `Content.KeyPress` with focus guard | Standard modifier convention |
| **Text editing** | Ctrl+A/Z/C/V | `KeyPress` on TextView | Platform standard |
| **Dialog buttons** | Alt+Letter | Terminal.Gui underscore convention | Built-in framework behavior |

### Priority

Dialog scope > Screen scope > Global scope (documented in `HotkeyRegistry`).

Table-level `KeyPress` handlers fire at a different dispatch level than `HotkeyRegistry`. If a screen registers a key at Screen scope, the table `KeyPress` handler for the same key **never fires** — the registry consumes the event first.

### Common Screen Keys

| Key | Convention | Screens Using |
|-----|-----------|---------------|
| Ctrl+R | Refresh | All data screens |
| Ctrl+F | Filter/Search | PluginTraces, WebResources, ConnectionReferences, EnvironmentVariables, Metadata |
| Ctrl+O | Open in Maker | WebResources, Metadata, ImportJobs, ConnectionReferences, EnvironmentVariables, Solutions |
| Ctrl+E | Export | SqlQuery, PluginTraces, EnvironmentVariables |

### Conflict Resolutions

**Ctrl+O (Open in browser vs Open in Maker):** QueryResultsTableView used Ctrl+O for "Open in browser" at KeyPress level, but 6 screens register Ctrl+O at Screen scope for "Open in Maker". Screen scope wins — the table handler never fires. **Fix:** Change table "Open in browser" to **Ctrl+Shift+O**. This aligns with the Ctrl+Shift convention for secondary actions (Ctrl+C copy / Ctrl+Shift+C copy with headers).

**Ctrl+T (New tab vs screen actions):** WebResourcesScreen uses Ctrl+T for "Toggle text-only", PluginTracesScreen uses Ctrl+T for "Timeline". Both shadow the global Ctrl+T "New tab". **Fix:** WebResources "Toggle text-only" → **Ctrl+Shift+T**. PluginTraces "Timeline" → **F6** (Ctrl+Shift+L conflicts with existing Ctrl+L "Trace Level"; F6 matches the "toggle view" convention from SqlQuery's F6 "toggle focus").

---

## Bug Fixes

Issues discovered during audit, fixed alongside the extractions.

### SqlQueryScreen

**Double try-catch nesting (line 524-526):** Outer `try` has no catches, only `finally` for timer cleanup. Inner `try` has all exception handlers. **Fix:** Flatten to single `try` with catches and `finally`.

**Inconsistent fire-and-forget:** Menu items use `_ = ExecuteQueryAsync()` (unmonitored). **Fix:** Use `ErrorService.FireAndForget()` consistently.

**`_confirmedDml` race condition:** Flag set true, could be consumed by a concurrent execute. **Fix:** Capture and reset atomically at the start of `ExecuteAsync`.

### ProfileCreationDialog

**CTS closure bug:** Device code callback captures `_authCompleteCts` by reference. If re-auth disposes and replaces the CTS, the callback references the disposed instance. **Fix:** Capture `_authCompleteCts` value in a local variable before passing to the lambda.

### QueryResultsTableView

**DataTable resource leak:** `DataTable` implements `IDisposable`. New DataTables are created on every `LoadResults`, `ApplyFilter`, `ClearData` — old instances are never disposed. **Fix:** Implement `IDisposable` on `QueryResultsTableView` (Constitution R1). Dispose old `DataTable` before replacing. Dispose `_unfilteredDataTable` when filter is cleared.

**Inline URL construction:** `GetCurrentRecordUrl()` builds record URL inline when `QueryResultConverter.BuildRecordUrl()` already exists. **Fix:** Replace with `DataverseUrlBuilder.BuildRecordUrl()`.

---

## Acceptance Criteria

### AuthMethodFormModel

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `AuthMethodFormModel` has zero Terminal.Gui dependencies (no `using Terminal.Gui`) | `AuthMethodFormModelTests.NoTerminalGuiDependency` | 🔲 |
| AC-02 | `Validate()` returns error for missing required fields per auth method (6 methods × field combinations) | `AuthMethodFormModelTests.Validate_*` | 🔲 |
| AC-03 | `BuildRequest()` produces correct `ProfileCreateRequest` for all 6 auth methods | `AuthMethodFormModelTests.BuildRequest_*` | 🔲 |
| AC-04 | `GetVisibleFields()` returns correct visibility for each auth method | `AuthMethodFormModelTests.GetVisibleFields_*` | 🔲 |
| AC-05 | `GetAvailableMethods()` excludes CertificateStore on non-Windows | `AuthMethodFormModelTests.AvailableMethods_*` | 🔲 |

### SqlQueryPresenter

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-06 | `SqlQueryPresenter` has zero Terminal.Gui dependencies | `SqlQueryPresenterTests.NoTerminalGuiDependency` | 🔲 |
| AC-07 | `ExecuteAsync()` raises `StreamingColumnsReady` and `StreamingRowsReady` events | `SqlQueryPresenterTests.ExecuteAsync_RaisesStreamingEvents` | 🔲 |
| AC-08 | `ExecuteAsync()` raises `AuthenticationRequired` on `DataverseAuthenticationException` | `SqlQueryPresenterTests.ExecuteAsync_AuthError_RaisesEvent` | 🔲 |
| AC-09 | `ExecuteAsync()` raises `DmlConfirmationRequired` on DML confirmation error | `SqlQueryPresenterTests.ExecuteAsync_DmlConfirmation_RaisesEvent` | 🔲 |
| AC-10 | `LoadMoreAsync()` raises `PageLoaded` with next page results | `SqlQueryPresenterTests.LoadMoreAsync_RaisesPageLoaded` | 🔲 |
| AC-11 | `ToggleTds()` toggles `UseTdsEndpoint` and raises `StatusChanged` | `SqlQueryPresenterTests.ToggleTds_TogglesState` | 🔲 |
| AC-12 | `ExecuteAsync()` saves to history via `IQueryHistoryService` on success | `SqlQueryPresenterTests.ExecuteAsync_SavesHistory` | 🔲 |
| AC-13 | `ExecuteAsync()` atomically captures and resets DML confirmation flag | `SqlQueryPresenterTests.ExecuteAsync_CapturesAndResetsDmlFlag` | 🔲 |

### DataverseUrlBuilder

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-14 | `BuildRecordUrl()` produces correct URL for entity + ID | `DataverseUrlBuilderTests.BuildRecordUrl_*` | 🔲 |
| AC-15 | `BuildMakerUrl()` produces correct URL with org name extraction | `DataverseUrlBuilderTests.BuildMakerUrl_*` | 🔲 |
| AC-16 | `BuildSolutionMakerUrl()`, `BuildEnvironmentVariableMakerUrl()`, `BuildImportJobMakerUrl()` produce correct entity-specific URLs | `DataverseUrlBuilderTests.BuildTypedMakerUrl_*` | 🔲 |
| AC-17 | `BuildEntityListUrl()` produces correct entity list URL | `DataverseUrlBuilderTests.BuildEntityListUrl_*` | 🔲 |
| AC-18 | `BuildWebResourceEditorUrl()` produces correct web resource editor URL | `DataverseUrlBuilderTests.BuildWebResourceEditorUrl_*` | 🔲 |
| AC-19 | `BuildFlowUrl()` produces correct Power Automate flow URL | `DataverseUrlBuilderTests.BuildFlowUrl_*` | 🔲 |
| AC-20 | No inline URL construction remains in C# production code (all callers use `DataverseUrlBuilder`) | `DataverseUrlBuilderTests.NoInlineUrlConstruction` | 🔲 |

### Bug Fixes

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-21 | `QueryResultsTableView` implements `IDisposable` and disposes `DataTable` instances | `QueryResultsTableViewTests.Dispose_CleansUpDataTables` | 🔲 |
| AC-22 | Old `DataTable` disposed before replacement in `LoadResults`, `ApplyFilter`, `ClearData` | `QueryResultsTableViewTests.LoadResults_DisposesOldDataTable` | 🔲 |
| AC-23 | Device code callback captures CTS value in local variable, not field reference | `ProfileCreationDialogTests.DeviceCodeCallback_CapturesCtsValue` | 🔲 |
| AC-24 | `SqlQueryScreen.ExecuteQueryAsync` error handling works for all exception types (auth, DML, cancellation, general) after flattening double try-catch | `SqlQueryPresenterTests.ExecuteAsync_*` (covered by AC-07 through AC-09) | 🔲 |
| AC-25 | Menu item fire-and-forget uses `ErrorService.FireAndForget` (no unmonitored `_ = Task`) | `SqlQueryScreenTests.MenuItems_UseFireAndForget` | 🔲 |

### Keyboard Convention

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-26 | Ctrl+O in QueryResultsTableView changed to Ctrl+Shift+O for "Open in browser" | `KeyboardConventionTests.CtrlShiftO_OpensInBrowser` | 🔲 |
| AC-27 | WebResourcesScreen does not register Ctrl+T (uses Ctrl+Shift+T instead) | `KeyboardConventionTests.WebResources_NoCtrlT` | 🔲 |
| AC-28 | PluginTracesScreen does not register Ctrl+T (uses F6 instead) | `KeyboardConventionTests.PluginTraces_NoCtrlT` | 🔲 |

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
| 2026-03-26 | Presenter/Model extraction pattern, keyboard convention, DataverseUrlBuilder, bug fixes (#434-437) |
| 2026-03-18 | Added Surfaces frontmatter, Changelog per spec governance |

## Roadmap

- PTY-based E2E tests for full visual verification
- Vim-style keybindings option
- Multi-environment dashboard view
- Query result visualization (charts)
- Consolidate TypeScript URL duplication (Extension panels — `buildRecordUrl` in query-panel.ts and notebookResultRenderer.ts)
