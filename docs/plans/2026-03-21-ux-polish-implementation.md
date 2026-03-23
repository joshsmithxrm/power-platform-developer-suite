# UX Polish — Implementation Plan

**Date:** 2026-03-21
**Findings:** [2026-03-18-ux-polish-findings.md](./2026-03-18-ux-polish-findings.md)
**Branch:** `feature/ux-polish`
**Constitution amendment:** I4 (data transparency) added to `specs/CONSTITUTION.md`

---

## Dependency Graph

```
Step 1: ListResult<T> + service signatures ─────────────────────────────┐
         │                                                               │
Step 2: DataTable shared upgrades ──────────────────────────────────────┤
         │ (virtual scroll, cell selection, sort, tooltips, striping,   │
         │  search, header styling)                                     │
         │                                                               │
Step 3: Quick wins (one-liners across all panels)                       │
         │                                                               │
    ┌────┴────┬────────┬────────┬────────┬────────┬────────┬────────┐   │
    v         v        v        v        v        v        v        v   │
Step 4:   Step 5:  Step 6:  Step 7:  Step 8:  Step 9:  Step 10: Step 11:
Solutions ImportJobs PluginTr DataExpl MetaBrw  ConnRef  EnvVars  WebRes │
                                                                        │
Step 12: I4 compliance pass (all services, RPC, MCP) ──────────────────┘
         │
Step 13: Panel title enforcement
         │
Step 14: Cross-panel visual QA
```

---

## Step 1: `ListResult<T>` — Service Return Type Foundation

**Files:** `src/PPDS.Dataverse/`
**Why first:** Every subsequent step depends on services returning total counts. Without this, no UI can show "X of Y."

### 1a. Create `ListResult<T>`

**File:** `src/PPDS.Dataverse/Models/ListResult.cs` (new)

```csharp
public sealed class ListResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public int TotalCount { get; init; }
    public bool WasTruncated { get; init; }
    public IReadOnlyList<string> FiltersApplied { get; init; } = [];
}
```

### 1b. Update all 9 service interfaces + implementations

Change every `ListAsync` from `Task<List<TInfo>>` to `Task<ListResult<TInfo>>`:

| Service | Current Return | New Return | Notes |
|---------|---------------|------------|-------|
| ImportJobService | `List<ImportJobInfo>` | `ListResult<ImportJobInfo>` | Remove `top=50` default, add paging |
| WebResourceService | `List<WebResourceInfo>` | `ListResult<WebResourceInfo>` | Remove `top=5000`, add progressive paging |
| SolutionService | `List<SolutionInfo>` | `ListResult<SolutionInfo>` | Add `includeInternal` param |
| PluginTraceService | `List<PluginTraceInfo>` | `ListResult<PluginTraceInfo>` | Wire existing `CountAsync` into result |
| ConnectionReferenceService | `List<ConnectionReferenceInfo>` | `ListResult<ConnectionReferenceInfo>` | Add `includeInactive` param |
| EnvironmentVariableService | `List<EnvironmentVariableInfo>` | `ListResult<EnvironmentVariableInfo>` | Add `includeInactive` param |
| UserService | `List<UserInfo>` | `ListResult<UserInfo>` | Remove `top=100` default, add paging |
| FlowService | `List<FlowInfo>` | `ListResult<FlowInfo>` | Add `includeClassic` param |
| RoleService | `List<RoleInfo>` | `ListResult<RoleInfo>` | Document "root roles only" in FiltersApplied |

### 1c. Add paging cookie support to services that need it

Apply the `QueryExecutor.cs` progressive loading pattern (paging cookie + while loop) to:
- ImportJobService (currently single call, `top=50`)
- WebResourceService (currently single call, `top=5000`)
- UserService (currently single call, `top=100`)
- PluginTraceService (keep `top` param for UI pagination, but return `TotalCount`)

### 1d. Add `includeIntersect` to MetadataService

**File:** `src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs:61`

Add parameter `bool includeIntersect = false`. When false, filter `IsIntersect != true` as today but return the count of filtered entities.

### 1e. Update all RPC handlers

Each handler in `RpcMethodHandler.cs` that calls a `ListAsync` must:
- Accept the new `ListResult<T>` return
- Pass `totalCount` through to the extension in the response DTO
- Pass any new `include*` params through from RPC arguments

### 1f. Update all MCP tools

Each MCP tool that calls a `ListAsync` must:
- Return `totalCount` alongside `count` in the response
- Align defaults with service defaults (fix V-12 MCP web_resources `top=100` vs service `top=5000`)

**Commit after step 1.**

---

## Step 2: DataTable Shared Component Upgrades

