# Plugin Traces Panel Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Plugin Traces panel across all 4 surfaces (Daemon RPC, VS Code extension, TUI, MCP delete tool), delivering the most complex panel in the panel-parity spec with multi-pane layout, rich filtering, timeline visualization, trace level management, and batch delete.

**Architecture:** The domain service (`IPluginTraceService`) already exists with full CRUD + timeline + settings (552 lines). We add 6 RPC endpoints in the daemon, a VS Code webview panel with split pane, filter bar, and 5-tab detail area, a TUI screen with dialogs, and 1 new MCP delete tool. All surfaces call the same service methods (Constitution A1, A2).

**Tech Stack:** C# (.NET 8), TypeScript (VS Code extension + webview), Terminal.Gui (TUI), StreamJsonRpc (RPC), ModelContextProtocol (MCP)

**Spec:** `specs/panel-parity.md` — Panel 4 (Plugin Traces), Acceptance Criteria AC-PT-01 through AC-PT-15

**Issues:** #342, #346, #585, #591

---

## Design Decisions

### Purpose-built PluginTraceFilterBar (not shared)
The existing `FilterBar<T>` is a client-side text search. Plugin Traces needs server-side filtering with 9+ typed controls (entity, message, plugin name, mode selector, exceptions toggle, date range, quick filter presets). This filter complexity is unique to Plugin Traces. If a pattern emerges across Phase 2c/2d panels, we can extract then. YAGNI.

### Horizontal waterfall timeline
The service already computes `OffsetPercent` and `WidthPercent` on `TimelineNode`. We render each trace as a row with a horizontal bar positioned/sized by these percentages, indented by depth, color-coded (red for exceptions, yellow for slow). Pure CSS/HTML, no canvas. Standard debugging visualization (browser DevTools, SQL plans).

### CSS + drag handle for split pane
Import Jobs uses a simple show/hide detail pane. Plugin Traces gets a true resizable splitter: a `div.resize-handle` between top (filter + table) and bottom (5-tab detail) panes. `mousedown`/`mousemove`/`mouseup` handlers adjust `flex-basis`. Split ratio persisted via `vscode.getState()`/`setState()`. ~40 lines of JS for significantly better debugging UX.

### Delete via RPC not inline service
The `pluginTraces/delete` RPC endpoint consolidates 3 delete modes (by IDs, by filter, by age) behind a single RPC method with optional parameters. The webview sends the mode + params; the RPC handler dispatches to the correct service method. This avoids 3 separate RPC endpoints for a single UI action.

### Auto-refresh preserves selection
Auto-refresh calls `pluginTraces/list` on interval, then does a targeted DOM update: new rows are inserted, removed rows are deleted, existing rows update in place. The selected row ID is preserved across refreshes. If the selected trace was deleted server-side, selection clears and detail pane shows a message.

### TraceFilter DTO reused across RPC, webview, TUI
A single `TraceFilterDto` shape is used in the RPC request, the webview filter bar state, and the TUI filter dialog. This ensures the filter contract is consistent across surfaces without duplicating filter logic.

---

## File Structure

### Files to modify
| File | Change |
|------|--------|
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Add 6 `pluginTraces/*` RPC methods + DTOs |
| `src/PPDS.Extension/src/types.ts` | Add Plugin Traces response interfaces |
| `src/PPDS.Extension/src/daemonClient.ts` | Add 6 `pluginTraces*` methods |
| `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` | Add Plugin Traces panel message types |
| `src/PPDS.Extension/esbuild.js` | Add plugin-traces-panel JS + CSS entries |
| `src/PPDS.Extension/src/extension.ts` | Register `ppds.openPluginTraces` + `ppds.openPluginTracesForEnv` commands |
| `src/PPDS.Extension/src/views/toolsTreeView.ts` | Add Plugin Traces to the Tools tree |
| `src/PPDS.Extension/package.json` | Add command contributions + menu items |
| `src/PPDS.Cli/Tui/TuiShell.cs` | Add menu item to open Plugin Traces screen |

### Files to create
| File | Purpose |
|------|---------|
| `src/PPDS.Extension/src/panels/PluginTracesPanel.ts` | Host-side panel (extends WebviewPanelBase) |
| `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts` | Browser-side webview script |
| `src/PPDS.Extension/src/panels/styles/plugin-traces-panel.css` | Panel-specific CSS |
| `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs` | TUI screen (extends TuiScreenBase) |
| `src/PPDS.Cli/Tui/Dialogs/PluginTraceFilterDialog.cs` | TUI filter dialog |
| `src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs` | TUI detail dialog |
| `src/PPDS.Cli/Tui/Dialogs/TraceTimelineDialog.cs` | TUI timeline dialog |
| `src/PPDS.Cli/Tui/Dialogs/TraceLevelDialog.cs` | TUI trace level dialog |
| `src/PPDS.Cli/Tui/Dialogs/TraceDeleteDialog.cs` | TUI delete confirmation dialog |
| `src/PPDS.Mcp/Tools/PluginTracesDeleteTool.cs` | MCP delete tool |
| `src/PPDS.Extension/src/__tests__/panels/pluginTracesPanel.test.ts` | Unit tests for panel message types |

---

## Chunk 1: Data Layer — RPC Endpoints + DTOs

### Task 1: Add Plugin Traces RPC DTOs

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — Add DTOs at the bottom

- [ ] **Step 1: Add Plugin Traces DTO classes**

Add after the existing Import Jobs DTOs at the bottom of `RpcMethodHandler.cs`:

