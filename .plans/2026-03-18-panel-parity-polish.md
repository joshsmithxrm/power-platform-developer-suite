# Panel Parity Polish — Implementation Plan

**Spec:** [specs/panel-parity.md](../../specs/panel-parity.md) (v2.0, Phase 3)
**Branch:** `feature/panel-parity-polish`
**ACs:** 25 remaining (AC-CC-01–06, AC-IJ-11, AC-CR-05/10/11, AC-EV-04/11/12, AC-PT-16–20, AC-MB-11/12, AC-WR-19–23)
**Stale issues to close:** #585–591, #593, #595, #597, #626 (11 total)

---

## Dependency Graph

```
Step 1: buildMakerUrl(envId, path?)
         │
Step 2: WebviewPanelBase extraction ──────────────────┐
         │                                             │
    ┌────┴────┬────────┬────────┬────────┬────────┐   │
    v         v        v        v        v        v   │
Step 3:   Step 4:  Step 5:  Step 6:  Step 7:  Step 8: │
ImportJobs ConnRef  EnvVar   Traces  MetaBrw  WebRes  │
(IJ-11)   (CR-05,  (EV-04,  (PT-16  (MB-11,  (WR-19 │
           CR-10)   EV-11)   –20)    MB-12)   –23)   │
                                                      │
Step 9: Unit tests (CR-11, EV-12) ────────────────────┘
         │
Step 10: Close stale issues
```

---

## Step 1: Extend `buildMakerUrl()` — AC prerequisite

**File:** `src/PPDS.Extension/src/commands/browserCommands.ts`

Add optional `path` parameter:

```typescript
export function buildMakerUrl(environmentId: string | null, path?: string): string {
    if (environmentId) {
        return `${MAKER_BASE_URL}/environments/${environmentId}${path ?? '/solutions'}`;
    }
    return MAKER_BASE_URL;
}
```

Existing callers pass no `path` → default `/solutions` → no regression.

**Commit after step 1.**

---

## Step 2: Base class extraction — AC-CC-01, CC-02, CC-03, CC-04, CC-05, CC-06

**File:** `src/PPDS.Extension/src/panels/WebviewPanelBase.ts`

### 2a. Add environment state properties to base class

Move these from all 6 panels into the base:

```typescript
protected environmentUrl: string | undefined;
protected environmentDisplayName: string | undefined;
protected environmentType: string | null = null;
protected environmentColor: string | null = null;
protected environmentId: string | null = null;
protected profileName: string | undefined;
```

### 2b. Add `resolveEnvironmentId()` — AC-CC-03

Extract from `ImportJobsPanel.ts:167-180` (identical in 5 panels). Maps environment URL to GUID via `daemon.envList()`.

### 2c. Add `updatePanelTitle()` — AC-CC-04

Extract from `ImportJobsPanel.ts:182-187`. Uses `panelId`, `profileName`, `environmentDisplayName`.

Requires adding abstract property: `protected abstract readonly panelId: string;`

### 2d. Add `initializePanel()` template method — AC-CC-01

Extract the common initialization flow from `ImportJobsPanel.ts:117-146`:

```typescript
protected async initializePanel(daemon: DaemonClient): Promise<void> {
    const who = await daemon.authWho();
    this.profileName = who.name ?? `Profile ${who.index}`;
    if (!this.environmentUrl && who.environment?.url) {
        this.environmentUrl = who.environment.url;
        this.environmentDisplayName = who.environment.displayName ?? who.environment.url;
        this.environmentType = who.environment.type ?? null;
    }
    this.environmentId = await this.resolveEnvironmentId(daemon);
    const config = await daemon.envConfigGet(this.environmentUrl);
    this.environmentColor = config?.color ?? null;
    if (!this.environmentType && config?.resolvedType) {       // AC-CC-05
        this.environmentType = config.resolvedType;
    }
    this.updatePanelTitle();
    this.postMessage({
        command: 'updateEnvironment',
        name: this.environmentDisplayName ?? this.environmentUrl ?? '',
        envType: this.environmentType,
        envColor: this.environmentColor,
    });
    await this.onInitialized(daemon);   // hook for panel-specific work
}
```

Add abstract hook: `protected abstract onInitialized(daemon: DaemonClient): Promise<void>;`

