# Import Jobs Panel Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Import Jobs panel across all 4 surfaces (Daemon RPC, VS Code extension, TUI, MCP), establishing the patterns that all subsequent panels will follow.

**Architecture:** The domain service (`IImportJobService`) already exists. We add RPC endpoints in the daemon, a VS Code webview panel with environment theming, a TUI screen, and two MCP tools. All surfaces call the same service methods through their respective infrastructure (Constitution A1, A2).

**Tech Stack:** C# (.NET 8), TypeScript (VS Code extension + webview), Terminal.Gui (TUI), StreamJsonRpc (RPC), ModelContextProtocol (MCP)

**Spec:** `specs/panel-parity.md` — Panel 1 (Import Jobs), Acceptance Criteria AC-IJ-01 through AC-IJ-10

---

## Design Decisions

### Status + Duration as computed properties on ImportJobInfo
The `importjob` entity has no `statecode`/`statuscode` fields. Status and duration are derived from existing fields. Rather than duplicating this logic across RPC, TUI, and MCP (violating Constitution A2), we add computed properties directly on the `ImportJobInfo` record:
- `Status`: `"Succeeded"` / `"Failed"` / `"In Progress"` (from `CompletedOn` + `Progress`)
- `FormattedDuration`: `"Xh Ym Zs"`, `"< 1s"`, or `"Xm Ys (ongoing)"` (from `StartedOn` + `CompletedOn`)

This ensures a single code path — all surfaces read `job.Status` and `job.FormattedDuration`.

