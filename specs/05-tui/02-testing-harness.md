# PPDS.TUI: Testing Harness

## Overview

The TUI Testing Harness provides infrastructure for automated testing of the Terminal.Gui-based UI without requiring visual inspection or manual interaction. The architecture uses a state capture pattern (`ITuiStateCapture<TState>`) that enables Claude and CI/CD pipelines to verify TUI behavior programmatically. Tests cover session lifecycle, profile switching, and component state without Terminal.Gui rendering, achieving fast, deterministic execution.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `ITuiStateCapture<TState>` | Generic interface for components to expose internal state |
| `IServiceProviderFactory` | Abstraction for dependency injection to enable mocking |

### Classes

| Class | Purpose |
|-------|---------|
| `MockServiceProviderFactory` | Test mock that returns ServiceProvider with fakes |
| `FakeSqlQueryService` | Configurable query result stub |
| `FakeQueryHistoryService` | In-memory history storage |
| `FakeExportService` | Export operation tracking |
| `TempProfileStore` | Isolated ProfileStore with temp directory |

### State Record Types

Each TUI component has a corresponding state record for testing:

| Component | State Record | Key Properties |
|-----------|--------------|----------------|
| `TuiShell` | `TuiShellState` | Title, MenuBarItems, CurrentScreenTitle, ScreenStackDepth |
| `TuiStatusBar` | `TuiStatusBarState` | ProfileText, EnvironmentText, EnvironmentType |
| `SqlQueryScreen` | `SqlQueryScreenState` | QueryText, IsExecuting, ResultCount, CanExport |
| `AboutDialog` | `AboutDialogState` | Title, ProductName, Version, GitHubUrl |
| `KeyboardShortcutsDialog` | `KeyboardShortcutsDialogState` | Shortcuts, ShortcutCount |
| `PreAuthenticationDialog` | `PreAuthenticationDialogState` | AvailableOptions, SelectedOption |
| `ProfileSelectorDialog` | `ProfileSelectorDialogState` | Profiles, SelectedIndex |
| `EnvironmentSelectorDialog` | `EnvironmentSelectorDialogState` | Environments, SelectedIndex |
| `ExportDialog` | `ExportDialogState` | Format, FilePath, IncludeHeaders |
| `QueryHistoryDialog` | `QueryHistoryDialogState` | Entries, SelectedIndex |
| `ErrorDetailsDialog` | `ErrorDetailsDialogState` | Errors, DebugLogContent |
| `FetchXmlPreviewDialog` | `FetchXmlPreviewDialogState` | FetchXml, SqlQuery |
| `TuiSpinner` | `TuiSpinnerState` | IsVisible, StatusText |
| `HotkeyRegistry` | `HotkeyRegistryState` | AllBindings, ContextBindings |
| `QueryResultsTableView` | `QueryResultsTableViewState` | ColumnHeaders, RowCount |

## Behaviors

### State Capture Pattern

```csharp
// Component implements ITuiStateCapture<TState>
public interface ITuiStateCapture<out TState>
{
    TState CaptureState();
}

// Test captures and verifies state
using var dialog = new AboutDialog();
var state = dialog.CaptureState();
Assert.Equal("About PPDS", state.Title);
```

### Test Pyramid

```
┌─────────────────┐
│   E2E/Scenario  │  PTY-based (future)
└────────┬────────┘
         │
┌────────┴────────┐
│  Integration    │  FakeXrmEasy + Mocks
└────────┬────────┘
         │
┌────────┴────────┐
│   Unit Tests    │  MockServiceProviderFactory, State Capture
└─────────────────┘
```

### Test Categories

| Category | Filter | Purpose | Speed |
|----------|--------|---------|-------|
| `TuiUnit` | `--filter Category=TuiUnit` | Session lifecycle, state capture | <5s |
| `TuiIntegration` | `--filter Category=TuiIntegration` | Query with FakeXrmEasy | <30s |
| `TuiE2E` | PTY-based | Full process (future) | Minutes |

### Dependency Injection for Testing

```csharp
// Production uses default factory
var session = new InteractiveSession(profileName, profileStore);

// Tests inject mock factory
var mockFactory = new MockServiceProviderFactory();
var session = new InteractiveSession(profileName, profileStore, mockFactory);
```

### Test Responsibility Matrix (ADR-0028)

| Concern | Tested By | Reason |
|---------|-----------|--------|
| Query execution logic | CLI/Service tests | Shared `ISqlQueryService` |
| Export format validity | CLI/Service tests | Shared `IExportService` |
| Authentication flows | CLI/Service tests | Shared auth infrastructure |
| Session state management | TuiUnit | TUI-specific state |
| Keyboard navigation | TUI E2E | TUI-specific behavior |
| Screen rendering | TUI E2E snapshots | Visual verification |

**Principle:** If CLI tests pass, the service works. TUI tests verify presentation layer only.

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| CaptureState before init | Returns default/empty values | Components handle gracefully |
| Concurrent state capture | Thread-safe | State records are immutable |
| Disposed component | May throw | Tests should dispose after assertions |
| Missing optional data | Null/empty in state | State records use nullable types |

