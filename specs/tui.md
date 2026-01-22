# TUI (Terminal User Interface)

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Cli/Tui/](../src/PPDS.Cli/Tui/)

---

## Overview

The TUI provides a full-screen terminal interface for PPDS built on Terminal.Gui. It serves as the **reference implementation** for UI patterns that are later ported to other interfaces (VS Code Extension, RPC clients).

### Goals

- **Interactive workflows**: Provide rich keyboard-driven interaction for profile management, environment discovery, and SQL querying
- **Reference UI patterns**: Establish patterns (dialogs, screens, navigation) that other interfaces follow
- **Testable without rendering**: Enable autonomous testing via state capture without Terminal.Gui rendering

### Non-Goals

- **Duplicate business logic**: Service logic lives in Application Services (see [application-services.md](./application-services.md))
- **Replace CLI for scripting**: TUI is for interactive use; CLI handles automation
- **Visual customization**: Users cannot define custom themes (environment-based colors only)

---

## Architecture

```
┌───────────────────────────────────────────────────────────────┐
│                    PpdsApplication                             │
│  (Entry point, Terminal.Gui init, global key interception)    │
└─────────────────────────────┬─────────────────────────────────┘
                              │
                              ▼
┌───────────────────────────────────────────────────────────────┐
│                        TuiShell                                │
│  (Navigation controller, menu bar, screen stack)              │
│  ┌──────────────────┐  ┌───────────────┐  ┌───────────────┐  │
│  │  Menu Bar        │  │ Content Area  │  │ Status Bar    │  │
│  │  (File/Tools/    │  │ (Screens or   │  │ (Profile +    │  │
│  │   Help + screen) │  │  main menu)   │  │  Environment) │  │
│  └──────────────────┘  └───────────────┘  └───────────────┘  │
└─────────────────────────────┬─────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
┌───────────────┐    ┌───────────────┐    ┌───────────────┐
│ ITuiScreen    │    │ TuiDialog     │    │Infrastructure │
│ (SqlQuery)    │    │ (14 dialogs)  │    │ (Services)    │
└───────┬───────┘    └───────────────┘    └───────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────────────┐
│                   InteractiveSession                           │
│  (Connection pool lifecycle, service provider management)     │
└─────────────────────────────┬─────────────────────────────────┘
                              │
                              ▼
┌───────────────────────────────────────────────────────────────┐
│                   Application Services                         │
│  ISqlQueryService, IExportService, IProfileService, etc.      │
└───────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| PpdsApplication | Terminal.Gui initialization, global key interception, session lifecycle |
| TuiShell | Navigation stack, menu bar composition, global hotkeys |
| ITuiScreen | Full-screen content contract (SqlQueryScreen) |
| TuiDialog | Modal dialog base class with hotkey integration |
| InteractiveSession | Connection pool lifecycle, service provider factory |
| HotkeyRegistry | Context-aware keyboard routing (Global/Screen/Dialog) |
| TuiColorPalette | Centralized color schemes with environment-aware themes |

### Dependencies

- Depends on: [application-services.md](./application-services.md), [authentication.md](./authentication.md), [connection-pool.md](./connection-pool.md)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Core Requirements

1. **Screen-based navigation**: Push/pop screens onto a stack; back navigation returns to previous screen
2. **Modal dialogs**: Dialogs block interaction with underlying content until dismissed
3. **Scoped hotkeys**: Hotkeys have Global, Screen, or Dialog scope; dialogs suppress screen hotkeys
4. **Environment-aware theming**: Status bar color reflects environment type (Production=red, Sandbox=yellow)
5. **Thread-safe UI updates**: All UI modifications must go through `Application.MainLoop.Invoke()`

### Primary Flows

**Screen Navigation:**

1. **User selects menu item**: TuiShell receives navigation request
2. **Current screen deactivated**: `OnDeactivating()` unregisters screen hotkeys
3. **Screen pushed to stack**: Previous screen saved for back navigation
4. **New screen activated**: `OnActivated()` registers screen-specific hotkeys
5. **Menu bar rebuilt**: Screen-specific menu items merged with global items

**Dialog Lifecycle:**

1. **Dialog created**: Constructor receives `InteractiveSession` for service access
2. **Dialog registered**: HotkeyRegistry notified (`SetActiveDialog()`)
3. **Dialog displayed**: `Application.Run(dialog)` blocks until dismissed
4. **Dialog closed**: HotkeyRegistry cleared, focus returns to screen/shell

**Query Execution:**

1. **User enters SQL**: TextView captures query text
2. **Ctrl+Enter pressed**: HotkeyRegistry dispatches to SqlQueryScreen handler
3. **Spinner started**: `TuiSpinner.Start("Executing query...")` provides feedback
4. **Service invoked**: `ISqlQueryService.ExecuteAsync()` runs query
5. **Results displayed**: `QueryResultsTableView.SetData()` populates table
6. **Pagination available**: "Load more" triggers next page fetch

### Constraints

- Never call `Application.Run(dialog)` from within `MainLoop.Invoke()` (causes deadlock)
- Always stop spinners in all code paths (use try/finally)
- Dispose screen hotkey registrations in `OnDeactivating()`

### Validation Rules

| Component | Rule | Error |
|-----------|------|-------|
| Environment URL | Must be valid HTTPS URL | Invalid environment URL |
| Profile name | Must not be empty | Profile name required |
| SQL query | Must not be empty | Enter a query to execute |

---

## Core Types

### ITuiScreen

Contract for full-screen content containers.

```csharp
public interface ITuiScreen
{
    View Content { get; }
    string Title { get; }
    MenuBarItem[]? ScreenMenuItems { get; }
    event Action? CloseRequested;
    void OnActivated(IHotkeyRegistry hotkeyRegistry);
    void OnDeactivating();
}
```

The implementation ([`ITuiScreen.cs`](../src/PPDS.Cli/Tui/Screens/ITuiScreen.cs)) defines the contract that all screens must implement. Currently `SqlQueryScreen` is the primary implementation.

### TuiDialog

Base class for modal dialogs with standardized hotkey handling.

```csharp
public abstract class TuiDialog : Dialog
{
    protected TuiDialog(string title, InteractiveSession? session = null);
    protected virtual void OnEscapePressed() => RequestStop();
}
```

The base class ([`TuiDialog.cs:1-60`](../src/PPDS.Cli/Tui/Dialogs/TuiDialog.cs#L1-L60)) automatically registers with HotkeyRegistry, applies color schemes, and handles Escape key.

### InteractiveSession

Manages connection pool lifecycle and service provider injection.

```csharp
public class InteractiveSession : IAsyncDisposable
{
    public InteractiveSession(
        string? profileName,
        ProfileStore profileStore,
        IServiceProviderFactory? serviceProviderFactory = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null);

