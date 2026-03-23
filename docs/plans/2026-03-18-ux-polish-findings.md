# UX Polish Findings — Complete Audit

**Date:** 2026-03-18
**Updated:** 2026-03-21 (all per-item decisions finalized)
**Branch:** `feature/ux-polish`
**Method:** Phase 1 code-level comparison of legacy v0.3.4 vs new extension across all 8 panels
**Status:** All decisions made. Awaiting Phase 2 interactive Playwright verification + owner walkthrough.
**GitHub issue:** #650 (Monaco squiggles — deferred)

---

## Decisions Made

### Top-Level Design Decisions

| Decision | Resolution |
|----------|-----------|
| Virtual scroll | **Build** — add to shared DataTable (60K web resources in default view) |
| Cell selection | **Build** — extract SelectionManager from DE, add to shared DataTable |
| Sync Deployment Settings | **Wire** — backend exists (`DeploymentSettingsService.cs`), add RPC endpoint + UI |
| Date sort | **Fix** — add `sortValue` option to DataTable columns |
| `retainContextWhenHidden` | **Fix** — set `true` on all panels |
| `enableFindWidget` | **Fix** — set `true` on all panels |
| Row striping | **Fix** — CSS in DataTable |
| Cell tooltips | **Fix** — title attrs in DataTable render |
| Search scope | **Fix** — search all rendered cell text (`row.textContent`) |
| Table header styling | **Fix** — middle ground: 12px, `--vscode-foreground`, 2px border-bottom, column separators |
| Sort behavior | **Fix** — all columns sortable, click to toggle asc/desc, match legacy |
| Panel title format | **Fix** — all panels use base class `updatePanelTitle()` format: `profileName · envName — PanelLabel`. Delete QueryPanel's custom `updateTitle()`. |
| VQB for Data Explorer | **Not doing** — Monaco + IntelliSense is the replacement |
| Global Choices | **Build** — peer of Entities in Metadata Browser tree |
| Properties detail panel | **Build** — comprehensive property enumeration via SDK |
| Raw Data JSON tab | **Not doing** — enumerate every property; raw dump adds no value if properties are complete |
| CR data model | **Enhance** — keep CR-as-primary-row, add flow count/names column + relationship-aware status |
| CR default solution | **Fix** — default to Default Solution (`fd140aaf-4df4-11dd-bd17-0019b9312238`), remove "All" option, persist per panel per env |
| Plugin Traces detail split | **Restyle** — side-by-side (like Chrome DevTools Network) |
| Plugin Traces timeline | **Build** — full overhaul to match/improve legacy (depth colors, shimmer, click nav, legend) |
| Plugin Traces query builder | **Build** — full three-tab filter (Quick Filters / Advanced / OData Preview) |
| Plugin Traces detail fields | **Fix** — add ALL fields from legacy (Stage, IsSystemCreated, Created By, Created On Behalf By, Plugin Step ID, Constructor Start Time, Persistence Key, Org ID, Profile) |
| Metadata Browser relationships | **Fix** — 3 separate tabs with correct columns per type (N:N has different fields — intersect entity, entity1/entity2 attributes) |
| Metadata Browser icons | **Fix** — match legacy emojis (🏷️ custom, 📋 system, 🔽 option sets) |
| Web Resources Maker URL | **Keep** — legacy v0.3.4 had no Web Resources panel; current `/solutions` default is correct |
| All other Maker URLs | **Verified correct** — deep links confirmed for all panels |
| Monaco squiggles (DE-44) | **Deferred** — GitHub issue #650 |

---

## Cross-Cutting Issues

These appear across **all or most panels** and should be addressed as shared infrastructure fixes.

### CC-01: Date column sorting is broken
**Decision:** Fix
**Severity:** Critical
**Panels:** Import Jobs, Plugin Traces, Connection References, Web Resources, Environment Variables, Solutions
`DataTable.sortItems()` strips HTML and uses `localeCompare` on rendered date strings. Sorts alphabetically by month name, not chronologically.
**Fix:** Add a `sortValue` option to DataTable columns that provides raw sortable values (timestamps, numbers).

### CC-02: No virtual scrolling — full DOM render
**Decision:** Fix
**Severity:** Critical
**Panels:** All 8
Legacy used `VirtualTableRenderer` rendering ~60 visible rows. New renders ALL rows via `innerHTML`. 60K web resources in default view.
**Fix:** Add virtual scrolling to DataTable.

### CC-03: No Excel-style cell selection or Ctrl+A/Ctrl+C clipboard copy
**Decision:** Fix
**Severity:** Critical
**Panels:** All except Data Explorer (which already has its own)
Legacy had `CellSelectionBehavior.js` + `KeyboardSelectionBehavior.js`. Power Platform admins copy data into spreadsheets constantly.
**Fix:** Extract SelectionManager from DE, add to shared DataTable component.

### CC-04: retainContextWhenHidden: false
**Decision:** Fix
**Severity:** Important
**Panels:** All 8
**Fix:** Set `retainContextWhenHidden: true` on all panels.

### CC-05: enableFindWidget missing
**Decision:** Fix
**Severity:** Important
**Panels:** All 8
**Fix:** Add `enableFindWidget: true` to all panel webview options.

### CC-06: Table header styling — flat/muted
**Decision:** Fix
**Severity:** Important
**Panels:** All 8
**Fix:** Middle ground — 12px font, `--vscode-foreground`, 2px `--vscode-panel-border` bottom border, column separators. Not legacy blue, but not invisible.

### CC-07: Row striping missing
**Decision:** Fix
**Severity:** Important
**Panels:** All except Data Explorer (which already has this)
**Fix:** Add CSS `tr:nth-child(even)` striping to DataTable.

### CC-08: Cell tooltips missing
**Decision:** Fix
**Severity:** Important
**Panels:** All 8
**Fix:** Add `title` attribute generation to DataTable `render()`.

### CC-09: Search scope narrower than legacy
**Decision:** Fix
**Severity:** Minor
**Panels:** Solutions, Import Jobs, Plugin Traces, Environment Variables
**Fix:** Search all rendered cell text (`row.textContent`).