```csharp
// ── Plugin Traces DTOs ──────────────────────────────────────────────────────

public class PluginTracesListResponse
{
    [JsonPropertyName("traces")]
    public List<PluginTraceInfoDto> Traces { get; set; } = [];
}

public class PluginTracesGetResponse
{
    [JsonPropertyName("trace")]
    public PluginTraceDetailDto Trace { get; set; } = null!;
}

public class PluginTracesTimelineResponse
{
    [JsonPropertyName("nodes")]
    public List<TimelineNodeDto> Nodes { get; set; } = [];
}

public class PluginTracesDeleteResponse
{
    [JsonPropertyName("deletedCount")]
    public int DeletedCount { get; set; }
}

public class PluginTracesTraceLevelResponse
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "";

    [JsonPropertyName("levelValue")]
    public int LevelValue { get; set; }
}

public class PluginTracesSetTraceLevelResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class PluginTraceInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = "";

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("createdOn")]
    public string CreatedOn { get; set; } = "";

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }
}

public class PluginTraceDetailDto : PluginTraceInfoDto
{
    [JsonPropertyName("constructorDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConstructorDurationMs { get; set; }

    [JsonPropertyName("executionStartTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionStartTime { get; set; }

    [JsonPropertyName("exceptionDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionDetails { get; set; }

    [JsonPropertyName("messageBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageBlock { get; set; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    [JsonPropertyName("secureConfiguration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecureConfiguration { get; set; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }
}

public class TimelineNodeDto
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    [JsonPropertyName("offsetPercent")]
    public double OffsetPercent { get; set; }

    [JsonPropertyName("widthPercent")]
    public double WidthPercent { get; set; }

    [JsonPropertyName("hierarchyDepth")]
    public int HierarchyDepth { get; set; }

    [JsonPropertyName("children")]
    public List<TimelineNodeDto> Children { get; set; } = [];
}

public class TraceFilterDto
{
    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("hasException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasException { get; set; }

    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("minDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinDurationMs { get; set; }

    [JsonPropertyName("startDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndDate { get; set; }
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded (DTOs are standalone, no consumers yet).

### Task 2: Add pluginTraces/list and pluginTraces/get RPC methods

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — Add methods in a new `#region Plugin Traces`

- [ ] **Step 1: Add mapper helpers and list/get methods**

Add in the RPC handler class (after the Import Jobs region):

```csharp
#region Plugin Traces

private static PluginTraceFilter? MapTraceFilterFromDto(TraceFilterDto? dto)
{
    if (dto == null) return null;

    PluginTraceMode? mode = dto.Mode?.ToLowerInvariant() switch
    {
        "sync" or "synchronous" => PluginTraceMode.Synchronous,
        "async" or "asynchronous" => PluginTraceMode.Asynchronous,
        _ => null
    };

    return new PluginTraceFilter
    {
        TypeName = dto.TypeName,
        MessageName = dto.MessageName,
        PrimaryEntity = dto.PrimaryEntity,
        Mode = mode,
        HasException = dto.HasException,
        CorrelationId = Guid.TryParse(dto.CorrelationId, out var cid) ? cid : null,
        MinDurationMs = dto.MinDurationMs,
        CreatedAfter = DateTime.TryParse(dto.StartDate, out var start) ? start : null,
        CreatedBefore = DateTime.TryParse(dto.EndDate, out var end) ? end : null,
    };
}

private static PluginTraceInfoDto MapTraceInfoToDto(PluginTraceInfo trace)
{
    return new PluginTraceInfoDto
    {
        Id = trace.Id.ToString(),
        TypeName = trace.TypeName,
        MessageName = trace.MessageName,
        PrimaryEntity = trace.PrimaryEntity,
        Mode = trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
        OperationType = trace.OperationType.ToString(),
        Depth = trace.Depth,
        CreatedOn = trace.CreatedOn.ToString("o"),
        DurationMs = trace.DurationMs,
        HasException = trace.HasException,
        CorrelationId = trace.CorrelationId?.ToString(),
    };
}

private static PluginTraceDetailDto MapTraceDetailToDto(PluginTraceDetail trace)
{
    var dto = new PluginTraceDetailDto
    {
        Id = trace.Id.ToString(),
        TypeName = trace.TypeName,
        MessageName = trace.MessageName,
        PrimaryEntity = trace.PrimaryEntity,
        Mode = trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
        OperationType = trace.OperationType.ToString(),
        Depth = trace.Depth,
        CreatedOn = trace.CreatedOn.ToString("o"),
        DurationMs = trace.DurationMs,
        HasException = trace.HasException,
        CorrelationId = trace.CorrelationId?.ToString(),
        ConstructorDurationMs = trace.ConstructorDurationMs,
        ExecutionStartTime = trace.ExecutionStartTime?.ToString("o"),
        ExceptionDetails = trace.ExceptionDetails,
        MessageBlock = trace.MessageBlock,
        Configuration = trace.Configuration,
        SecureConfiguration = trace.SecureConfiguration,
        RequestId = trace.RequestId?.ToString(),
    };
    return dto;
}

private static TimelineNodeDto MapTimelineNodeToDto(TimelineNode node)
{
    return new TimelineNodeDto
    {
        TraceId = node.Trace.Id.ToString(),
        TypeName = node.Trace.TypeName,
        MessageName = node.Trace.MessageName,
        Depth = node.Trace.Depth,
        DurationMs = node.Trace.DurationMs,
        HasException = node.Trace.HasException,
        OffsetPercent = node.OffsetPercent,
        WidthPercent = node.WidthPercent,
        HierarchyDepth = node.HierarchyDepth,
        Children = node.Children.Select(MapTimelineNodeToDto).ToList(),
    };
}

/// <summary>
/// Lists plugin traces with optional filtering.
/// Maps to: ppds plugin-traces list --json
/// </summary>
[JsonRpcMethod("pluginTraces/list")]
public async Task<PluginTracesListResponse> PluginTracesListAsync(
    TraceFilterDto? filter = null,
    int top = 100,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var service = sp.GetRequiredService<IPluginTraceService>();
        var domainFilter = MapTraceFilterFromDto(filter);
        var traces = await service.ListAsync(domainFilter, top, ct);

        return new PluginTracesListResponse
        {
            Traces = traces.Select(MapTraceInfoToDto).ToList()
        };
    }, cancellationToken);
}

/// <summary>
/// Gets a single plugin trace with full detail.
/// Maps to: ppds plugin-traces get --json
/// </summary>
[JsonRpcMethod("pluginTraces/get")]
public async Task<PluginTracesGetResponse> PluginTracesGetAsync(
    string id,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var traceId))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'id' parameter must be a valid GUID");
    }

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var service = sp.GetRequiredService<IPluginTraceService>();
        var trace = await service.GetAsync(traceId, ct)
            ?? throw new RpcException(
                ErrorCodes.Operation.NotFound,
                $"Plugin trace '{id}' not found");

        return new PluginTracesGetResponse
        {
            Trace = MapTraceDetailToDto(trace)
        };
    }, cancellationToken);
}

#endregion
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 3: Add pluginTraces/timeline, delete, traceLevel, setTraceLevel RPC methods

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — Add inside `#region Plugin Traces`

- [ ] **Step 1: Add timeline method**

Add inside the Plugin Traces region:

```csharp
/// <summary>
/// Builds a timeline hierarchy from traces with the given correlation ID.
/// Maps to: ppds plugin-traces timeline --json
/// </summary>
[JsonRpcMethod("pluginTraces/timeline")]
public async Task<PluginTracesTimelineResponse> PluginTracesTimelineAsync(
    string correlationId,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(correlationId) || !Guid.TryParse(correlationId, out var cid))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'correlationId' parameter must be a valid GUID");
    }

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var service = sp.GetRequiredService<IPluginTraceService>();
        var nodes = await service.BuildTimelineAsync(cid, ct);

        return new PluginTracesTimelineResponse
        {
            Nodes = nodes.Select(MapTimelineNodeToDto).ToList()
        };
    }, cancellationToken);
}
```

- [ ] **Step 2: Add delete method**

```csharp
/// <summary>
/// Deletes plugin traces by IDs or by age.
/// Consolidates DeleteByIdsAsync and DeleteOlderThanAsync behind a single RPC.
/// </summary>
[JsonRpcMethod("pluginTraces/delete")]
public async Task<PluginTracesDeleteResponse> PluginTracesDeleteAsync(
    string[]? ids = null,
    int? olderThanDays = null,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (ids == null && olderThanDays == null)
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "Either 'ids' or 'olderThanDays' must be provided");
    }

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var service = sp.GetRequiredService<IPluginTraceService>();
        int deletedCount;

        if (ids != null && ids.Length > 0)
        {
            var guids = ids.Select(id =>
            {
                if (!Guid.TryParse(id, out var g))
                    throw new RpcException(
                        ErrorCodes.Validation.RequiredField,
                        $"Invalid trace ID: '{id}'");
                return g;
            }).ToList();

            deletedCount = await service.DeleteByIdsAsync(guids, cancellationToken: ct);
        }
        else if (olderThanDays.HasValue)
        {
            if (olderThanDays.Value < 1)
                throw new RpcException(
                    ErrorCodes.Validation.RequiredField,
                    "olderThanDays must be >= 1");

            deletedCount = await service.DeleteOlderThanAsync(
                TimeSpan.FromDays(olderThanDays.Value),
                cancellationToken: ct);
        }
        else
        {
            deletedCount = 0;
        }

        return new PluginTracesDeleteResponse { DeletedCount = deletedCount };
    }, cancellationToken);
}
```

- [ ] **Step 3: Add traceLevel and setTraceLevel methods**

```csharp
/// <summary>
/// Gets the current plugin trace logging level.
/// </summary>
[JsonRpcMethod("pluginTraces/traceLevel")]
public async Task<PluginTracesTraceLevelResponse> PluginTracesTraceLevelAsync(
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var service = sp.GetRequiredService<IPluginTraceService>();
        var settings = await service.GetSettingsAsync(ct);

        return new PluginTracesTraceLevelResponse
        {
            Level = settings.SettingName,
            LevelValue = (int)settings.Setting
        };
    }, cancellationToken);
}

/// <summary>
/// Sets the plugin trace logging level.
/// </summary>
[JsonRpcMethod("pluginTraces/setTraceLevel")]
public async Task<PluginTracesSetTraceLevelResponse> PluginTracesSetTraceLevelAsync(
    string level,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(level))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'level' parameter is required");
    }

    var setting = level.ToLowerInvariant() switch
    {
        "off" => PluginTraceLogSetting.Off,
        "exception" => PluginTraceLogSetting.Exception,
        "all" => PluginTraceLogSetting.All,
        _ => throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            $"Invalid trace level: '{level}'. Valid values: Off, Exception, All")
    };

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var service = sp.GetRequiredService<IPluginTraceService>();
        await service.SetSettingsAsync(setting, ct);

        return new PluginTracesSetTraceLevelResponse { Success = true };
    }, cancellationToken);
}
```

- [ ] **Step 4: Build to verify all 6 RPC methods**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 4: Add TypeScript response types

**Files:**
- Modify: `src/PPDS.Extension/src/types.ts`

- [ ] **Step 1: Add Plugin Traces TS interfaces**

Add at the bottom of `types.ts`:

```typescript
// ── Plugin Traces ────────────────────────────────────────────────────────────

export interface PluginTracesListResponse {
    traces: PluginTraceInfoDto[];
}

export interface PluginTracesGetResponse {
    trace: PluginTraceDetailDto;
}

export interface PluginTracesTimelineResponse {
    nodes: TimelineNodeDto[];
}

export interface PluginTracesDeleteResponse {
    deletedCount: number;
}

export interface PluginTracesTraceLevelResponse {
    level: string;
    levelValue: number;
}

export interface PluginTracesSetTraceLevelResponse {
    success: boolean;
}

export interface PluginTraceInfoDto {
    id: string;
    typeName: string;
    messageName: string | null;
    primaryEntity: string | null;
    mode: string;
    operationType: string;
    depth: number;
    createdOn: string;
    durationMs: number | null;
    hasException: boolean;
    correlationId: string | null;
}

export interface PluginTraceDetailDto extends PluginTraceInfoDto {
    constructorDurationMs: number | null;
    executionStartTime: string | null;
    exceptionDetails: string | null;
    messageBlock: string | null;
    configuration: string | null;
    secureConfiguration: string | null;
    requestId: string | null;
}

export interface TimelineNodeDto {
    traceId: string;
    typeName: string;
    messageName: string | null;
    depth: number;
    durationMs: number | null;
    hasException: boolean;
    offsetPercent: number;
    widthPercent: number;
    hierarchyDepth: number;
    children: TimelineNodeDto[];
}

export interface TraceFilterDto {
    typeName?: string;
    messageName?: string;
    primaryEntity?: string;
    mode?: string;
    hasException?: boolean;
    correlationId?: string;
    minDurationMs?: number;
    startDate?: string;
    endDate?: string;
}
```

### Task 5: Add daemon client methods

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts`

- [ ] **Step 1: Add imports for new response types**

Add to the existing import from `./types.js`:

```typescript
import type {
    // ... existing imports ...
    PluginTracesListResponse,
    PluginTracesGetResponse,
    PluginTracesTimelineResponse,
    PluginTracesDeleteResponse,
    PluginTracesTraceLevelResponse,
    PluginTracesSetTraceLevelResponse,
    TraceFilterDto,
} from './types.js';
```

- [ ] **Step 2: Add 6 pluginTraces methods**

Add in the daemon client class (in a new `// ── Plugin Traces ──` section):