    public Task<IServiceProvider> GetServiceProviderAsync(string environmentUrl, CancellationToken ct);
    public Task<ISqlQueryService> GetSqlQueryServiceAsync(string environmentUrl, CancellationToken ct);
}
```

The implementation ([`InteractiveSession.cs:1-579`](../src/PPDS.Cli/Tui/InteractiveSession.cs#L1-L579)) lazy-loads service providers and invalidates them on environment change.

### IHotkeyRegistry

Context-aware keyboard routing with scope management.

```csharp
public interface IHotkeyRegistry
{
    IDisposable Register(Key key, HotkeyScope scope, string description,
        Action handler, object? owner = null);
    bool TryHandle(KeyEvent keyEvent);
    void SetActiveScreen(object? screen);
    void SetActiveDialog(object? dialog);
}
```

The registry ([`HotkeyRegistry.cs:1-410`](../src/PPDS.Cli/Tui/Infrastructure/HotkeyRegistry.cs#L1-L410)) dispatches keys in priority order: Dialog → Screen → Global.

---

## Infrastructure Services

### HotkeyRegistry

Provides context-aware keyboard routing with three scopes:

| Scope | When Active | Example |
|-------|-------------|---------|
| Global | Always | Alt+P (switch profile), F1 (help) |
| Screen | When screen is active, no dialog | Ctrl+Enter (execute query) |
| Dialog | When dialog is open | Enter (confirm), Escape (cancel) |

**Dispatch Priority:**
1. Dialog-scope hotkeys (if dialog open)
2. Screen-scope hotkeys (if no dialog)
3. Global hotkeys (always checked)

### TuiColorPalette

Centralized color scheme management with 18 predefined schemes.

**Blue Background Rule:** When background is Cyan/BrightCyan/Blue/BrightBlue, foreground MUST be Black (accessibility).

**Environment-Aware Status Bar:**

| Environment | Colors |
|-------------|--------|
| Production | White on Red |
| Sandbox | Black on BrightYellow |
| Development | White on Green |
| Trial | Black on Cyan |
| Unknown | Black on Gray |

### TuiSpinner

Animated braille spinner for async operation feedback.

```csharp
var spinner = new TuiSpinner { X = 1, Y = 1 };
spinner.Start("Loading...");
// ... async operation ...
spinner.Stop();
```

Animation frames: `⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏` (100ms intervals)

### TuiErrorService

Collects and displays the last 20 errors with full stack traces. Accessed via F12 global hotkey.

---

## Dialogs

| Dialog | Purpose | Key Features |
|--------|---------|--------------|
| ProfileSelectorDialog | Select/create/delete profiles | Alt+P global hotkey |
| EnvironmentSelectorDialog | Discover/select environments | Filter, manual URL entry |
| PreAuthenticationDialog | Choose auth method | Browser vs device code |
| ReAuthenticationDialog | Handle token expiration | Re-auth on 401 errors |
| ExportDialog | Configure export format | CSV, Excel options |
| QueryHistoryDialog | Browse/reload past queries | Ctrl+Shift+H hotkey |
| FetchXmlPreviewDialog | Show converted FetchXML | Ctrl+Shift+F hotkey |
| ErrorDetailsDialog | Display recent errors | F12 global hotkey |
| KeyboardShortcutsDialog | Show all hotkeys | F1 global hotkey |
| AboutDialog | Version and credits | Help menu |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Authentication required | Token expired or missing | Show ReAuthenticationDialog |
| Connection failed | Network/Dataverse error | Show error in ErrorDetailsDialog |
| Query syntax error | Invalid SQL | Display error message inline |
| Throttling (429) | Rate limit hit | Wait and retry (pool handles) |

### Recovery Strategies

- **Auth errors (401)**: Automatically show ReAuthenticationDialog
- **Network errors**: Log to error service, display in ErrorDetailsDialog (F12)
- **Query errors**: Display inline in status area, preserve query text

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Dialog open + global hotkey | Close dialog, don't execute hotkey |
| Rapid key presses | Queue through MainLoop, process in order |
| Screen disposed mid-operation | Cancel pending operations, cleanup gracefully |
| Environment change mid-query | Invalidate old provider, re-auth if needed |

---

## Design Decisions

### Why TUI-First Development?

**Context:** Multiple interfaces (CLI, TUI, VS Code Extension, MCP) need consistent UI patterns. Without a reference implementation, patterns diverge.

**Decision:** TUI is the reference implementation. All UI patterns are designed and tested in TUI first, then ported to other interfaces.

**Development order:**
1. Application Service (business logic)
2. CLI Command (parameter exposure)
3. TUI Panel (reference UI)
4. RPC Method (if extension needs new data)
5. MCP Tool (if AI analysis adds value)
6. Extension View (ports TUI patterns)

**Alternatives considered:**
- Extension-first: Rejected because extension-specific patterns don't translate to terminal
- Parallel development: Rejected because patterns would diverge without clear reference

**Consequences:**
- Positive: Consistent patterns, clear development order
- Negative: Extension development blocked on TUI patterns

### Why IServiceProviderFactory?

**Context:** Terminal.Gui 1.19 lacks good testing APIs (FakeDriver is internal). Manual testing created slow feedback loops blocking autonomous Claude iteration.

**Decision:** Inject `IServiceProviderFactory` into `InteractiveSession` to enable mock service injection in tests.

```csharp
// Production
var session = new InteractiveSession(profile, store);