## Error Handling

### Debug Log

TUI debug information is logged to `~/.ppds/tui-debug.log`:
- Timestamps and thread IDs
- Caller info (method, file, line)
- Session lifecycle events
- Error details

Each `ppds` run clears the previous log.

### Common Test Issues

| Symptom | Check | Likely Cause |
|---------|-------|--------------|
| State missing data | CaptureState timing | Called before UI update |
| Test hang | MainLoop.Invoke misuse | Nested Application.Run |
| Random failures | Thread safety | Shared mutable state |

## Dependencies

- **Internal**:
  - `PPDS.Cli.Tui.*` - Components under test
  - `PPDS.Auth.Profiles.ProfileStore` - Profile storage
  - `PPDS.Cli.Services.*` - Application services
- **External**:
  - `xUnit` - Test framework
  - `Moq` - Mocking framework
  - `FakeXrmEasy` (future) - Dataverse mocking

## Configuration

### Test Project Setup

```xml
<!-- PPDS.Cli.Tests.csproj -->
<ItemGroup>
  <PackageReference Include="xunit" Version="2.*" />
  <PackageReference Include="Moq" Version="4.*" />
</ItemGroup>
```

### Test Trait Assignment

```csharp
[Trait("Category", "TuiUnit")]
public class DialogStateCaptureTests
{
    // Tests run with: dotnet test --filter Category=TuiUnit
}
```

## Thread Safety

### State Capture Thread Safety

- State records are immutable (C# `record` types)
- `CaptureState()` creates new record instance
- Safe to call from any thread
- Components may have internal locking for state consistency

### Test Isolation

- Each test creates its own component instances
- `TempProfileStore` uses temp directory for isolation
- No shared mutable state between tests

## Testing Patterns

### Dialog State Capture

```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void AboutDialog_CaptureState_ReturnsValidState()
{
    using var dialog = new AboutDialog();

    var state = dialog.CaptureState();

    Assert.Equal("About PPDS", state.Title);
    Assert.NotEmpty(state.Version);
}
```

### Session Lifecycle

```csharp
[Fact]
public async Task InvalidateAsync_WhenNoProvider_DoesNotThrow()
{
    using var store = new ProfileStore();
    var session = new InteractiveSession(null, store);

    await session.InvalidateAsync(); // Should not throw
}
```

### Contract Tests

```csharp
[Fact]
public void ITuiScreen_HasMenuStateChangedEvent()
{
    var eventInfo = typeof(ITuiScreen).GetEvent("MenuStateChanged");

    Assert.NotNull(eventInfo);
    Assert.Equal(typeof(Action), eventInfo.EventHandlerType);
}
```

### Record Equality

```csharp
[Fact]
public void AboutDialogState_RecordEquality_WorksCorrectly()
{
    var state1 = new AboutDialogState("Title", "Product", "1.0", "Desc", "License", "url");
    var state2 = new AboutDialogState("Title", "Product", "1.0", "Desc", "License", "url");

    Assert.Equal(state1, state2);  // Record equality
}
```

## Development Workflow

1. **Implement** the feature in code
2. **Run TuiUnit** tests: `dotnet test --filter Category=TuiUnit`
3. **Run E2E** tests (when available): `npm test --prefix tests/tui-e2e`
4. **Read snapshot diffs** - show exactly what's rendering
5. **If diff shows bug** → fix code → re-run
6. **If diff shows intentional change** → update snapshots
7. **Repeat** until tests pass

## Related

- [PPDS.TUI: Architecture](01-architecture.md) - Component structure
- [ADR-0028: TUI Testing Strategy](../../docs/adr/0028_TUI_TESTING_STRATEGY.md) - Full rationale
- [ADR-0029: Testing Strategy](../../docs/adr/0029_TESTING_STRATEGY.md) - Overall test approach

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Cli/Tui/Testing/ITuiStateCapture.cs` | State capture interface |
| `src/PPDS.Cli/Tui/Testing/States/*.cs` | State record types |
| `src/PPDS.Cli/Infrastructure/IServiceProviderFactory.cs` | DI abstraction |
| `src/PPDS.Cli/Infrastructure/ProfileBasedServiceProviderFactory.cs` | Production factory |
| `tests/PPDS.Cli.Tests/Tui/Dialogs/DialogStateCaptureTests.cs` | Dialog state tests |
| `tests/PPDS.Cli.Tests/Tui/Screens/ITuiScreenContractTests.cs` | Interface contract tests |
| `tests/PPDS.Cli.Tests/Tui/InteractiveSessionTests.cs` | Session lifecycle tests |
| `tests/PPDS.Cli.Tests/Tui/InteractiveSessionLifecycleTests.cs` | Advanced lifecycle tests |
| `tests/PPDS.Cli.Tests/Tui/Infrastructure/*.cs` | Infrastructure component tests |
| `tests/PPDS.Cli.Tests/Tui/Views/*.cs` | View component tests |