**Files:** `src/PPDS.Extension/src/panels/webview/shared/data-table.ts`, `src/PPDS.Extension/src/panels/styles/shared.css`
**Why second:** Every panel consumes DataTable. Upgrading it once fixes CC-01 through CC-09 across all panels.

### 2a. Virtual scrolling (CC-02)

Add virtual scroll to DataTable: render only visible rows (~60) plus buffer. Track scroll position, calculate visible range, render window. Recycle DOM nodes on scroll. This is the highest-complexity item — budget accordingly.

### 2b. Cell selection + TSV copy (CC-03)

Extract selection logic from `query-panel.ts` into a `SelectionManager` class in `shared/`:
- Add `data-row`/`data-col` attributes to cells
- Mouse click, drag, Shift+click range selection
- Ctrl+A select all visible rows
- Ctrl+C copy as TSV (tab-separated, pasteable into Excel)
- Abstract `getCopyValue(row, col)` so panels can customize

### 2c. Sort with `sortValue` and direction toggle (CC-01)

Add `sortValue` option to column definitions:
```typescript
interface DataTableColumn {
    key: string;
    label: string;
    sortValue?: (item: T) => string | number; // raw value for sorting
    render?: (item: T) => string; // display value
}
```
- Click header to sort ascending, click again for descending, click again to clear
- Sort indicator arrows in header
- Date columns use ISO timestamp as sortValue, display formatted string

### 2d. Row striping (CC-07)

Add `tr:nth-child(even)` background using `--vscode-list-hoverBackground` at reduced opacity.

### 2e. Cell tooltips (CC-08)

In `render()`, add `title` attribute to every `<td>` with the cell's text content.

### 2f. Header styling (CC-06)

Update shared.css:
- Font: 12px (from 11px)
- Color: `--vscode-foreground` (from `--vscode-descriptionForeground`)
- Border-bottom: 2px `--vscode-panel-border`
- Column separators: 1px right border on each th

### 2g. Search all cell text (CC-09)

Change filter logic from per-field matching to `row.textContent.toLowerCase().includes(term)`.

### 2h. "X of Y" status bar pattern

Add a shared utility that panels call to format status text:
```typescript
function formatStatusCount(filtered: number, total: number, noun: string, filters?: string[]): string
// "50 import jobs" when no filter
// "12 of 50 import jobs (filtered)" when search active
// "50 of 251 import jobs (showing first 50)" when truncated
```

**Commit after step 2.**

---

## Step 3: Quick Wins (One-Liners)

**Why here:** These are trivial changes that improve every panel immediately. Do them in one pass.

| Fix | Where | Change |
|-----|-------|--------|
| CC-04: `retainContextWhenHidden: true` | All 8 panel host files, `createWebviewPanel()` call | Add option |
| CC-05: `enableFindWidget: true` | All 8 panel host files, `createWebviewPanel()` call | Add option |
| CC-11: `box-sizing: border-box` | `shared.css` | `* { box-sizing: border-box; }` |
| CC-12: `user-select: none` on body | `shared.css` | `body { user-select: none; }` with whitelist |
| CC-13: Button loading states | All panels with Refresh button | Disable buttons during load |
| WR-10: Search CSS class bug | `web-resources-panel.ts` | Change `search-input` to `toolbar-search` |
| EV-41: Filter count not appended | `environment-variables-panel.ts` | Append element to DOM |

**Commit after step 3.**

---

## Step 4: Solutions Panel

| Finding | Fix |
|---------|-----|
| S-02 | Display name first, schema name in parentheses |
| S-03 | Add secondary text line: `Installed: date · Modified: date · Visible · API Managed` |
| S-04/S-31 | Sort all columns, toggle direction (handled by DataTable step 2c) |
| S-05/V-04 | Add `Visible \| All` segmented control in toolbar. Wire to `includeInternal` param from step 1 |
| S-09 | Use `toLocaleString()` for dates (include time) |
| S-10 | Add `createdOn` to view |
| S-11 | Show "managed" or "unmanaged" badge on all solutions |

**Commit after step 4.**

---

## Step 5: Import Jobs Panel

| Finding | Fix |
|---------|-----|
| IJ-01 | Make solution name clickable → opens import log in editor |
| IJ-02/IJ-43 | Remove inline detail pane. Row click opens prettified, syntax-highlighted XML in VS Code editor tab |
| IJ-15 | Add status badge styles for Cancelled, Queued, Completed with Errors |
| IJ-45 | Verify daemon return value for status string, align badge + footer count |
| P2-IJ-01 | Load all import jobs (paging from step 1c), show "X of Y" in status bar |

**Commit after step 5.**

---

## Step 6: Plugin Traces Panel