// Testing
var mockFactory = new MockServiceProviderFactory();
var session = new InteractiveSession(profile, store, mockFactory);
```

**Test categories:**
- `TuiUnit`: Session lifecycle, profile switching (<5s)
- `TuiIntegration`: Query execution with FakeXrmEasy (<30s)
- `TuiE2E`: Future PTY-based full rendering tests

**Alternatives considered:**
- Complete mocking without FakeXrmEasy: Insufficient coverage
- Full E2E with PTY from day one: Too fragile and slow

**Consequences:**
- Positive: Claude can run `dotnet test --filter "Category=TuiUnit"` autonomously
- Negative: Slightly more complex DI setup; need to maintain mock implementations

### Why Three-Scope Hotkeys?

**Context:** Hotkeys need different behavior depending on context. Global hotkeys (Alt+P) should always work. Screen hotkeys (Ctrl+Enter) should only work when no dialog is open. Dialog hotkeys (Escape) should only work in that dialog.

**Decision:** HotkeyRegistry manages three scopes with strict priority: Dialog → Screen → Global.

**Critical rule:** When a dialog is open and a global hotkey is pressed, the dialog closes instead of executing the hotkey. This prevents unexpected actions while modal UI is visible.

**Consequences:**
- Positive: Predictable keyboard behavior, no hotkey conflicts
- Negative: More complex registry implementation

### Why Static OperationClock?

**Context:** Long-running operations display elapsed time in multiple places (progress reporter, MEL logger). Each creating its own Stopwatch caused desynchronized timestamps.

**Decision:** Static `OperationClock` class owns the operation start time. Both progress reporter and logger read from same source.

**Alternatives considered:**
- Inject IOperationClock via DI: Rejected because clock needed by MEL formatters created early in DI pipeline
- Progress reporter owns clock: Rejected because creates coupling between logging and progress

**Consequences:**
- Positive: Consistent elapsed times across all displays
- Negative: Static state harder to test

### Why Unified Authentication Session?

**Context:** Switching profiles or restarting TUI prompted for re-auth even with valid cached tokens. Root cause: `HomeAccountId` wasn't persisted after successful authentication.

**Decision:** Persist `HomeAccountId` to profile after successful authentication via callback. All interfaces share same MSAL token cache (`~/.ppds/msal_token_cache.bin`).

**Consequences:**
- Positive: Single sign-on across all interfaces, session persistence
- Negative: Slightly more file I/O per authentication

---

## Extension Points

### Loading Spinner Pattern

Use `TuiSpinner` for animated feedback during async operations expected to take more than ~500ms:

```csharp
var spinner = new TuiSpinner
{
    X = 1,
    Y = Pos.Bottom(listFrame),
    Width = Dim.Fill() - 2,
    Height = 1
};
Add(spinner);