```typescript
// ── Plugin Traces ────────────────────────────────────────────────────────

async pluginTracesList(filter?: TraceFilterDto, top?: number, environmentUrl?: string): Promise<PluginTracesListResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = {};
    if (filter !== undefined) params.filter = filter;
    if (top !== undefined) params.top = top;
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info('Calling pluginTraces/list...');
    const result = await this.connection!.sendRequest<PluginTracesListResponse>('pluginTraces/list', params);
    this.log.debug(`Got ${result.traces.length} plugin traces`);
    return result;
}

async pluginTracesGet(id: string, environmentUrl?: string): Promise<PluginTracesGetResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = { id };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info(`Calling pluginTraces/get for ${id}...`);
    return await this.connection!.sendRequest<PluginTracesGetResponse>('pluginTraces/get', params);
}

async pluginTracesTimeline(correlationId: string, environmentUrl?: string): Promise<PluginTracesTimelineResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = { correlationId };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info(`Calling pluginTraces/timeline for ${correlationId}...`);
    return await this.connection!.sendRequest<PluginTracesTimelineResponse>('pluginTraces/timeline', params);
}

async pluginTracesDelete(ids?: string[], olderThanDays?: number, environmentUrl?: string): Promise<PluginTracesDeleteResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = {};
    if (ids !== undefined) params.ids = ids;
    if (olderThanDays !== undefined) params.olderThanDays = olderThanDays;
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info('Calling pluginTraces/delete...');
    return await this.connection!.sendRequest<PluginTracesDeleteResponse>('pluginTraces/delete', params);
}

async pluginTracesTraceLevel(environmentUrl?: string): Promise<PluginTracesTraceLevelResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = {};
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info('Calling pluginTraces/traceLevel...');
    return await this.connection!.sendRequest<PluginTracesTraceLevelResponse>('pluginTraces/traceLevel', params);
}

async pluginTracesSetTraceLevel(level: string, environmentUrl?: string): Promise<PluginTracesSetTraceLevelResponse> {
    await this.ensureConnected();
    const params: Record<string, unknown> = { level };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
    this.log.info(`Calling pluginTraces/setTraceLevel to ${level}...`);
    return await this.connection!.sendRequest<PluginTracesSetTraceLevelResponse>('pluginTraces/setTraceLevel', params);
}
```

- [ ] **Step 3: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors.

### Task 6: Commit — Data Layer

- [ ] **Step 1: Run quality gates**

Run: `dotnet build PPDS.sln -v q && cd src/PPDS.Extension && npm run typecheck:all`
Expected: All pass.

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs \
        src/PPDS.Extension/src/types.ts \
        src/PPDS.Extension/src/daemonClient.ts
git commit -m "feat(rpc): add pluginTraces/* RPC endpoints

Add 6 RPC methods (list, get, timeline, delete, traceLevel, setTraceLevel)
with DTOs, TypeScript response types, and daemon client methods.
Addresses #342."
```

---

## Chunk 2: VS Code Extension — Message Types + Panel Host

### Task 7: Add message types

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`

- [ ] **Step 1: Add Plugin Traces panel message unions**

Add at the bottom of `message-types.ts`:

```typescript
// ── Plugin Traces Panel ──────────────────────────────────────────────────

/** Plugin trace info as sent to the webview for table display. */
export interface PluginTraceViewDto {
    id: string;
    typeName: string;
    messageName: string | null;
    primaryEntity: string | null;
    mode: string;
    operationType: string;
    depth: number;
    createdOn: string;
    durationMs: number | null;
    hasException: boolean;
    correlationId: string | null;
}

/** Plugin trace detail for the detail pane. */
export interface PluginTraceDetailViewDto extends PluginTraceViewDto {
    constructorDurationMs: number | null;
    executionStartTime: string | null;
    exceptionDetails: string | null;
    messageBlock: string | null;
    configuration: string | null;
    secureConfiguration: string | null;
    requestId: string | null;
}

/** Timeline node for the waterfall visualization. */
export interface TimelineNodeViewDto {
    traceId: string;
    typeName: string;
    messageName: string | null;
    depth: number;
    durationMs: number | null;
    hasException: boolean;
    offsetPercent: number;
    widthPercent: number;
    hierarchyDepth: number;
    children: TimelineNodeViewDto[];
}

/** Filter state sent from webview to host. */
export interface TraceFilterViewDto {
    typeName?: string;
    messageName?: string;
    primaryEntity?: string;
    mode?: string;
    hasException?: boolean;
    correlationId?: string;
    minDurationMs?: number;
    startDate?: string;
    endDate?: string;
}

/** Messages the Plugin Traces Panel webview sends to the extension host. */
export type PluginTracesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'applyFilter'; filter: TraceFilterViewDto }
    | { command: 'selectTrace'; id: string }
    | { command: 'loadTimeline'; correlationId: string }
    | { command: 'deleteTraces'; ids: string[] }
    | { command: 'deleteOlderThan'; days: number }
    | { command: 'requestTraceLevel' }
    | { command: 'setTraceLevel'; level: string }
    | { command: 'setAutoRefresh'; intervalSeconds: number | null }
    | { command: 'requestEnvironmentList' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Plugin Traces Panel webview. */
export type PluginTracesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'tracesLoaded'; traces: PluginTraceViewDto[] }
    | { command: 'traceDetailLoaded'; trace: PluginTraceDetailViewDto }
    | { command: 'timelineLoaded'; nodes: TimelineNodeViewDto[] }
    | { command: 'traceLevelLoaded'; level: string; levelValue: number }
    | { command: 'deleteComplete'; deletedCount: number }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

- [ ] **Step 2: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors.

### Task 8: Create PluginTracesPanel host class

**Files:**
- Create: `src/PPDS.Extension/src/panels/PluginTracesPanel.ts`

- [ ] **Step 1: Write the host-side panel**

Create `src/PPDS.Extension/src/panels/PluginTracesPanel.ts` following the ImportJobsPanel pattern but with:
- Auto-refresh timer (using `setInterval`/`clearInterval` disposed on panel close)
- Filter state forwarded to RPC calls
- Timeline loading from correlation ID
- Delete operations with VS Code confirmation dialog
- Trace level management with volume warning for "All"

Key structure:

```typescript
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type {
    PluginTracesPanelWebviewToHost,
    PluginTracesPanelHostToWebview,
    TraceFilterViewDto,
} from './webview/shared/message-types.js';
import type { TraceFilterDto } from '../types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class PluginTracesPanel extends WebviewPanelBase<
    PluginTracesPanelWebviewToHost,
    PluginTracesPanelHostToWebview