### Shared DataTable component (Option C)
The spec requires "one table component, one code path" across all panels. Rather than building virtual scrolling now (Import Jobs doesn't need it), we extract a shared `DataTable` class in `webview/shared/data-table.ts` with a clean API (column definitions, data, sort, row selection, status badges). Import Jobs uses it. Phase 2 panels (Plugin Traces, Web Resources) add virtual scrolling to the same component. One component, progressive enhancement.

### CreatedBy field
The `importjob` entity has a `createdby` EntityReference. Including it in the ColumnSet populates `.Name` server-side. We add `CreatedByName` to the domain `ImportJobInfo` record.

### Detail view pattern
Clicking a row fetches the import log XML via `importJobs/get` and displays it in a detail pane below the table. This establishes the "table + detail pane" pattern for future panels (Plugin Traces, Connection References).

### No Application Service wrapper needed
`IImportJobService` is already a clean domain service with the right abstraction level. The RPC handler calls it directly via DI, same as `ISolutionService`. No intermediate Application Service layer is needed for read-only panels.

---

## File Structure

### Files to modify
| File | Change |
|------|--------|
| `src/PPDS.Dataverse/Services/IImportJobService.cs` | Add `CreatedByName` to `ImportJobInfo` record |
| `src/PPDS.Dataverse/Services/ImportJobService.cs` | Add `createdby` to ColumnSet, map `.Name` |
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Add `importJobs/list` and `importJobs/get` RPC methods + DTOs |
| `src/PPDS.Extension/src/types.ts` | Add `ImportJobsListResponse`, `ImportJobsGetResponse`, DTO interfaces |
| `src/PPDS.Extension/src/daemonClient.ts` | Add `importJobsList()` and `importJobsGet()` methods |
| `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` | Add Import Jobs panel message types |
| `src/PPDS.Extension/esbuild.js` | Add import-jobs-panel JS + CSS entries |
| `src/PPDS.Extension/src/extension.ts` | Register `ppds.openImportJobs` + `ppds.openImportJobsForEnv` commands |
| `src/PPDS.Extension/src/views/toolsTreeView.ts` | Add Import Jobs to the Tools tree |
| `src/PPDS.Extension/package.json` | Add command contributions + menu items |
| `src/PPDS.Cli/Tui/TuiShell.cs` | Add menu item / hotkey to open Import Jobs screen |
| `.claude/skills/webview-panels/SKILL.md` | Update with new patterns established |

### Files to create
| File | Purpose |
|------|---------|
| `src/PPDS.Extension/src/panels/ImportJobsPanel.ts` | Host-side panel (extends WebviewPanelBase) |
| `src/PPDS.Extension/src/panels/webview/shared/data-table.ts` | Shared DataTable component for all panels |
| `src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts` | Browser-side webview script |
| `src/PPDS.Extension/src/panels/styles/import-jobs-panel.css` | Panel-specific CSS (imports shared data-table styles) |
| `src/PPDS.Cli/Tui/Screens/ImportJobsScreen.cs` | TUI screen (extends TuiScreenBase) |
| `src/PPDS.Mcp/Tools/ImportJobsListTool.cs` | MCP list tool |
| `src/PPDS.Mcp/Tools/ImportJobsGetTool.cs` | MCP get tool (with XML data) |
| `src/PPDS.Extension/src/__tests__/panels/importJobsPanel.test.ts` | Unit tests for panel message handling |

---

## Chunk 1: Data Layer — Domain Service + RPC Endpoints

### Task 1: Enhance ImportJobInfo record

**Files:**
- Modify: `src/PPDS.Dataverse/Services/IImportJobService.cs:57-66`

- [ ] **Step 1: Add CreatedByName to the ImportJobInfo record**

Change the record definition from:

```csharp
public record ImportJobInfo(
    Guid Id,
    string? Name,
    string? SolutionName,
    Guid? SolutionId,
    double Progress,
    DateTime? StartedOn,
    DateTime? CompletedOn,
    DateTime? CreatedOn,
    bool IsComplete);
```

To:

```csharp
public record ImportJobInfo(
    Guid Id,
    string? Name,
    string? SolutionName,
    Guid? SolutionId,
    double Progress,
    DateTime? StartedOn,
    DateTime? CompletedOn,
    DateTime? CreatedOn,
    bool IsComplete,
    string? CreatedByName = null)
{
    /// <summary>
    /// Computed status: Succeeded, Failed, or In Progress.
    /// Single code path for all surfaces (Constitution A2).
    /// </summary>
    public string Status => CompletedOn.HasValue
        ? (Progress >= 100 ? "Succeeded" : "Failed")
        : "In Progress";

    /// <summary>
    /// Computed formatted duration, or null if StartedOn is not set.
    /// </summary>
    public string? FormattedDuration
    {
        get
        {
            if (!StartedOn.HasValue) return null;
            var span = (CompletedOn ?? DateTime.UtcNow) - StartedOn.Value;
            var formatted = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                    : span.TotalSeconds >= 1
                        ? $"{span.Seconds}s"
                        : "< 1s";
            return CompletedOn.HasValue ? formatted : formatted + " (ongoing)";
        }
    }
}
```

Note: Default parameter value on `CreatedByName` ensures backward compatibility with the single construction site in `MapToImportJobInfo`. Computed properties provide a single code path for status and duration across RPC, TUI, and MCP.

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v q`
Expected: Build succeeded (the default parameter means no existing callers break).

### Task 2: Update ImportJobService to retrieve CreatedBy

**Files:**
- Modify: `src/PPDS.Dataverse/Services/ImportJobService.cs:46-55` (ListAsync ColumnSet)
- Modify: `src/PPDS.Dataverse/Services/ImportJobService.cs:78-87` (GetAsync ColumnSet)
- Modify: `src/PPDS.Dataverse/Services/ImportJobService.cs:164-179` (MapToImportJobInfo)

- [ ] **Step 1: Add CreatedBy to ColumnSets in ListAsync and GetAsync**

In `ListAsync` (around line 47), add `ImportJob.Fields.CreatedBy` to the ColumnSet:

```csharp
ColumnSet = new ColumnSet(
    ImportJob.Fields.ImportJobId,
    ImportJob.Fields.Name,
    ImportJob.Fields.SolutionName,
    ImportJob.Fields.SolutionId,
    ImportJob.Fields.Progress,
    ImportJob.Fields.StartedOn,
    ImportJob.Fields.CompletedOn,
    ImportJob.Fields.CreatedOn,
    ImportJob.Fields.CreatedBy),
```

Apply the same change to `GetAsync` (around line 79).

- [ ] **Step 2: Update MapToImportJobInfo to include CreatedByName**

Change the mapper (around line 164):

```csharp
private static ImportJobInfo MapToImportJobInfo(Entity entity)
{
    var completedOn = entity.GetAttributeValue<DateTime?>(ImportJob.Fields.CompletedOn);
    var progress = entity.GetAttributeValue<double?>(ImportJob.Fields.Progress) ?? 0;
    var createdByRef = entity.GetAttributeValue<EntityReference>(ImportJob.Fields.CreatedBy);

    return new ImportJobInfo(
        entity.Id,
        entity.GetAttributeValue<string>(ImportJob.Fields.Name),
        entity.GetAttributeValue<string>(ImportJob.Fields.SolutionName),
        entity.GetAttributeValue<Guid?>(ImportJob.Fields.SolutionId),
        progress,
        entity.GetAttributeValue<DateTime?>(ImportJob.Fields.StartedOn),
        completedOn,
        entity.GetAttributeValue<DateTime?>(ImportJob.Fields.CreatedOn),
        completedOn.HasValue,
        createdByRef?.Name);
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v q`
Expected: Build succeeded.

- [ ] **Step 4: Build full solution to ensure no downstream breaks**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded (all existing callers work unchanged due to default parameter).

### Task 3: Add RPC DTOs and endpoint methods

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — Add methods + DTOs

- [ ] **Step 1: Add importJobs/list RPC method**

Add to the RPC handler class (in a new `#region Import Jobs` section, after the Solutions region):

```csharp
#region Import Jobs

/// <summary>
/// Lists import jobs for an environment.
/// Maps to: ppds importjobs list --json
/// </summary>
[JsonRpcMethod("importJobs/list")]
public async Task<ImportJobsListResponse> ImportJobsListAsync(
    int top = 50,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var importJobService = sp.GetRequiredService<IImportJobService>();
        var jobs = await importJobService.ListAsync(top: top, cancellationToken: ct);

        return new ImportJobsListResponse
        {
            Jobs = jobs.Select(MapImportJobToDto).ToList()
        };
    }, cancellationToken);
}

/// <summary>
/// Gets a single import job with full detail including XML data.
/// Maps to: ppds importjobs get + ppds importjobs data
/// </summary>
[JsonRpcMethod("importJobs/get")]
public async Task<ImportJobsGetResponse> ImportJobsGetAsync(
    string id,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var importJobId))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'id' parameter must be a valid GUID");
    }

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var importJobService = sp.GetRequiredService<IImportJobService>();

        var job = await importJobService.GetAsync(importJobId, ct)
            ?? throw new RpcException(
                ErrorCodes.Operation.NotFound,
                $"Import job '{id}' not found");

        var data = await importJobService.GetDataAsync(importJobId, ct);

        var dto = MapImportJobToDto(job);

        return new ImportJobsGetResponse
        {
            Job = new ImportJobDetailDto
            {
                Id = dto.Id,
                SolutionName = dto.SolutionName,
                Status = dto.Status,
                Progress = dto.Progress,
                CreatedBy = dto.CreatedBy,
                CreatedOn = dto.CreatedOn,
                StartedOn = dto.StartedOn,
                CompletedOn = dto.CompletedOn,
                Duration = dto.Duration,
                Data = data
            }
        };
    }, cancellationToken);
}

private static ImportJobInfoDto MapImportJobToDto(ImportJobInfo job)
{
    return new ImportJobInfoDto
    {
        Id = job.Id.ToString(),
        SolutionName = job.SolutionName,
        Status = job.Status,                       // computed property on record
        Progress = job.Progress,
        CreatedBy = job.CreatedByName,
        CreatedOn = job.CreatedOn?.ToString("o"),
        StartedOn = job.StartedOn?.ToString("o"),
        CompletedOn = job.CompletedOn?.ToString("o"),
        Duration = job.FormattedDuration           // computed property on record
    };
}

#endregion
```

Note: `ErrorCodes.Operation.NotFound` is the correct error code (value `"Operation.NotFound"`) — defined in `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`.

- [ ] **Step 2: Add DTO classes at the bottom of RpcMethodHandler.cs**

Add after the existing DTO classes (after the last `}` of the existing DTOs):

```csharp
// ── Import Jobs DTOs ────────────────────────────────────────────────────────

public class ImportJobsListResponse
{
    [JsonPropertyName("jobs")]
    public List<ImportJobInfoDto> Jobs { get; set; } = [];
}

public class ImportJobsGetResponse
{
    [JsonPropertyName("job")]
    public ImportJobDetailDto Job { get; set; } = null!;
}

public class ImportJobInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("solutionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SolutionName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("startedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedOn { get; set; }

    [JsonPropertyName("completedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedOn { get; set; }

    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }
}

public class ImportJobDetailDto : ImportJobInfoDto
{
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 4: Add TypeScript response types

**Files:**
- Modify: `src/PPDS.Extension/src/types.ts`

- [ ] **Step 1: Add import job TS interfaces**

Add at the bottom of `src/PPDS.Extension/src/types.ts`:

```typescript
// ── Import Jobs ──────────────────────────────────────────────────────────────

export interface ImportJobsListResponse {
    jobs: ImportJobInfoDto[];
}

export interface ImportJobsGetResponse {
    job: ImportJobDetailDto;
}

export interface ImportJobInfoDto {
    id: string;
    solutionName: string | null;
    status: string;
    progress: number;
    createdBy: string | null;
    createdOn: string | null;
    startedOn: string | null;
    completedOn: string | null;
    duration: string | null;
}

export interface ImportJobDetailDto extends ImportJobInfoDto {
    data: string | null;
}
```

### Task 5: Add daemon client methods

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts`

- [ ] **Step 1: Add import to types.ts**

Add `ImportJobsListResponse` and `ImportJobsGetResponse` to the existing import statement at the top of `daemonClient.ts`:

```typescript
import type {
    // ... existing imports ...
    ImportJobsListResponse,
    ImportJobsGetResponse,
} from './types.js';
```

- [ ] **Step 2: Add importJobsList method**

Add in the daemon client class (in a new `// ── Import Jobs ──` section):

```typescript
// ── Import Jobs ─────────────────────────────────────────────────────────

async importJobsList(top?: number, environmentUrl?: string): Promise<ImportJobsListResponse> {
    await this.ensureConnected();

    const params: Record<string, unknown> = {};
    if (top !== undefined) params.top = top;
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

    this.log.info('Calling importJobs/list...');
    const result = await this.connection!.sendRequest<ImportJobsListResponse>('importJobs/list', params);
    this.log.debug(`Got ${result.jobs.length} import jobs`);

    return result;
}

async importJobsGet(id: string, environmentUrl?: string): Promise<ImportJobsGetResponse> {
    await this.ensureConnected();

    const params: Record<string, unknown> = { id };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

    this.log.info(`Calling importJobs/get for ${id}...`);
    const result = await this.connection!.sendRequest<ImportJobsGetResponse>('importJobs/get', params);
    this.log.debug(`Got import job detail: ${result.job.solutionName}`);

    return result;
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
git add src/PPDS.Dataverse/Services/IImportJobService.cs \
        src/PPDS.Dataverse/Services/ImportJobService.cs \
        src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs \
        src/PPDS.Extension/src/types.ts \
        src/PPDS.Extension/src/daemonClient.ts
git commit -m "feat(rpc): add importJobs/list and importJobs/get RPC endpoints

Wire IImportJobService through daemon RPC with computed status, duration,
and createdBy fields. Add TypeScript client methods and response types."
```

---

## Chunk 2: VS Code Extension Panel

### Task 7: Add message types

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`

- [ ] **Step 1: Add Import Jobs panel message unions**

Add at the bottom of `message-types.ts`:

```typescript
// ── Import Jobs Panel ─────────────────────────────────────────────────────

/** Import job info as sent to the webview for table display. */
export interface ImportJobViewDto {
    id: string;
    solutionName: string | null;
    status: string;
    progress: number;
    createdBy: string | null;
    createdOn: string | null;
    startedOn: string | null;
    completedOn: string | null;
    duration: string | null;
}

/** Messages the Import Jobs Panel webview sends to the extension host. */
export type ImportJobsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectJob'; id: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Import Jobs Panel webview. */
export type ImportJobsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'importJobsLoaded'; jobs: ImportJobViewDto[] }
    | { command: 'importJobDetailLoaded'; id: string; data: string | null }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

- [ ] **Step 2: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors.

### Task 8: Create shared DataTable component

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/shared/data-table.ts`

This is the pattern-setting shared component. All panels will use it. Import Jobs establishes the API; Phase 2 panels add virtual scrolling to the same class.

- [ ] **Step 1: Write the shared DataTable class**

Create `src/PPDS.Extension/src/panels/webview/shared/data-table.ts`:

```typescript
import { escapeHtml, escapeAttr } from './dom-utils.js';

/** Column definition for the DataTable. */
export interface DataTableColumn<T> {
    /** Column key — used for sort state and data-col attribute. */
    key: string;
    /** Header label displayed in thead. */
    label: string;
    /** Render cell content (must return escaped HTML string). */
    render: (item: T) => string;
    /** Optional CSS class for the column (e.g., for width). */
    className?: string;
    /** Whether this column is sortable. Default: true. */
    sortable?: boolean;
}

/** Sort direction. */
export type SortDirection = 'asc' | 'desc';

/** Options for creating a DataTable. */
export interface DataTableOptions<T> {
    /** Container element to render into. */
    container: HTMLElement;
    /** Column definitions. */
    columns: DataTableColumn<T>[];
    /** Get the unique ID for a row (used for data-id attribute). */
    getRowId: (item: T) => string;
    /** Called when a row is clicked. */
    onRowClick?: (item: T) => void;
    /** Default sort column key. */
    defaultSortKey?: string;
    /** Default sort direction. */
    defaultSortDirection?: SortDirection;
    /** CSS class for the table element. */
    tableClass?: string;
    /** Status bar element to update with count. */
    statusEl?: HTMLElement;
    /** Format the status text. */
    formatStatus?: (items: T[]) => string;
    /** Empty state message. */
    emptyMessage?: string;
}

/**
 * Shared data table component for all PPDS panels.
 *
 * Provides: sortable columns, row selection, keyboard navigation,
 * sticky header, and status bar updates.
 *
 * Phase 2 will add virtual scrolling to this component for large datasets
 * (Plugin Traces, Web Resources).
 */
export class DataTable<T> {
    private items: T[] = [];
    private sortKey: string;
    private sortDirection: SortDirection;
    private selectedId: string | null = null;
    private readonly opts: DataTableOptions<T>;

    constructor(opts: DataTableOptions<T>) {
        this.opts = opts;
        this.sortKey = opts.defaultSortKey ?? opts.columns[0]?.key ?? '';
        this.sortDirection = opts.defaultSortDirection ?? 'desc';

        // Keyboard: Enter on row (delegated, registered once)
        opts.container.addEventListener('keydown', (e) => {
            if (e.key !== 'Enter') return;
            const row = (e.target as HTMLElement).closest<HTMLElement>('.data-table-row');
            if (row) { e.preventDefault(); row.click(); }
        });
    }

    /** Set data and re-render. */
    setItems(items: T[]): void {
        this.items = items;
        this.selectedId = null;
        this.render();
    }

    /** Get current items. */
    getItems(): T[] {
        return this.items;
    }

    /** Clear selection. */
    clearSelection(): void {
        this.selectedId = null;
        this.opts.container
            .querySelectorAll<HTMLElement>('.data-table-row.selected')
            .forEach(r => r.classList.remove('selected'));
    }

    /** Get selected item ID. */
    getSelectedId(): string | null {
        return this.selectedId;
    }

    private render(): void {
        const { container, columns, getRowId, emptyMessage, statusEl, formatStatus } = this.opts;

        if (this.items.length === 0) {
            container.innerHTML = '<div class="empty-state">' + escapeHtml(emptyMessage ?? 'No data') + '</div>';
            if (statusEl) statusEl.textContent = emptyMessage ?? 'No data';
            return;
        }

        const sorted = this.sortItems();
        const tableClass = this.opts.tableClass ?? 'data-table';
        const indicator = (key: string): string =>
            this.sortKey === key ? (this.sortDirection === 'asc' ? ' \u25B2' : ' \u25BC') : '';

        let html = '<table class="' + escapeAttr(tableClass) + '">';
        html += '<thead><tr>';
        for (const col of columns) {
            const sortable = col.sortable !== false;
            html += '<th' +
                (sortable ? ' class="sortable" data-col="' + escapeAttr(col.key) + '"' : '') +
                (col.className ? ' style="width:' + escapeAttr(col.className) + '"' : '') +
                '>' + escapeHtml(col.label) + (sortable ? indicator(col.key) : '') + '</th>';
        }
        html += '</tr></thead><tbody>';

        for (const item of sorted) {
            const id = getRowId(item);
            html += '<tr class="data-table-row" data-id="' + escapeAttr(id) + '" tabindex="0">';
            for (const col of columns) {
                html += '<td>' + col.render(item) + '</td>';
            }
            html += '</tr>';
        }
        html += '</tbody></table>';
        container.innerHTML = html;

        // Sort click handlers
        container.querySelectorAll<HTMLElement>('.sortable').forEach(th => {
            th.addEventListener('click', () => {
                const col = th.dataset.col!;
                if (this.sortKey === col) {
                    this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
                } else {
                    this.sortKey = col;
                    this.sortDirection = 'asc';
                }
                this.render();
            });
        });

        // Row click handlers
        container.querySelectorAll<HTMLElement>('.data-table-row').forEach(row => {
            row.addEventListener('click', () => {
                this.selectedId = row.dataset.id!;
                container.querySelectorAll<HTMLElement>('.data-table-row.selected')
                    .forEach(r => r.classList.remove('selected'));
                row.classList.add('selected');
                const item = this.items.find(i => getRowId(i) === this.selectedId);
                if (item && this.opts.onRowClick) this.opts.onRowClick(item);
            });
        });

        // Status bar
        if (statusEl && formatStatus) {
            statusEl.textContent = formatStatus(this.items);
        }
    }

    private sortItems(): T[] {
        const col = this.opts.columns.find(c => c.key === this.sortKey);
        if (!col) return [...this.items];

        const sorted = [...this.items];
        sorted.sort((a, b) => {
            const aHtml = col.render(a);
            const bHtml = col.render(b);
            // Strip HTML tags for comparison (renders return escaped text)
            const aText = aHtml.replace(/<[^>]*>/g, '');
            const bText = bHtml.replace(/<[^>]*>/g, '');
            const cmp = aText.localeCompare(bText, undefined, { numeric: true });
            return this.sortDirection === 'asc' ? cmp : -cmp;
        });
        return sorted;
    }
}
```

- [ ] **Step 2: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors (no consumers yet — just verifying the module compiles).

### Task 9: Create ImportJobsPanel host class

**Files:**
- Create: `src/PPDS.Extension/src/panels/ImportJobsPanel.ts`

- [ ] **Step 1: Write the host-side panel**

Create `src/PPDS.Extension/src/panels/ImportJobsPanel.ts`:

```typescript
import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type {
    ImportJobsPanelWebviewToHost,
    ImportJobsPanelHostToWebview,
} from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class ImportJobsPanel extends WebviewPanelBase<
    ImportJobsPanelWebviewToHost,
    ImportJobsPanelHostToWebview
> {
    private static instances: ImportJobsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    private environmentUrl: string | undefined;
    private environmentDisplayName: string | undefined;
    private environmentType: string | null = null;
    private environmentColor: string | null = null;
    private environmentId: string | null = null;
    private profileName: string | undefined;

    static get instanceCount(): number {
        return ImportJobsPanel.instances.length;
    }

    static show(
        extensionUri: vscode.Uri,
        daemon: DaemonClient,
        envUrl?: string,
        envDisplayName?: string,
    ): ImportJobsPanel {
        if (ImportJobsPanel.instances.length >= ImportJobsPanel.MAX_PANELS) {
            const oldest = ImportJobsPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        return new ImportJobsPanel(extensionUri, daemon, envUrl, envDisplayName);
    }

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        initialEnvUrl?: string,
        initialEnvDisplayName?: string,
    ) {
        super();

        if (initialEnvUrl) {
            this.environmentUrl = initialEnvUrl;
            this.environmentDisplayName = initialEnvDisplayName ?? initialEnvUrl;
        }

        this.panelId = ImportJobsPanel.nextId++;
        ImportJobsPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.importJobs',
            `Import Jobs #${this.panelId}`,
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                retainContextWhenHidden: false,
                localResourceRoots: [
                    vscode.Uri.joinPath(extensionUri, 'node_modules'),
                    vscode.Uri.joinPath(extensionUri, 'dist'),
                ],
            },
        );

        panel.webview.html = this.getHtmlContent(panel.webview);
        this.initPanel(panel);
        this.subscribeToDaemonReconnect(this.daemon);
    }

    protected async handleMessage(message: ImportJobsPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initialize();
                break;
            case 'refresh':
                await this.loadImportJobs();
                break;
            case 'selectJob':
                await this.loadJobDetail(message.id);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPicker();
                break;
            case 'openInMaker': {
                if (this.environmentId) {
                    const url = `https://make.powerapps.com/environments/${this.environmentId}/solutionsHistory`;
                    await vscode.env.openExternal(vscode.Uri.parse(url));
                } else {
                    vscode.window.showInformationMessage(
                        'Environment ID not available — cannot open Maker Portal.',
                    );
                }
                break;
            }
            case 'copyToClipboard':
                await vscode.env.clipboard.writeText(message.text);
                break;
            case 'webviewError':
                this.logWebviewError(message.error, message.stack);
                break;
            default:
                assertNever(message);
        }
    }

    protected override onDaemonReconnected(): void {
        void this.loadImportJobs();
    }

    override dispose(): void {
        const idx = ImportJobsPanel.instances.indexOf(this);
        if (idx >= 0) ImportJobsPanel.instances.splice(idx, 1);
        super.dispose();
    }

    private async initialize(): Promise<void> {
        try {
            const who = await this.daemon.authWho();
            this.profileName = who.name ?? `Profile ${who.index}`;
            if (!this.environmentUrl && who.environment?.url) {
                this.environmentUrl = who.environment.url;
                this.environmentDisplayName =
                    who.environment.displayName || who.environment.url;
            }
            this.environmentType = who.environment?.type ?? null;
            if (who.environment?.environmentId) {
                this.environmentId = who.environment.environmentId;
            } else {
                this.environmentId = await this.resolveEnvironmentId();
            }
            if (this.environmentUrl) {
                try {
                    const config = await this.daemon.envConfigGet(this.environmentUrl);
                    this.environmentColor = config.resolvedColor ?? null;
                } catch {
                    this.environmentColor = null;
                }
            }
            this.updatePanelTitle();
            this.postMessage({
                command: 'updateEnvironment',
                name: this.environmentDisplayName ?? 'No environment',
                envType: this.environmentType,
                envColor: this.environmentColor,
            });
            await this.loadImportJobs();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({
                command: 'error',
                message: `Failed to initialize: ${msg}`,
            });
        }
    }

    private async handleEnvironmentPicker(): Promise<void> {
        const result = await showEnvironmentPicker(
            this.daemon,
            this.environmentUrl,
        );
        if (result) {
            this.environmentUrl = result.url;
            this.environmentDisplayName = result.displayName;
            this.environmentType = result.type;
            this.environmentId = await this.resolveEnvironmentId();
            try {
                const config = await this.daemon.envConfigGet(result.url);
                this.environmentColor = config.resolvedColor ?? null;
            } catch {
                this.environmentColor = null;
            }
            this.updatePanelTitle();
            this.postMessage({
                command: 'updateEnvironment',
                name: result.displayName,
                envType: result.type,
                envColor: this.environmentColor,
            });
            await this.loadImportJobs();
        }
    }

    private async resolveEnvironmentId(): Promise<string | null> {
        if (!this.environmentUrl) return null;
        try {
            const normalise = (u: string): string =>
                u.replace(/\/+$/, '').toLowerCase();
            const targetUrl = normalise(this.environmentUrl);
            const envResult = await this.daemon.envList();
            const match = envResult.environments.find(
                (e) =>
                    normalise(e.apiUrl) === targetUrl ||
                    (e.url && normalise(e.url) === targetUrl),
            );
            return match?.environmentId ?? null;
        } catch {
            return null;
        }
    }

    private updatePanelTitle(): void {
        if (!this.panel) return;
        const context = [this.profileName, this.environmentDisplayName]
            .filter(Boolean)
            .join(' \u00B7 ');
        const suffix =
            ImportJobsPanel.instances.length > 1 ? ` ${this.panelId}` : '';
        this.panel.title = context
            ? `${context} \u2014 Import Jobs${suffix}`
            : `Import Jobs${suffix}`;
    }

    private async loadImportJobs(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.importJobsList(undefined, this.environmentUrl);

            this.postMessage({
                command: 'importJobsLoaded',
                jobs: result.jobs.map((j) => ({
                    id: j.id,
                    solutionName: j.solutionName,
                    status: j.status,
                    progress: j.progress,
                    createdBy: j.createdBy,
                    createdOn: j.createdOn,
                    startedOn: j.startedOn,
                    completedOn: j.completedOn,
                    duration: j.duration,
                })),
            });
        } catch (error) {
            if (
                await handleAuthError(this.daemon, error, isRetry, () =>
                    this.loadImportJobs(true),
                )
            ) {
                return;
            }
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadJobDetail(id: string): Promise<void> {
        try {
            const result = await this.daemon.importJobsGet(
                id,
                this.environmentUrl,
            );
            this.postMessage({
                command: 'importJobDetailLoaded',
                id,
                data: result.job.data,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({
                command: 'error',
                message: `Failed to load import log: ${msg}`,
            });
        }
    }

    getHtmlContent(webview: vscode.Webview): string {
        const cssUri = webview
            .asWebviewUri(
                vscode.Uri.joinPath(
                    this.extensionUri,
                    'dist',
                    'import-jobs-panel.css',
                ),
            )
            .toString();
        const jsUri = webview
            .asWebviewUri(
                vscode.Uri.joinPath(
                    this.extensionUri,
                    'dist',
                    'import-jobs-panel.js',
                ),
            )
            .toString();
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <link rel="stylesheet" href="${cssUri}">
</head>
<body>

<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>

<div class="toolbar">
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh import jobs (Ctrl+R)">Refresh</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open in Maker Portal">Open in Maker</vscode-button>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading import jobs...</div>
</div>

<div id="detail-pane" class="detail-pane" style="display: none;">
    <div class="detail-header">
        <span id="detail-title">Import Log</span>
        <button id="detail-close" class="detail-close-btn" title="Close detail">&times;</button>
    </div>
    <pre id="detail-content" class="detail-content"></pre>
</div>

<div class="status-bar">
    <span id="status-text">Ready</span>
</div>

<script nonce="${nonce}" src="${jsUri}"></script>
</body>
</html>`;
    }
}
```

- [ ] **Step 2: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors (may have errors until webview script exists — proceed to next task).

### Task 10: Create webview script

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts`

- [ ] **Step 1: Write the browser-side webview script using shared DataTable**

Create `src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts`:

```typescript
// import-jobs-panel.ts
// External webview script for the Import Jobs panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, formatDate } from './shared/dom-utils.js';
import { DataTable } from './shared/data-table.js';
import type {
    ImportJobsPanelWebviewToHost,
    ImportJobsPanelHostToWebview,
    ImportJobViewDto,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';

const vscode = getVsCodeApi<ImportJobsPanelWebviewToHost>();
installErrorHandler(
    (msg) => vscode.postMessage(msg as ImportJobsPanelWebviewToHost),
);

// DOM references
const content = document.getElementById('content')!;
const statusText = document.getElementById('status-text')!;
const refreshBtn = document.getElementById('refresh-btn')!;
const makerBtn = document.getElementById('maker-btn')!;
const detailPane = document.getElementById('detail-pane')!;
const detailTitle = document.getElementById('detail-title')!;
const detailContent = document.getElementById('detail-content')!;
const detailCloseBtn = document.getElementById('detail-close')!;

// ── Status badge helper ──
function statusBadgeHtml(status: string): string {
    const cls = status === 'Succeeded' ? 'status-succeeded'
        : status === 'Failed' ? 'status-failed'
        : status === 'In Progress' ? 'status-inprogress' : '';
    return '<span class="status-badge ' + cls + '">' + escapeHtml(status) + '</span>';
}

// ── Shared DataTable instance ──
const table = new DataTable<ImportJobViewDto>({
    container: content,
    columns: [
        { key: 'solutionName', label: 'Solution Name', render: j => escapeHtml(j.solutionName ?? '\u2014') },
        { key: 'status', label: 'Status', render: j => statusBadgeHtml(j.status) },
        { key: 'progress', label: 'Progress', render: j => Math.round(j.progress) + '%' },
        { key: 'createdBy', label: 'Created By', render: j => escapeHtml(j.createdBy ?? '\u2014') },
        { key: 'createdOn', label: 'Created On', render: j => j.createdOn ? formatDate(j.createdOn) : '\u2014' },
        { key: 'duration', label: 'Duration', render: j => escapeHtml(j.duration ?? '\u2014') },
    ],
    getRowId: j => j.id,
    onRowClick: j => vscode.postMessage({ command: 'selectJob', id: j.id }),
    defaultSortKey: 'createdOn',
    defaultSortDirection: 'desc',
    tableClass: 'data-table',
    statusEl: statusText,
    formatStatus: items => {
        const s = items.filter(j => j.status === 'Succeeded').length;
        const f = items.filter(j => j.status === 'Failed').length;
        const p = items.filter(j => j.status === 'In Progress').length;
        const parts = [`${items.length} import job${items.length !== 1 ? 's' : ''}`];
        if (s > 0) parts.push(`${s} succeeded`);
        if (f > 0) parts.push(`${f} failed`);
        if (p > 0) parts.push(`${p} in progress`);
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No import jobs found',
});

// ── Environment picker ──
const envPickerBtn = document.getElementById('env-picker-btn')!;
const envPickerName = document.getElementById('env-picker-name')!;
envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'refresh' });
});

makerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'openInMaker' });
});

