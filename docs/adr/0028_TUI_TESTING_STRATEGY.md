# ADR-0028: TUI Testing Strategy

## Status
Accepted

## Context
The TUI (Terminal.Gui application) requires manual testing: user runs `ppds`, performs actions, and shares debug logs. This creates a slow feedback loop that blocks autonomous iteration on TUI features and bugs.

Terminal.Gui 1.19 doesn't provide good testing APIs (FakeDriver exists but is internal/undocumented). We need a testing strategy that enables:
1. Claude to iterate on TUI bugs without user intervention
2. Fast, deterministic tests that run in CI
3. Coverage of session lifecycle, profile switching, and query execution

## Decision
Introduce `IServiceProviderFactory` abstraction to enable mock injection in `InteractiveSession`, and create a test pyramid:

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
│   Unit Tests    │  MockServiceProviderFactory
└─────────────────┘
```

### Architecture Change
```csharp
// Before: Inline dependency creation
_serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(...);

// After: Injected factory
public InteractiveSession(
    string? profileName,
    ProfileStore profileStore,
    IServiceProviderFactory? serviceProviderFactory = null,  // NEW
    Action<DeviceCodeInfo>? deviceCodeCallback = null)
{
    _serviceProviderFactory = serviceProviderFactory ?? new ProfileBasedServiceProviderFactory();
}
```

### Test Categories
| Category | Purpose | Speed |
|----------|---------|-------|
| `TuiUnit` | Session lifecycle, profile switching | <5s |
| `TuiIntegration` | Query execution with FakeXrmEasy | <30s |
| `TuiE2E` | Full process with PTY (future) | Minutes |

### Test Mocks
- `MockServiceProviderFactory` - Logs creation calls, returns ServiceProvider with fakes
- `FakeSqlQueryService` - Returns configurable query results
- `FakeQueryHistoryService` - In-memory history storage
- `FakeExportService` - Tracks export operations
- `TempProfileStore` - Isolated ProfileStore with temp directory

### Test Responsibility Matrix

CLI and TUI share Application Services (ADR-0015). This means service logic is tested once at the CLI/service layer, not duplicated in TUI tests.

| Concern | Tested By | Why |
|---------|-----------|-----|
| Query execution logic | CLI/Service tests | Shared `ISqlQueryService` |
| Data transformation | CLI/Service tests | Service layer responsibility |
| Export format validity | CLI/Service tests | Shared `IExportService` |
| Authentication flows | CLI/Service tests | Shared auth infrastructure |
| Screen rendering | TUI E2E snapshots | Visual verification |
| Keyboard navigation | TUI E2E | TUI-specific behavior |
| Session state management | TuiUnit | TUI-specific state (profile switching, caching) |
| Error dialog display | TUI E2E | Presentation of errors |
| Threading/UI loop | TUI E2E | TUI-specific concurrency |

**Principle:** If CLI tests pass, the service works. TUI tests verify the presentation layer only.

### TUI Development Workflow

When implementing TUI features:

1. **Implement** the feature in code
2. **Run TuiUnit** tests for logic: `dotnet test --filter Category=TuiUnit`
3. **Run E2E** tests: `npm test --prefix tests/tui-e2e`
4. **Read snapshot diffs** - they show exactly what's rendering
5. **If diff shows bug** → fix code → re-run
6. **If diff shows intentional change** → update snapshots: `npm test --prefix tests/tui-e2e -- --update-snapshots`
7. **Repeat** until tests pass

### What TUI Tests Should NOT Verify

Do not write TUI E2E tests for:
- Query results are correct (CLI tests `ISqlQueryService`)
- Export format is valid (CLI tests `IExportService`)
- Authentication works (CLI tests auth flows)
- Bulk operations succeed (CLI tests `BulkOperationExecutor`)

These are already covered by CLI/service tests. Duplicating them in TUI E2E wastes effort and creates maintenance burden.

## Consequences

### Positive
- Claude can run `dotnet test --filter "Category=TuiUnit"` to validate changes
- Fast iteration on TUI bugs without manual testing
- Clear separation between UI and service logic
- Regression prevention with comprehensive test coverage

### Negative
- Slightly more complex DI setup in InteractiveSession
- Need to maintain mock implementations alongside real services
- E2E tests (when implemented) will be slower and more brittle

### Neutral
- Tests don't validate Terminal.Gui rendering (acceptable trade-off)
- Profile persistence still uses file system (TempProfileStore handles isolation)

## Files Changed
- NEW: `src/PPDS.Cli/Infrastructure/IServiceProviderFactory.cs`
- NEW: `src/PPDS.Cli/Infrastructure/ProfileBasedServiceProviderFactory.cs`
- NEW: `tests/PPDS.Cli.Tests/Mocks/MockServiceProviderFactory.cs`
- NEW: `tests/PPDS.Cli.Tests/Mocks/Fake*.cs` (services)
- NEW: `tests/PPDS.Cli.Tests/Tui/InteractiveSessionLifecycleTests.cs`
- MODIFIED: `src/PPDS.Cli/Tui/InteractiveSession.cs`

## Troubleshooting

### Debug Log

When troubleshooting TUI issues (`ppds`), check:

```
~/.ppds/tui-debug.log
```

The log contains timestamps, thread IDs, caller info, and status updates. Each `ppds` run clears the previous log.

### Common Issues

| Symptom | Check | Likely Cause |
|---------|-------|--------------|
| Query hangs at "Executing..." | Debug log for errors | Dataverse API error, deadlock |
| UI doesn't update | Debug log for status | `MainLoop.Invoke()` not called |
| Dialog doesn't close | Debug log | Nested `Application.Run()` (deadlock) |

### Terminal.Gui Safety Rules

**Safe from MainLoop.Invoke:**
- `MessageBox.Query()` / `MessageBox.ErrorQuery()` - modal, blocks, returns
- Direct property updates (`_label.Text = "..."`)
- `Application.Refresh()`

**UNSAFE (causes deadlock):**
- `Application.Run(dialog)` inside `MainLoop.Invoke()` - **NEVER DO THIS**
- Nested event loops

**Async pattern:**
```csharp
#pragma warning disable PPDS013
_ = DoWorkAsync().ContinueWith(t =>
{
    if (t.IsFaulted)
    {
        Application.MainLoop?.Invoke(() =>
            _statusLabel.Text = $"Error: {t.Exception?.InnerException?.Message}");
    }
}, TaskScheduler.Default);
#pragma warning restore PPDS013
```

## References
- [Terminal.Gui Testing Wiki](https://github.com/gui-cs/Terminal.Gui/wiki/Testing)
- ADR-0015: Application Services Layer
- ADR-0024: Shared Local State
- ADR-0029: Testing Strategy