This is the largest panel effort. Split into sub-steps.

### 6a. Detail split — side-by-side

Change detail panel from bottom to side-by-side (like Chrome DevTools Network tab). Add resize handle, persist split ratio to `globalState`.

### 6b. Add all missing detail fields

Add to detail tabs: Stage (PT-01), Constructor Start Time (PT-02), IsSystemCreated (PT-03), Created By (PT-04), Created On Behalf By (PT-05), Plugin Step ID (PT-06), Persistence Key (PT-07), Organization ID (PT-08), Profile (PT-09). Enumerate every field the SDK returns — no raw data tab needed.

### 6c. Add Overview tab (PT-11)

Default landing tab combining: status, exception text, message block in one scroll. Keep individual tabs for deep dives.

### 6d. Add missing table columns (PT-14, PT-16)

Add Operation Type and Correlation ID columns.

### 6e. Timeline overhaul (PT-25, PT-26)

Full rebuild of timeline visualization:
- Hierarchical grouping by correlation ID
- Duration bars with shimmer animation + gradient fills (green success / red exception)
- Depth-based left-border colors (5 levels: blue/purple/teal/gold/gray)
- Click navigation: click bar → select + scroll to trace
- Legend with Success/Exception indicators
- Header showing total duration + trace count
- Hover: scaleY(1.1) + shadow
- Empty state: "No timeline data" + correlation ID hint

### 6f. Advanced query builder (PT-13)

Build the full three-tab filter panel:
- **Quick Filters tab:** 8 presets (Exceptions, Success Only, Last Hour, Last 24 Hours, Today, Async Only, Sync Only, Recursive Calls) with auto-apply
- **Advanced tab:** Condition builder with field dropdown, type-aware operator dropdown, value input. AND/OR logical grouping, enable/disable per condition, add/remove rows. Fields: Plugin Name, Entity, Message, Status, Created On, Duration, Mode, Stage
- **Query Preview tab:** Show generated filter conditions in readable format (SDK query representation, not OData)
- Collapsible + resizable panel (120px min, 60vh max), height persisted
- Filter count in header: "Filters (X / Y)" active vs total

### 6g. Fix remaining items

- PT-43: `data-selection-zone` for Ctrl+A in exception block
- PT-49: `stopPropagation` on filter inputs for keyboard shortcuts
- PT-59: Add visible X close button on detail pane
- PT-20: Persist detail panel width to globalState
- V-21: Show "X of Y traces" when search is active

**Commit after each sub-step (6a through 6g).**

---

## Step 7: Data Explorer Panel

| Finding | Fix |
|---------|-----|
| DE-06 | Show inline transpilation warnings when SQL→FetchXML conversion loses information |
| DE-15 | Add hover copy button on record links (more discoverable than right-click only) |
| DE-31 | Add Top N / DISTINCT checkboxes to overflow menu |

**Commit after step 7.**

---

## Step 8: Metadata Browser Panel

### 8a. Global Choices tree section (MB-01)

Add "CHOICES (N)" as peer of "ENTITIES (N)" in the left tree. Global option sets listed independently, not nested under entities. Use 🔽 icon (matching legacy).

### 8b. Collapsible tree headers with counts (MB-02)

Add count badges and collapse/expand to section headers: "ENTITIES (789)" / "CHOICES (56)".

### 8c. Three relationship tabs (MB-03)

Replace single combined Relationships tab with:
- **1:N tab:** Schema Name, Related Entity, Referencing Attribute, Cascade Delete
- **N:1 tab:** Schema Name, Referenced Entity, Referencing Attribute, Cascade Delete
- **N:N tab:** Schema Name, Related Entity, Intersect Entity, Entity1 Attribute, Entity2 Attribute

### 8d. Properties detail panel (MB-05)

Comprehensive property enumeration when clicking an attribute: every field the SDK exposes for each attribute type (including Precision, MinValue, MaxValue, lookup Targets — MB-32/33).

### 8e. Per-tab search (MB-04)

Add search input within each tab (Attributes, Keys, each Relationship tab, Privileges, Choices).

### 8f. Fix remaining items

- MB-06: Entity selection header/breadcrumb ("Account — account" above tabs)
- MB-07: Match legacy emojis (🏷️ custom, 📋 system, 🔽 option sets)
- MB-09: Relationship navigation links (click related entity → navigate)
- MB-10: Display name first in attribute table (swap column order)
- MB-13: Add Intersect Entity column back for N:N (covered by 8c)
- MB-22: Right-click "Copy Schema Name"
- V-03: Add `includeIntersect` toggle, show "X of Y (N intersection hidden)"

**Commit after each sub-step (8a through 8f).**

---