Each panel implements `onInitialized()` with its data-loading call(s). WebResourcesPanel adds FSP registration + `loadSolutionList()` here.

### 2e. Add `handleEnvironmentPickerClick()` — AC-CC-02

Extract common env picker flow:

```typescript
protected async handleEnvironmentPickerClick(daemon: DaemonClient): Promise<void> {
    const result = await showEnvironmentPicker(daemon, this.environmentUrl);
    if (!result) return;
    this.environmentUrl = result.url;
    this.environmentDisplayName = result.displayName;
    this.environmentType = result.type;
    this.environmentId = await this.resolveEnvironmentId(daemon);
    const config = await daemon.envConfigGet(this.environmentUrl);
    this.environmentColor = config?.color ?? null;
    if (!this.environmentType && config?.resolvedType) {       // AC-CC-05
        this.environmentType = config.resolvedType;
    }
    this.updatePanelTitle();
    this.postMessage({
        command: 'updateEnvironment',
        name: this.environmentDisplayName ?? this.environmentUrl ?? '',
        envType: this.environmentType,
        envColor: this.environmentColor,
    });
    await this.onEnvironmentChanged(daemon);   // hook for panel-specific work
}
```

Add abstract hook: `protected abstract onEnvironmentChanged(daemon: DaemonClient): Promise<void>;`

Each panel implements `onEnvironmentChanged()` to reload data. WebResourcesPanel adds FSP re-registration + solution filter reset + `loadSolutionList()`.

### 2f. Add `copyToClipboard` to base message handling — AC-CC-06

Add to base class:

```typescript
protected handleCopyToClipboard(text: string): void {
    vscode.env.clipboard.writeText(text);
}
```

Wire in each panel's `handleMessage()` to delegate `copyToClipboard` to base. WebResourcesPanel currently missing this handler — it gets added automatically.

### 2g. Update all 6 panels

For each panel:
1. Remove local `initialize()` → implement `onInitialized()`
2. Remove local environment picker handler → call `handleEnvironmentPickerClick()`
3. Remove local `resolveEnvironmentId()` → inherited
4. Remove local `updatePanelTitle()` → inherited
5. Remove local environment state properties → inherited
6. Add `readonly panelId` property
7. Ensure `copyToClipboard` case exists in `handleMessage()` (delegates to base)

**WebResourcesPanel-specific:** `onInitialized()` and `onEnvironmentChanged()` include FSP registration and `loadSolutionList()` calls.

**PluginTracesPanel-specific:** Previously had no `resolveEnvironmentId()` — now inherits it from base. Add `environmentId` tracking (was missing).

**Commit after step 2.**

---

## Step 3: ImportJobsPanel — AC-IJ-11

**File:** `src/PPDS.Extension/src/panels/ImportJobsPanel.ts`

Replace inline URL at line ~235:
```typescript
// Before:
`https://make.powerapps.com/environments/${this.environmentId}/solutionsHistory`
// After:
buildMakerUrl(this.environmentId, '/solutionsHistory')
```

Add import: `import { buildMakerUrl } from '../commands/browserCommands.js';`

**Commit after step 3.**

---

## Step 4: ConnectionReferencesPanel — AC-CR-05, AC-CR-10

**File:** `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts`

**AC-CR-05:** Solution filter persistence. The host panel already sends solution list to webview, and the webview uses `SolutionFilter` component. Verify `storageKey` is set to `'connectionReferences.solutionFilter'` (distinct from EnvVars). If webview already uses `SolutionFilter` with `getState`/`setState`, persistence is handled. If not, wire it.

**AC-CR-10:** Replace inline URL at line ~311:
```typescript
buildMakerUrl(this.environmentId, '/connections')
```

**Commit after step 4.**

---

## Step 5: EnvironmentVariablesPanel — AC-EV-04, AC-EV-11

**File:** `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts`

**AC-EV-04:** Same as CR-05 — verify `SolutionFilter` uses distinct `storageKey` (`'environmentVariables.solutionFilter'`).

**AC-EV-11:** Replace inline URL at line ~346:
```typescript
buildMakerUrl(this.environmentId, '/solutions/environmentvariables')
```

**Commit after step 5.**

---

## Step 6: PluginTracesPanel — AC-PT-16, PT-17, PT-18, PT-19, PT-20

**Files:**
- `src/PPDS.Extension/src/panels/PluginTracesPanel.ts` (host)
- `src/PPDS.Extension/src/panels/webview/plugin-traces-panel.ts` (webview)
- `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` (types)
- `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs` (TUI)

### 6a. AC-PT-18: resolveEnvironmentId + openInMaker

Already handled by base class extraction (Step 2). Add `openInMaker` handler to `handleMessage()`:

```typescript
case 'openInMaker':
    const url = buildMakerUrl(this.environmentId, '/plugintraceloglist');
    vscode.env.openExternal(vscode.Uri.parse(url));
    break;