### CC-10: Sync Deployment Settings is a stub
**Decision:** Fix — wire to existing backend
**Severity:** Critical
**Panels:** Connection References, Environment Variables
Backend exists: `DeploymentSettingsService.cs` with `GenerateAsync`, `SyncAsync`, `ValidateAsync`. Smart merge with added/removed/preserved counts.
**Fix:** Add RPC endpoint, wire extension UI to call it. Legacy code reference: `C:\VS\ppdsw\extension archived\`.

### CC-11: Missing box-sizing: border-box global reset
**Decision:** Fix
**Severity:** Cosmetic

### CC-12: Missing user-select: none on body
**Decision:** Fix
**Severity:** Cosmetic

### CC-13: Button loading states missing
**Decision:** Fix
**Severity:** Minor
**Panels:** Import Jobs, Connection References, Environment Variables, Web Resources
**Fix:** Disable toolbar buttons during loading, show spinner on Refresh.

### CC-14: Panel title format inconsistent
**Decision:** Fix
**Severity:** Important
**Panels:** All 7 except Data Explorer (which already does it right)
All panels show `Panel Name #N`. Data Explorer shows `profileName · envName — Data Explorer`.
**Fix:** Delete QueryPanel's custom `updateTitle()`. All panels already use `WebviewPanelBase.updatePanelTitle()` which has the correct format — just need to verify QueryPanel calls it instead of its own method.

---

## Per-Panel Findings

### Solutions Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| S-01 | Different layout paradigm — table vs tree list | **Keep tree** — component drill-down is better than legacy flat table | N/A |
| S-02 | friendlyName not primary (uniqueName shown first) | **Fix** — displayName first, schema name in parentheses | Important |
| S-03 | Columns hidden behind expand (Visible, API Managed, Installed On, Modified On) | **Fix** — add as secondary text line on each row | Important |
| S-04 | Sort reduced — 4 fixed options vs 9 sortable columns | **Fix** — all columns sortable, toggle direction (CC sort decision) | Important |
| S-05 | No `isvisible eq true` filter like legacy | **Fix** — add filter, default to visible only | Important |
| S-06 | Click expands instead of opening Maker | **Keep** — expand is better UX; per-row 🔗 button exists for Maker | N/A |
| S-07 | Virtual scrolling removed | **Fix** — covered by CC-02 | Important |
| S-08 | Cell selection/clipboard missing | **Fix** — covered by CC-03 | Important |
| S-09 | Date-only format, time lost | **Fix** — use `toLocaleString()` to include time | Minor |
| S-10 | `createdOn` fetched but never displayed | **Fix** — add to view (legacy didn't show it, but it's useful data — owner will evaluate) | Minor |
| S-11 | Unmanaged solutions have no type indicator | **Fix** — show "managed" or "unmanaged" badge on all solutions | Minor |
| S-12 | Panel title shows `#N` not env name | **Fix** — covered by CC-14 | Minor |
| S-13 | retainContextWhenHidden: false | **Fix** — covered by CC-04 | Minor |
| S-15 | Search scope: 3 fields vs all cell text | **Fix** — covered by CC-09 | Minor |
| S-17 | Missing per-solution Export/Publish/Delete | **Skip** — verified: legacy did NOT have these as working UI buttons | N/A |
| S-25 | enableFindWidget missing | **Fix** — covered by CC-05 | Minor |
| S-31 | Sort direction not toggleable | **Fix** — covered by CC sort decision | Minor |

**New features to keep:** Component drill-down, description shown, reconnect banner, multi-panel, manual URL entry

---

### Import Jobs Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| IJ-01 | Solution name not a clickable link | **Fix** — make it open the import log | Important |
| IJ-02 | Row click shows inline pane instead of editor tab | **Fix** — open in VS Code editor tab, prettified XML with syntax highlighting | Important |
| IJ-06 | No virtual scrolling | **Fix** — covered by CC-02 | Important |
| IJ-07 | Table header styling | **Fix** — covered by CC-06 | Important |
| IJ-09 | Cell tooltips missing | **Fix** — covered by CC-08 | Minor |
| IJ-10 | Cell selection missing | **Fix** — covered by CC-03 | Important |
| IJ-11 | Ctrl+C clipboard missing | **Fix** — covered by CC-03 | Important |
| IJ-12 | Search filters 3 fields vs all text | **Fix** — covered by CC-09 | Minor |
| IJ-15 | Missing status styles: Cancelled, Queued, Completed with Errors | **Fix** | Minor |
| IJ-17 | retainContextWhenHidden + enableFindWidget | **Fix** — covered by CC-04 + CC-05 | Important |
| IJ-23 | Date column sorting broken | **Fix** — covered by CC-01 | Important |
| IJ-26 | No button loading state | **Fix** — covered by CC-13 | Minor |
| IJ-43 | Detail pane not resizable | **Remove** — inline detail pane goes away; import log opens in editor tab (IJ-02) | N/A |
| IJ-45 | "Succeeded" vs "Completed" mismatch | **Fix** — verify daemon return value, align | Minor |

---