## Step 9: Connection References Panel

| Finding | Fix |
|---------|-----|
| CR-01/CR-02 | Add flow count/names column at table level + relationship-aware status |
| CR-03/CR-30 | Per-flow Maker deep link (`make.powerautomate.com/environments/{id}/flows/{flowId}/details`) |
| CR-05 | Status reflects relationship health (orphaned flows, missing CRs) |
| CR-09 | Default to Default Solution (`fd140aaf-4df4-11dd-bd17-0019b9312238`), persist per panel per env |
| CR-11 | AbortController to cancel requests on solution change |
| CR-17 | Search placeholder: "Search connection references..." |
| V-16 | Add `includeInactive` param + toggle, show count |

**Commit after step 9.**

---

## Step 10: Environment Variables Panel

| Finding | Fix |
|---------|-----|
| EV-09/EV-10/EV-37 | Wire Sync Deployment Settings — add RPC endpoint for `DeploymentSettingsService.SyncAsync`, wire UI button, use correct schema format |
| EV-22 | Use `toLocaleString()` for dates (include time) |
| V-17 | Add `includeInactive` param + toggle, show count |

**Commit after step 10.**

---

## Step 11: Web Resources Panel

| Finding | Fix |
|---------|-----|
| WR-03 | Server-side search fallback (when client search misses data beyond loaded set) |
| WR-04 | Progressive background loading (show first page fast, continue loading in background) |
| WR-20 | "Publish now?" prompt after save |
| WR-23 | Cache data by solution ID, invalidate on refresh |
| P2-WR-01 | Virtual scroll (handled by step 2a) + progressive loading addresses 60K DOM issue |

**Commit after step 11.**

---

## Step 12: I4 Compliance Pass

Sweep all services, RPC handlers, and MCP tools to verify:

| Check | Verification |
|-------|-------------|
| Every `ListAsync` returns `ListResult<T>` | Compile check — bare `List<T>` returns won't compile |
| Every panel status bar shows "X of Y" when filtered/truncated | Visual inspection per panel |
| Every silent filter has an `include*` param or documented exclusion | Code review of all `Where()` and `ConditionExpression` |
| V-15: FlowService `includeClassic` | Add param, show "cloud flows only" in status |
| V-18: DeploymentSettings secrets | Show "N secrets excluded" in output |
| V-19: RoleService root roles | Show "root roles only" in status (low priority) |
| MCP tools return `totalCount` | V-12, V-13, V-14 |

**Commit after step 12.**

---

## Step 13: Panel Title Enforcement (CC-14)

1. Delete `QueryPanel.updateTitle()` — use inherited `WebviewPanelBase.updatePanelTitle()`
2. Verify all 8 panels produce format: `profileName · envName — PanelLabel`
3. Add comment on `updatePanelTitle()`: "Single source of truth for panel titles — do not override"

**Commit after step 13.**

---

## Step 14: Cross-Panel Visual QA

Final pass using CDP tooling:
1. Launch new extension with `--build`
2. Open each panel, screenshot
3. Verify: header styling consistent, row striping consistent, status bar format consistent, sort indicators consistent, search behavior consistent, "X of Y" visible when filtered
4. Verify: date formats include time, tooltips appear on hover, Ctrl+A/C works, Ctrl+F opens find widget
5. Document any remaining issues

**No commit — this is verification only.**

---

## Estimated Commit Count

| Step | Commits | Scope |
|------|---------|-------|
| 1 | 1 | Service return types + paging |
| 2 | 1 | DataTable shared upgrades |
| 3 | 1 | Quick wins |
| 4 | 1 | Solutions panel |
| 5 | 1 | Import Jobs panel |
| 6 | 7 | Plugin Traces (6a–6g) |
| 7 | 1 | Data Explorer |
| 8 | 6 | Metadata Browser (8a–8f) |
| 9 | 1 | Connection References |
| 10 | 1 | Environment Variables |
| 11 | 1 | Web Resources |
| 12 | 1 | I4 compliance |
| 13 | 1 | Panel title enforcement |
| **Total** | **~24** | |

---

## Risk Notes

- **Step 1 is a breaking change** across all callers. Every RPC handler, CLI command, MCP tool, and TUI screen that calls `ListAsync` must be updated. Do it all at once to avoid partial states.
- **Step 2 virtual scroll** is highest complexity. If it takes too long, consider simple pagination (page 1 of N with next/prev) as a fallback that still solves the DOM problem.
- **Step 6 Plugin Traces** is the largest per-panel effort (~7 sub-steps). The timeline overhaul and query builder are independent and can be parallelized.
- **Step 8 Metadata Browser** has many sub-steps but each is self-contained.