> {
    private static instances: PluginTracesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;
    private static readonly MAX_PANELS = 5;

    private environmentUrl: string | undefined;
    private environmentDisplayName: string | undefined;
    private environmentType: string | null = null;
    private environmentColor: string | null = null;
    private environmentId: string | null = null;
    private profileName: string | undefined;
    private currentFilter: TraceFilterDto | undefined;
    private autoRefreshTimer: ReturnType<typeof setInterval> | null = null;

    // ... static show(), constructor, handleMessage(), dispose() ...
    // Pattern identical to ImportJobsPanel with additions for:
    // - applyFilter: stores filter, calls loadTraces()
    // - selectTrace: calls daemon.pluginTracesGet(), posts traceDetailLoaded
    // - loadTimeline: calls daemon.pluginTracesTimeline(), posts timelineLoaded
    // - deleteTraces: shows vscode confirmation, calls daemon.pluginTracesDelete(), posts deleteComplete
    // - deleteOlderThan: shows vscode confirmation with day count, calls daemon.pluginTracesDelete()
    // - requestTraceLevel: calls daemon.pluginTracesTraceLevel(), posts traceLevelLoaded
    // - setTraceLevel: if "All", shows volume warning first, then calls daemon.pluginTracesSetTraceLevel()
    // - setAutoRefresh: clears existing timer, sets new interval or null
}
```

Implement all message handlers following the `handleMessage` switch pattern from ImportJobsPanel. Key differences:
- `applyFilter`: Convert `TraceFilterViewDto` to `TraceFilterDto` (identical shape), store as `this.currentFilter`, call `loadTraces()`
- `deleteTraces`: Show `vscode.window.showWarningMessage` with "Delete" and "Cancel" options before calling RPC
- `deleteOlderThan`: Show `vscode.window.showWarningMessage` with count of days
- `setTraceLevel` with level "All": Show warning "Setting trace level to 'All' can generate significant log volume and impact performance. Continue?"
- `setAutoRefresh`: `clearInterval` existing, `setInterval` with new value calling `loadTraces()`, null clears
- `dispose()`: Clear auto-refresh timer, remove from instances

HTML template includes:
- Reconnect banner
- Toolbar with buttons: Refresh, Auto-refresh dropdown, Delete dropdown, Trace Level dropdown, environment picker
- Filter bar container
- Content container (for data table)
- Resize handle
- Detail pane with 5 tabs (Details, Exception, Message Block, Configuration, Timeline)
- Status bar

CSS references: `shared.css` + `plugin-traces-panel.css`
Script reference: `dist/plugin-traces-panel.js`

- [ ] **Step 2: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors.

### Task 9: Commit — Panel Host

- [ ] **Step 1: Commit**

```bash
git add src/PPDS.Extension/src/panels/webview/shared/message-types.ts \
        src/PPDS.Extension/src/panels/PluginTracesPanel.ts
git commit -m "feat(ext): add PluginTracesPanel host class with message types

Split pane, filter forwarding, auto-refresh timer, delete confirmation,
trace level management with volume warning, timeline loading."
```

---

## Chunk 3: VS Code Extension — Webview Script + Styles

### Task 10: Create plugin-traces-panel.css

**Files:**
- Create: `src/PPDS.Extension/src/panels/styles/plugin-traces-panel.css`

- [ ] **Step 1: Write panel styles**

Create `plugin-traces-panel.css` with `@import './shared.css'` and sections for:

1. **Data table** — Reuse `.data-table` pattern from `import-jobs-panel.css` with additions:
   - `.trace-row-exception` — red tint background (`rgba(255, 0, 0, 0.08)`)
   - `.trace-row-slow` — yellow tint background (`rgba(255, 200, 0, 0.08)`)
   - `.trace-status-icon` — colored dot (red for exception, green for success)
   - Column widths: Status 40px, Time 140px, Duration 80px, Plugin 200px, Entity 120px, Message 100px, Depth 50px, Mode 60px

2. **Filter bar** — Collapsible section:
   - `.filter-bar` — flex row with wrap, gap, border-bottom, padding
   - `.filter-bar.collapsed` — `display: none` for content, toggle button visible
   - `.filter-field` — flex column with label + input/select
   - `.filter-field label` — small, uppercase, description foreground
   - `.filter-field input, .filter-field select` — VS Code input styling
   - `.quick-filters` — flex row of pill buttons
   - `.quick-filter-pill` — rounded, small, badge-like
   - `.quick-filter-pill.active` — highlighted with accent color
   - `.filter-actions` — Clear All button

3. **Split pane** — Resizable layout:
   - `.panel-content` — flex column, flex: 1
   - `.table-pane` — flex: 1 1 60%, min-height: 100px, overflow auto
   - `.resize-handle` — height: 4px, cursor: row-resize, background on hover
   - `.detail-pane` — flex: 0 0 auto, min-height: 100px, border-top

4. **Detail tabs** — Tab bar + content:
   - `.detail-tabs` — flex row, border-bottom
   - `.detail-tab` — padding, cursor pointer, border-bottom transparent
   - `.detail-tab.active` — border-bottom with accent color, bold
   - `.detail-tab-content` — padding, overflow auto, max-height from split
   - `.monospace-content` — `font-family: var(--vscode-editor-font-family)`, white-space pre-wrap

5. **Timeline waterfall** — Horizontal bar chart:
   - `.timeline-container` — flex column, padding
   - `.timeline-row` — flex row, height: 28px, align-items center
   - `.timeline-label` — width: 200px, ellipsis, padding-left by depth (indent)
   - `.timeline-bar-container` — flex: 1, position relative, background: subtle
   - `.timeline-bar` — position absolute, height: 20px, border-radius: 2px, min-width: 2px
   - `.timeline-bar.exception` — red background
   - `.timeline-bar.slow` — yellow/amber background
   - `.timeline-bar.normal` — blue/accent background
   - `.timeline-duration` — right-aligned text after bar
   - `.timeline-header` — column headers (Plugin, Timeline)

6. **Auto-refresh indicator** — small animated dot or text in toolbar

### Task 11: Create plugin-traces-panel.ts webview script

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts`

- [ ] **Step 1: Write the browser-side webview script**

Create `plugin-traces-panel.ts` as an IIFE (same pattern as `import-jobs-panel.ts`) with these sections:

1. **Setup** — Error handler, VS Code API, DOM refs
2. **Filter bar controller** — Renders filter controls, collects values, emits `applyFilter`:
   - Text inputs for: Entity, Message, Plugin Name
   - Select for Mode: All / Sync / Async
   - Checkbox for Exceptions Only
   - Date inputs for Start/End
   - Quick filter pills: Last Hour, Exceptions Only, Long Running (>1s)
   - Clear All button
   - Toggle collapse button
   - On any filter change: debounce 300ms, collect filter object, post `applyFilter` message