detailCloseBtn.addEventListener('click', () => {
    detailPane.style.display = 'none';
    table.clearSelection();
});

document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

// ── Keyboard shortcuts ──
document.addEventListener('keydown', (e) => {
    const mod = e.metaKey || e.ctrlKey;
    if (mod && e.key === 'r') {
        e.preventDefault();
        vscode.postMessage({ command: 'refresh' });
    }
    if (e.key === 'Escape' && detailPane.style.display !== 'none') {
        detailPane.style.display = 'none';
        table.clearSelection();
    }
});

// ── Message handling ──
window.addEventListener(
    'message',
    (event: MessageEvent<ImportJobsPanelHostToWebview>) => {
        const msg = event.data;
        if (!msg || typeof msg !== 'object' || !('command' in msg)) return;
        switch (msg.command) {
            case 'updateEnvironment':
                envPickerName.textContent = msg.name || 'No environment';
                {
                    const toolbar = document.querySelector('.toolbar');
                    if (toolbar) {
                        if (msg.envType) toolbar.setAttribute('data-env-type', msg.envType.toLowerCase());
                        else toolbar.removeAttribute('data-env-type');
                        if (msg.envColor) toolbar.setAttribute('data-env-color', msg.envColor.toLowerCase());
                        else toolbar.removeAttribute('data-env-color');
                    }
                }
                break;
            case 'importJobsLoaded':
                table.setItems(msg.jobs);
                detailPane.style.display = 'none';
                { const b = document.getElementById('reconnect-banner'); if (b) b.style.display = 'none'; }
                break;
            case 'importJobDetailLoaded':
                showDetail(msg.id, msg.data);
                break;
            case 'loading':
                content.innerHTML =
                    '<div class="loading-state"><div class="spinner"></div><div>Loading import jobs...</div></div>';
                statusText.textContent = 'Loading...';
                detailPane.style.display = 'none';
                break;
            case 'error':
                content.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
                statusText.textContent = 'Error';
                break;
            case 'daemonReconnected':
                document.getElementById('reconnect-banner')!.style.display = '';
                break;
            default:
                assertNever(msg);
        }
    },
);