```

### 6b. AC-PT-20: buildMakerUrl

Covered by 6a — uses `buildMakerUrl()`.

### 6c. AC-PT-16: Export CSV/JSON

**Webview changes (`plugin-traces-panel.ts`):**
- Add export button to toolbar (copy dropdown pattern from `query-panel.ts:487-499`)
- Options: CSV, JSON, Clipboard
- Post `{ command: 'exportTraces', format: 'csv' | 'json' | 'clipboard' }`

**Host changes (`PluginTracesPanel.ts`):**
- Add `exportTraces` handler
- Get current filtered traces from last loaded data
- Format as CSV (headers + rows) or JSON (array)
- CSV: show save dialog with `.csv` filter
- JSON: show save dialog with `.json` filter
- Clipboard: copy to clipboard

**Message types:** Add `{ command: 'exportTraces'; format: string }` to webview-to-host union.

### 6d. AC-PT-17: Date range filter

**Webview changes (`plugin-traces-panel.ts`):**
- Add two `<input type="datetime-local">` fields to filter bar (after Mode dropdown)
- Labels: "From" and "To"
- Wire values into `collectFilter()` → `filter.startDate` / `filter.endDate`
- When "Last Hour" quick filter activates, update the From input value and clear To

**Host changes:** None — `TraceFilterViewDto` already has `startDate` and `endDate`, RPC handler already passes them through.

### 6e. AC-PT-19: TUI export hotkey

**File:** `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs`

- Register Ctrl+E hotkey
- Show export dialog: format selection (CSV/JSON), file path input
- Call `IPluginTraceService.ListAsync()` with current filter
- Write to file using format

**Commit after step 6.**

---

## Step 7: MetadataBrowserPanel — AC-MB-11, AC-MB-12

### 7a. AC-MB-11: openMetadataBrowserForEnv context menu

**Files:**
- `src/PPDS.Extension/package.json` — add menu entry
- `src/PPDS.Extension/src/extension.ts` — add command handler

**package.json:** Add to `contributes.menus["view/item/context"]`:
```json
{
    "command": "ppds.openMetadataBrowserForEnv",
    "when": "view == ppds.profiles && viewItem == environment",
    "group": "env-tools@5"
}
```

**package.json:** Add to `contributes.commands`:
```json
{
    "command": "ppds.openMetadataBrowserForEnv",
    "title": "Open Metadata Browser",
    "category": "PPDS"
}
```

**extension.ts:** Register command handler following existing pattern:
```typescript
vscode.commands.registerCommand('ppds.openMetadataBrowserForEnv', cmd((item) => {
    if (!item?.envUrl) return;
    MetadataBrowserPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
})),
```

Verify `MetadataBrowserPanel.show()` accepts `envUrl` and `envDisplayName` parameters.

### 7b. AC-MB-12: buildMakerUrl

Replace inline URL at lines ~240, 242:
```typescript
buildMakerUrl(this.environmentId, '/entities')
buildMakerUrl(this.environmentId, `/entities/${entityLogicalName}`)
```

**Commit after step 7.**

---

## Step 8: WebResourcesPanel — AC-WR-19, WR-20, WR-21, WR-22, WR-23

### 8a. AC-WR-20: copyToClipboard

Already handled by base class extraction (Step 2) — add `copyToClipboard` case to `handleMessage()`.

### 8b. AC-WR-21: SolutionFilter component

**Webview (`web-resources-panel.ts`):**
- Import `SolutionFilter` from `'./shared/solution-filter.js'`
- Replace raw `<select id="solution-select">` with `<div id="solution-filter-container"></div>`
- Instantiate `new SolutionFilter(container, { onChange, getState, setState, storageKey: 'webResources.solutionFilter' })`
- Update solution list handler to call `solutionFilter.setSolutions()`

**Host (`WebResourcesPanel.ts`):**
- Remove any manual solution state management that duplicates SolutionFilter behavior

### 8c. AC-WR-19: Search input

**Webview (`web-resources-panel.ts`):**
- Add `<input type="text" id="wr-search" placeholder="Search web resources..." />` to toolbar
- 300ms debounce filter (copy pattern from `metadata-browser-panel.ts:113-137`)
- Client-side filter against `name`, `displayName`, `typeName`
- Update status bar with filter count

### 8d. AC-WR-22: Publish All button (VS Code)

**Webview:**
- Add "Publish All" button to toolbar
- Post `{ command: 'publishAll' }`

**Host (`WebResourcesPanel.ts`):**
- Add `publishAll` handler
- Call `this.daemon.webResourcesPublishAll(this.environmentUrl)`
- Show progress notification
- Refresh panel on completion

**Message types:** Add `{ command: 'publishAll' }` to webview-to-host union.

### 8e. AC-WR-23: TUI Publish All hotkey

**File:** `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs`

- Register Ctrl+Shift+P hotkey (avoid collision with existing Ctrl+P for publish selected)
- Show confirmation dialog: "Publish all customizations? This publishes everything, not just web resources."
- Call `IWebResourceService.PublishAllAsync()`
- Refresh list on completion

**Commit after step 8.**

---

## Step 9: Unit tests — AC-CR-11, AC-EV-12

**Files to create:**
- `src/PPDS.Extension/src/__tests__/panels/connectionReferencesPanel.test.ts`
- `src/PPDS.Extension/src/__tests__/panels/environmentVariablesPanel.test.ts`

Follow pattern from `importJobsPanel.test.ts` (66 lines) and `pluginTracesPanel.test.ts` (296 lines):

1. **Message type coverage:** Verify all WebviewToHost and HostToWebview command variants
2. **DTO field coverage:** Verify all DTO interfaces have required fields
3. **Edge cases:** Null/empty field handling

**Commit after step 9.**

---

## Step 10: Close stale issues

Close 11 issues with verification comments:

```bash
gh issue close 585 -c "Shipped in PR #615"
gh issue close 586 -c "ImportJobsListTool.cs + ImportJobsGetTool.cs exist"
gh issue close 587 -c "ConnectionReferencesListTool.cs + GetTool + AnalyzeTool exist"
gh issue close 588 -c "EnvironmentVariablesListTool.cs + GetTool + SetTool exist"
gh issue close 589 -c "WebResourcesListTool.cs + GetTool + PublishTool exist"
gh issue close 590 -c "MetadataEntitiesListTool.cs exists"
gh issue close 591 -c "PluginTracesDeleteTool.cs exists"
gh issue close 593 -c "IWebResourceService.cs + WebResourceService.cs in PPDS.Dataverse/Services/"
gh issue close 595 -c "KeyboardShortcutsDialog uses scrollable TextView, shows all bindings"
gh issue close 597 -c "EnvironmentSelectorDialog has Open in Maker + Open in Dynamics buttons"
gh issue close 626 -c "Per-env SemaphoreSlim in PooledClientExtensions.cs handles publish coordination"
```

**No commit needed — issue management only.**

---

## Summary

| Step | ACs | Estimated scope |
|------|-----|----------------|
| 1. buildMakerUrl path param | prerequisite | ~5 lines |
| 2. Base class extraction | CC-01–06 | ~200 lines added to base, ~400 lines removed from panels |
| 3. ImportJobs buildMakerUrl | IJ-11 | ~3 lines |
| 4. ConnRefs persistence + buildMakerUrl | CR-05, CR-10 | ~10 lines |
| 5. EnvVars persistence + buildMakerUrl | EV-04, EV-11 | ~10 lines |
| 6. Plugin Traces features | PT-16–20 | ~150 lines (export dropdown, date inputs, TUI hotkey) |
| 7. Metadata Browser context menu + buildMakerUrl | MB-11, MB-12 | ~15 lines |
| 8. Web Resources features | WR-19–23 | ~100 lines (search, SolutionFilter, publishAll) |
| 9. Unit tests | CR-11, EV-12 | ~200 lines (2 test files) |
| 10. Close stale issues | N/A | 11 gh issue close commands |