// Start with a message
spinner.Start("Loading environments...");

// ... async operation ...

// Stop and hide when done
spinner.Stop();

// Or stop with a final message
spinner.StopWithMessage("Found 5 environments");
```

**Animation frames:** `⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏` (100ms intervals)

**Integration with IOperationProgress:**
```csharp
var progress = new TuiOperationProgress(progressBar: null, statusLabel: null, spinner: spinner);
progress.ReportStatus("Processing...");  // Animates the spinner
progress.ReportComplete("Done!");        // Stops spinner and shows final message
```

**Best practices:**
- Always stop the spinner (use try/finally)
- Use action verbs: "Loading...", "Connecting...", "Executing..."
- Handle errors by stopping spinner and showing error state
- Avoid nested spinners (one per visible area)

### TUI Panel Pattern

Full-screen panel with layout, keyboard navigation, and async updates:

```csharp
public class DataExplorerPanel : View
{
    private readonly InteractiveSession _session;
    private readonly IDataExportService _exportService;

    public DataExplorerPanel(InteractiveSession session)
    {
        _session = session;
        _exportService = session.Services.GetRequiredService<IDataExportService>();

        // Fill entire container
        Width = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = TuiColorPalette.Default;

        // Fixed-height input area
        _queryFrame = new FrameView("Query (Ctrl+Enter to execute)")
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = 6
        };

        // Results fill remaining space
        _resultsTable = new TableView
        {
            X = 0, Y = Pos.Bottom(_queryFrame),
            Width = Dim.Fill(), Height = Dim.Fill() - 2
        };

        // Status bar anchored to bottom
        _statusLabel = new Label("Ready...")
        {
            X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
            ColorScheme = TuiColorPalette.StatusBar_Default
        };

