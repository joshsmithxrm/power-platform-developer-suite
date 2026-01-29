# TUI Plugin Traces

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-01-28
**Code:** None

---

## Overview

The Plugin Traces screen provides an interactive TUI for investigating plugin execution failures, browsing trace logs with rich filtering, and visualizing execution timelines. It replaces the multi-command CLI workflow (`list` → `get` → `related` → `timeline`) with a single screen where users scan, drill down, and explore without losing context.

### Goals

- **Investigation Flow**: Browse → filter → inspect detail → view timeline in one screen
- **Rich Filtering**: Inline quick-filter bar plus advanced 16-criteria filter dialog
- **Timeline Visualization**: Hierarchical execution tree using TimelineHierarchyBuilder
- **Trace Management**: Multi-select deletion with confirmation and bulk cleanup by filter/age

### Non-Goals

- Plugin registration management (handled by [tui-plugin-registration.md](./tui-plugin-registration.md))
- Real-time trace streaming (traces are queried after execution)
- Plugin profiling or replay
- Trace export to file (future roadmap in [plugin-traces.md](./plugin-traces.md))

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        TuiShell                                  │
│    Tools > Plugin Traces  (replaces "Coming Soon" placeholder)   │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                    PluginTraceScreen                              │
│          (ITuiScreen + ITuiStateCapture)                         │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ Filter Bar: [Type] [Message] [Entity] [☑ Errors] [⚙ Adv] │   │
│  ├──────────────────────────────────────────────────────────┤   │
│  │ DataTableView: PluginTraceInfo list                       │   │
│  │   TypeName | Message | Entity | Mode | Duration | Error   │   │
│  ├──────────────────────────────────────────────────────────┤   │
│  │ Status: 142 traces | Filter: errors-only, last 1h         │   │
│  └──────────────────────────────────────────────────────────┘   │
└──────────┬───────────────┬──────────────────────────────────────┘
           │               │
           ▼               ▼
┌─────────────────┐  ┌──────────────────┐  ┌─────────────────────┐
│ TraceDetail      │  │ TraceFilter       │  │ Timeline            │
│ Dialog           │  │ Dialog            │  │ Dialog              │
│ (exception,      │  │ (16 criteria      │  │ (TreeView of        │
│  message block)  │  │  form)            │  │  TimelineNodes)     │
└────────┬─────────┘  └──────────────────┘  └──────────┬──────────┘
         │                                              │
         └──────────────┬───────────────────────────────┘
                        ▼