3. **Data table** — Reuse shared `DataTable<PluginTraceViewDto>` with columns:
   - Status: colored dot icon (red exception, green success)
   - Time: formatted `createdOn` timestamp
   - Duration: `durationMs` with "ms" suffix, yellow highlight if >1000
   - Plugin: `typeName` (truncated with title attribute)
   - Entity: `primaryEntity` or "—"
   - Message: `messageName` or "—"
   - Depth: numeric
   - Mode: "Sync" or "Async"
   - Row CSS class: `trace-row-exception` if hasException, `trace-row-slow` if durationMs > 1000
   - Row click → post `selectTrace` message
4. **Split pane resize** — mousedown/mousemove/mouseup on `.resize-handle`:
   - Track initial Y, compute delta, adjust flex-basis of table-pane and detail-pane
   - Persist ratio via `vscode.getState()`/`setState()`
   - Restore on load from `vscode.getState()`
5. **Detail pane (5 tabs)** — Tab bar with click handlers:
   - **Details tab**: Key-value pairs (Type, Message, Entity, Mode, Depth, Duration, Created, Correlation ID, Request ID)
   - **Exception tab**: Monospace pre-formatted exception text, or "No exception" message
   - **Message Block tab**: Monospace pre-formatted trace output, or "No message block"
   - **Configuration tab**: Two sections — unsecured + secured config, both monospace
   - **Timeline tab**: Waterfall visualization (renders on tab click, not on detail load)
6. **Timeline renderer** — Flattens tree to rows, renders waterfall:
   - For each node (recursive): render a `.timeline-row` with label indented by `hierarchyDepth`, bar positioned at `offsetPercent%` with width `widthPercent%`
   - Color: red if hasException, yellow if durationMs > 1000, blue otherwise
   - Duration label after bar
   - Clickable rows → post `selectTrace` with traceId
7. **Message handler** — Switch on incoming `command`:
   - `updateEnvironment` → update toolbar attributes
   - `tracesLoaded` → `dataTable.setItems()`, update status bar with count
   - `traceDetailLoaded` → populate detail pane, show Details tab, show detail pane if hidden
   - `timelineLoaded` → render waterfall in Timeline tab
   - `traceLevelLoaded` → update trace level indicator in toolbar
   - `deleteComplete` → show notification, refresh list
   - `loading` → show loading state
   - `error` → show error message
   - `daemonReconnected` → show reconnect banner
8. **Toolbar handlers** — Button click events:
   - Refresh button → post `refresh`
   - Auto-refresh select → post `setAutoRefresh` with seconds or null
   - Delete button dropdown → selected IDs or older-than dialog
   - Trace level button dropdown → Off/Exception/All options
   - Environment picker button → post `requestEnvironmentList`
9. **Keyboard shortcuts**:
   - Escape → close detail pane or clear selection
   - F5 → refresh

- [ ] **Step 2: Typecheck (webview files excluded from main tsconfig but must be valid TS)**

The webview scripts are bundled by esbuild which handles type checking implicitly. Verify by running the build.

### Task 12: Update esbuild.js and extension registration

**Files:**
- Modify: `src/PPDS.Extension/esbuild.js`
- Modify: `src/PPDS.Extension/src/extension.ts`
- Modify: `src/PPDS.Extension/package.json`
- Modify: `src/PPDS.Extension/src/views/toolsTreeView.ts` (if it exists, otherwise skip)

- [ ] **Step 1: Add plugin-traces-panel to esbuild.js**

In the panel webview scripts array, add:

```javascript
'src/panels/webview/plugin-traces-panel.ts'  // → dist/plugin-traces-panel.js
```

And in the CSS entries:

```javascript
'src/panels/styles/plugin-traces-panel.css'  // → dist/plugin-traces-panel.css
```

- [ ] **Step 2: Register commands in extension.ts**

In the `registerPanelCommands()` function, add:

```typescript
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.openPluginTraces', () => {
        PluginTracesPanel.show(context.extensionUri, daemon);
    }),
);
```

And for environment-specific opening:

```typescript
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.openPluginTracesForEnv', (envUrl: string, envDisplayName: string) => {
        PluginTracesPanel.show(context.extensionUri, daemon, envUrl, envDisplayName);
    }),
);
```

- [ ] **Step 3: Add command contributions to package.json**

In `contributes.commands`, add:

```json
{
    "command": "ppds.openPluginTraces",
    "title": "Plugin Traces",
    "category": "PPDS"
},
{
    "command": "ppds.openPluginTracesForEnv",
    "title": "Open Plugin Traces",
    "category": "PPDS"
}
```

In the environment context menu (under `ppds.environments` submenu), add the Plugin Traces item following the Import Jobs pattern (group `env-tools@4`).

- [ ] **Step 4: Add to tools tree view (if applicable)**

If `src/PPDS.Extension/src/views/toolsTreeView.ts` exists and lists panels, add Plugin Traces entry.

- [ ] **Step 5: Build extension**

Run: `cd src/PPDS.Extension && npm run build`
Expected: Build succeeded, `dist/plugin-traces-panel.js` and `dist/plugin-traces-panel.css` created.

### Task 13: Commit — Webview + Extension Registration

- [ ] **Step 1: Run quality gates**

Run: `cd src/PPDS.Extension && npm run typecheck:all && npm run lint`
Expected: All pass.

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts \
        src/PPDS.Extension/src/panels/styles/plugin-traces-panel.css \
        src/PPDS.Extension/esbuild.js \
        src/PPDS.Extension/src/extension.ts \
        src/PPDS.Extension/package.json
git commit -m "feat(ext): add Plugin Traces webview with filter bar, split pane, timeline

Filter bar with entity/message/plugin/mode/exceptions/date/quick filters.
Resizable split pane with 5-tab detail (Details, Exception, Message Block,
Configuration, Timeline). Horizontal waterfall timeline visualization.
Auto-refresh with configurable interval. Delete with confirmation."
```

---

## Chunk 4: Unit Tests

### Task 14: Add panel message type tests

**Files:**
- Create: `src/PPDS.Extension/src/__tests__/panels/pluginTracesPanel.test.ts`

- [ ] **Step 1: Write tests following importJobsPanel.test.ts pattern**

Test suites:
1. **WebviewToHost message types** — Verify all 13 command variants are valid discriminated union members
2. **HostToWebview message types** — Verify all 9 command variants
3. **PluginTraceViewDto required fields** — Verify required fields exist and types are correct
4. **PluginTraceDetailViewDto extends PluginTraceViewDto** — Verify inheritance fields
5. **TraceFilterViewDto** — Verify all filter fields are optional
6. **TimelineNodeViewDto** — Verify children array and positioning fields

- [ ] **Step 2: Run tests**

Run: `cd src/PPDS.Extension && npm run ext:test`
Expected: All tests pass.

### Task 15: Commit — Tests

- [ ] **Step 1: Commit**

```bash
git add src/PPDS.Extension/src/__tests__/panels/pluginTracesPanel.test.ts
git commit -m "test(ext): add Plugin Traces panel message type tests