        Add(_queryFrame, _resultsTable, _statusLabel);
    }

    private async Task ExecuteQueryAsync()
    {
        // Update status immediately
        Application.MainLoop?.Invoke(() =>
        {
            _statusLabel.Text = "Executing query...";
            _statusLabel.ColorScheme = TuiColorPalette.StatusBar_Working;
        });

        try
        {
            // Logic in service, not in TUI
            var result = await _exportService.QueryAsync(query, CancellationToken.None);

            // Update UI on main thread
            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.Table = ConvertToDataTable(result.Records);
                _statusLabel.Text = $"{result.Records.Count} records returned";
                _statusLabel.ColorScheme = TuiColorPalette.StatusBar_Success;
            });
        }
        catch (PpdsException ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Error: {ex.UserMessage}";
                _statusLabel.ColorScheme = TuiColorPalette.StatusBar_Error;
            });
        }
    }
}
```

**Anti-patterns to avoid:**
- Absolute positioning (`_resultsTable.X = 10`) - doesn't adapt to resize
- Updating UI from background thread (must use `MainLoop.Invoke`)
- Business logic in TUI (use services)
- Hardcoded colors (use `TuiColorPalette`)

### Adding a New Screen

1. **Create screen class**: Implement `ITuiScreen` in `src/PPDS.Cli/Tui/Screens/`
2. **Implement interface**: Provide `Content`, `Title`, `OnActivated()`, `OnDeactivating()`
3. **Register navigation**: Add menu item in TuiShell that calls `NavigateTo(new YourScreen(session))`
4. **Add state capture**: Create `YourScreenState` in `Testing/States/` for testability

**Example skeleton:**

```csharp
public class NewFeatureScreen : ITuiScreen
{
    public View Content { get; }
    public string Title => "New Feature";
    public MenuBarItem[]? ScreenMenuItems => null;
    public event Action? CloseRequested;

    public void OnActivated(IHotkeyRegistry registry) { }
    public void OnDeactivating() { }
}
```

### Adding a New Dialog

1. **Create dialog class**: Inherit from `TuiDialog` in `src/PPDS.Cli/Tui/Dialogs/`
2. **Pass session**: Constructor should accept `InteractiveSession` for service access
3. **Handle escape**: Override `OnEscapePressed()` if custom behavior needed
4. **Add state capture**: Create `YourDialogState` in `Testing/States/`

**Example skeleton:**

```csharp
public class NewFeatureDialog : TuiDialog
{
    public NewFeatureDialog(InteractiveSession session)
        : base("New Feature", session)
    {
        // Build dialog content
    }
}
```

---

## Configuration

The TUI uses shared PPDS configuration from `~/.ppds/`:

| File | Purpose |
|------|---------|
| `profiles.json` | Stored profiles with credentials |
| `msal_token_cache.bin` | MSAL token cache (encrypted) |
| `tui-debug.log` | Debug logging output |
| `query-history.json` | SQL query history |

No TUI-specific configuration settings exist. Environment detection and theming are automatic based on the connected environment URL.

---

## Testing

### Acceptance Criteria

- [x] Profile switching works without re-authentication when tokens cached
- [x] Query execution displays results in paginated table
- [x] Error details accessible via F12
- [x] Environment-aware status bar colors
- [x] All dialogs respond to Escape key

### Test Categories

| Category | Scope | Run Time |
|----------|-------|----------|
| TuiUnit | Session lifecycle, profile switching | <5s |
| TuiIntegration | Query execution with FakeXrmEasy | <30s |
| TuiE2E | Full PTY rendering (future) | <60s |

### Test Examples

```csharp
[Trait("Category", "TuiUnit")]
public class InteractiveSessionTests
{
    [Fact]
    public async Task GetSqlQueryService_ReturnsService_WhenConnected()
    {
        // Arrange
        var mockFactory = new MockServiceProviderFactory();
        var session = new InteractiveSession("test", _profileStore, mockFactory);

        // Act
        var service = await session.GetSqlQueryServiceAsync("https://org.crm.dynamics.com");

        // Assert
        Assert.NotNull(service);
    }
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Multi-interface architecture and TUI-first development
- [application-services.md](./application-services.md) - Services consumed by TUI screens
- [authentication.md](./authentication.md) - Profile and credential management
- [connection-pool.md](./connection-pool.md) - Connection lifecycle managed by InteractiveSession
- [cli.md](./cli.md) - Shares Application Services with TUI

---

## Roadmap

- **PTY-based E2E testing**: Full rendering tests with screenshot comparison
- **Additional screens**: Plugin trace viewer, solution explorer
- **Keyboard customization**: User-defined hotkey bindings
- **Split panes**: Multiple views in single screen