┌─────────────────────────────────────────────────────────────────┐
│                   IPluginTraceService                             │
│    ListAsync, GetAsync, GetRelatedAsync, BuildTimelineAsync,     │
│    DeleteAsync, DeleteByFilterAsync, CountAsync, Settings         │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│              IDataverseConnectionPool                             │
└─────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PluginTraceScreen` | Main screen: filter bar, trace table, status line, hotkey registration |
| `PluginTraceDetailDialog` | Modal: full trace detail with exception, message block, timing |
| `PluginTraceFilterDialog` | Modal: advanced 16-criteria filter form |
| `PluginTraceTimelineDialog` | Modal: hierarchical timeline tree from correlated traces |

### Dependencies

- Depends on: [plugin-traces.md](./plugin-traces.md) for `IPluginTraceService` and all data types
- Depends on: [tui.md](./tui.md) for `ITuiScreen`, `TuiDialog`, `IHotkeyRegistry`, `ITuiErrorService`
- Uses patterns from: [architecture.md](./architecture.md) for Application Service boundary

---

## Specification

### Core Requirements

1. Screen implements `ITuiScreen` and `ITuiStateCapture<PluginTraceScreenState>`
2. Trace list displays in `DataTableView` with columns: TypeName, MessageName, PrimaryEntity, Mode, DurationMs, HasException, CreatedOn
3. Inline filter bar provides quick filtering by type name, message name, entity, and errors-only toggle
4. Advanced filter dialog exposes all 16 `PluginTraceFilter` criteria
5. Enter on a trace row opens `PluginTraceDetailDialog` with full `PluginTraceDetail`
6. Timeline hotkey opens `PluginTraceTimelineDialog` showing correlated execution hierarchy
7. Multi-select traces for bulk deletion with confirmation
8. All service calls run on background thread; UI updates via `Application.MainLoop.Invoke()`
9. Error handling via `ITuiErrorService.ReportError()` with F12 detail access

### Primary Flows

**Browse and Filter Traces:**

1. **Screen opens**: Load recent 100 traces via `IPluginTraceService.ListAsync()` with default filter
2. **Quick filter**: User types in filter bar fields; each field change triggers debounced reload (300ms)
3. **Toggle errors-only**: Checkbox sets `PluginTraceFilter.HasException = true`, triggers reload
4. **Advanced filter**: Ctrl+F opens `PluginTraceFilterDialog`; Apply triggers reload with full filter
5. **Refresh**: F5 reloads with current filter
6. **Status line**: Shows trace count and active filter summary

**Inspect Trace Detail:**

1. **Select trace**: Navigate table with arrow keys
2. **Open detail**: Enter opens `PluginTraceDetailDialog` with `GetAsync(traceId)`
3. **View exception**: Scrollable `TextView` shows `ExceptionDetails` with full stack trace
4. **View message block**: Scrollable `TextView` shows `MessageBlock` (trace output)
5. **Copy exception**: Hotkey copies exception text to clipboard
6. **Navigate to timeline**: Button in detail dialog opens timeline for this trace's correlation

**View Execution Timeline:**

1. **Open timeline**: Ctrl+T with trace selected, or button from detail dialog
2. **Load timeline**: `BuildTimelineAsync(correlationId)` returns `TimelineNode` tree
3. **Display tree**: `TreeView` or indented `ListView` showing hierarchy with depth indentation
4. **Node info**: Each node shows TypeName, DurationMs, HasException (color-coded)
5. **Select node**: Selecting a timeline node opens its detail dialog

**Delete Traces:**

1. **Select traces**: Multi-select in DataTableView (Space to toggle, Ctrl+A for all)
2. **Delete selected**: Delete key opens confirmation dialog with count
3. **Bulk delete**: Ctrl+Shift+D opens bulk delete options (by filter, older than, all)
4. **Confirm**: Confirmation dialog shows impact; high-impact actions require typing "DELETE"
5. **Progress**: Deletion progress reported via status line; table reloads on completion

### Constraints

- Never block the UI thread during trace queries or deletion
- Filter bar debounce at 300ms to avoid excessive Dataverse calls
- Maximum 1000 traces per query (configurable via top parameter)
- Timeline dialog shows traces for one correlation ID only
- Deletion confirmation is mandatory; no silent bulk deletes

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Min Duration | Non-negative integer | "Duration must be a positive number" |
| Max Duration | Greater than Min Duration | "Max duration must be greater than min" |
| Created After/Before | Valid date format | "Enter date as YYYY-MM-DD or YYYY-MM-DD HH:mm" |
| Older Than (bulk delete) | Valid duration format | "Enter duration as 7d, 24h, or 30m" |

---

## Core Types

### PluginTraceScreen

Main screen for plugin trace investigation.

```csharp
internal sealed class PluginTraceScreen : ITuiScreen, ITuiStateCapture<PluginTraceScreenState>
{
    public PluginTraceScreen(
        InteractiveSession session,
        ITuiErrorService errorService,
        Action<DeviceCodeInfo>? deviceCodeCallback = null);

    // ITuiScreen
    public View Content { get; }
    public string Title { get; }  // "Plugin Traces - {environment}"
    public MenuBarItem[]? ScreenMenuItems { get; }
    public Action? ExportAction { get; }

    // ITuiStateCapture
    public PluginTraceScreenState CaptureState();
}
```

### PluginTraceScreenState

State capture record for autonomous testing.

```csharp
public sealed record PluginTraceScreenState(
    int TraceCount,
    Guid? SelectedTraceId,
    string? SelectedTypeName,
    bool IsLoading,
    bool IsErrorsOnly,
    string? QuickFilterType,
    string? QuickFilterMessage,
    string? QuickFilterEntity,
    bool HasAdvancedFilter,
    string? StatusText,
    string? ErrorMessage);
```

### PluginTraceDetailDialog

Modal dialog showing full trace details.

```csharp
internal sealed class PluginTraceDetailDialog
    : TuiDialog, ITuiStateCapture<PluginTraceDetailDialogState>
{
    public PluginTraceDetailDialog(
        PluginTraceDetail detail,
        InteractiveSession? session = null);

    public PluginTraceDetailDialogState CaptureState();

    // Action to open timeline for this trace's correlation
    public event Action<Guid>? ViewTimelineRequested;
}
```

### PluginTraceDetailDialogState

```csharp
public sealed record PluginTraceDetailDialogState(
    Guid TraceId,
    string TypeName,
    string? MessageName,
    string? PrimaryEntity,
    int? DurationMs,
    bool HasException,
    string? ExceptionText,
    string? MessageBlock,
    Guid? CorrelationId,
    int Depth);