### Plugin Traces Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| PT-01 | Missing `Stage` field in detail | **Fix** — critical for plugin debugging | Important |
| PT-02 | Missing Constructor Start Time | **Fix** — was in legacy, useful for init overhead debugging | Minor |
| PT-03 | Missing IsSystemCreated | **Fix** — was in legacy | Minor |
| PT-04 | Missing Created By | **Fix** — was in legacy, needed for multi-user environments | Minor |
| PT-05 | Missing Created On Behalf By | **Fix** — was in legacy, needed for impersonation debugging | Minor |
| PT-06 | Missing Plugin Step ID | **Fix** — was in legacy, needed to distinguish multiple registrations | Minor |
| PT-07 | Missing Persistence Key | **Fix** — was in legacy | Minor |
| PT-08 | Missing Organization ID | **Fix** — was in legacy | Minor |
| PT-09 | Missing Profile | **Fix** — was in legacy | Minor |
| PT-10 | Missing Raw Data tab | **Skip** — enumerate every property in detail tabs instead; raw dump adds no value if properties are complete | N/A |
| PT-11 | 5 tabs vs legacy Overview approach | **Fix** — add Overview tab as default landing that combines status + exception + message block. Keep individual tabs for deep dives. | Minor |
| PT-12 | Detail panel bottom split, not side-by-side | **Fix** — side-by-side (like Chrome DevTools Network). Scan-and-drill workflow needs full list visible. | Important |
| PT-13 | Missing advanced query builder | **Build** — full three-tab filter panel. DOES NOT EXIST, must build from scratch. | Critical |
| PT-14 | Missing Operation Type column | **Fix** — add as table column | Minor |
| PT-15 | Operation Type not filterable | **Fix** — comes with query builder build | Minor |
| PT-16 | Missing Correlation ID column | **Fix** — critical for grouping related traces | Minor |
| PT-19 | Filter state not persisted | **Fix** — comes with query builder build | Minor |
| PT-20 | Detail panel width not persisted | **Fix** — save split ratio to globalState | Minor |
| PT-21 | Filter panel not resizable | **Fix** — comes with query builder build | Minor |
| PT-22 | No virtual scrolling | **Fix** — covered by CC-02 | Minor |
| PT-23–24 | Cell selection + Ctrl+A/C | **Fix** — covered by CC-03 | Minor |
| PT-25 | Timeline bars not clickable | **Fix** — click bar → select + scroll to trace | Important |
| PT-26 | Timeline visual richness gap | **Build** — full overhaul: depth colors (5 levels), shimmer animation, click navigation, legend, header, hover effects, status gradients | Critical |
| PT-29 | Time/Duration sort by text | **Fix** — covered by CC-01 | Important |
| PT-43 | No Ctrl+A in exception block | **Fix** — Ctrl+A in exception block selects just that block | Minor |
| PT-44 | JSON syntax highlighting | **Skip** — no Raw Data tab being built | N/A |
| PT-49 | Keyboard shortcut protection in filter inputs | **Fix** — `stopPropagation` so Ctrl+A selects text in input, not whole page | Minor |
| PT-57 | OData query preview | **Fix** — comes with query builder build (third tab) | Minor |
| PT-59 | Detail pane no visible close button | **Fix** — add X button | Cosmetic |

**New features to keep:** Export (CSV/JSON/clipboard), auto-refresh, "Delete older than", exception/slow row tinting, trace level "All" confirmation, duration warning styling

---

### Data Explorer Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| DE-01 | Visual Query Builder missing | **Not doing** — Monaco + IntelliSense is the intentional replacement | N/A |
| DE-05 | FetchXML live preview panel missing | **Not doing** — verified: SQL/FetchXML segmented toggle already exists, Ctrl+Shift+F shows FetchXML preview | N/A |
| DE-06 | No inline transpilation warnings | **Fix** — show warnings when SQL→FetchXML conversion loses information | Important |
| DE-12 | In-webview modal vs VS Code native | **Keep native** — consistent with VS Code patterns | N/A |
| DE-13 | No virtual scrolling in results | **Fix** — covered by CC-02 | Important |
| DE-14 | No server-side search fallback | **Defer** — Load More pagination mitigates this | Minor |
| DE-15 | Record link hover copy button missing | **Fix** — add hover copy button, more discoverable than right-click only | Minor |
| DE-28 | Column picker missing | **Not doing** — VQB not being built | N/A |
| DE-29 | Filter condition builder missing | **Not doing** — VQB not being built | N/A |
| DE-30 | Pre-query sort config gone | **Keep removed** — post-results column sort is more intuitive | N/A |
| DE-31 | Top N / DISTINCT not discoverable | **Fix** — add to overflow menu as checkbox options | Minor |
| DE-44 | Error display missing line/column position | **Deferred** — GitHub issue #650 (includes Monaco squiggles) | N/A |

**New features to keep:** Monaco editor, IntelliSense, query history, Load More pagination, export, save/load query files, cancellation, DML safety, TDS toggle, right-click context menu, resize handle, auto-detect language, cell selection with copy hints, "Filter to Value"

---

### Metadata Browser Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| MB-01 | Global Choices tree section missing | **Build** — peer of Entities, not nested inside them | Critical |
| MB-02 | Collapsible tree headers with count badges | **Fix** — "ENTITIES (342)" and "CHOICES (56)" with collapse | Important |
| MB-03 | 3 relationship tabs consolidated into 1 | **Fix** — restore 3 separate tabs with correct columns per type. N:N has fundamentally different fields (intersect entity, entity1/entity2 attributes) — legacy was WRONG to render them identically. Correct columns: **1:N** (Schema Name, Related Entity, Referencing Attribute, Cascade Delete), **N:1** (Schema Name, Referenced Entity, Referencing Attribute, Cascade Delete), **N:N** (Schema Name, Related Entity, Intersect Entity, Entity1 Attribute, Entity2 Attribute) | Important |
| MB-04 | Per-tab search missing | **Fix** — each tab needs its own search input | Important |
| MB-05 | Properties detail panel missing | **Build** — comprehensive property enumeration via SDK | Critical |
| MB-06 | Entity selection header/breadcrumb missing | **Fix** — show "Account — account" above tabs | Important |
| MB-07 | Entity icons differ | **Fix** — match legacy emojis: 🏷️ custom, 📋 system, 🔽 option sets | Minor |
| MB-08 | Single-line vs two-line entity list | **Keep single-line** — more compact, density matters with 300+ entities | N/A |
| MB-09 | Relationship navigation links missing | **Fix** — click related entity to navigate there | Important |
| MB-10 | logicalName first vs displayName first | **Fix** — displayName first, it's what users scan for | Important |
| MB-11 | Row striping missing | **Fix** — covered by CC-07 | Minor |
| MB-13 | Lost Intersect Entity column for N:N | **Fix** — add back alongside new columns (covered by MB-03 fix) | Minor |
| MB-19 | retainContextWhenHidden: false | **Fix** — covered by CC-04 | Important |
| MB-20 | Ctrl+A/C keyboard selection | **Fix** — covered by CC-03 | Important |
| MB-21 | Cell tooltips missing | **Fix** — covered by CC-08 | Cosmetic |
| MB-22 | No copy schema name affordance | **Fix** — add right-click "Copy Schema Name" | Minor |
| MB-32–33 | Precision, MinValue, MaxValue, lookup Targets | **Fix** — include in Properties detail panel | Minor |

