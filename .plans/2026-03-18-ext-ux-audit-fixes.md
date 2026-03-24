# Extension UX Audit Fixes

**Date:** 2026-03-18
**Branch:** `feature/ext-ux-audit`
**Scope:** 28 findings from side-by-side comparison of legacy v0.3.4 vs new extension

## Findings Reference

Each task references an audit finding number (#N) from the comparison session.

## Phase 1: Cross-Cutting Infrastructure

Independent of any single panel. Must complete before phases 2–9.

### Task 1.1: Add Connection References + Environment Variables to sidebar (#56, #57)

**File:** `src/PPDS.Extension/src/views/ToolsTreeView.ts`

The static tools list has 8 items but is missing Connection References and Environment Variables.
Add them between Import Jobs and Plugin Traces to match logical grouping.

### Task 1.2: Fix "Open" prefix inconsistency in commands (#51)

**File:** `src/PPDS.Extension/package.json`, relevant panel registration files

Current state: Some panels use "PPDS: Open X" (Import Jobs, Web Resources, Connection Refs, Env Variables, Metadata Browser, Solutions) while others use "PPDS: X" (Data Explorer, Plugin Traces).

Decision: Standardize on "PPDS: Open X" for all panel-opening commands. Add aliases without "Open" for backward compat if needed. Ensure command palette titles are consistent.

### Task 1.3: Align column header casing (#10)

**Files:** All `src/PPDS.Extension/src/panels/webview/*-panel.ts` and `src/PPDS.Extension/src/panels/styles/*-panel.css`

Current: Some panels use UPPERCASE headers ("SOLUTION", "STATUS"), others use Title Case. The shared `data-table.ts` component may control this. Standardize on **Title Case** to match VS Code conventions and the legacy extension.

If casing comes from CSS `text-transform: uppercase`, remove it. If hardcoded in HTML, change to Title Case.

### Task 1.4: Align "Maker Portal" button naming (#22)

**Files:** All panel webview TS files that have maker buttons

Legacy used "Open in Maker". New uses "Maker Portal" in some panels but the per-row button in Solutions uses a 🔗 icon. Standardize: toolbar buttons say "Maker Portal", per-row icons keep 🔗 with title="Open in Maker Portal".

---

## Phase 2: Solutions Panel (#1, #2, #3, #5)

### Task 2.1: Show all solutions with managed toggle (#1)

**Files:**
- `src/PPDS.Extension/src/panels/SolutionsPanel.ts`
- `src/PPDS.Extension/src/panels/webview/solutions-panel.ts`

The RPC `solutions/list` already supports `includeManaged: true`. Currently the panel calls it without this flag, so only unmanaged solutions (8) appear.

Fix: Add a toggle/checkbox "Include Managed" (default: true to match legacy's 190-record view). Pass `includeManaged` to the RPC call. Persist the toggle state via `globalState`.

### Task 2.2: Add sortable columns (#2, #5)

**Files:**
- `src/PPDS.Extension/src/panels/webview/solutions-panel.ts`
- `src/PPDS.Extension/src/panels/styles/solutions-panel.css`

Current: Solutions uses a tree/list view with expandable items. Legacy had a 9-column sortable table.

The existing expandable tree view is a net improvement over legacy (component drill-down). Don't regress to a flat table. Instead:

1. Add a **table view toggle** (list view vs table view) or convert the list to include sortable column headers above the list items.
2. At minimum, add the missing data columns to each solution row: **Visible**, **API Managed**, **Installed On**, **Modified On** — these should appear in the expandable detail card which already shows Unique Name, Publisher, Type, Installed, Modified.
3. Add client-side sort controls (by name, version, publisher, modified date).

### Task 2.3: Add toolbar "Maker Portal" button (#3)

**File:** `src/PPDS.Extension/src/panels/webview/solutions-panel.ts`

Per-row 🔗 buttons exist but there's no toolbar-level "Maker Portal" button. Add one that opens the Solutions list in Maker Portal (uses `buildMakerUrl(envId, '/solutions')`).

---

## Phase 3: Import Jobs Panel (#7, #8, #9)

### Task 3.1: Add search bar (#55 — Import Jobs)

**File:** `src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts`

Add a search/filter input using the shared `filter-bar.ts` pattern or inline search input. Filter on solution name client-side.

### Task 3.2: Add Operation Context column (#8)

**Files:**
- `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — add `operationContext` to `ImportJobInfoDto`
- `src/PPDS.Dataverse/Services/IImportJobService.cs` or its implementation — include `operationcontext` in the query
- `src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts` — add column to table

The `importjob` entity has `operationcontext` field (verified in generated entity). The RPC DTO just doesn't include it. Add it end-to-end.

### Task 3.3: Add record count (#9)

**File:** `src/PPDS.Extension/src/panels/webview/import-jobs-panel.ts`

Add footer status showing record count (e.g., "227 import jobs"). Follow the pattern used by other panels.

---

## Phase 4: Plugin Traces Panel — CRITICAL (#11, #12, #17, #18)

### Task 4.1: Fix Trace Level button — CRITICAL (#11)

**Files:**
- `src/PPDS.Extension/src/panels/PluginTracesPanel.ts` — already has `pluginTracesTraceLevel()` and `pluginTracesSetTraceLevel()` methods
- `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts` — the button click handler needs to show current level and allow changing

The daemon client already has both `pluginTracesTraceLevel` and `pluginTracesSetTraceLevel` methods. The PluginTracesPanel.ts backend already has handler methods. The issue is in the **webview JS** — the button exists but doesn't properly:
1. Fetch and display the current trace level on load
2. Show a dropdown/picker to change the level when clicked

Fix: When the button is clicked, show a dropdown (or VS Code quick pick via postMessage) with options: Off, Exception, All. Display current level on the button text: "Trace Level: All". Update `trace-level-indicator` span.

### Task 4.2: Fix Maker Portal URL (#12)

**File:** `src/PPDS.Extension/src/panels/PluginTracesPanel.ts:163`

Current: `buildMakerUrl(this.environmentId, '/plugintraceloglist')` — this path doesn't exist in Maker Portal.

The correct approach: Plugin Trace Logs don't have a direct Maker Portal URL. Change to open the Dynamics 365 classic URL: `https://{org}.crm.dynamics.com/main.aspx?pagetype=entitylist&etn=plugintracelog`. This requires the environment URL (which we have) rather than the environment ID.

Alternatively, if the Maker Portal does support a traces path, use it. Research needed — fallback to the Dynamics 365 URL.

### Task 4.3: Add record count (#17)

**File:** `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts`

Add footer with trace count (e.g., "100 traces").

### Task 4.4: Add text labels to status indicators (#18)

**File:** `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts`

Current: Green/red dots only. Add "Success" / "Exception" text next to the dot for accessibility and clarity.

---

## Phase 5: Web Resources Panel (#19)

### Task 5.1: Restore Created On / Created By columns (#19)

**File:** `src/PPDS.Extension/src/panels/webview/web-resources-panel.ts`

The RPC `webResources/list` already returns `createdByName` and `createdOn` in the DTO. The webview just doesn't render columns for them. Add "Created On" and "Created By" columns to the table.

---

## Phase 6: Connection References Panel (#23, #24, #25, #26, #28, #55)

### Task 6.1: Add expandable flow/connection detail (#23)

**Files:**
- `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts`
- `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts`
- `src/PPDS.Extension/src/panels/styles/connection-references-panel.css`

The `connectionReferences/get` RPC already returns `flows: FlowInfoDto[]` per CR. When a user clicks/expands a CR row, call `connectionReferences/get` for that CR and display:
- Connection status with bound connection name
- List of flows using this CR
- Connector display name

Follow the same expandable pattern used by Solutions panel (chevron toggle, detail card).

### Task 6.2: Fix raw ISO timestamp formatting (#24)

**File:** `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts`

Current: Displays raw `2026-03-08T19:35:34.0000000Z`. Use the shared `dom-utils.ts` date formatting function (or `new Date(iso).toLocaleString()`) to display human-readable dates.

### Task 6.3: Add "Sync Deployment Settings" button (#25)

**Files:**
- `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — add `deploymentSettings/sync` RPC endpoint
- `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts` — handle the sync command
- `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts` — add toolbar button

The `IDeploymentSettingsService` and `SyncCommand` already exist in the CLI. Wire up an RPC endpoint that calls the same service, then add a toolbar button.

### Task 6.4: Add search bar (#55 — Connection Refs)

**File:** `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts`

Add search/filter input that filters on display name, logical name, and connector name.

### Task 6.5: Fix Status "N/A" display (#28)

**File:** `src/PPDS.Extension/src/panels/webview/connection-references-panel.ts`

Current: Shows "N/A" badge without context. When connection status is unknown (SPN can't access Power Apps API), display "Unknown (limited access)" or a tooltip explaining why. When unbound, show "Unbound" with warning styling.

---

## Phase 7: Environment Variables Panel (#29, #30, #31, #55)

### Task 7.1: Add "Sync Deployment Settings" button (#29)

Same RPC endpoint as Task 6.3 (shared service). Add toolbar button to Environment Variables panel.

**File:** `src/PPDS.Extension/src/panels/webview/environment-variables-panel.ts`

### Task 7.2: Restore Modified On column (#31)

**File:** `src/PPDS.Extension/src/panels/webview/environment-variables-panel.ts`

The RPC returns `modifiedOn` in the DTO. Add the column to the table.

### Task 7.3: Add search bar (#55 — Env Variables)

**File:** `src/PPDS.Extension/src/panels/webview/environment-variables-panel.ts`

Add search/filter input that filters on schema name, display name, and current value.

---

## Phase 8: Metadata Browser Panel (#37)

### Task 8.1: Add "Hide metadata" filter (#37)

**File:** `src/PPDS.Extension/src/panels/webview/metadata-browser-panel.ts`

Legacy had a "Hide metadata" checkbox that filtered out system/internal attributes (those with `isCustom=false` or similar). Add a toggle checkbox in the toolbar. Default to showing all (matching current behavior), but allow users to hide system metadata.

---

## Phase 9: Data Explorer Panel (#41, #42)

### Task 9.1: Add Clear button (#41)

**File:** `src/PPDS.Extension/src/panels/webview/query-panel.ts`

Add a "Clear" button to the toolbar that clears the editor content and results area.

### Task 9.2: Add Import button (#42)

**Files:**
- `src/PPDS.Extension/src/panels/QueryPanel.ts`
- `src/PPDS.Extension/src/panels/webview/query-panel.ts`

Add an "Import" button/menu that allows importing a FetchXML file from disk into the editor. Use `vscode.window.showOpenDialog` via postMessage to the panel host.

---

## Phase 10: Plugin Traces Search Bar (#55 — Plugin Traces)

### Task 10.1: Add text search to Plugin Traces

**File:** `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts`

The panel already has filter fields (Entity, Message, Plugin, Mode) and quick-filter pills. Add a general text search input that filters the loaded traces client-side across all visible columns (plugin name, entity, message, etc.).

---

## Execution Strategy

- **Phase 1** first (cross-cutting) — commit when done
- **Phases 2–10** are independent per-panel — can be parallelized via agents
- Each phase gets its own commit
- After all phases: gates → verify → QA → review → PR

## Files Changed Summary

| Layer | Files |
|-------|-------|
| Sidebar | `ToolsTreeView.ts` |
| Commands | `package.json` |
| Panel hosts | `SolutionsPanel.ts`, `PluginTracesPanel.ts`, `ConnectionReferencesPanel.ts`, `QueryPanel.ts` |
| Webviews | All 8 `*-panel.ts` files in `panels/webview/` |
| Styles | Potentially all 8 CSS files |
| RPC | `RpcMethodHandler.cs` (add operationContext, deploymentSettings/sync) |
| Services | Import job DTO, deployment settings wiring |
| Shared | `data-table.ts` or `dom-utils.ts` if header casing is centralized |
