# Panel Parity Project Coordination

**Created:** 2026-03-18
**Source:** Extracted from `specs/panel-parity.md` during SL1 decomposition
**Purpose:** Project coordination content (shared patterns, phasing, issue tracking) that does not belong in individual feature specs

---

## Shared Patterns (All Panels)

| Pattern | Description | Status |
|---------|-------------|--------|
| Virtual table | `DataTable` component — sortable columns, row selection, keyboard nav (`data-table.ts`) | Done |
| Environment picker | Toolbar dropdown with environment theming via `data-env-type` / `data-env-color` CSS attributes | Done |
| Solution filter | `SolutionFilter` component — dropdown with `storageKey`-based persistence (`solution-filter.ts`) | Done (ConnRefs, EnvVars); WebResources uses raw `<select>` |
| Panel lifecycle | `WebviewPanelBase.initializePanel()` — auth, environment resolution, title update, initial data load | Phase 3 — currently duplicated in each panel's local `initialize()` |
| Environment switching | `WebviewPanelBase.handleEnvironmentPickerClick()` — picker, state update, reload | Phase 3 — currently duplicated in each panel |
| Environment ID resolution | `WebviewPanelBase.resolveEnvironmentId()` — maps environment URL to GUID for Maker Portal links | Phase 3 — currently local in 5 panels, missing from PluginTraces |
| Maker URL construction | `buildMakerUrl(environmentId, path?)` from `browserCommands.ts` | Phase 3 — only WebResources uses it; 5 panels construct inline |
| RPC method convention | `{domain}/{operation}` with typed request/response DTOs | Done |
| MCP tool convention | `ppds_{domain}_{operation}` with structured input/output | Done |
| TUI hotkey convention | Ctrl+R (refresh), Enter (detail), Ctrl+O (open in Maker), Ctrl+F (filter) | Done |

---