**New features to keep:** "Custom Only" filter, column sorting, row keyboard focus, environment type accent, richer status bar counts

---

### Connection References Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| CR-01 | Different data model — CRs only vs flow-to-CR relationships | **Enhance** — keep CR-as-primary-row, add flow count/names column + relationship-aware status | Critical |
| CR-02 | Missing columns: Flow Name, Flow Managed, Flow Modified | **Fix** — surface flow info at table level | Critical |
| CR-03 | Missing flow link in table rows | **Fix** — add per-flow Maker deep link | Important |
| CR-05 | Different status semantics | **Fix** — status should reflect relationship health (orphaned flows, missing CRs) | Critical |
| CR-06 | Sync Deployment Settings stub | **Fix** — covered by CC-10 | Important |
| CR-08 | retainContextWhenHidden: false | **Fix** — covered by CC-04 | Important |
| CR-09 | Solution filter defaults to "All" | **Fix** — default to Default Solution (`fd140aaf-4df4-11dd-bd17-0019b9312238`), remove "All" option, persist per panel per env | Minor |
| CR-11 | No request cancellation on solution change | **Fix** — AbortController pattern | Minor |
| CR-12 | Table header styling | **Fix** — covered by CC-06 | Important |
| CR-15 | Cell selection missing | **Fix** — covered by CC-03 | Important |
| CR-16 | Keyboard navigation missing | **Fix** — covered by CC-03 | Important |
| CR-17 | Search placeholder differs | **Fix** — "Search connection references..." | Minor |
| CR-28 | Date sorting broken | **Fix** — covered by CC-01 | Minor |
| CR-30 | Per-flow Maker deep link | **Fix** — use `make.powerautomate.com/environments/{id}/flows/{flowId}/details` | Important |

**New features to keep:** Expandable row detail with flows list, Analyze button

---

### Environment Variables Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| EV-01 | Table header styling | **Fix** — covered by CC-06 | Important |
| EV-02 | Row striping missing | **Fix** — covered by CC-07 | Important |
| EV-09 | Sync Deployment Settings stub | **Fix** — covered by CC-10 | Critical |
| EV-10 | Export has no merge capability | **Fix** — wire to `DeploymentSettingsService.SyncAsync` | Important |
| EV-13 | Cell selection missing | **Fix** — covered by CC-03 | Important |
| EV-14–15 | Ctrl+A / Ctrl+C TSV copy missing | **Fix** — covered by CC-03 | Important |
| EV-17 | retainContextWhenHidden: false | **Fix** — covered by CC-04 | Important |
| EV-18 | enableFindWidget missing | **Fix** — covered by CC-05 | Minor |
| EV-22 | Date only, no time | **Fix** — use `toLocaleString()` to include time | Minor |
| EV-25 | Cell tooltips missing | **Fix** — covered by CC-08 | Minor |
| EV-37 | Export format may not match PP schema | **Fix** — use `DeploymentSettingsService` format | Important |
| EV-40 | Search scope: 3 fields vs all text | **Fix** — covered by CC-09 | Minor |
| EV-41 | Filter count element created but never appended | **Fix** — bug, append element to DOM | Minor |

**New features to keep:** Override/missing indicators, detail pane, value editing dialog, env type accent

---

### Web Resources Panel

| ID | Finding | Decision | Severity |
|----|---------|----------|----------|
| WR-01 | retainContextWhenHidden: false | **Fix** — covered by CC-04 | Important |
| WR-02 | No virtual scrolling — 60K records | **Fix** — covered by CC-02 | Critical |
| WR-03 | No server-side search fallback | **Fix** — with 60K resources and 5K client cap, search only covers 8% | Important |
| WR-04 | No background progressive loading | **Fix** — load first page fast, continue in background | Important |
| WR-07 | Table header styling | **Fix** — covered by CC-06 | Important |
| WR-08 | Row striping missing | **Fix** — covered by CC-07 | Important |
| WR-09 | Cell tooltips missing | **Fix** — covered by CC-08 | Minor |
| WR-10 | Search input wrong CSS class (`search-input` has no CSS) | **Fix** — bug, should be `toolbar-search` | Important |
| WR-11 | Date sort broken | **Fix** — covered by CC-01 | Important |
| WR-12 | Cell selection + Ctrl+A/C missing | **Fix** — covered by CC-03 | Important |
| WR-13 | enableFindWidget missing | **Fix** — covered by CC-05 | Minor |
| WR-15 | Maker Portal URL generic | **N/A** — legacy v0.3.4 had no WR panel; current URL is correct | N/A |
| WR-20 | Missing "offer to publish" after save | **Fix** — Edit → Save → "Publish now?" workflow prompt | Minor |
| WR-23 | No data cache for solution switching | **Fix** — cache by solution ID, invalidate on refresh | Minor |
| WR-30 | Full innerHTML re-render on sort | **Fix** — addressed by virtual scroll build | Important |

**New features to keep:** Multi-select publish, type badges with colors, error handler, reconnect banner

---

## Summary

### Counts by Decision

