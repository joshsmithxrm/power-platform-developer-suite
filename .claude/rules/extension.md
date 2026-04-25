---
paths: ["src/PPDS.Extension/**"]
---

# Extension Conventions

## Message Architecture

- All message types in `src/panels/webview/shared/message-types.ts` as discriminated unions
- Both host and webview switches MUST use `assertNever` for exhaustive checking (compile error if case missed)
- Host-to-webview type: `{Feature}PanelHostToWebview`; webview-to-host type: `{Feature}PanelWebviewToHost`

## File Organization

- Host panel: `src/panels/{Feature}Panel.ts` (extends `WebviewPanelBase<TIn, TOut>`)
- Webview entry: `src/panels/webview/{feature}-panel.ts` (IIFE bundle)
- Styles: `src/panels/styles/{feature}-panel.css` (`@import './shared.css'`)
- Discover panels dynamically via glob, not hardcoded lists

## HTML Structure

- Content wrapper uses `class="content"` (not `class="panel-content"`) — all panels follow this convention

## Toolbar Layout Convention

Standard button order (left to right):

```
Refresh | Panel Actions | Maker Portal | spacer | Search | Filters/Toggles | Sort | EnvPicker
```

- **Refresh** — always first
- **Panel-specific actions** — next (e.g., Analyze, Sync, Delete, Export, Publish, New Table)
- **Maker Portal** — last action button before the spacer
- **`<span class="toolbar-spacer">`** — pushes everything after it to the right
- **Search** — `<input class="toolbar-search">`, always first in the right group
- **Filters/Toggles** — segmented controls, checkboxes (`class="toolbar-checkbox"`), solution filters
- **Sort** — `<select class="toolbar-select">` when present
- **EnvPicker** — always last (`${getEnvironmentPickerHtml()}`)

Shared CSS classes (defined in `shared.css`, not in panel CSS):
- `.toolbar-search` — search input styling
- `.toolbar-checkbox` — label+checkbox toggle in toolbar

## Code Rules

- External CSS only (no inline styles) — VS Code silently drops inline scripts exceeding ~32KB
- External JS only (IIFE bundles via esbuild) — same size limit applies
- Daemon communication via `daemonClient.ts` — never spawn processes directly
- Use shared utilities: `dom-utils.ts` (escapeHtml), `filter-bar.ts` (FilterBar<T>), `data-table.ts` (DataTable<T>)
- Use `getNonce()` from `webviewUtils.ts` for CSP nonces
- No `innerHTML` with untrusted data — use `escapeHtml`/`escapeAttr` from `dom-utils.ts` (Constitution S1)

## Environment Theming

- Type-based top border color (Dev/Test/Prod)
- Color-based left border from environment config
- Detail pane pattern: tables with drill-down row selection

## LogOutputChannel

- Writes to `exthost/<extId>/Name.log`, NOT `N-Name.log` — verify log file location when debugging