## Cross-Cutting Base Class Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-CC-01 | `WebviewPanelBase.initializePanel()` template method handles auth, env resolution, title update, initial load — all 6 panels use it instead of duplicated `initialize()` | TBD | Not started |
| AC-CC-02 | `WebviewPanelBase.handleEnvironmentPickerClick()` handles picker, state update, reload — all 6 panels use it instead of duplicated handler | TBD | Not started |
| AC-CC-03 | `WebviewPanelBase.resolveEnvironmentId()` maps environment URL to GUID — all 6 panels call it (including PluginTraces) | TBD | Not started |
| AC-CC-04 | `WebviewPanelBase.updatePanelTitle()` sets panel title with profile and environment — all 6 panels use it | TBD | Not started |
| AC-CC-05 | All panels use `config.resolvedType` fallback for environment type resolution | TBD | Not started |
| AC-CC-06 | All panels support `copyToClipboard` message handler | TBD | Not started |
| AC-CC-07 | TUI environment selector has Open in Maker and Open in Dynamics actions (#597) | `EnvironmentSelectorDialog.cs:225-237` | Done |
| AC-CC-08 | TUI keyboard shortcuts dialog scrolls to show all bindings including screen-specific (#595) | `KeyboardShortcutsDialog.cs:42` (scrollable `TextView`) | Done |

---

## Work Breakdown

### Phasing

```
MVP PR merged to main
         |
         v
  Phase 1: Import Jobs (pattern-setter)          Done (PR #611)
  Branch: feature/panel-import-jobs
         |
         v (merge to main)
         |
    +----+----+----------+----------+
    v         v          v          v
 Phase 2a  Phase 2b   Phase 2c   Phase 2d         Done
 Env+Conn  Traces     Metadata   Web Res           (PRs #617, #615, #616, #618)
    |         |          |          |
    v         v          v          v
  (all merge to main independently)
         |
         v
  Phase 3: Parity Polish                          In Progress
  Branch: feature/panel-parity-polish
  Issues: #619-625, #595, #597
  Close: #585-591, #593, #626
```

### Phase 3 Details

| Group | Items | ACs |
|-------|-------|-----|
| Base class extraction | `initializePanel()`, `handleEnvironmentPickerClick()`, `resolveEnvironmentId()`, `updatePanelTitle()` to `WebviewPanelBase` | AC-CC-01-04 |
| Plugin Traces gaps | Export CSV/JSON, date range filter, resolveEnvironmentId, openInMaker | AC-PT-16-20 |
| Web Resources gaps | Search input, copyToClipboard, SolutionFilter component, publishAll UI | AC-WR-19-23 |
| Consistency | `buildMakerUrl()` in 5 panels, `resolvedType` fallback, `copyToClipboard` | AC-IJ-11, AC-CR-10, AC-EV-11, AC-MB-12, AC-CC-05-06 |
| Persistence | Solution filter globalState in ConnRefs + EnvVars | AC-CR-05, AC-EV-04 |
| Context menus | `openMetadataBrowserForEnv` command | AC-MB-11 |
| TUI parity | Env selector Maker/Dynamics actions, keyboard shortcuts scroll | AC-CC-07-08 |
| Unit tests | ConnRefs + EnvVars panel tests | AC-CR-11, AC-EV-12 |
| Issue closure | Close #585-591, #593, #626 (9 stale issues) | N/A |

---

## Issue Reconciliation

### Phase 3 Issues (In Scope)

| Issue | Title | ACs | Status |
|-------|-------|-----|--------|
| #619 | openMetadataBrowserForEnv context menu | AC-MB-11 | Open |
| #620 | Persist solution filter in ConnRefs + EnvVars | AC-CR-05, AC-EV-04 | Open |
| #621 | Unit tests for ConnRefs + EnvVars panels | AC-CR-11, AC-EV-12 | Open |
| #622 | CSV/JSON export for Plugin Traces | AC-PT-16, AC-PT-19 | Open |
| #623 | Date range filter for Plugin Traces | AC-PT-17 | Open |
| #624 | Search input for Web Resources | AC-WR-19 | Open |
| #625 | publishAll UI button (RPC exists) | AC-WR-22, AC-WR-23 | Open |

### Stale Issues — Close After Verification

| Issue | Title | Evidence | Action |
|-------|-------|----------|--------|
| #585 | Plugin Traces panel | Shipped in PR #615 | Close |
| #586 | MCP: Import jobs tools | `ImportJobsListTool.cs`, `ImportJobsGetTool.cs` exist | Close |
| #587 | MCP: Connection references tools | `ConnectionReferencesListTool.cs`, `GetTool`, `AnalyzeTool` exist | Close |
| #588 | MCP: Environment variables tools | `EnvironmentVariablesListTool.cs`, `GetTool`, `SetTool` exist | Close |
| #589 | MCP: Web resources tools | `WebResourcesListTool.cs`, `GetTool`, `PublishTool` exist | Close |
| #590 | MCP: Metadata entities list tool | `MetadataEntitiesListTool.cs` exists | Close |
| #591 | MCP: Plugin traces delete tool | `PluginTracesDeleteTool.cs` exists | Close |
| #593 | IWebResourceService extraction | `IWebResourceService.cs` + `WebResourceService.cs` in `PPDS.Dataverse/Services/` | Close |
| #626 | PublishCoordinator | Per-env `SemaphoreSlim` in `PooledClientExtensions.cs:22` | Close |
| #595 | TUI keyboard shortcuts scroll | `KeyboardShortcutsDialog.cs:42` uses scrollable `TextView` | Close |
| #597 | TUI env selector Maker/Dynamics actions | `EnvironmentSelectorDialog.cs:225-237` has both buttons | Close |

### Consistency Fixes (No Dedicated Issues)

| Fix | ACs | Scope |
|-----|-----|-------|
| Base class extraction | AC-CC-01-04 | All 6 panels |
| `buildMakerUrl()` standardization | AC-IJ-11, AC-CR-10, AC-EV-11, AC-MB-12, AC-PT-20 | 5 panels (WebResources already uses it) |
| `resolvedType` fallback | AC-CC-05 | 5 panels |
| `copyToClipboard` handler | AC-CC-06, AC-WR-20 | WebResources (others already have it) |
| `SolutionFilter` component | AC-WR-21 | WebResources (switch from raw `<select>`) |
| `resolveEnvironmentId` + `openInMaker` | AC-PT-18 | PluginTraces |

---

## Roadmap

- **Connection Picker (option c):** Add connection binding dropdown to Connection References panel after parity
- **Plugin Registration:** Full plugin registration panels across all surfaces (separate spec)
- **Solution History:** Expand Import Jobs to include exports/publishes/uninstalls (matches Maker solutionsHistory)
- **Bulk operations UI:** Surface existing bulk operation infrastructure in panel actions