Cover all 13 WebviewToHost and 9 HostToWebview message variants,
view DTOs, filter DTO, and timeline node DTO."
```

---

## Chunk 5: MCP Delete Tool

### Task 16: Create PluginTracesDeleteTool

**Files:**
- Create: `src/PPDS.Mcp/Tools/PluginTracesDeleteTool.cs`

- [ ] **Step 1: Write the MCP delete tool**

Follow the pattern from `PluginTracesListTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.Services;

namespace PPDS.Mcp.Tools;

[McpServerToolType]
public sealed class PluginTracesDeleteTool
{
    private readonly McpContext _context;

    public PluginTracesDeleteTool(McpContext context)
    {
        _context = context;
    }

    [McpServerTool("ppds_plugin_traces_delete")]
    [Description("Delete plugin trace logs. Provide either specific IDs or an age threshold (olderThanDays) for bulk cleanup.")]
    public async Task<PluginTracesDeleteResult> ExecuteAsync(
        [Description("Trace IDs to delete (array of GUIDs). Use for targeted deletion.")]
        string[]? ids = null,
        [Description("Delete all traces older than this many days. Use for bulk cleanup.")]
        int? olderThanDays = null,
        CancellationToken cancellationToken = default)
    {
        if (ids == null && olderThanDays == null)
        {
            return new PluginTracesDeleteResult
            {
                Error = "Provide either 'ids' or 'olderThanDays'"
            };
        }

        await using var sp = _context.CreateServiceProvider();
        var service = sp.GetRequiredService<IPluginTraceService>();
        int deletedCount;

        if (ids != null && ids.Length > 0)
        {
            var guids = new List<Guid>();
            foreach (var id in ids)
            {
                if (!Guid.TryParse(id, out var g))
                    return new PluginTracesDeleteResult { Error = $"Invalid ID: '{id}'" };
                guids.Add(g);
            }
            deletedCount = await service.DeleteByIdsAsync(guids, cancellationToken: cancellationToken);
        }
        else
        {
            if (olderThanDays!.Value < 1)
                return new PluginTracesDeleteResult { Error = "olderThanDays must be >= 1" };

            deletedCount = await service.DeleteOlderThanAsync(
                TimeSpan.FromDays(olderThanDays.Value),
                cancellationToken: cancellationToken);
        }

        return new PluginTracesDeleteResult { DeletedCount = deletedCount };
    }
}