function showDetail(id: string, data: string | null): void {
    const job = table.getItems().find(j => j.id === id);
    detailTitle.textContent = job
        ? `Import Log: ${job.solutionName ?? 'Unknown'}`
        : 'Import Log';
    detailContent.textContent = data ?? '(No import log data available)';
    detailPane.style.display = '';
}

// Signal ready
vscode.postMessage({ command: 'ready' });
```

### Task 11: Create panel CSS

**Files:**
- Create: `src/PPDS.Extension/src/panels/styles/import-jobs-panel.css`

- [ ] **Step 1: Write the panel CSS**

Create `src/PPDS.Extension/src/panels/styles/import-jobs-panel.css`:

```css
@import './shared.css';

/* ── Import Jobs Table ────────────────────────────────────── */

.data-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 12px;
    table-layout: fixed;
}

.data-table thead {
    position: sticky;
    top: 0;
    z-index: 1;
    background: var(--vscode-editor-background);
}

.data-table th {
    text-align: left;
    padding: 6px 8px;
    font-weight: 600;
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--vscode-foreground);
    border-bottom: 1px solid var(--vscode-widget-border, var(--vscode-panel-border, rgba(128,128,128,0.35)));
    user-select: none;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.data-table th.sortable {
    cursor: pointer;
}

