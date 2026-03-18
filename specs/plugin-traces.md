# Plugin Traces

**Status:** Implemented
**Last Updated:** 2026-03-18
**Code:** [src/PPDS.Dataverse/Services/IPluginTraceService.cs](../src/PPDS.Dataverse/Services/IPluginTraceService.cs) | [src/PPDS.Cli/Commands/PluginTraces/](../src/PPDS.Cli/Commands/PluginTraces/) | [src/PPDS.Extension/src/panels/PluginTracesPanel.ts](../src/PPDS.Extension/src/panels/PluginTracesPanel.ts) | [src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs](../src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

The plugin traces system provides querying, inspection, and management of Dataverse plugin trace logs. It supports filtered listing, detailed trace inspection, execution timeline visualization with depth-based hierarchy, trace log settings management, and bulk deletion with progress reporting. Available across all four PPDS surfaces — CLI, TUI, VS Code extension, and MCP.

### Goals

- **Diagnostics**: Query and filter plugin trace logs for debugging plugin execution issues
- **Timeline**: Visualize plugin execution chains as hierarchical timelines using correlation IDs
- **Management**: Delete traces (single, filtered, bulk) and control trace logging settings
- **Performance**: Identify slow plugins via duration filtering and execution metrics
- **Multi-surface consistency**: Same data and operations via VS Code, TUI, MCP, and CLI (Constitution A1, A2)

### Non-Goals

- Plugin registration management (handled by [plugins.md](./plugins.md))
- Real-time trace streaming (traces are queried after execution)
- Plugin profiling or replay (handled by Plugin Registration Tool)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                      UI Surfaces (thin)                       │
│  ┌───────────┐  ┌──────────────┐  ┌──────┐  ┌─────────┐    │
│  │  VS Code  │  │     TUI      │  │ MCP  │  │   CLI   │    │
│  │  Webview  │  │   Screen     │  │ Tool │  │ Command │    │
│  └─────┬─────┘  └──────┬───────┘  └──┬───┘  └────┬────┘    │
│   JSON-RPC          Direct        Direct       Direct        │
│  ┌─────▼────────────────▼────────────▼────────────▼────────┐ │
│  │                IPluginTraceService                       │ │
│  │    ListAsync, GetAsync, GetRelatedAsync,                 │ │
│  │    BuildTimelineAsync, DeleteAsync, Settings              │ │
│  ├─────────────────────────────────────────────────────────┤ │
│  │            TimelineHierarchyBuilder                      │ │
│  │    (depth-based hierarchy, positioning calculation)       │ │
│  └─────────────────────┬───────────────────────────────────┘ │
│                        │                                      │
│  ┌─────────────────────▼───────────────────────────────────┐ │
│  │         IDataverseConnectionPool                         │ │
│  │         (FetchXml + QueryExpression)                     │ │
│  └──────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

VS Code panel communicates through the daemon (JSON-RPC over stdio). TUI, MCP, and CLI call services directly. All surfaces get the same data from the same service methods (Constitution A1, A2).

### Components

| Component | Responsibility |
|-----------|----------------|
| `PluginTraceService` | Query, delete, settings operations via connection pool |
| `TimelineHierarchyBuilder` | Static utility: builds depth-based timeline hierarchies |
| CLI Commands (6) | list, get, related, timeline, settings, delete |
| `PluginTracesPanel.ts` | VS Code webview panel — filter bar, split pane, 5-tab detail, auto-refresh |
| `PluginTracesScreen.cs` | TUI screen — split pane, filter/timeline/delete/traceLevel dialogs |
| `PluginTracesListTool.cs` | MCP tool — filtered trace listing |
| `PluginTracesGetTool.cs` | MCP tool — full trace detail |
| `PluginTracesTimelineTool.cs` | MCP tool — execution timeline |
| `PluginTracesDeleteTool.cs` | MCP tool — bulk trace cleanup |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for pooled Dataverse clients
- Depends on: [authentication.md](./authentication.md) for environment connection
- Uses patterns from: [architecture.md](./architecture.md) for Application Service layer
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, D1

---

## Specification

### Core Requirements

1. **Filtered listing**: Query traces with 16 filter criteria including type, message, entity, mode, time range, duration range, error state, and correlation
2. **Detail inspection**: Retrieve full trace details including exception stack traces, trace output, and configuration
3. **Related traces**: Find all traces sharing a correlation ID for request-level debugging
4. **Timeline**: Build hierarchical execution tree from flat traces using Dataverse execution depth
5. **Settings**: Read and update the organization-level plugin trace log setting (Off/Exception/All)
6. **Deletion**: Delete single, by IDs, by filter, by age, or all traces with progress reporting
7. **Count**: Count matching traces for dry-run deletion previews

### Primary Flows

**Trace Investigation:**

1. **List traces**: `ppds plugintraces list --errors-only --last-hour` to find recent failures
2. **Get details**: `ppds plugintraces get <trace-id>` to see exception and trace output
3. **View timeline**: `ppds plugintraces timeline <trace-id>` to see full execution chain
4. **Find related**: `ppds plugintraces related <trace-id>` to see all traces from same request

**Trace Cleanup:**

1. **Preview**: `ppds plugintraces delete --older-than 7d --dry-run` to count traces
2. **Delete**: `ppds plugintraces delete --older-than 7d` to remove old traces
3. **Delete all**: `ppds plugintraces delete --all --force` to clear all traces

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `pluginTraces/list` | `{ filter?: TraceFilter, top?, environmentUrl? }` | `{ traces: PluginTraceInfo[] }` |
| `pluginTraces/get` | `{ id, environmentUrl? }` | `{ trace: PluginTraceDetail }` |
| `pluginTraces/timeline` | `{ correlationId, environmentUrl? }` | `{ nodes: TimelineNode[] }` |
| `pluginTraces/delete` | `{ ids?, olderThanDays?, environmentUrl? }` | `{ deletedCount: number }` |
| `pluginTraces/traceLevel` | `{ environmentUrl? }` | `{ level: string }` |
| `pluginTraces/setTraceLevel` | `{ level, environmentUrl? }` | `{ success: boolean }` |

**TraceFilter fields:** typeName?, messageName?, primaryEntity?, mode? (Sync/Async), hasException?, correlationId?, minDuration?, startDate?, endDate?

**PluginTraceInfo fields (list):** id, createdOn, typeName, primaryEntity, messageName, operationType, mode, depth, duration, hasException

**PluginTraceDetail fields:** all of PluginTraceInfo + exceptionDetails, messageBlock, configuration, secureConfiguration, correlationId, executionStartTime, performanceConstructorDuration, performanceExecutionDuration

**TimelineNode fields:** traceId, typeName, messageName, depth, duration, startTime, hasException, children[]

### Extension Surface

- **viewType:** `ppds.pluginTraces`
- **Layout:** Three-zone with split pane — top: filter bar + trace list, bottom: detail/timeline (resizable splitter)
- **Filter bar (persistent, collapsible):** Entity filter, message filter, plugin name filter, mode (Sync/Async/All), exceptions only toggle, date range, quick filters (Last Hour, Exceptions Only, Long Running >1s), clear all
- **Table columns:** Status (icon), Time, Duration, Plugin Name, Entity, Message, Depth, Mode
- **Color coding:** Exception rows red, long-running (>1s) yellow
- **Detail pane (5 tabs):** Details, Exception (monospace), Message Block (monospace), Configuration, Timeline (hierarchical tree with timing)
- **Actions:** Refresh, filter, auto-refresh toggle (5/15/30/60/300s), delete (selected/filtered/older than N days with confirmation), set trace level (Off/Exception/All with volume warning), export (CSV/JSON), open related traces (by correlation ID), environment picker with theming

### TUI Surface

- **Class:** `PluginTracesScreen` extending `TuiScreenBase`, implementing `ITuiScreen` and `ITuiStateCapture<PluginTraceScreenState>`
- **Layout:** Split pane — top: data table, bottom: detail view (resizable via SplitterView)
- **Filter bar:** Inline quick-filter bar for type name, message name, entity, and errors-only toggle. Advanced filter via Ctrl+F opens `PluginTraceFilterDialog` with all 16 `PluginTraceFilter` criteria. Quick filter bar uses 300ms debounce to avoid excessive Dataverse calls.
- **Hotkeys:** Ctrl+R (refresh), Enter (toggle detail), Ctrl+F (filter dialog), Ctrl+T (timeline), Ctrl+D (delete dialog), Ctrl+L (trace level), Ctrl+E (export), Tab (cycle detail tabs)
- **Dialogs:**
  - `PluginTraceDetailDialog` — full trace detail with exception (scrollable), message block, timing. Button to navigate to timeline for this trace's correlation ID. Implements `ITuiStateCapture<PluginTraceDetailDialogState>`.
  - `PluginTraceFilterDialog` — advanced 16-criteria filter form. Returns configured `PluginTraceFilter` on Apply, null on Cancel. Implements `ITuiStateCapture<PluginTraceFilterDialogState>`.
  - `PluginTraceTimelineDialog` — hierarchical timeline tree from `TimelineHierarchyBuilder`. Selecting a node opens its detail dialog. Implements `ITuiStateCapture<PluginTraceTimelineDialogState>`.
  - `TraceLevelDialog` — read/set trace logging level with volume warning for "All"
  - `TraceDeleteDialog` — delete by selected IDs, by filter, or older than N days with confirmation
- **Status line:** Shows trace count and active filter summary
- **State capture types:**
  - `PluginTraceScreenState`: TraceCount, SelectedTraceId, SelectedTypeName, IsLoading, IsErrorsOnly, QuickFilterType/Message/Entity, HasAdvancedFilter, StatusText, ErrorMessage
  - `PluginTraceDetailDialogState`: TraceId, TypeName, MessageName, PrimaryEntity, DurationMs, HasException, ExceptionText, MessageBlock, CorrelationId, Depth
  - `PluginTraceFilterDialogState`: All 16 filter criteria fields + IsApplied
  - `PluginTraceTimelineDialogState`: RootCount, TotalNodeCount, SelectedTraceId, SelectedTypeName, TotalDurationMs
- **Error handling:** All service calls run on background thread; UI updates via `Application.MainLoop.Invoke()`. Errors reported via `ITuiErrorService.ReportError()` with F12 detail access. Connection errors show "Press F5 to retry" in status line.
- **Edge cases:** Trace with no correlation ID disables timeline button (tooltip explains why). No traces matching filter shows "0 traces matching filter" in status line. Delete button disabled during load.

### MCP Surface

| Tool | Input | Output | Status |
|------|-------|--------|--------|
| `ppds_plugin_traces_list` | `{ filter?, top? }` | Filtered trace list | Exists |
| `ppds_plugin_traces_get` | `{ id }` | Full trace detail | Exists |
| `ppds_plugin_traces_timeline` | `{ correlationId }` | Execution tree | Exists |
| `ppds_plugin_traces_delete` | `{ ids?, olderThanDays? }` | Deleted count | Exists |

### Constraints

- Plugin trace logging must be enabled in the environment (settings set to Exception or All)
- Traces are created by the platform, not by this system
- `plugintracelog` entity has OData limitations; FetchXml is used for count operations
- Bulk deletion uses parallel requests via connection pool
- TUI filter bar debounce at 300ms to avoid excessive Dataverse calls
- Maximum 1000 traces per query (configurable via top parameter)
- Timeline dialog shows traces for one correlation ID only
- Deletion confirmation is mandatory; no silent bulk deletes

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `trace-id` | Must be valid GUID | `ArgumentException` |
| `--older-than` | Must be valid duration (e.g., 7d, 24h, 30m) | Parse error |
| `--all` | Requires `--force` flag | Error message |
| `--record` | Format: `entity` or `entity/guid` | Parse error with example |
| Min Duration (TUI filter) | Non-negative integer | "Duration must be a positive number" |
| Max Duration (TUI filter) | Greater than Min Duration | "Max duration must be greater than min" |
| Created After/Before (TUI filter) | Valid date format | "Enter date as YYYY-MM-DD or YYYY-MM-DD HH:mm" |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-PT-01 | `pluginTraces/list` applies all filter combinations server-side | TBD | ✅ |
| AC-PT-02 | `pluginTraces/get` returns full detail including exception, message block, configuration | TBD | ✅ |
| AC-PT-03 | `pluginTraces/timeline` returns hierarchical execution tree | TBD | ✅ |
| AC-PT-04 | `pluginTraces/delete` supports by IDs, by filter, and by age | TBD | ✅ |
| AC-PT-05 | `pluginTraces/traceLevel` and `setTraceLevel` read/write organization setting | TBD | ✅ |
| AC-PT-06 | VS Code panel displays trace list with filter bar, color-coded status, resizable detail pane | TBD | ✅ |
| AC-PT-07 | Detail pane has 5 tabs (Details, Exception, Message Block, Configuration, Timeline) | TBD | ✅ |
| AC-PT-08 | Timeline tab renders hierarchical execution chain with timing | TBD | ✅ |
| AC-PT-09 | Quick filters apply correct filter combinations | TBD | ✅ |
| AC-PT-10 | Auto-refresh updates list at configured interval without losing selection | TBD | ✅ |
| AC-PT-11 | Delete operations require confirmation and report count | TBD | ✅ |
| AC-PT-12 | Trace level change shows warning about volume impact for "All" | TBD | ✅ |
| AC-PT-13 | TUI PluginTracesScreen provides equivalent functionality with split pane | TBD | ✅ |
| AC-PT-14 | Existing MCP tools continue working; delete tool supports bulk cleanup | TBD | ✅ |
| AC-PT-15 | All surfaces handle "trace level is Off" — informational message, not empty table | TBD | ✅ |
| AC-PT-16 | VS Code panel has export button — CSV and JSON formats, respects current filter state | TBD | 🔲 |
| AC-PT-17 | VS Code filter bar has start date and end date inputs; quick filters update date inputs | TBD | 🔲 |
| AC-PT-18 | Panel calls `resolveEnvironmentId()` and supports Open in Maker action | TBD | 🔲 |
| AC-PT-19 | TUI Ctrl+E hotkey exports filtered traces to CSV/JSON file | TBD | 🔲 |
| AC-PT-20 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |

---

## Core Types

### IPluginTraceService

Service for querying and managing plugin trace logs ([`IPluginTraceService.cs:11-139`](../src/PPDS.Dataverse/Services/IPluginTraceService.cs#L11-L139)).

```csharp
public interface IPluginTraceService
{
    // Query Operations
    Task<List<PluginTraceInfo>> ListAsync(
        PluginTraceFilter? filter = null, int top = 100,
        CancellationToken cancellationToken = default);
    Task<PluginTraceDetail?> GetAsync(
        Guid traceId, CancellationToken cancellationToken = default);
    Task<List<PluginTraceInfo>> GetRelatedAsync(
        Guid correlationId, int top = 1000,
        CancellationToken cancellationToken = default);
    Task<List<TimelineNode>> BuildTimelineAsync(
        Guid correlationId, CancellationToken cancellationToken = default);

    // Delete Operations
    Task<bool> DeleteAsync(
        Guid traceId, CancellationToken cancellationToken = default);
    Task<int> DeleteByIdsAsync(
        IEnumerable<Guid> traceIds, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    Task<int> DeleteByFilterAsync(
        PluginTraceFilter filter, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    Task<int> DeleteAllAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    Task<int> DeleteOlderThanAsync(
        TimeSpan olderThan, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    // Settings
    Task<PluginTraceSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default);
    Task SetSettingsAsync(
        PluginTraceLogSetting setting,
        CancellationToken cancellationToken = default);

    // Count
    Task<int> CountAsync(
        PluginTraceFilter? filter = null,
        CancellationToken cancellationToken = default);
}
```

The implementation ([`PluginTraceService.cs`](../src/PPDS.Dataverse/Services/PluginTraceService.cs)) uses `IDataverseConnectionPool` for all Dataverse access, with QueryExpression for list/filter operations and FetchXml for count operations.

### PluginTraceInfo

Summary record for list views ([`IPluginTraceService.cs:144-184`](../src/PPDS.Dataverse/Services/IPluginTraceService.cs#L144-L184)).

```csharp
public record PluginTraceInfo
{
    public required Guid Id { get; init; }
    public required string TypeName { get; init; }
    public string? MessageName { get; init; }
    public string? PrimaryEntity { get; init; }
    public PluginTraceMode Mode { get; init; }
    public PluginTraceOperationType OperationType { get; init; }
    public int Depth { get; init; }
    public required DateTime CreatedOn { get; init; }
    public int? DurationMs { get; init; }
    public bool HasException { get; init; }
    public Guid? CorrelationId { get; init; }
    public Guid? RequestId { get; init; }
    public Guid? PluginStepId { get; init; }
}
```

### PluginTraceDetail

Full trace details, extends PluginTraceInfo ([`IPluginTraceService.cs:189-229`](../src/PPDS.Dataverse/Services/IPluginTraceService.cs#L189-L229)).

```csharp
public sealed record PluginTraceDetail : PluginTraceInfo
{
    public int? ConstructorDurationMs { get; init; }
    public DateTime? ExecutionStartTime { get; init; }
    public DateTime? ConstructorStartTime { get; init; }
    public string? ExceptionDetails { get; init; }
    public string? MessageBlock { get; init; }
    public string? Configuration { get; init; }
    public string? SecureConfiguration { get; init; }
    public string? Profile { get; init; }
    public Guid? OrganizationId { get; init; }
    public Guid? PersistenceKey { get; init; }
    public bool IsSystemCreated { get; init; }
    public Guid? CreatedById { get; init; }
    public Guid? CreatedOnBehalfById { get; init; }
}
```

### PluginTraceFilter

Filter criteria for trace queries ([`IPluginTraceService.cs:234-283`](../src/PPDS.Dataverse/Services/IPluginTraceService.cs#L234-L283)).

```csharp
public sealed record PluginTraceFilter
{
    public string? TypeName { get; init; }            // Contains match
    public string? MessageName { get; init; }
    public string? PrimaryEntity { get; init; }       // Contains match
    public PluginTraceMode? Mode { get; init; }
    public PluginTraceOperationType? OperationType { get; init; }
    public int? MinDepth { get; init; }
    public int? MaxDepth { get; init; }
    public DateTime? CreatedAfter { get; init; }
    public DateTime? CreatedBefore { get; init; }
    public int? MinDurationMs { get; init; }
    public int? MaxDurationMs { get; init; }
    public bool? HasException { get; init; }
    public Guid? CorrelationId { get; init; }
    public Guid? RequestId { get; init; }
    public Guid? PluginStepId { get; init; }
    public string? OrderBy { get; init; }             // Default: "createdon desc"
}
```

### PluginTraceSettings

Current trace logging configuration ([`IPluginTraceService.cs:330-343`](../src/PPDS.Dataverse/Services/IPluginTraceService.cs#L330-L343)).

```csharp
public sealed record PluginTraceSettings
{
    public required PluginTraceLogSetting Setting { get; init; }
    public string SettingName => Setting switch { ... }; // Computed: "Off", "Exception", "All"
}
```

### TimelineNode

Node in the plugin execution timeline hierarchy ([`IPluginTraceService.cs:348-364`](../src/PPDS.Dataverse/Services/IPluginTraceService.cs#L348-L364)).

```csharp
public sealed record TimelineNode
{
    public required PluginTraceInfo Trace { get; init; }
    public IReadOnlyList<TimelineNode> Children { get; init; } = Array.Empty<TimelineNode>();
    public int HierarchyDepth { get; init; }       // 0-based (converted from Dataverse 1-based depth)
    public double OffsetPercent { get; init; }      // Timeline visualization offset
    public double WidthPercent { get; init; }       // Timeline visualization width
}
```

### Enums

```csharp
public enum PluginTraceMode
{
    Synchronous = 0,    // Blocks user transaction
    Asynchronous = 1    // Background processing
}

public enum PluginTraceOperationType
{
    Unknown = 0,
    Plugin = 1,
    WorkflowActivity = 2
}

public enum PluginTraceLogSetting
{
    Off = 0,            // No tracing
    Exception = 1,      // Log only exceptions
    All = 2             // Log all executions
}
```

### TimelineHierarchyBuilder

Static utility for building hierarchical timelines from flat trace records ([`TimelineHierarchyBuilder.cs`](../src/PPDS.Dataverse/Services/TimelineHierarchyBuilder.cs)).

```csharp
public static class TimelineHierarchyBuilder
{
    // Build hierarchy from flat traces using execution depth
    static List<TimelineNode> Build(IReadOnlyList<PluginTraceInfo> traces);

    // Build hierarchy with offset/width positioning pre-calculated
    static List<TimelineNode> BuildWithPositioning(IReadOnlyList<PluginTraceInfo> traces);

    // Get total duration span across all traces (ms)
    static long GetTotalDuration(IReadOnlyList<PluginTraceInfo> traces);

    // Count total nodes including descendants
    static int CountTotalNodes(IReadOnlyList<TimelineNode> roots);
}
```

### Usage Pattern

```csharp
var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

// List recent errors
var errors = await traceService.ListAsync(
    new PluginTraceFilter { HasException = true, CreatedAfter = DateTime.UtcNow.AddHours(-1) });

// Get full details
var detail = await traceService.GetAsync(errors[0].Id);

// Build timeline from correlation
if (detail?.CorrelationId is { } corrId)
{
    var timeline = await traceService.BuildTimelineAsync(corrId);
    // timeline is a tree: root nodes with Children
}

// Cleanup old traces
var deleted = await traceService.DeleteOlderThanAsync(
    TimeSpan.FromDays(30), new Progress<int>(n => Console.Write($"\rDeleted {n}")));
```

---

## CLI Commands

All commands accept `--profile` and `--environment` options for authentication.

### `ppds plugintraces list`

Lists traces with comprehensive filtering. Supports CSV and JSON output formats.

| Option | Description |
|--------|-------------|
| `--type, -t` | Filter by plugin type name (contains) |
| `--message, -m` | Filter by message name (Create, Update, etc.) |
| `--entity` | Filter by primary entity (contains) |
| `--mode` | Filter by execution mode: sync or async |
| `--errors-only` | Show only traces with exceptions |
| `--success-only` | Show only successful traces |
| `--since` | Traces created after (ISO 8601) |
| `--until` | Traces created before (ISO 8601) |
| `--min-duration` | Minimum execution duration (ms) |
| `--max-duration` | Maximum execution duration (ms) |
| `--correlation-id` | Filter by correlation ID |
| `--request-id` | Filter by request ID |
| `--step-id` | Filter by plugin step ID |
| `--last-hour` | Shortcut: traces from last hour |
| `--last-24h` | Shortcut: traces from last 24 hours |
| `--async-only` | Show only asynchronous traces |
| `--recursive` | Show only nested traces (depth > 1) |
| `--record` | Filter by record (entity or entity/guid) |
| `--filter` | JSON file with filter criteria |
| `--top, -n` | Max results (default: 100) |
| `--order-by` | Sort field (default: createdon desc) |

### `ppds plugintraces get <trace-id>`

Displays full trace details: basic info, timing, correlation, exception details, trace output, and configuration.

### `ppds plugintraces related`

Finds all traces sharing a correlation ID.

| Option | Description |
|--------|-------------|
| `<trace-id>` | Get correlation ID from this trace (optional) |
| `--correlation-id` | Direct correlation ID filter |
| `--record` | Filter by record (entity or entity/guid) |

### `ppds plugintraces timeline`

Displays plugin execution as a hierarchical tree showing parent-child relationships and timing.

| Option | Description |
|--------|-------------|
| `<trace-id>` | Get correlation ID from this trace (optional) |
| `--correlation-id` | Direct correlation ID filter |

### `ppds plugintraces settings get`

Shows the current plugin trace logging setting (Off, Exception, or All).

### `ppds plugintraces settings set <value>`

Updates the organization-level trace logging setting. Values: `off`, `exception`, `all`.

### `ppds plugintraces delete`

Deletes plugin trace logs with multiple modes.

| Option | Description |
|--------|-------------|
| `<trace-id>` | Delete a single trace by ID |
| `--ids` | Comma-separated list of trace IDs |
| `--older-than` | Delete traces older than duration (7d, 24h, 30m) |
| `--all` | Delete ALL traces (requires --force) |
| `--dry-run` | Preview count without deleting |
| `--force` | Skip confirmation for --all |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Trace not found | Invalid trace ID | Returns null (get) or false (delete) |
| Settings update failed | Insufficient privileges | Requires System Administrator role |
| Deletion failed | Service protection limits | Automatic retry via connection pool |
| Filter parse error | Invalid --record or --older-than format | Error message with expected format |
| Service unavailable | No connection to environment | Status line error; retry with F5 (TUI) or Ctrl+R (Extension) |
| Authentication expired | Token expired mid-session | Re-authentication dialog (TUI) or reconnect prompt (Extension) |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No traces match filter | Return empty list, count returns 0. TUI/Extension show "No traces found" message |
| Delete non-existent trace | Return false (not found) |
| --all without --force | Error: "--all requires --force" |
| Depth = 1 trace | Root node in timeline (HierarchyDepth = 0) |
| No correlation ID | Timeline returns single-node list. TUI disables timeline button with tooltip |
| Trace level is Off | Informational message on all surfaces, not just empty table |
| Very long exception text | Scrollable views on all surfaces (TextView in TUI, monospace in Extension) |
| Delete while loading (TUI) | Delete button disabled during load; re-enabled after |

---

## Design Decisions

### Why Depth-Based Timeline Hierarchy?

**Context:** Dataverse traces include an `Execution Depth` field (1 = top-level, 2 = called by depth 1, etc.) and a `Correlation ID` grouping related traces.

**Decision:** Build parent-child hierarchy using stack-based depth tracking on chronologically sorted traces. Convert 1-based Dataverse depth to 0-based hierarchy depth.

**Algorithm:**
1. Sort traces by CreatedOn ascending
2. For each trace, pop stack entries at same or greater depth (siblings)
3. If stack empty, trace is root; otherwise, child of stack top
4. Push trace onto stack as potential parent

**Consequences:**
- Positive: Accurate hierarchy from flat data without explicit parent references
- Positive: Handles arbitrary nesting depth
- Negative: Assumes chronological ordering reflects call order (valid for Dataverse)

### Why IProgress\<int\> Instead of IProgressReporter?

**Context:** Delete operations report a single metric: count of deleted traces. Migration's `IProgressReporter` is designed for multi-entity, multi-phase operations with rich metrics.

**Decision:** Use `IProgress<int>` from the BCL for single-metric progress. Simpler interface, no migration dependency.

**Consequences:**
- Positive: No coupling to migration library
- Positive: Standard BCL pattern, familiar to .NET developers
- Negative: Cannot report errors or phases (not needed for delete count)

### Why FetchXml for Count Operations?

**Context:** OData `$count` has limitations on the `plugintracelog` entity. QueryExpression with `ReturnTotalRecordCount` also has known issues.

**Decision:** Use FetchXml `aggregate="true"` with `count` for reliable counts.

**Consequences:**
- Positive: Reliable counts across all filter combinations
- Negative: FetchXml requires string construction (mitigated by builder methods)

### Why Parallel Deletion via Connection Pool?

**Context:** Plugin trace tables can contain millions of records. Sequential deletion is impractical.

**Decision:** Use `Parallel.ForEachAsync` with connection pool to delete multiple traces concurrently, reporting progress via `IProgress<int>`.

**Consequences:**
- Positive: Deletion throughput scales with pool size
- Positive: Progress feedback for long operations
- Negative: Service protection limits may throttle (handled by pool retry)

### Why Inline Filter Bar + Advanced Filter Dialog (TUI)?

**Context:** Plugin traces have 16 filter criteria. Exposing all in the main UI would be overwhelming; exposing none would lose the quick-filter experience.

**Decision:** Two-tier filtering. Inline filter bar for the 3 most common text filters (type, message, entity) plus errors-only toggle. Ctrl+F opens advanced dialog for all 16 criteria.

**Consequences:**
- Positive: Fast common-case filtering without leaving the table
- Positive: Full power available via Ctrl+F for complex investigations
- Negative: Two places to set filters; must show combined summary in status line

### Why Split Pane in Extension but Modal Dialogs in TUI?

**Context:** Both surfaces need to show trace detail alongside the trace list.

**Decision:** Extension uses a resizable split pane (top: list, bottom: detail). TUI uses modal dialogs for detail and timeline.

**Rationale:** Extension webview has sufficient viewport height and CSS layout flexibility for split panes. TUI has limited terminal height and 7 trace table columns that need full width. Modal dialogs give the TUI detail view full focus for exception reading, with Esc returning to the table with selection preserved.

---

## Configuration

### PluginTraceFilter Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `TypeName` | string? | null | Plugin type name (contains match) |
| `MessageName` | string? | null | SDK message name |
| `PrimaryEntity` | string? | null | Entity logical name (contains match) |
| `Mode` | enum? | null | Synchronous or Asynchronous |
| `OperationType` | enum? | null | Plugin or WorkflowActivity |
| `MinDepth` | int? | null | Minimum execution depth |
| `MaxDepth` | int? | null | Maximum execution depth |
| `CreatedAfter` | DateTime? | null | Traces after this time |
| `CreatedBefore` | DateTime? | null | Traces before this time |
| `MinDurationMs` | int? | null | Minimum duration (ms) |
| `MaxDurationMs` | int? | null | Maximum duration (ms) |
| `HasException` | bool? | null | Filter by error state |
| `CorrelationId` | Guid? | null | Correlation ID |
| `RequestId` | Guid? | null | Request ID |
| `PluginStepId` | Guid? | null | Plugin step ID |
| `OrderBy` | string? | null | Sort field (default: "createdon desc") |

### TUI Screen Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Default top | int | 100 | Number of traces per query |
| Default order | string | "createdon desc" | Default sort order |
| Debounce delay | int | 300 | Filter debounce in milliseconds |

---

## Testing

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty environment | Any list query | Empty list |
| Single trace | Timeline with 1 trace | Single root node, no children |
| Deep nesting | Depth 1->2->3->2->1 | Two root nodes, first with 2-level subtree |
| Missing duration | Trace with DurationMs=null | Display as "--" or 0 |
| Concurrent deletion | Parallel delete + list | Eventually consistent results |
| No correlation ID (TUI) | Selected trace has null CorrelationId | Ctrl+T shows "No correlation ID" message |
| Filter produces 0 results | Restrictive filter | Empty table, status "0 traces matching filter" |
| Delete all visible (TUI) | Select all + delete | Confirmation with count, table empties on success |
| Long exception text | 500+ line stack trace | Scrollable TextView in TUI detail dialog |

### Test Examples

```csharp
[Fact]
public void TimelineHierarchyBuilder_BuildsCorrectHierarchy()
{
    var traces = new List<PluginTraceInfo>
    {
        CreateTrace(depth: 1, createdOn: t0),
        CreateTrace(depth: 2, createdOn: t1),
        CreateTrace(depth: 3, createdOn: t2),
        CreateTrace(depth: 2, createdOn: t3),
        CreateTrace(depth: 1, createdOn: t4)
    };

    var roots = TimelineHierarchyBuilder.Build(traces);

    Assert.Equal(2, roots.Count);          // Two root nodes
    Assert.Single(roots[0].Children);       // First root has 1 child
    Assert.Single(roots[0].Children[0].Children); // That child has 1 grandchild
    Assert.Empty(roots[1].Children);        // Second root has no children
}

[Fact]
public async Task ListAsync_FiltersErrorsOnly()
{
    var filter = new PluginTraceFilter { HasException = true };

    var traces = await traceService.ListAsync(filter);

    Assert.All(traces, t => Assert.True(t.HasException));
}

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
```

---

## Related Specs

- [plugins.md](./plugins.md) - Plugin registration (different from trace inspection)
- [connection-pooling.md](./connection-pooling.md) - Pooled clients for parallel operations
- [architecture.md](./architecture.md) - Application Service layer pattern
- [cli.md](./cli.md) - CLI output formatting and global options
- [tui.md](./tui.md) - TUI framework: ITuiScreen, TuiDialog, IHotkeyRegistry, ITuiErrorService, state capture

---

## Changelog

| Date | Change |
|------|--------|
| 2026-01-28 | Initial spec — service layer and CLI commands |
| 2026-03-18 | Merged TUI surface from tui-plugin-traces.md, Extension/MCP surfaces from panel-parity.md per SL1/SL3 |

---

## Roadmap

- Real-time trace tailing with configurable poll interval
- Trace export to file (CSV/JSON) for offline analysis
- Aggregate statistics (slowest plugins, error rates by entity)
- Trace comparison view (diff two traces side-by-side)
- Direct navigation from trace to plugin registration tree node