| Decision | Count |
|----------|-------|
| **Fix** | ~95 |
| **Build** (new component/feature) | 9 major items |
| **Skip / Not doing** | 8 (S-17, DE-01/05/28/29/30, PT-10/44) |
| **Keep current** | 5 (S-01/06, DE-12, MB-08, WR-15) |
| **Deferred** | 2 (DE-14, DE-44 → #650) |

### Major Build Items

| Item | Scope | Complexity |
|------|-------|------------|
| Virtual scroll in DataTable | Shared component — all panels | High |
| Cell selection + TSV copy in DataTable | Shared component — extract from DE | High |
| Plugin Traces timeline overhaul | PT panel — depth colors, shimmer, click nav, legend, header, hover | High |
| Plugin Traces advanced query builder | PT panel — 3-tab filter, condition builder, OData preview | High |
| Plugin Traces side-by-side detail | PT panel — change flex-direction, resize handle | Medium |
| Global Choices tree section | MB panel — peer of Entities | Medium |
| Properties detail panel | MB panel — comprehensive SDK property enumeration | Medium |
| Sync Deployment Settings wiring | CR + EV panels — RPC endpoint + UI for existing backend | Medium |
| CR flow relationship at table level | CR panel — flow count/names column, relationship-aware status | Medium |
| Metadata Browser 3 relationship tabs | MB panel — separate tabs with correct per-type columns | Medium |

### Quick Wins (one-liner or near-one-liner fixes)

| Fix | Scope |
|-----|-------|
| `retainContextWhenHidden: true` | All 8 panels |
| `enableFindWidget: true` | All 8 panels |
| Row striping CSS | DataTable shared |
| Cell tooltips (title attrs) | DataTable shared |
| `box-sizing: border-box` reset | shared.css |
| `user-select: none` on body | shared.css |
| WR-10 search input CSS class | Web Resources |
| EV-41 filter count append | Environment Variables |
| Panel title format (CC-14) | Delete QueryPanel custom method |

---

## Aggregate New Extension Improvements

The new extension adds significant capabilities not in legacy:
- **Data Explorer:** Monaco editor, IntelliSense, query history, export, cancellation, DML safety, cell selection, "Filter to Value"
- **Plugin Traces:** Export, auto-refresh, delete older than, exception/slow row tinting, trace level confirmation
- **All panels:** Multi-panel support, environment type accents, reconnect banners, environment QuickPick with manual URL
- **Environment Variables:** Value editing dialog, override/missing indicators
- **Connection References:** Expandable detail with flows, analyze button
- **Solutions:** Component drill-down, description shown
- **Web Resources:** Multi-select publish, colored type badges
- **Metadata Browser:** Column sorting, custom-only filter

---

## Plugin Traces — Legacy Feature Reference

### Timeline (must match or improve)
- Hierarchical view grouped by correlation ID
- Duration bars: shimmer animation, gradient fills (green success / red exception)
- Depth-based left-border colors: depth 0 blue, 1 purple, 2 teal, 3 gold, 4+ gray
- Click navigation: click bar → select + scroll to trace
- Legend: Success/Exception with color indicators
- Header: total duration + trace count
- Hover: scaleY(1.1) + shadow
- Empty state: "No timeline data" + hint about correlation ID

### Advanced Query Builder (must build)
- **Three tabs:** Quick Filters, Advanced, OData Query Preview
- **Condition builder:** field dropdown, type-aware operator dropdown, value input (text/number/date/enum), AND/OR logical grouping, enable/disable per condition, add/remove rows
- **Operators by type:**
  - Text: Contains, Equals, Not Equals, Starts With, Ends With
  - Enum: Equals, Not Equals
  - Date/Number: Equals, GT, LT, GTE, LTE
  - All types: Is Null, Is Not Null
- **Filterable fields:** Plugin Name, Entity, Message, Status, Created On, Duration, Mode, Stage
- **OData preview:** Read-only generated `$filter` with copy button
- **Quick filters:** Checkbox-based, auto-apply
- **Panel:** Collapsible, resizable (120px min, 60vh max), height persisted
- **Filter count:** Header shows "Filters (X / Y)" active vs total

### Connection References — Default Solution
- **GUID:** `fd140aaf-4df4-11dd-bd17-0019b9312238` (Microsoft global constant, same in all Dataverse orgs)
- **Source:** archived extension `SolutionConstants.ts`
- **Behavior:** Default to this solution, persist user's choice per panel per environment

### Metadata Browser — Relationship Tab Columns
- **1:N:** Schema Name, Related Entity, Referencing Attribute, Cascade Delete
- **N:1:** Schema Name, Referenced Entity, Referencing Attribute, Cascade Delete
- **N:N:** Schema Name, Related Entity, Intersect Entity, Entity1 Attribute, Entity2 Attribute
- Legacy rendered all 3 identically (bug) — we fix it with proper columns per type

---

## Phase 2: Interactive Verification Findings

**Date:** 2026-03-23
**Method:** Side-by-side CDP testing — legacy v0.3.4 vs new extension, both connected to PPDS Demo - Dev
**Environment:** https://orgcabef92d.crm.dynamics.com

### Cross-Cutting Runtime Findings

| # | Finding | Panels | Severity | Phase 1 Ref |
|---|---------|--------|----------|-------------|
| P2-CC-01 | New panel queries return far fewer records than legacy (8 vs 511 solutions, 50 vs 251 import jobs, 5000 vs unknown WR). Each panel has different root cause: Solutions filters to `isvisible eq true`, Import Jobs has `$top=50`, Web Resources caps at 5000. | Solutions, Import Jobs, Web Resources | Critical | S-05 (updates) |
| P2-CC-02 | Web Resources renders all 5000 rows in DOM — confirmed CC-02 at runtime. Page will be extremely heavy with 60K resources in production environments. | Web Resources | Critical | CC-02 (confirms) |
| P2-CC-03 | Date format inconsistency across panels — Environment Variables shows date-only ("Dec 11, 2025"), Import Jobs shows date+time ("Mar 22, 2026, 10:30:17 PM"), Web Resources shows date+time ("Dec 11, 2025, 03:55:14 AM"). Legacy consistently uses `toLocaleString()` with time everywhere. | Environment Variables | Important | EV-22 (confirms) |
| P2-CC-04 | Solution filter defaults to "All Solutions" in new panels (CR, EV, WR) vs "Default Solution" in legacy (CR, WR). | Connection References, Environment Variables, Web Resources | Minor | CR-09 (confirms) |

### Panel: Solutions

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-S-01 | Record count: legacy shows **511 solutions** (all, including invisible), new shows **8 solutions** (visible only). Phase 1's S-05 stated new was missing the `isvisible` filter — runtime shows the **opposite**: new HAS the filter (showing 8), legacy does NOT (showing 511). The decision to "add filter, default to visible only" is already implemented. Consider adding "Show All" toggle for users who need invisible solutions. | `$TEMP/legacy-solutions-2.png`, `$TEMP/new-solutions.png` | Important |
| P2-S-02 | Legacy virtual scrolling confirmed at runtime — renders ~43 rows at a time from 511 total. New renders all 8 in DOM (acceptable at this count, but would be a problem if "Show All" toggle is added). | `$TEMP/legacy-solutions-2.png` | — (info) |
| P2-S-03 | Legacy shows "511 records" in footer. New shows "8 solutions (4 unmanaged, 4 managed)" — new footer format is more informative. | — | — (info) |
| P2-S-04 | Legacy has "Open in Maker" + "Refresh" buttons only. New has "Refresh" + "Maker Portal" + "Include Managed" checkbox + "Sort" dropdown + filter search. New toolbar is richer. | — | — (info) |
| P2-S-05 | Expand/drill-down in new panel works correctly — clicking chevron (▶) shows component categories (Entity, Model-Driven App, System Form, etc.). | `$TEMP/new-solutions-expanded2.png` | — (info) |
| P2-S-06 | Search filter works — "PPDS" correctly filtered to 1 result with "1 of 8 solutions" shown in footer. | `$TEMP/new-solutions-search.png` | — (info) |

### Panel: Import Jobs

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-IJ-01 | Record count: legacy **251 import jobs**, new **50 import jobs**. New appears to use `$top=50` limit, losing 80% of data. Users reviewing import history won't see older jobs. | `$TEMP/legacy-importjobs.png`, `$TEMP/new-importjobs.png` | Critical |
| P2-IJ-02 | Status text confirmed at runtime: legacy "Completed" vs new "Succeeded". Same semantic, different label. | — | Minor (IJ-45) |
| P2-IJ-03 | Both default to "Created On ▼" (descending) sort — consistent. | — | — (info) |
| P2-IJ-04 | Same 7 columns in both: Solution, Status, Progress, Created By, Created On, Duration, Operation Context. Column parity is good. | — | — (info) |

### Panel: Plugin Traces

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-PT-01 | No trace data in test environment — could not test timeline rendering, row click detail, or data rendering. | `$TEMP/legacy-plugintraces.png`, `$TEMP/new-plugintraces.png` | — (limitation) |
| P2-PT-02 | Legacy has full 3-tab filter confirmed at runtime: Quick Filters (8 presets with OData field badges: Exceptions, Success Only, Last Hour, Last 24 Hours, Today, Async Only, Sync Only, Recursive Calls), Advanced Filters (condition builder with Add Condition/Apply/Clear), OData Query Preview (with Copy). New has basic inline filters (Entity, Message, Plugin, Mode, Exceptions, From/To dates) + 3 quick filter pills (Last Hour, Exceptions Only, Long Running). | `$TEMP/legacy-plugintraces.png`, `$TEMP/new-plugintraces.png` | Critical (PT-13 confirmed) |
| P2-PT-03 | Legacy Quick Filters has **8 presets** vs new's **3 pills**. Missing from new: Success Only, Last 24 Hours, Today, Async Only, Sync Only, Recursive Calls. | — | Important |
| P2-PT-04 | Legacy columns: Status, Started, Duration, Operation, Plugin, Entity, Message, Depth, Mode (9). New columns could not be verified (no data), but toolbar/layout suggests similar set. | — | — (info) |
| P2-PT-05 | Legacy detail panel tabs: Overview, Details, Timeline, Raw Data (4 tabs). New detail panel tabs: Details, Exception, Message Block, Configuration, Timeline (5 tabs). Different organization — new splits into more granular tabs. | — | Minor |
| P2-PT-06 | New has "Maker Portal" button and "Delete selected" / "Delete older than..." submenu — legacy only has "Delete" dropdown. | — | — (info, new feature) |

### Panel: Data Explorer

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-DE-01 | Legacy Data Explorer opens as **text editor tabs** (Untitled files), not a webview panel. Cannot do side-by-side comparison — fundamentally different UX paradigm. | `$TEMP/legacy-full-sidebar.png` | — (info) |
| P2-DE-02 | New Data Explorer query execution works: `SELECT TOP 10 * FROM account` returned 10 rows in 1.041ms. Results table has blue header, row striping, "Load More" pagination, "Filter results" search. | `$TEMP/new-dataexplorer-results.png` | — (info) |
| P2-DE-03 | Results columns very wide for `SELECT *` queries — horizontal scrollbar needed. Many cells show "Default Value" for optionset label columns, which could confuse users. | `$TEMP/new-dataexplorer-results.png` | Cosmetic |
| P2-DE-04 | Status bar shows useful info: "Ready  10 rows (more available)  In 1.041ms via Dataverse". | — | — (info) |

### Panel: Metadata Browser

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-MB-01 | Entity count: legacy **855 entities**, new **789 entities** (467 custom, 322 system). Difference of 66 entities — new may be filtering out some system entities. | `$TEMP/legacy-metadata.png`, `$TEMP/new-metadata.png` | Important |
| P2-MB-02 | Legacy tabs: Attributes, Keys, **1:N Relationships, N:1 Relationships, N:N Relationships**, Privileges, Choice Values (7 tabs). New tabs: Attributes, **Relationships** (combined), Keys, Privileges, Choices (5 tabs). Confirms Phase 1 MB-03. | — | Important (MB-03 confirmed) |
| P2-MB-03 | New combined Relationships tab columns: "Schema Name ▲, Type, Related Entity, Lookup Field, Cascade Delete". The "Type" column distinguishes 1:N/N:1/N:N. Missing for N:N: Intersect Entity, Entity1/Entity2 Attribute columns. | — | Important (MB-03 confirmed) |
| P2-MB-04 | New attribute columns (7): Name, Display Name, Type, Required, Custom, Max Length, Description. Legacy attribute columns (5): Display Name, Logical Name, Type, Required, Max Length. New adds **Custom** and **Description** columns — useful additions. | `$TEMP/legacy-metadata-account.png`, `$TEMP/new-metadata-account2.png` | — (info, improvement) |
| P2-MB-05 | New Choices tab shows "Entity Choices" (per-entity option sets with Attribute, Option Set Name, Values Count) and "Global Option Sets" section. For Account entity, global section shows "No global option sets". | `$TEMP/new-metadata-choices.png` | — (info) |
| P2-MB-06 | Entity list: legacy has "ENTITIES 855" header with count badge. New has no explicit count badge on section header — count only in status bar. | — | Cosmetic |
| P2-MB-07 | New has "Custom Only" checkbox filter in entity list — legacy does not. Good improvement. | — | — (info, improvement) |

### Panel: Connection References

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-CR-01 | Data model difference confirmed at runtime: Legacy shows **flow→CR relationships** (3 rows: flow name + CR info), new shows **CRs only** (2 rows: just CRs). Phase 1 CR-01/CR-02 confirmed. | `$TEMP/legacy-connref.png`, `$TEMP/new-connref.png` | Critical (CR-01/CR-02 confirmed) |
| P2-CR-02 | Legacy columns (8): Flow Name, CR Display Name, Connection Reference, Status, Flow Managed, Flow Modified, CR Managed, CR Modified On. New columns (6): Display Name, Logical Name, Connector, Status, Managed, Modified On. New lacks all flow-related columns. | — | Critical (CR-02 confirmed) |
| P2-CR-03 | Status semantics differ: legacy shows "Valid" (connection health), new shows "unbound" (binding state). Both are useful but different perspectives. | `$TEMP/new-connref.png` | Important (CR-05 confirmed) |
| P2-CR-04 | Solution default: legacy "Default Solution", new "All Solutions". Confirmed CR-09. | — | Minor (CR-09 confirmed) |
| P2-CR-05 | New has "Analyze" button and "Connector" column — improvements over legacy. New also shows connector provider paths (e.g., `/providers/Microsoft.PowerApps/api...`). | — | — (info, improvement) |

### Panel: Environment Variables

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-EV-01 | Record count matches: both show **7 environment variables**. Data parity is good. | `$TEMP/legacy-envvars3.png`, `$TEMP/new-envvars.png` | — (info) |
| P2-EV-02 | Date format: legacy "12/11/2025, 4:00:36 AM" (locale with time), new "Dec 11, 2025" (date only, no time). Confirms EV-22. | — | Important (EV-22 confirmed) |
| P2-EV-03 | New footer shows "7 environment variables — 1 overridden" — informative count not in legacy. | — | — (info, improvement) |
| P2-EV-04 | New has "Export" button — legacy does not. | — | — (info, improvement) |
| P2-EV-05 | Column order slightly different: new moves "Managed" to last column. Same 7 columns in both. | — | Cosmetic |

### Panel: Web Resources

| # | Finding | Screenshot | Severity |
|---|---------|------------|----------|
| P2-WR-01 | New renders **5000 DOM rows** (all in DOM, no virtual scrolling). Legacy renders ~43 at a time via virtual table. Confirms CC-02 — this WILL cause performance problems in production environments with 60K+ web resources. | `$TEMP/legacy-webresources.png`, `$TEMP/new-webresources.png` | Critical (CC-02 confirmed) |
| P2-WR-02 | New loads 5000 web resources (capped at Dataverse page size). Legacy appears to load more (footer shows "0 records" — possible legacy counter bug, but virtual scroll implies more data available). | — | Important |
| P2-WR-03 | New has colored **type badges**: JavaScript (green), Image/SVG (pink/magenta), XML (blue/purple). Legacy shows plain text type. Nice visual improvement. | `$TEMP/new-webresources.png` | — (info, improvement) |
| P2-WR-04 | Legacy has "Show All" button (to show all solutions' web resources). New does not have this. | — | Minor |
| P2-WR-05 | Legacy footer shows "0 records" despite visible data — possible legacy bug with virtual scroll counter. New footer shows "5000 web resources — 5000 text — 4998 managed" — comprehensive. | — | — (info) |

---

### Phase 2 Summary

**Runtime-only findings (not caught by Phase 1 code review):**

| Category | Finding | Severity |
|----------|---------|----------|
| Data completeness | Solutions: 8 vs 511 (isvisible filter already implemented, contrary to Phase 1 S-05 which said it was missing) | Important |
| Data completeness | Import Jobs: 50 vs 251 ($top=50 limit) | Critical |
| Data completeness | Metadata Browser: 789 vs 855 entities (66 missing) | Important |
| DOM performance | Web Resources: 5000 DOM rows confirmed (CC-02) | Critical |
| Phase 1 correction | S-05 stated "No isvisible filter like legacy" — runtime shows the **opposite**: new already filters to visible, legacy shows everything | Important (correction) |
| Phase 1 correction | PT-13 stated advanced query builder "DOES NOT EXIST, must build from scratch" — new actually has basic filter panel (Entity, Message, Plugin, Mode, Exceptions, Date range) + 3 quick filter pills. Missing: condition builder, OData preview, 5 additional quick presets | Important (correction) |
| Legacy bug | Web Resources footer count shows "0 records" despite loaded data — legacy virtual scroll counter bug | — (info) |

**Phase 1 findings confirmed at runtime:**
- CC-02 (no virtual scrolling) — confirmed in Web Resources with 5000 DOM rows
- CC-06 (header styling) — flat/muted vs legacy blue
- CC-07 (row striping missing) — confirmed across all panels
- CR-01/CR-02 (CR data model) — flow→CR relationships missing
- CR-09 (solution filter default) — "All Solutions" vs "Default Solution"
- EV-22 (date format missing time) — confirmed
- IJ-45 (Completed vs Succeeded) — confirmed
- MB-03 (combined relationships tab) — confirmed, missing N:N-specific columns
- PT-13 (advanced query builder) — partially confirmed (basic exists, advanced missing)

**New extension improvements confirmed at runtime:**
- Solutions: Component drill-down, managed toggle, informative footer counts
- Import Jobs: Same column structure, consistent sort behavior
- Data Explorer: Monaco editor, query execution, Load More pagination, performance metrics
- Metadata Browser: Custom/Description columns, Custom Only filter, Choices tab
- Connection References: Analyze button, Connector column with provider paths
- Environment Variables: Export button, overridden count, override indicators
- Web Resources: Colored type badges, comprehensive footer stats

---

## I4 Transparency Audit

**Constitution amendment:** I4 added — "Never silently hide, truncate, or filter data."
**Audit scope:** All services, RPC handlers, MCP tools, and webview panels.

### Shared Fix Pattern

Every `ListAsync` must return a result object with `{ Items, TotalCount, FiltersApplied }` instead of a bare `List<T>`. Every status bar must show "X of Y" when any filter/truncation is active.

### Silent Truncations (no total count returned)

| ID | Service/Layer | Default | Decision |
|----|---------------|---------|----------|
| V-01 | ImportJobService | `top=50` | **Fix** — remove cap, load all with paging |
| V-02 | WebResourceService | `top=5000` | **Fix** — progressive loading, load all |
| V-06 | UserService | `top=100` | **Fix** — return totalCount |
| V-08 | PluginTraceService | `top=100` | **Fix** — wire existing `CountAsync()`, show "X of Y" |
| V-10 | RPC pluginTraces/list | `top=100` | **Fix** — pass through totalCount |
| V-11 | RPC importJobs/list | `top=50` | **Fix** — same as V-01 |
| V-12 | MCP web_resources | `top=100` (vs service 5000) | **Fix** — return totalCount, align defaults |
| V-13 | MCP plugin_traces | `maxRows=50`, cap 500 | **Fix** — return totalCount |
| V-14 | MCP plugins_list | `maxRows=50`, types `top=100` | **Fix** — return totalCount, expose types limit |

### Silent Filters (no parameter to reverse, no count)

| ID | Service | Filter | Decision |
|----|---------|--------|----------|
| V-03 | MetadataService | `IsIntersect != true` | **Fix** — add `includeIntersect` param, show "X of Y (N intersection hidden)" |
| V-04 | SolutionService | `isvisible eq true` | **Fix** — add Visible/All toggle, show count |
| V-15 | FlowService | `Category=5,6` (cloud only) | **Fix** — add `includeClassic` param, show "cloud flows only" in status |
| V-16 | ConnectionReferenceService | `StateCode=0` | **Fix** — add `includeInactive` param, show count |
| V-17 | EnvironmentVariableService | `statecode=0` | **Fix** — add `includeInactive` param, show count |
| V-18 | DeploymentSettingsService | Type != Secret | **Visibility only** — show "N secrets excluded" (security-correct, no toggle) |
| V-19 | RoleService | `ParentRoleId IS NULL` | **Visibility only** — show "root roles only" (dedup-correct, low priority) |

### Client-Side Gaps

| ID | Panel | Issue | Decision |
|----|-------|-------|----------|
| V-21 | Plugin Traces | Search shows count but not "X of Y total" | **Fix** — show "X of Y traces" |

### Already Compliant

- V-05: Solutions "Include Managed" toggle — visible + reversible
- V-20: Metadata Browser "Custom Only" checkbox — shows "N of M" with count breakdown

---

## Phase 2 Corrections

### S-05: isvisible filter already implemented
Phase 1 stated "No `isvisible` filter like legacy." Runtime shows the **opposite**: new already filters to visible (8 solutions), legacy shows everything (511). The `isvisible eq true` filter is at `SolutionService.cs:187`. Per I4, we need a Visible/All toggle so users can reach the full dataset.

### PT-13: Basic filters already exist
Phase 1 stated "DOES NOT EXIST, must build from scratch." Actually, the new panel has: Entity, Message, Plugin text inputs, Mode dropdown, Exceptions checkbox, date range, and 3 quick filter pills (Last Hour, Exceptions Only, Long Running). What's missing: condition builder with field/operator/value rows, AND/OR logic, 5 more quick presets (Success Only, Last 24 Hours, Today, Async Only, Sync Only, Recursive Calls). The OData preview tab is **not applicable** — we use the SDK, not OData. Replace with "Query Preview" showing generated filter conditions in readable format, or skip the preview tab entirely.

### P2-IJ-01: Import Jobs $top=50 limit
New loads 50 of 251 jobs. Root cause: `ImportJobService.cs:39` defaults to `top=50`, RPC passes through. Fix: remove cap, implement paging cookie progressive loading (same pattern as `QueryExecutor.cs`).

### P2-MB-01: 66 entity gap — intentional
New filters `IsIntersect != true` at `DataverseMetadataService.cs:61`, removing 66 N:N junction tables. This is correct behavior — intersection entities are noise. But per I4, needs visibility: add `includeIntersect` toggle and show count.

---

## Next Steps

1. ~~**Phase 2: Interactive Playwright verification** — side-by-side CDP testing with real data~~ Done
2. **Owner walkthrough** — review all findings, add missed items
3. **Implementation plan** — sequence:
   - **Layer 0:** Service return types (`ListResult<T>` with Items/TotalCount/FiltersApplied)
   - **Layer 1:** Quick wins (retainContextWhenHidden, enableFindWidget, CSS fixes)
   - **Layer 2:** Shared DataTable upgrades (virtual scroll, cell selection, sort, tooltips, striping, search)
   - **Layer 3:** Service layer fixes (remove caps, add paging, add `include*` params, return counts)
   - **Layer 4:** Per-panel builds (PT timeline, PT query builder, MB global choices, MB properties, sync deployment, CR flow relationships)
   - **Layer 5:** Per-panel fixes (all remaining per-panel items)