public class PluginTracesDeleteResult
{
    public int DeletedCount { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -v q`
Expected: Build succeeded.

### Task 17: Commit — MCP Tool

- [ ] **Step 1: Commit**

```bash
git add src/PPDS.Mcp/Tools/PluginTracesDeleteTool.cs
git commit -m "feat(mcp): add ppds_plugin_traces_delete tool

Supports deletion by specific IDs or bulk cleanup by age threshold.
Addresses AC-PT-14."
```

---

## Chunk 6: TUI — PluginTracesScreen + Dialogs

### Task 18: Create PluginTraceFilterDialog

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/PluginTraceFilterDialog.cs`

- [ ] **Step 1: Write filter dialog**

Follow the `EnvironmentDetailsDialog` pattern for async dialogs, but with form controls:

```csharp
// Dialog with text fields for: TypeName, MessageName, PrimaryEntity
// ComboBox for Mode (All, Synchronous, Asynchronous)
// CheckBox for HasException
// TextField for MinDurationMs
// Apply and Cancel buttons
// Returns a PluginTraceFilter on Apply, null on Cancel
// Width: Dim.Percent(60), Height: 20
```

Key design:
- Constructor takes optional `PluginTraceFilter` to pre-populate
- `Filter` property returns the built filter (null if cancelled)
- No async operations needed — this is a form dialog

### Task 19: Create PluginTraceDetailDialog

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs`

- [ ] **Step 1: Write detail dialog**

Tabbed dialog showing trace detail:
- TabView with 4 tabs: Details, Exception, Message Block, Configuration
- Details tab: Label grid with key-value pairs (same fields as webview Details tab)
- Exception tab: TextView with monospace content (read-only)
- Message Block tab: TextView with monospace content (read-only)
- Configuration tab: Two TextViews — unsecured and secured
- Close button (Escape also closes)
- Width: `Dim.Percent(80)`, Height: `Dim.Percent(80)`
- Constructor takes `PluginTraceDetail` directly (no RPC — screen already fetched it)

### Task 20: Create TraceTimelineDialog

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/TraceTimelineDialog.cs`

- [ ] **Step 1: Write timeline dialog**

Text-based timeline visualization in a dialog:
- Takes `List<TimelineNode>` in constructor
- Renders a text-based waterfall:
  - Each node: indent by depth, type name, message, duration, bar of `#` characters proportional to `WidthPercent`
  - Exception nodes: colored red (Terminal.Gui color scheme)
  - Slow nodes (>1s): colored yellow
- ListView or TableView for scrollable output
- Width: `Dim.Percent(90)`, Height: `Dim.Percent(80)`

### Task 21: Create TraceLevelDialog

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/TraceLevelDialog.cs`

- [ ] **Step 1: Write trace level dialog**

Simple selection dialog:
- RadioGroup with 3 options: Off, Exception, All
- Pre-selects current level (passed to constructor)
- "All" option shows warning label: "Warning: 'All' can generate significant log volume"
- Apply and Cancel buttons
- `SelectedLevel` property returns choice (null if cancelled)
- Width: 50, Height: 12

### Task 22: Create TraceDeleteDialog

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/TraceDeleteDialog.cs`

- [ ] **Step 1: Write delete confirmation dialog**

Confirmation dialog with options:
- RadioGroup: "Delete selected traces", "Delete traces older than N days"
- TextField for day count (only enabled when age option selected)
- Shows count of selected traces (passed to constructor)
- Confirm and Cancel buttons
- `DeleteMode` enum: ByIds, ByAge
- `Result` property: mode + day count (null if cancelled)
- Width: 60, Height: 14

### Task 23: Create PluginTracesScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs`

- [ ] **Step 1: Write the TUI screen**

Follow `ImportJobsScreen` and `SolutionsScreen` patterns:

```csharp
public class PluginTracesScreen : TuiScreenBase
{
    public override string Title => "Plugin Traces";

    // TableView with columns matching spec: Time, Duration, Plugin, Entity, Message, Depth, Mode, Status
    // Status label for feedback messages
    // Current filter (PluginTraceFilter?)
    // Auto-cancel in-flight loads (like SolutionsScreen)

    // RegisterHotkeys:
    //   Ctrl+R → Refresh
    //   Enter  → Open detail dialog for selected trace
    //   Ctrl+F → Open filter dialog
    //   Ctrl+T → Open timeline dialog (requires correlation ID from selected trace)
    //   Ctrl+D → Open delete dialog
    //   Ctrl+L → Open trace level dialog
    //   Ctrl+E → Export (future, register but show "not implemented" for now)
    //   Tab    → No-op in main screen (used in dialogs)

    // LoadTracesAsync:
    //   Calls IPluginTraceService.ListAsync with current filter
    //   Populates TableView
    //   Updates status label with count
    //   Handles empty state

    // Constructor:
    //   Takes InteractiveSession
    //   Resolves IPluginTraceService from session
    //   Builds layout: TableView fills Content
    //   Fires initial load
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 24: Register PluginTracesScreen in TuiShell

**Files:**
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs`

- [ ] **Step 1: Add menu item**

In the Tools menu or equivalent navigation, add "Plugin Traces" entry that creates and navigates to a `PluginTracesScreen`.

Follow the pattern from `NavigateToSolutions()` — show loading label, create screen in idle callback, call `NavigateTo()`.

- [ ] **Step 2: Build to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 25: Commit — TUI

- [ ] **Step 1: Run quality gates**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs \
        src/PPDS.Cli/Tui/Dialogs/PluginTraceFilterDialog.cs \
        src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs \
        src/PPDS.Cli/Tui/Dialogs/TraceTimelineDialog.cs \
        src/PPDS.Cli/Tui/Dialogs/TraceLevelDialog.cs \
        src/PPDS.Cli/Tui/Dialogs/TraceDeleteDialog.cs \
        src/PPDS.Cli/Tui/TuiShell.cs
git commit -m "feat(tui): add PluginTracesScreen with filter, detail, timeline, delete dialogs

Split pane screen with TableView, 5 dialogs (filter, detail, timeline,
trace level, delete confirmation), hotkey registration.
Addresses #346."
```

---

## Chunk 7: Quality Gates + Final Verification

### Task 26: Full quality gates

- [ ] **Step 1: .NET build**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: .NET unit tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests pass.

- [ ] **Step 3: Extension typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors.

- [ ] **Step 4: Extension lint**

Run: `cd src/PPDS.Extension && npm run lint`
Expected: No errors.

- [ ] **Step 5: Extension unit tests**

Run: `cd src/PPDS.Extension && npm run ext:test`
Expected: All tests pass.

- [ ] **Step 6: Extension build**

Run: `cd src/PPDS.Extension && npm run build`
Expected: Build succeeded, all dist files generated.

### Task 27: Verification checklist

- [ ] **AC-PT-01**: `pluginTraces/list` applies all filter combinations server-side — verify RPC method maps all TraceFilterDto fields to PluginTraceFilter
- [ ] **AC-PT-02**: `pluginTraces/get` returns full detail — verify DTO includes exception, message block, configuration
- [ ] **AC-PT-03**: `pluginTraces/timeline` returns hierarchical tree — verify recursive TimelineNodeDto mapping
- [ ] **AC-PT-04**: `pluginTraces/delete` supports by IDs and by age — verify both code paths in RPC method
- [ ] **AC-PT-05**: traceLevel and setTraceLevel read/write — verify both RPC methods work with all 3 levels
- [ ] **AC-PT-06**: VS Code panel has filter bar, color-coded status, resizable detail — verify HTML structure and CSS
- [ ] **AC-PT-07**: 5 detail tabs present — verify tab rendering in webview script
- [ ] **AC-PT-08**: Timeline tab renders waterfall — verify horizontal bar positioning with offsetPercent/widthPercent
- [ ] **AC-PT-09**: Quick filters apply correct filter combinations — verify Last Hour sets startDate, Exceptions Only sets hasException, Long Running sets minDurationMs
- [ ] **AC-PT-10**: Auto-refresh preserves selection — verify selectedId maintained across tracesLoaded messages
- [ ] **AC-PT-11**: Delete requires confirmation — verify vscode.window.showWarningMessage calls
- [ ] **AC-PT-12**: Trace level "All" shows volume warning — verify warning message before setTraceLevel RPC
- [ ] **AC-PT-13**: TUI PluginTracesScreen provides equivalent functionality — verify screen + 5 dialogs
- [ ] **AC-PT-14**: MCP delete tool supports bulk cleanup — verify PluginTracesDeleteTool with ids and olderThanDays
- [ ] **AC-PT-15**: "Trace level is Off" handled — verify informational message in tracesLoaded handler when list is empty and level is Off

---

## Acceptance Criteria Mapping

| AC | Chunk | Task | Description |
|----|-------|------|-------------|
| AC-PT-01 | 1 | 2 | pluginTraces/list with all filter fields |
| AC-PT-02 | 1 | 2 | pluginTraces/get with full detail DTO |
| AC-PT-03 | 1 | 3 | pluginTraces/timeline with hierarchical nodes |
| AC-PT-04 | 1 | 3 | pluginTraces/delete by IDs and by age |
| AC-PT-05 | 1 | 3 | traceLevel + setTraceLevel RPC methods |
| AC-PT-06 | 3 | 10-11 | VS Code panel with filter bar, colors, split pane |
| AC-PT-07 | 3 | 11 | 5-tab detail pane |
| AC-PT-08 | 3 | 11 | Timeline waterfall visualization |
| AC-PT-09 | 3 | 11 | Quick filter presets |
| AC-PT-10 | 2-3 | 8, 11 | Auto-refresh with selection preservation |
| AC-PT-11 | 2 | 8 | Delete confirmation dialogs |
| AC-PT-12 | 2 | 8 | Volume warning for trace level "All" |
| AC-PT-13 | 6 | 18-24 | TUI screen + 5 dialogs |
| AC-PT-14 | 5 | 16 | MCP delete tool |
| AC-PT-15 | 3 | 11 | "Trace level Off" informational message |