```

### PluginTraceFilterDialog

Modal dialog for advanced 16-criteria filtering.

```csharp
internal sealed class PluginTraceFilterDialog
    : TuiDialog, ITuiStateCapture<PluginTraceFilterDialogState>
{
    public PluginTraceFilterDialog(
        PluginTraceFilter? currentFilter = null,
        InteractiveSession? session = null);

    // Returns the configured filter on Apply, null on Cancel
    public PluginTraceFilter? Result { get; }

    public PluginTraceFilterDialogState CaptureState();
}
```

### PluginTraceFilterDialogState

```csharp
public sealed record PluginTraceFilterDialogState(
    string? TypeName,
    string? MessageName,
    string? PrimaryEntity,
    string? Mode,            // "Synchronous", "Asynchronous", or null
    string? OperationType,
    int? MinDepth,
    int? MaxDepth,
    DateTime? CreatedAfter,
    DateTime? CreatedBefore,
    int? MinDurationMs,
    int? MaxDurationMs,
    bool? HasException,
    Guid? CorrelationId,
    Guid? RequestId,
    Guid? PluginStepId,
    string? OrderBy,
    bool IsApplied);
```

### PluginTraceTimelineDialog

Modal dialog showing hierarchical execution timeline.

```csharp
internal sealed class PluginTraceTimelineDialog
    : TuiDialog, ITuiStateCapture<PluginTraceTimelineDialogState>
{
    public PluginTraceTimelineDialog(
        List<TimelineNode> roots,
        InteractiveSession? session = null);

    // Opens detail dialog for selected node
    public event Action<Guid>? ViewDetailRequested;

    public PluginTraceTimelineDialogState CaptureState();
}
```

### PluginTraceTimelineDialogState

```csharp
public sealed record PluginTraceTimelineDialogState(
    int RootCount,
    int TotalNodeCount,
    Guid? SelectedTraceId,
    string? SelectedTypeName,
    long TotalDurationMs);
```

### Usage Pattern

```csharp
// TuiShell creates and navigates to the screen
var screen = new PluginTraceScreen(_session, _errorService, _deviceCodeCallback);
NavigateTo(screen);

// Screen internally uses IPluginTraceService:
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var traceService = provider.GetRequiredService<IPluginTraceService>();
var traces = await traceService.ListAsync(filter, top: 100);

// Update UI on main thread:
Application.MainLoop.Invoke(() =>
{
    _dataTable.UpdateData(traces);
    _statusLabel.Text = $"{traces.Count} traces";
});
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Service unavailable | No connection to environment | Status line error; retry with F5 |
| Authentication expired | Token expired mid-session | Re-authentication dialog |
| Trace not found | Trace deleted between list and get | Status line warning; refresh list |
| Throttle exceeded | Too many parallel requests | Automatic retry via connection pool |
| Filter parse error | Invalid date or duration in filter dialog | Inline field validation message |

### Recovery Strategies

- **Connection errors**: Display error in status line with "Press F5 to retry" hint
- **Auth errors**: Intercept `PpdsAuthException`, show `ReAuthenticationDialog`, retry on success
- **Stale data**: If `GetAsync` returns null for a listed trace, show warning and refresh list
- **Deletion errors**: Report partial success count and list of failed trace IDs

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No traces match filter | Show empty table with "No traces found" message |
| Trace has no correlation ID | Timeline button disabled; tooltip explains why |
| Trace has no exception | Exception section in detail dialog shows "No exception" |
| Very long exception text | Scrollable TextView with word wrap |
| Delete while loading | Delete button disabled during load; re-enabled after |

---

## Design Decisions

### Why Inline Filter Bar + Advanced Filter Dialog?

**Context:** Plugin traces have 16 filter criteria. Exposing all in the main UI would be overwhelming; exposing none would lose the quick-filter experience.

**Decision:** Two-tier filtering. Inline filter bar for the 3 most common text filters (type, message, entity) plus errors-only toggle. Ctrl+F opens advanced dialog for all 16 criteria.

**Alternatives considered:**
- All filters inline: Rejected — too much visual noise, pushes table down
- Filter dialog only: Rejected — adds friction to the most common filter action (type name)
- Sidebar filter panel: Rejected — consumes horizontal space needed for trace columns

**Consequences:**
- Positive: Fast common-case filtering without leaving the table
- Positive: Full power available via Ctrl+F for complex investigations
- Negative: Two places to set filters; must show combined summary in status line

### Why Modal Dialogs for Detail and Timeline?

**Context:** Could use split-pane layout (detail on right) or modal dialogs for detail/timeline.

**Decision:** Modal dialogs. The trace table needs maximum horizontal space for its columns. Split-pane would compress either the table or the detail view.

**Alternatives considered:**
- Horizontal split (table left, detail right): Rejected — trace table has 7 columns, needs width
- Vertical split (table top, detail bottom): Possible but reduces visible rows; dialog is more focused
- Tab-based (switch between table and detail): Rejected — loses table context

