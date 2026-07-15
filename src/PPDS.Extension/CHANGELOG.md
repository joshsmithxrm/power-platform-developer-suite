# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.6.0] - 2026-07-15

Stable-channel release (even minor per odd/even convention; 1.5.x is the pre-release channel).

### Fixed
- **Metadata Browser and 4 other panels rendered only the first ~22 rows, then an empty void** — the shared `DataTable` virtual scroll sized its window from an inner wrapper that was never height-bounded, so it never scrolled and only the initial buffer rendered. The wrapper is now a bounded flex-column host, so long lists (attributes, import jobs, environment variables, connection references, web resources) render and scroll fully (#1365, #1366).
- **Metadata Browser Choices tab** — expanding a choice row no longer collapses the table columns into unreadable slivers (an over-wide colspan under `table-layout:fixed`), and every entity choice / global option set now renders instead of stopping after ~21 rows (tables were populated while detached from the DOM) (#1367).
- **Single-row tables rendered empty** in every DataTable panel due to an initial visible-range sentinel colliding with a real single-row range (#1367).

### Added
- **Full attribute metadata in the properties panel** — clicking an attribute now shows the complete, grouped property set (General / Flags / Behavior / Security / Type Details / System) instead of a curated dozen; booleans display for both true and false. Backed by a widened daemon attribute contract (bundled CLI 1.4.0) (#1369).
- **Auxiliary attributes are identified and managed** — lookup name/yomi companion attributes (which carry `AttributeOf`) are hidden by default with a disclosed **"Hide auxiliary (N)"** toggle that restores them in one click; when shown they are visually marked and labelled `aux of <parent>` instead of appearing as broken blank-name fields (#1368).

### Changed
- **Metadata Browser tab order** — Configuration now precedes Attributes and is the landing tab when selecting an entity (entity-level detail before the attribute drill-down) (#1370).
- **Bundled CLI updated to 1.4.0** — carries the full-fidelity attribute metadata contract and the `attributeOf` marker that power the properties panel and auxiliary handling above (#1369).

## [1.4.1] - 2026-07-14

### Changed
- **Bundled CLI updated to 1.3.0** — brings plugin-step functional-identity matching for deploy/diff/clean, a clear error instead of a silent global step when a configured entity has no SDK message filter (surfaced through the plugin panel's register/deploy flows), and working `plugins extract` from the extension's bundled single-file CLI (#1294, #1295, #1332).

## [1.4.0] - 2026-06-17

### Added
- **Connection picker for connection-reference binding** — the Connection References panel detail card gains a Change/Bind button (disabled for managed CRs) that opens a modal picker filtered by connector id and supports unbind via the empty option. Backed by new `connectionReferences/bind` and `connections/list` daemon RPC endpoints and `IConnectionReferenceService.BindAsync`, which writes `connectionid` on the Dataverse `connectionreference` entity (#592, #1058).

### Fixed
- **Plugin Traces "Status" Advanced filter is now honored** — the conditions switch had no `Status` case, so `Status = Exception`/`Success` silently fell through and the daemon returned all rows regardless of the selection; it now maps Equals/Not Equals against `Exception`/`Success` to the `hasException` filter. Also guards against an invalid `Created On` date (#1006, #1040).
- **Command palette grouping** — 17 commands that previously appeared ungrouped now group under the **PPDS** category in the command palette, matching the other commands (#1263).

## [1.2.0] - 2026-04-26

Stable-channel release (even minor per odd/even convention). Post-GA polish across all panels.

### Added
- **Diagnostic markers** — Monaco squiggles for SQL and FetchXML syntax errors in notebooks (#650, #966).
- **Metadata Browser** entity configuration tab and global optionset details (#924).
- **Per-panel profile and environment scoping** — each panel tracks its own active Dataverse context independently (#887, #888, #903, #936).

### Changed
- Standardized toolbar layout and search bar placement across all panels (#898, #899, #933).

### Fixed
- Profiles tree view no longer stuck in infinite spinner — added RPC timeouts and error states (#904, #909).
- Sync Deployment Settings no longer hangs on large environments (#893, #908).
- Import Jobs panel no longer opens two XML documents on single click (#891, #919).
- Plugin Traces toggle now shows visual feedback when changing trace settings (#895, #919).
- Environment Variables and Connection References "Active only / All" toggle clarified (#894, #919).
- Solutions panel no longer shows fabricated "All Solutions" entry; component type names auto-synced (#890, #892, #916).
- Solution filter state now syncs to webview on panel open (#902, #918).
- Plugin Registration toolbar buttons and tree collapse toggles styled correctly (#896, #897, #922).
- Plugin Registration help link updated to main docs page (#900, #921).

## [1.0.0] - 2026-04-18

> Enjoying PPDS v1.0? Please [leave a review on the Marketplace](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite&ssr=false#review-details) — reviews are the single biggest conversion signal for new users.

First stable release. Per the odd/even minor convention, `1.0.0` is a stable-channel release on the VS Code Marketplace. Consolidates `0.5.0` (daemon-backed ground-up rebuild) and `0.7.0` (pre-release feature pass) into a single stable baseline.

### Added

- **Daemon-backed architecture** — Thin VS Code UI layer delegates all operations to the `ppds serve` daemon via JSON-RPC. Authentication managed by the CLI profile store.
- **Profile and environment management** — Create, delete, rename, select profiles from the sidebar. Browse and select Dataverse environments. Status-bar profile indicator with click-to-switch quick pick; shows a friendly name for unnamed profiles. Environment color theming — a 3-pixel top border on panel toolbars driven by environment type (Dev / Test / Prod) and a 4-pixel left border driven by per-environment color, with per-panel persistence.
- **Notebooks (`.ppdsnb`)** — SSMS-like query experience with SQL and FetchXML cells, IntelliSense (autocomplete for tables, columns, FetchXML elements/attributes), FetchXML syntax highlighting, query history, CSV/JSON export per cell, notebook-environment selection.
- **Data Explorer panel** — Webview for quick ad-hoc queries with virtual scrolling for large result sets.
- **Solutions panel** — Expandable component groups with managed/visible toggles and Maker Portal buttons.
- **Plugin Traces panel** — Split-pane interface with timeline waterfall, trace-level management, volume warnings, filter bar, age-based cleanup, date-range filters, and Maker Portal links.
- **Metadata Browser panel** — Split-pane entity explorer with five-tab detail (Attributes, Relationships, Keys, Privileges, Choices), global option-set aggregation, per-panel environment picker with search/filter.
- **Connection References panel** — Status badges (Connected / Error / N/A), detail pane with related flows, orphan detection.
- **Environment Variables panel** — Type-aware editing, override / missing-value indicators, persisted solution filter.
- **Web Resources panel** — Type-color badges, solution filtering, text-only toggle, publish-selected button, modified-on detection, binary-type protection.
- **Import Jobs panel** — Search bar, Operation Context column, record count with filtered-status display.
- **Plugin Registration panel** — Tree view with enable/disable/unregister step actions and binary download.
- **Shared `SolutionFilter` component** — Reused across multiple panels for consistent filter UX.
- **`ListResult<T>` and `DataTable` upgrades** — Virtual scrolling for large result sets, three-state column sorting with `sortValue` support for dates/numbers, cell selection plus `Ctrl+A` / `Ctrl+C` TSV copy via `SelectionManager`, header styling, row striping, cell tooltips, full-row search matching. All eight panels gain `findWidget` and `retainContextWhenHidden`.
- **Query hints banner** — RPC response includes `dataSources` and `appliedHints`; webview renders a cross-environment banner when multi-profile queries are detected.
- **Environment Details command** — Org and connection info for the active environment.
- **Environment-variable authentication** — Stateless CI/CD via `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL`. Takes precedence over profiles.
- **Session persistence** — Solution-filter selections restored across sessions via `globalState`.
- **UX polish across all panels** — Search bars on Import Jobs, Plugin Traces, Connection References, Environment Variables, and Web Resources; Managed/Visible toggles for Solutions; `Unknown` status labels instead of `N/A`; ISO timestamp formatting; expandable detail rows with chevron toggles; `X of Y` record count display; Maker Portal buttons on Solutions and Plugin Traces; title-case column headers.
- **FetchXML parsing performance** — Lazy regex for multi-entity queries reduces parse time on complex FetchXML.
- **"Open Documentation" command** — Opens the canonical GitHub Pages docs site.

## [0.7.0] - 2026-04-17 (pre-release)

Major feature pass restoring most legacy v0.3.4 panels and adding cross-cutting UX polish across all surfaces. Per the odd-minor pre-release convention, `0.7.0` is a pre-release; the next stable release is `1.0.0`.

### Added

- **Plugin Traces panel** — Split-pane interface with timeline waterfall, trace-level management, volume warnings, filter bar, age-based cleanup, date-range filters, and Maker Portal links.
- **Metadata Browser panel** — Split-pane entity explorer with five-tab detail (Attributes, Relationships, Keys, Privileges, Choices), global option-set aggregation, per-panel environment picker with search/filter.
- **Connection References panel** — Status badges (Connected / Error / N/A), detail pane with related flows, orphan detection.
- **Environment Variables panel** — Type-aware editing, override / missing-value indicators, persisted solution filter.
- **Web Resources panel** — Type-color badges, solution filtering, text-only toggle, publish-selected button, modified-on detection, binary-type protection.
- **Import Jobs panel** — Search bar, Operation Context column, record count with filtered-status display.
- **Shared `SolutionFilter` component** — Reused across multiple panels.
- **Session persistence for panel state** — Solution-filter selections restored across sessions via `globalState`.
- **Environment color theming** — a 3-pixel top border on panel toolbars driven by environment type (Dev / Test / Prod) and a 4-pixel left border driven by per-environment color, with per-panel persistence.
- **Status-bar profile indicator** — Shows the active profile name and supports click-to-switch via a quick pick; hidden when the daemon is not ready.
- **Environment Details command** — Org and connection info for the active environment (guarded against non-active selection).
- **`ListResult<T>` and `DataTable` upgrades** — Virtual scrolling for large result sets, three-state column sorting with `sortValue` support for dates/numbers, cell selection plus `Ctrl+A`/`Ctrl+C` TSV copy via `SelectionManager`, header styling, row striping, cell tooltips, full-row search matching. All eight panels gain `findWidget` and `retainContextWhenHidden`.
- **Query hints banner** — RPC response now includes `dataSources` and `appliedHints`; webview renders a cross-environment banner when multi-profile queries are detected.
- **Environment-variable authentication** — CI/CD via `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL`. Stateless and takes precedence over profiles.

### Changed

- **All eight panels** — UX audit fixes (28 findings): search bars added to Import Jobs, Plugin Traces, Connection References, Environment Variables, Web Resources; Managed/Visible toggles for Solutions; status text labels (e.g., `Unknown` instead of `N/A`); ISO timestamp formatting; expandable detail rows with chevron toggles; `X of Y` record count display; Maker Portal buttons on Solutions and Plugin Traces; title-case headers (no uppercase transform).
- **FetchXML parsing** — Lazy regex for multi-entity queries, performance improvement.
- **CSS class standardization** — `.panel-content` → `.content` in `PluginsPanel` and `PluginTracesPanel` for naming consistency.
- **`IMetadataService` consumption** — Renamed to `IMetadataQueryService` ahead of forthcoming `IMetadataAuthoringService` UI.

### Fixed

- **Multi-panel webview targeting** — Targets the active panel correctly.
- **Environment-type resolution** — Reads from config when rendering toolbar borders.
- **Webview message handlers** — Guarded against VS Code internal messages.
- **Daemon binary shadow-copy** — In debug mode, prevents file-locking issues during reinstalls.
- **Profile status bar** — Shows a friendly name for unnamed profiles.
- **FetchXML nested-filter parsing** — Documented edge case and limitation.
- **CSS reset** — `box-sizing` reset applied globally across all panels.

## [0.5.0] - 2026-03-03

Complete ground-up rebuild of the extension. The new architecture uses a thin VS Code UI layer that delegates all operations to the `ppds serve` daemon via JSON-RPC, replacing the previous self-contained approach.

### Added

- Profile management — create, delete, rename, select profiles from the sidebar
- Environment discovery — browse and select Dataverse environments
- Solutions browser — explore solutions with expandable component groups
- Dataverse notebooks (.ppdsnb) — SSMS-like query experience with SQL and FetchXML
- SQL IntelliSense — autocomplete for tables and columns
- FetchXML IntelliSense — autocomplete for elements and attributes
- FetchXML syntax highlighting
- Data Explorer — webview panel for quick ad-hoc queries
- Query history — automatic persistence of executed queries
- Results export — save query results as CSV or JSON
- Environment status bar indicator
- Virtual scrolling for large result sets

### Changed

- Architecture: thin UI layer delegating to `ppds serve` daemon via JSON-RPC (was self-contained with direct Dataverse API calls)
- Build system: esbuild (was webpack)
- Test framework: Vitest + Playwright (was Jest)
- Authentication: managed by CLI profiles (was MSAL in-extension)

### Removed

- Direct Dataverse API calls (now handled by daemon)
- In-extension MSAL authentication (now managed by CLI)
- Plugin Trace viewer (will be re-added in a future release)
- Connection References viewer (will be re-added in a future release)
- Environment Variables viewer (will be re-added in a future release)
- Metadata Browser (will be re-added in a future release)
- Web Resources viewer (will be re-added in a future release)
- Import Job viewer (will be re-added in a future release)

## [0.3.4] - 2026-01-01

_Last stable release of the legacy architecture. See [archived repository](https://github.com/joshsmithxrm/power-platform-developer-suite/tree/archived) for full history._

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Extension-v1.4.1...HEAD
[1.6.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Extension-v1.4.1...Extension-v1.6.0
[1.4.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Extension-v1.4.0...Extension-v1.4.1
[1.4.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Extension-v1.2.0...Extension-v1.4.0
[1.2.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Extension-v1.2.0