.data-table th.sortable:hover {
    color: var(--vscode-textLink-foreground);
}

.data-table td {
    padding: 4px 8px;
    border-bottom: 1px solid var(--vscode-widget-border, var(--vscode-panel-border, rgba(128,128,128,0.12)));
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.data-table-row {
    cursor: pointer;
}

.data-table-row:hover {
    background: var(--vscode-list-hoverBackground);
}

.data-table-row:focus {
    outline: 1px solid var(--vscode-focusBorder);
    outline-offset: -1px;
}

.data-table-row.selected {
    background: var(--vscode-list-activeSelectionBackground);
    color: var(--vscode-list-activeSelectionForeground);
}

/* Column widths */
.data-table th:nth-child(1),
.data-table td:nth-child(1) { width: 30%; }
.data-table th:nth-child(2),
.data-table td:nth-child(2) { width: 12%; }
.data-table th:nth-child(3),
.data-table td:nth-child(3) { width: 10%; }
.data-table th:nth-child(4),
.data-table td:nth-child(4) { width: 20%; }
.data-table th:nth-child(5),
.data-table td:nth-child(5) { width: 16%; }
.data-table th:nth-child(6),
.data-table td:nth-child(6) { width: 12%; }

/* ── Status badges ────────────────────────────────────────── */

.status-badge {
    display: inline-block;
    padding: 1px 6px;
    border-radius: 2px;
    font-size: 11px;
    font-weight: 500;
}

.status-succeeded {
    background: rgba(40, 167, 69, 0.2);
    color: var(--vscode-testing-iconPassed, #28a745);
}

.status-failed {
    background: rgba(220, 53, 69, 0.2);
    color: var(--vscode-testing-iconFailed, #dc3545);
}

.status-inprogress {
    background: rgba(0, 123, 255, 0.2);
    color: var(--vscode-textLink-foreground, #007bff);
}

/* ── Detail pane ──────────────────────────────────────────── */

.detail-pane {
    border-top: 1px solid var(--vscode-widget-border, var(--vscode-panel-border, rgba(128,128,128,0.35)));
    max-height: 40vh;
    overflow: hidden;
    display: flex;
    flex-direction: column;
}

.detail-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 6px 12px;
    font-size: 12px;
    font-weight: 600;
    background: var(--vscode-editor-background);
    border-bottom: 1px solid var(--vscode-widget-border, var(--vscode-panel-border, rgba(128,128,128,0.12)));
}

.detail-close-btn {
    background: none;
    border: none;
    color: var(--vscode-foreground);
    font-size: 16px;
    cursor: pointer;
    padding: 0 4px;
    line-height: 1;
}

.detail-close-btn:hover {
    color: var(--vscode-textLink-foreground);
}

.detail-content {
    flex: 1;
    overflow: auto;
    padding: 8px 12px;
    margin: 0;
    font-family: var(--vscode-editor-font-family, monospace);
    font-size: 12px;
    line-height: 1.5;
    white-space: pre-wrap;
    word-break: break-word;
    color: var(--vscode-editor-foreground);
    background: var(--vscode-editor-background);
}
```

### Task 12: Add esbuild entries

**Files:**
- Modify: `src/PPDS.Extension/esbuild.js`

- [ ] **Step 1: Add import-jobs-panel entries to the builds array**

Add two new entries to the `builds` array (after the solutions-panel entries):

```javascript
    // Import Jobs panel webview (browser, IIFE)
    {
        entryPoints: ['src/panels/webview/import-jobs-panel.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/import-jobs-panel.js',
        logLevel: 'warning',
    },
    // Import Jobs panel CSS
    {
        entryPoints: ['src/panels/styles/import-jobs-panel.css'],
        bundle: true,
        minify: production,
        outfile: 'dist/import-jobs-panel.css',
        logLevel: 'warning',
    },
```

### Task 13: Register in extension.ts and tools tree view

**Files:**
- Modify: `src/PPDS.Extension/src/extension.ts`

- [ ] **Step 1: Import ImportJobsPanel**

Add to the imports at the top of extension.ts:

```typescript
import { ImportJobsPanel } from './panels/ImportJobsPanel.js';
```

- [ ] **Step 2: Register commands in registerPanelCommands function**

Add inside the existing `context.subscriptions.push(...)` call (after the `ppds.openSolutions` registration):

```typescript
        vscode.commands.registerCommand('ppds.openImportJobs', () => {
            ImportJobsPanel.show(context.extensionUri, client);
        }),
```

- [ ] **Step 3: Add Import Jobs to the Tools tree view**

In `src/PPDS.Extension/src/views/toolsTreeView.ts`, add to the static `tools` array (after Solutions):

```typescript
        { label: 'Import Jobs', commandId: 'ppds.openImportJobs', icon: 'history' },
```

- [ ] **Step 4: Register environment-scoped command**

Find the section where `ppds.openSolutionsForEnv` is registered (around line 232). Add after it:

```typescript
        vscode.commands.registerCommand('ppds.openImportJobsForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            ImportJobsPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
        })),
```

### Task 14: Register in package.json

**Files:**
- Modify: `src/PPDS.Extension/package.json`

- [ ] **Step 1: Add command definitions**

Find the `contributes.commands` array and add:

```json
      {
        "command": "ppds.openImportJobs",
        "title": "Open Import Jobs",
        "category": "PPDS",
        "icon": "$(history)"
      },
      {
        "command": "ppds.openImportJobsForEnv",
        "title": "Open Import Jobs",
        "icon": "$(history)"
      },
```

- [ ] **Step 2: Add menu item for environment context menu**

Find the `view/item/context` section in `menus` where `ppds.openSolutionsForEnv` is registered. Add after it:

```json
        {
          "command": "ppds.openImportJobsForEnv",
          "when": "view == ppds.profiles && viewItem == environment",
          "group": "env-tools@3"
        },
```

Note: The existing `ForEnv` commands (e.g., `ppds.openSolutionsForEnv`) are not hidden from the command palette. Match that pattern — do not add a `commandPalette` section for Import Jobs alone.

### Task 15: Build + Typecheck + Lint

- [ ] **Step 1: Build .NET**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

- [ ] **Step 2: Build extension (esbuild)**

Run: `cd src/PPDS.Extension && node esbuild.js`
Expected: No errors.

- [ ] **Step 3: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors.

- [ ] **Step 4: Lint**

Run: `cd src/PPDS.Extension && npx eslint --quiet src/panels/ImportJobsPanel.ts src/panels/webview/import-jobs-panel.ts`
Expected: No errors (fix any issues before committing).

- [ ] **Step 5: CSS lint**

Run: `cd src/PPDS.Extension && npm run lint:css`
Expected: No errors.

### Task 16: Commit — VS Code Panel

- [ ] **Step 1: Commit**

```bash
git add src/PPDS.Extension/src/panels/ImportJobsPanel.ts \
        src/PPDS.Extension/src/panels/webview/shared/data-table.ts \
        src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts \
        src/PPDS.Extension/src/panels/styles/import-jobs-panel.css \
        src/PPDS.Extension/src/panels/webview/shared/message-types.ts \
        src/PPDS.Extension/src/views/toolsTreeView.ts \
        src/PPDS.Extension/esbuild.js \
        src/PPDS.Extension/src/extension.ts \
        src/PPDS.Extension/package.json
git commit -m "feat(ext): add Import Jobs webview panel with shared DataTable

Shared DataTable component (data-table.ts) establishes the table pattern
for all panels. Import Jobs panel uses it with environment picker, status
color-coding, sortable columns, and XML import log detail pane."
```

### Task 17: Visual verification

- [ ] **Step 1: Verify with @webview-cdp**

Use the @webview-cdp skill to:
1. Launch VS Code with the extension
2. Open the Import Jobs panel via command palette (`PPDS: Open Import Jobs`)
3. Take a screenshot and verify:
   - Three-zone layout renders (toolbar, content, status bar)
   - Environment picker appears in toolbar
   - Loading state shows spinner
   - If connected: table renders with columns, status badges have color coding
   - If not connected: error state displays properly

---

## Chunk 3: TUI Screen

### Task 18: Create ImportJobsScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/ImportJobsScreen.cs`

- [ ] **Step 1: Create the TUI screen**

Create `src/PPDS.Cli/Tui/Screens/ImportJobsScreen.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for viewing import jobs.
/// </summary>
internal sealed class ImportJobsScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private List<ImportJobInfo> _jobs = [];
    private Dialog? _detailDialog;

    public override string Title => "Import Jobs";

    public ImportJobsScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1), // Leave room for status
            FullRowSelect = true,
            Style = { ShowHorizontalHeaderOverline = false, ShowHorizontalHeaderUnderline = true }
        };

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(_table),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Loading...",
            ColorScheme = TuiColorPalette.Default
        };

        _table.CellActivated += OnCellActivated;

        Content.Add(_table, _statusLabel);

        // Load data after layout is ready
        Application.MainLoop.AddIdle(() =>
        {
            _ = LoadDataAsync();
            return false;
        });
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.R.WithCtrl, "Refresh", () => _ = LoadDataAsync());
        RegisterHotkey(registry, Key.O.WithCtrl, "Open in Maker", OpenInMaker);
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _statusLabel.Text = "Loading import jobs...";
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IImportJobService>();

            _jobs = await service.ListAsync(top: 50, cancellationToken: ScreenCancellation);

            var dt = new System.Data.DataTable();
            dt.Columns.Add("Solution", typeof(string));
            dt.Columns.Add("Status", typeof(string));
            dt.Columns.Add("Progress", typeof(string));
            dt.Columns.Add("Created By", typeof(string));
            dt.Columns.Add("Created On", typeof(string));
            dt.Columns.Add("Duration", typeof(string));

            foreach (var job in _jobs)
            {
                dt.Rows.Add(
                    job.SolutionName ?? "—",
                    job.Status,                             // computed property
                    $"{job.Progress:F0}%",
                    job.CreatedByName ?? "—",
                    job.CreatedOn?.ToString("g") ?? "—",
                    job.FormattedDuration ?? "—");           // computed property
            }

            Application.MainLoop.Invoke(() =>
            {
                _table.Table = dt;
                var succeeded = _jobs.Count(j => j.Status == "Succeeded");
                var failed = _jobs.Count(j => j.Status == "Failed");
                var inProgress = _jobs.Count(j => j.Status == "In Progress");
                _statusLabel.Text = $"{_jobs.Count} import job{(_jobs.Count != 1 ? "s" : "")} — {succeeded} succeeded, {failed} failed, {inProgress} in progress";
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ShowError("Failed to load import jobs", ex);
                _statusLabel.Text = "Error loading import jobs";
            });
        }
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (args.Row < 0 || args.Row >= _jobs.Count) return;
        var job = _jobs[args.Row];
        _ = ShowDetailDialogAsync(job);
    }

    private async Task ShowDetailDialogAsync(ImportJobInfo job)
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IImportJobService>();

            var data = await service.GetDataAsync(job.Id, ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                _detailDialog?.Dispose();

                var textView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    Text = data ?? "(No import log data available)"
                };

                _detailDialog = new Dialog(
                    $"Import Log: {job.SolutionName ?? "Unknown"}",
                    new Button("Close", is_default: true))
                {
                    Width = Dim.Percent(80),
                    Height = Dim.Percent(80)
                };
                _detailDialog.Add(textView);
                Application.Run(_detailDialog);
                _detailDialog.Dispose();
                _detailDialog = null;
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ShowError("Failed to load import log", ex);
            });
        }
    }

    private void OpenInMaker()
    {
        if (EnvironmentUrl == null)
        {
            ErrorService.ShowError("No environment URL available");
            return;
        }
        // TUI can't open browser easily — show URL in a dialog
        var dialog = new Dialog("Open in Maker", new Button("OK", is_default: true))
        {
            Width = 60,
            Height = 7
        };
        dialog.Add(new Label { X = 1, Y = 1, Text = "Open this URL in your browser:" });
        dialog.Add(new Label { X = 1, Y = 2, Text = EnvironmentUrl + "/solutionsHistory" });
        Application.Run(dialog);
        dialog.Dispose();
    }

    protected override void OnDispose()
    {
        _table.CellActivated -= OnCellActivated;
        _detailDialog?.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 19: Register screen in TUI shell

**Files:**
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs`

- [ ] **Step 1: Add menu item or hotkey to open Import Jobs**

Find the section where `NavigateToSqlQuery` is called (around the tab bar setup or menu creation). Add a menu item or method to create and navigate to ImportJobsScreen. Follow the same pattern as SqlQueryScreen:

```csharp
// Add a method:
private bool NavigateToImportJobs()
{
    var screen = new ImportJobsScreen(_session);
    NavigateTo(screen);
    return false;
}
```

Wire it to a menu item in the "Tools" or "View" menu, or to a hotkey in the TUI shell.

- [ ] **Step 2: Build to verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

### Task 20: Commit — TUI Screen

- [ ] **Step 1: Commit**

```bash
git add src/PPDS.Cli/Tui/Screens/ImportJobsScreen.cs \
        src/PPDS.Cli/Tui/TuiShell.cs
git commit -m "feat(tui): add ImportJobsScreen

Data table with status, progress, duration, and scrollable import log
detail dialog. Ctrl+R to refresh, Enter for detail, Ctrl+O for Maker URL."
```

---

## Chunk 4: MCP Tools

### Task 21: Create ImportJobsListTool

**Files:**
- Create: `src/PPDS.Mcp/Tools/ImportJobsListTool.cs`

- [ ] **Step 1: Write the MCP list tool**

Create `src/PPDS.Mcp/Tools/ImportJobsListTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists import jobs for the current environment.
/// </summary>
[McpServerToolType]
public sealed class ImportJobsListTool
{
    private readonly McpToolContext _context;

    public ImportJobsListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [McpServerTool(Name = "ppds_import_jobs_list")]
    [Description("List recent solution import jobs for the current environment. Shows import status, progress, solution name, and timing. Use this to check if a solution import succeeded or failed.")]
    public async Task<ImportJobsListResult> ExecuteAsync(
        [Description("Maximum number of results to return (default 50)")]
        int top = 50,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IImportJobService>();

        var jobs = await service.ListAsync(top: top, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ImportJobsListResult
        {
            Jobs = jobs.Select(j => new ImportJobSummary
            {
                Id = j.Id.ToString(),
                SolutionName = j.SolutionName,
                Status = j.Status,                       // computed property
                Progress = j.Progress,
                CreatedBy = j.CreatedByName,
                CreatedOn = j.CreatedOn?.ToString("o"),
                CompletedOn = j.CompletedOn?.ToString("o"),
                Duration = j.FormattedDuration           // computed property
            }).ToList()
        };
    }
}

public sealed class ImportJobsListResult
{
    [JsonPropertyName("jobs")]
    public List<ImportJobSummary> Jobs { get; set; } = [];
}

public sealed class ImportJobSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("solutionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SolutionName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("completedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedOn { get; set; }

    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }
}
```

### Task 22: Create ImportJobsGetTool

**Files:**
- Create: `src/PPDS.Mcp/Tools/ImportJobsGetTool.cs`

- [ ] **Step 1: Write the MCP get tool**

Create `src/PPDS.Mcp/Tools/ImportJobsGetTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets full import job detail including XML import log.
/// </summary>
[McpServerToolType]
public sealed class ImportJobsGetTool
{
    private readonly McpToolContext _context;

    public ImportJobsGetTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [McpServerTool(Name = "ppds_import_jobs_get")]
    [Description("Get full details of a specific import job including the XML import log. Use the id from ppds_import_jobs_list. The import log XML contains detailed component-level success/failure information for troubleshooting failed imports.")]
    public async Task<ImportJobGetResult> ExecuteAsync(
        [Description("The import job ID (GUID) from ppds_import_jobs_list")]
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var importJobId))
        {
            throw new ArgumentException($"Invalid import job ID: '{id}'. Must be a valid GUID.");
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IImportJobService>();

        var job = await service.GetAsync(importJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Import job '{id}' not found.");

        var data = await service.GetDataAsync(importJobId, cancellationToken).ConfigureAwait(false);

        return new ImportJobGetResult
        {
            Id = job.Id.ToString(),
            SolutionName = job.SolutionName,
            Status = job.Status,                       // computed property
            Progress = job.Progress,
            CreatedBy = job.CreatedByName,
            CreatedOn = job.CreatedOn?.ToString("o"),
            CompletedOn = job.CompletedOn?.ToString("o"),
            Data = data
        };
    }
}

public sealed class ImportJobGetResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("solutionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SolutionName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("completedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedOn { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
}
```

### Task 23: Build + Commit — MCP Tools

- [ ] **Step 1: Build**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Mcp/Tools/ImportJobsListTool.cs \
        src/PPDS.Mcp/Tools/ImportJobsGetTool.cs
git commit -m "feat(mcp): add ppds_import_jobs_list and ppds_import_jobs_get tools

MCP tools for listing import jobs with status/progress and retrieving
full import log XML for troubleshooting failed solution imports."
```

---

## Chunk 5: Tests + Skill Update

### Task 24: Add extension unit tests

**Files:**
- Create: `src/PPDS.Extension/src/__tests__/panels/importJobsPanel.test.ts`

- [ ] **Step 1: Write unit tests for message type contracts**

Create `src/PPDS.Extension/src/__tests__/panels/importJobsPanel.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';

// Type-level tests to ensure message contracts are well-formed.
// These verify that the discriminated unions compile correctly
// and that both sides of the protocol are exhaustive.

import type {
    ImportJobsPanelWebviewToHost,
    ImportJobsPanelHostToWebview,
    ImportJobViewDto,
} from '../../panels/webview/shared/message-types.js';

describe('ImportJobsPanel message types', () => {
    it('WebviewToHost covers all commands', () => {
        // Type assertion: if this compiles, all variants are valid
        const messages: ImportJobsPanelWebviewToHost[] = [
            { command: 'ready' },
            { command: 'refresh' },
            { command: 'selectJob', id: 'test-id' },
            { command: 'requestEnvironmentList' },
            { command: 'openInMaker' },
            { command: 'copyToClipboard', text: 'test' },
            { command: 'webviewError', error: 'test', stack: 'trace' },
        ];
        expect(messages).toHaveLength(7);
    });

    it('HostToWebview covers all commands', () => {
        const messages: ImportJobsPanelHostToWebview[] = [
            { command: 'updateEnvironment', name: 'test', envType: null, envColor: null },
            { command: 'importJobsLoaded', jobs: [] },
            { command: 'importJobDetailLoaded', id: 'test', data: null },
            { command: 'loading' },
            { command: 'error', message: 'test' },
            { command: 'daemonReconnected' },
        ];
        expect(messages).toHaveLength(6);
    });

    it('ImportJobViewDto has all required fields', () => {
        const dto: ImportJobViewDto = {
            id: '00000000-0000-0000-0000-000000000001',
            solutionName: 'TestSolution',
            status: 'Succeeded',
            progress: 100,
            createdBy: 'admin@test.com',
            createdOn: '2026-03-16T00:00:00Z',
            startedOn: '2026-03-16T00:00:00Z',
            completedOn: '2026-03-16T00:01:00Z',
            duration: '1m 0s',
        };
        expect(dto.status).toBe('Succeeded');
        expect(dto.progress).toBe(100);
    });

    it('handles empty/null fields gracefully', () => {
        const dto: ImportJobViewDto = {
            id: '00000000-0000-0000-0000-000000000002',
            solutionName: null,
            status: 'In Progress',
            progress: 45,
            createdBy: null,
            createdOn: null,
            startedOn: null,
            completedOn: null,
            duration: null,
        };
        expect(dto.solutionName).toBeNull();
        expect(dto.status).toBe('In Progress');
    });
});
```

- [ ] **Step 2: Run tests**

Run: `cd src/PPDS.Extension && npm run test`
Expected: All tests pass including new ones.

### Task 25: Update @webview-panels skill

**Files:**
- Modify: `.claude/skills/webview-panels/SKILL.md`

- [ ] **Step 1: Add Import Jobs Panel as a reference implementation**

In the "Reference Implementations" section, add:

```markdown
- ImportJobsPanel: `src/panels/ImportJobsPanel.ts` + `src/panels/webview/import-jobs-panel.ts` + `src/panels/styles/import-jobs-panel.css` — **data table panel pattern** (shared DataTable component, status badges, detail pane)
```

- [ ] **Step 2: Add data table pattern to the Design Guidance section**

In the "Reusable CSS Patterns" table, add a row:

```markdown
| Shared DataTable (sortable, status badges, row selection) | `shared/data-table.ts` `DataTable<T>` | ImportJobsPanel | All tabular data panels — shared component |
```

- [ ] **Step 3: Add detail pane pattern note**

In the Design Guidance section, after the keyboard shortcuts table, add:

```markdown
### Detail Pane Pattern

For panels that show detail when a row is selected (Import Jobs, Plugin Traces, Connection References), use the split layout:
- Table in `.content` with `flex: 1`
- Detail pane below with `max-height: 40vh` and `overflow: auto`
- Close button (×) and Escape key to dismiss
- `display: none` when no row selected
- Reference: ImportJobsPanel's `.detail-pane` in `import-jobs-panel.css`
```

### Task 26: Final quality gate + Commit

- [ ] **Step 1: Full quality gate**

Run all gates:
```bash
dotnet build PPDS.sln -v q
cd src/PPDS.Extension && npm run typecheck:all && npm run lint && npm run lint:css && npm run test
```
Expected: All pass.

- [ ] **Step 2: Commit tests and skill update**

```bash
git add src/PPDS.Extension/src/__tests__/panels/importJobsPanel.test.ts \
        .claude/skills/webview-panels/SKILL.md
git commit -m "test(ext): add Import Jobs panel message type tests

Also update @webview-panels skill with data table panel pattern
and detail pane pattern established by ImportJobsPanel (Rule 7)."
```

---

## Acceptance Criteria Mapping

| AC | Task(s) | Verification |
|----|---------|-------------|
| AC-IJ-01 | Tasks 1-4 (RPC endpoint) | `importJobs/list` returns jobs sorted by createdOn desc |
| AC-IJ-02 | Task 3 (`importJobs/get` + `GetDataAsync`) | Returns full detail with XML data |
| AC-IJ-03 | Tasks 8-11 (DataTable + panel + CSS) | Status badges: green/red/blue |
| AC-IJ-04 | Tasks 9, 13-14 (env picker + theming) | Environment picker + data-env-type |
| AC-IJ-05 | Tasks 9-10 (`selectJob` + detail pane) | Click row → XML log display |
| AC-IJ-06 | Task 18 (TUI screen) | Same columns in DataTable |
| AC-IJ-07 | Task 18 (CellActivated → Dialog) | Enter opens scrollable dialog |
| AC-IJ-08 | Task 21 (MCP list tool) | Structured JSON output |
| AC-IJ-09 | Task 22 (MCP get tool) | Full detail with XML |
| AC-IJ-10 | Tasks 10, 18, 21-22 | Empty state messages in all surfaces |

---

## Execution Notes

### Parallel opportunities
Tasks 18-20 (TUI) and Tasks 21-23 (MCP) are independent of Tasks 7-17 (VS Code panel). They can be parallelized as subagent work after Chunk 1 is committed.

### TUI service access pattern
Task 17 uses `Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation)` to get a DI-resolved service provider, then resolves `IImportJobService` via DI. This matches the `SqlQueryScreen.cs` pattern. Note: `EnvironmentUrl` must not be null when calling this — the screen captures it from the session at construction time.

### Single code path for Status + Duration
Status and FormattedDuration are computed properties on the `ImportJobInfo` record (Constitution A2). All surfaces — RPC, TUI, MCP — read `job.Status` and `job.FormattedDuration` directly. No duplication.