**Consequences:**
- Positive: Full-width table with all columns visible
- Positive: Detail dialog gets full focus for exception reading
- Negative: User cannot see table and detail simultaneously
- Mitigation: Esc returns to table with selection preserved

### Why Debounced Quick Filter?

**Context:** Each keystroke in the filter bar could trigger a Dataverse query. At 60 WPM, that's one query per second.

**Decision:** 300ms debounce on filter bar changes. Query fires only when user stops typing for 300ms.

**Consequences:**
- Positive: Responsive feel without excessive API calls
- Positive: Matches standard search-as-you-type UX pattern
- Negative: 300ms delay before results update (acceptable for network-bound operation)

---

## Configuration

Screen-level settings stored in `InteractiveSession` or passed via constructor:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Default top | int | 100 | Number of traces per query |
| Default order | string | "createdon desc" | Default sort order |
| Debounce delay | int | 300 | Filter debounce in milliseconds |

---

## Testing

### Acceptance Criteria

- [ ] Screen loads traces from IPluginTraceService.ListAsync on activation
- [ ] Quick filter bar filters by type name, message name, entity (debounced)
- [ ] Errors-only checkbox sets HasException=true in filter
- [ ] Advanced filter dialog exposes all 16 PluginTraceFilter fields
- [ ] Enter on trace row opens detail dialog with full PluginTraceDetail
- [ ] Detail dialog displays exception text and message block in scrollable views
- [ ] Ctrl+T opens timeline dialog for selected trace's correlation ID
- [ ] Timeline dialog displays hierarchical tree from TimelineHierarchyBuilder
- [ ] Multi-select and Delete removes traces with confirmation
- [ ] Status line shows trace count and active filter summary
- [ ] State capture returns accurate PluginTraceScreenState
- [ ] All background operations marshal UI updates via MainLoop.Invoke

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty environment | ListAsync returns empty | Empty table, status "0 traces" |
| No correlation ID | Selected trace has null CorrelationId | Ctrl+T shows "No correlation ID" message |
| Filter produces 0 results | Restrictive filter | Empty table, status "0 traces matching filter" |
| Delete all visible | Select all + delete | Confirmation with count, table empties on success |
| Long exception text | 500+ line stack trace | Scrollable TextView in detail dialog |

### Test Examples

```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void PluginTraceScreen_CapturesInitialState()
{
    var session = CreateMockSession();
    var screen = new PluginTraceScreen(session, new TuiErrorService());

    var state = screen.CaptureState();

    Assert.Equal(0, state.TraceCount);
    Assert.Null(state.SelectedTraceId);
    Assert.False(state.IsLoading);
    Assert.False(state.IsErrorsOnly);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void PluginTraceDetailDialog_CapturesState_WithException()
{
    var detail = new PluginTraceDetail
    {
        Id = Guid.NewGuid(),
        TypeName = "MyPlugin",
        HasException = true,
        ExceptionDetails = "NullReferenceException: ...",
        CreatedOn = DateTime.UtcNow
    };
    var dialog = new PluginTraceDetailDialog(detail);

    var state = dialog.CaptureState();

    Assert.True(state.HasException);
    Assert.Contains("NullReference", state.ExceptionText);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void PluginTraceFilterDialog_DefaultState_AllNull()
{
    var dialog = new PluginTraceFilterDialog();

    var state = dialog.CaptureState();

    Assert.Null(state.TypeName);
    Assert.Null(state.MessageName);
    Assert.False(state.IsApplied);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void PluginTraceTimelineDialog_CapturesNodeCounts()
{
    var roots = new List<TimelineNode>
    {
        new() { Trace = CreateTrace(depth: 1), Children = new[]
        {
            new TimelineNode { Trace = CreateTrace(depth: 2) }
        }}
    };
    var dialog = new PluginTraceTimelineDialog(roots);

    var state = dialog.CaptureState();

    Assert.Equal(1, state.RootCount);
    Assert.Equal(2, state.TotalNodeCount);
}
```

---

## Related Specs

- [plugin-traces.md](./plugin-traces.md) - Backend service: IPluginTraceService, all data types, TimelineHierarchyBuilder
- [tui.md](./tui.md) - TUI framework: ITuiScreen, TuiDialog, IHotkeyRegistry, ITuiErrorService, state capture
- [tui-plugin-registration.md](./tui-plugin-registration.md) - Related screen: plugin registration (different from trace inspection)
- [architecture.md](./architecture.md) - Application Service boundary pattern
- [connection-pooling.md](./connection-pooling.md) - Pooled clients for trace queries

---

## Roadmap

- Real-time trace tailing with configurable poll interval
- Trace comparison view (diff two traces side-by-side)
- Aggregate statistics panel (slowest plugins, error rates by entity)
- Direct navigation from trace to plugin registration tree node
