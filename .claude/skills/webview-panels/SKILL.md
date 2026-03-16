---
name: webview-panels
description: Building or modifying VS Code webview panels. Use when creating new panels, adding features to existing panels, or fixing bugs in panel code. Establishes typed TypeScript webview scripts, external CSS, discriminated union message protocols with exhaustive switch checking.
---

# Webview Panel Development

## The Rules

1. **All webview logic lives in TypeScript files** under `src/panels/webview/`, bundled by esbuild as IIFE for browser.
2. **All CSS lives in external `.css` files** under `src/panels/styles/`, bundled by esbuild.
3. **All messages are typed** with discriminated unions in `src/panels/webview/shared/message-types.ts`. Both host and webview switches use `assertNever` for exhaustive checking.

VS Code silently drops inline `<script>` tags exceeding ~32KB. External scripts have no size limit.

## Architecture

```
src/panels/
  QueryPanel.ts                    ← host-side panel (extends WebviewPanelBase<TIn, TOut>)
  SolutionsPanel.ts                ← host-side panel
  WebviewPanelBase.ts              ← abstract base with typed postMessage + AbortSignal
  environmentPicker.ts             ← shared HTML generator + QuickPick helper
  monacoUtils.ts                   ← detectLanguage, mapCompletionKind, mapCompletionItems
  querySelectionUtils.ts           ← getSelectionRect, isSingleCell, sanitizeValue, buildTsv
  webviewUtils.ts                  ← shared webview helper utilities
  monaco-entry.ts                  ← Monaco editor browser entry (IIFE bundle)
  monaco-worker.ts                 ← Monaco editor worker entry

  webview/                         ← browser-side TypeScript (tsconfig.webview.json)
    query-panel.ts                 ← Query Panel webview entry point
    solutions-panel.ts             ← Solutions Panel webview entry point
    shared/
      message-types.ts             ← discriminated unions for ALL panel messages
      dom-utils.ts                 ← escapeHtml, escapeAttr, cssEscape, formatDate, sanitizeValue
      error-handler.ts             ← centralized webview error handler (onerror, unhandledrejection)
      filter-bar.ts                ← generic FilterBar<T> — debounced text filtering with count
      selection-utils.ts           ← getSelectionRect, isSingleCell, sanitizeValue, buildTsv
      vscode-api.ts                ← typed getVsCodeApi<T>() wrapper
      assert-never.ts              ← exhaustive switch helper

  styles/                          ← CSS files (esbuild bundles @import)
    shared.css                     ← common styles (toolbar, status bar, spinner, env picker)
    query-panel.css                ← @import './shared.css' + Query Panel specific
    solutions-panel.css            ← @import './shared.css' + Solutions Panel specific

esbuild.js                         ← builds host + webview TS + CSS entry points
dist/
  query-panel.js                   ← built webview bundle (IIFE)
  query-panel.css                  ← built CSS bundle
  solutions-panel.js / .css        ← same pattern
  monaco-editor.js / .css          ← Monaco bundle
  editor.worker.js                 ← Monaco worker
```

## Creating a New Panel

### 1. Define message types

Add to `src/panels/webview/shared/message-types.ts`:

```typescript
export type FooPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'someAction'; payload: string };

export type FooPanelHostToWebview =
    | { command: 'dataLoaded'; items: SomeDto[] }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

### 2. Create the host-side panel

Create `src/panels/FooPanel.ts`:

```typescript
import type { FooPanelWebviewToHost, FooPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class FooPanel extends WebviewPanelBase<FooPanelWebviewToHost, FooPanelHostToWebview> {
    // ... constructor, show(), dispose() ...

    // Typed message handler — no `as string` casts needed
    private setupMessageHandler(): void {
        this.disposables.push(
            this.panel!.webview.onDidReceiveMessage(
                async (message: FooPanelWebviewToHost) => {
                    switch (message.command) {
                        case 'ready': await this.initialize(); break;
                        case 'refresh': await this.loadData(); break;
                        case 'someAction': await this.handleAction(message.payload); break;
                        default: assertNever(message); // compile error if case missed
                    }
                }
            )
        );
    }

    getHtmlContent(webview: vscode.Webview): string {
        const cssUri = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'dist', 'foo-panel.css'));
        const jsUri = webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'dist', 'foo-panel.js'));
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <link rel="stylesheet" href="${cssUri}">
</head>
<body>
    <!-- HTML structure only — no inline CSS, no inline JS -->
    <div class="toolbar">...</div>
    <div class="content" id="content"></div>
    <div class="status-bar"><span id="status-text">Ready</span></div>
    <script nonce="${nonce}" src="${jsUri}"></script>
</body>
</html>`;
    }
}
```

### 3. Create the webview script

Create `src/panels/webview/foo-panel.ts`:

```typescript
import type { FooPanelWebviewToHost, FooPanelHostToWebview } from './shared/message-types.js';
import { escapeHtml } from './shared/dom-utils.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { assertNever } from './shared/assert-never.js';

const vscode = getVsCodeApi<FooPanelWebviewToHost>();

// DOM element references
const content = document.getElementById('content')!;
const statusText = document.getElementById('status-text')!;

// Message handler — exhaustive switch
window.addEventListener('message', (event: MessageEvent<FooPanelHostToWebview>) => {
    const msg = event.data;
    switch (msg.command) {
        case 'dataLoaded': renderData(msg.items); break;
        case 'error': showError(msg.message); break;
        case 'daemonReconnected': /* handle */ break;
        default: assertNever(msg); // compile error if case missed
    }
});

function renderData(items: SomeDto[]): void { /* ... */ }
function showError(message: string): void { /* ... */ }

// Signal ready
vscode.postMessage({ command: 'ready' });
```

### 4. Create the CSS file

Create `src/panels/styles/foo-panel.css`:

```css
@import './shared.css';

/* Foo Panel specific styles */
.content { flex: 1; overflow: auto; }
/* ... */
```

### 5. Add esbuild entry points

Add to `esbuild.js`:

```javascript
const fooPanelCtx = await esbuild.context({
    entryPoints: ['src/panels/webview/foo-panel.ts'],
    bundle: true, format: 'iife', minify: production,
    sourcemap: !production, sourcesContent: false,
    platform: 'browser', outfile: 'dist/foo-panel.js', logLevel: 'warning',
});

const fooPanelCssCtx = await esbuild.context({
    entryPoints: ['src/panels/styles/foo-panel.css'],
    bundle: true, minify: production,
    outfile: 'dist/foo-panel.css', logLevel: 'warning',
});
```

Add both to the `watch()`, `rebuild()`, and `dispose()` arrays.

### 6. Register in extension.ts and package.json

- Register the command handler in `src/extension.ts`
- Add command contribution in `package.json`

## Naming Conventions

| File | Convention | Example |
|---|---|---|
| Host panel class | `{Name}Panel.ts` | `PluginTracePanel.ts` |
| Webview entry | `src/panels/webview/{name}-panel.ts` | `plugin-trace-panel.ts` |
| Panel CSS | `src/panels/styles/{name}-panel.css` | `plugin-trace-panel.css` |
| Built JS output | `dist/{name}-panel.js` | `dist/plugin-trace-panel.js` |
| Built CSS output | `dist/{name}-panel.css` | `dist/plugin-trace-panel.css` |

## What Goes Where

| Content | Location | Why |
|---------|----------|-----|
| DOM manipulation, event handlers, message handling | `webview/{name}-panel.ts` | Browser context, typed |
| CSS styles | `styles/{name}-panel.css` | External, linted by Stylelint |
| HTML structure | `getHtmlContent()` template literal | Small, just structure + links |
| RPC calls, VS Code API, business logic | `{Name}Panel.ts` | Host context, Node.js |
| Message type definitions | `webview/shared/message-types.ts` | Shared between host and webview |
| Shared DOM utilities | `webview/shared/dom-utils.ts` | DRY across all panels |
| Shared HTML generators | `environmentPicker.ts` | Reused across panels |
| Debounced text filtering | `webview/shared/filter-bar.ts` | Generic, shared across panels |
| Monaco utilities (detect lang, map completions) | `monacoUtils.ts` | Pure functions, host-side |
| Selection/copy utilities | `querySelectionUtils.ts` | Pure functions, testable |

## Daemon Communication

Host-side panels call the daemon via `this.daemon` (a `DaemonClient` instance passed at construction).

### Calling RPC methods

```typescript
// All daemon methods accept an optional CancellationToken as last arg
const result = await this.daemon.querySql({
    sql, top: 100, environmentUrl: this.environmentUrl
}, token);
```

Available methods are defined in `daemonClient.ts`. Common ones: `querySql`, `queryFetch`, `queryExplain`, `queryExport`, `queryComplete`, `solutionsList`, `solutionsComponents`, `authWho`, `envList`.

### Handling daemon disconnection

WebviewPanelBase provides a reconnection hook. Subscribe in your constructor and override to auto-refresh:

```typescript
// In constructor:
this.subscribeToDaemonReconnect(daemon);

// Override to handle reconnection:
protected override onDaemonReconnected(): void {
    void this.loadData(); // re-fetch stale data
}
```

The base class sends `{ command: 'daemonReconnected' }` to the webview automatically — add it to your HostToWebview union type and handle it (e.g., show a refresh banner).

### Query cancellation

For long-running operations, use `CancellationTokenSource` from vscode-jsonrpc:

```typescript
private queryCts: CancellationTokenSource | undefined;

async executeQuery(): Promise<void> {
    this.queryCts?.cancel();
    this.queryCts = new CancellationTokenSource();
    const token = this.queryCts.token;

    const result = await this.daemon.querySql(params, token);
    if (token.isCancellationRequested) return;
    // ... use result
}

// Cancel from webview message:
case 'cancelQuery': this.queryCts?.cancel(); break;

// Cleanup in dispose:
this.queryCts?.cancel();
this.queryCts?.dispose();
```

### Panel-scoped environment

Panels can target a specific environment (not the global active one). Store `environmentUrl` as an instance property, pass it to every daemon call, and update it from the environment picker:

```typescript
case 'selectEnvironment':
    this.environmentUrl = message.url;
    await this.loadData(); // re-fetch with new env
    break;
```

## Adding/Removing a Command

When you add a new message command:

1. Add the variant to the union type in `message-types.ts`
2. The TypeScript compiler will error in BOTH the host switch and the webview switch (via `assertNever`)
3. Handle the new case in both switches
4. No cast needed — the discriminated union gives you typed access to all fields

When you remove a command:
1. Remove the variant from the union type
2. The compiler will error wherever it's still referenced
3. Remove all handling code

## Exceptions

- **Tiny config scripts** (<200 bytes) that set globals before external scripts load are OK inline:
  ```html
  <script nonce="${nonce}">self.__MONACO_WORKER_URL__ = '${workerUri}';</script>
  ```
- **Notebook cell output scripts** (`virtualScrollScript.ts`) are generated per-cell, tiny, and don't accumulate features. These are fine inline.

## Quality Gates

Before committing panel changes:
- `npm run lint` — ESLint (strict: no-floating-promises, no-explicit-any, etc.)
- `npm run lint:css` — Stylelint for CSS files
- `npm run typecheck:all` — both host and webview tsconfigs
- `npm run test` — Vitest unit tests
- **Visual verification (MANDATORY for UI changes):** After any CSS, layout, HTML template, or message wiring change, use @webview-cdp to take a screenshot and verify rendering. A passing typecheck is not proof of correct rendering. See @webview-cdp skill for the verification protocol.
- **Blind QA (recommended):** For non-trivial features, run `/qa extension` to dispatch a fresh agent that tests the panel without seeing source code.

## Reference Implementations

- QueryPanel: `src/panels/QueryPanel.ts` + `src/panels/webview/query-panel.ts` + `src/panels/styles/query-panel.css`
- SolutionsPanel: `src/panels/SolutionsPanel.ts` + `src/panels/webview/solutions-panel.ts` + `src/panels/styles/solutions-panel.css`

## Design Guidance

### Panel Anatomy

Every panel follows the same three-zone layout defined in `shared.css`:

```
┌─────────────────────────────────────────┐
│ Toolbar    [env picker] [actions]  [...] │  ← .toolbar (flex, 8px gap, border-bottom)
├─────────────────────────────────────────┤
│                                         │
│              Content area               │  ← .content (flex: 1, overflow: auto)
│         (table / tree / detail)         │
│                                         │
├─────────────────────────────────────────┤
│ Status bar: record count, timing, etc.  │  ← .status-bar (border-top, 12px font)
└─────────────────────────────────────────┘
```

**Rules:**
- Toolbar always contains the environment picker (via `environmentPicker.ts`)
- Content area gets `flex: 1` and handles its own scrolling
- Status bar shows contextual counts/timing — never empty, show "Ready" as default
- Empty state (`.empty-state`), error state (`.error-state`), and loading state (`.loading-state`) are in `shared.css` — use them, don't reinvent

### Reusable CSS Patterns

Before writing panel-specific CSS, check what already exists. Each pattern has a reference implementation — read it before building yours.

| Pattern | CSS Source | Reference Panel | Use When |
|---------|-----------|----------------|----------|
| Data table (sticky header, sort, selection) | `query-panel.css` `.results-table` | QueryPanel | Tabular data with columns |
| Tree/list (chevron expand, nested indent) | `solutions-panel.css` `.solution-list` | SolutionsPanel | Hierarchical browsing |
| Detail card (standalone, label/value grid) | `solutions-panel.css` `.detail-card` | SolutionsPanel | Inline record details |
| Detail card (nested, inside list items) | `solutions-panel.css` `.component-detail-card` | SolutionsPanel | Expandable item details |
| Filter bar (debounced input, count badge) | `query-panel.css` `.filter-bar` | QueryPanel | Filtering loaded results |
| Dropdown menu | `query-panel.css` `.dropdown-menu` | QueryPanel | Export, overflow actions |
| Context menu | `query-panel.css` `.context-menu` | QueryPanel | Right-click actions |

**Rules:**
- `@import './shared.css'` as your first line — every panel gets toolbar, status bar, states for free
- Copy the CSS pattern from the reference, don't `@import` panel-specific files into other panels
- Use `var(--vscode-*)` tokens for all colors — never hardcode hex values
- Spacing: 4/6/8/12/16/40px scale (match existing panels, don't introduce new values)
- Font sizes: 11px (labels/badges), 12px (secondary text, detail cards), 13px (body/inputs)
- Border radius: 2px (inputs, badges), 4px (menus, dropdowns)

### Environment Theming

Panels display two color accents based on the active environment:

**1. Type-based top border** (`data-env-type` attribute on `.toolbar`):
- Production → red, Sandbox → yellow, Development → green, Test → yellow, Trial → blue
- CSS rules in `shared.css` using `[data-env-type]` selectors
- Maps to TUI's `StatusBar_Production/Sandbox/Development/Test/Trial` color schemes

**2. Color-based left border** (`data-env-color` attribute on `.toolbar`):
- Uses the environment's configured hex color (from `environments.json`)
- CSS rule: `[data-env-color] { border-left: 4px solid attr(data-env-color); }` in `shared.css`
- Provides environment-specific branding beyond the type-based palette

**Implementation:** When the environment is selected (via picker or `authWho` on init), the host panel sends both `envType` and `envColor` in the `updateEnvironment` message. The webview script sets `data-env-type` and `data-env-color` on the `.toolbar` element. See QueryPanel and SolutionsPanel for reference.

### Keyboard Shortcuts

All panels should support a standard set of keyboard shortcuts. Register in the webview script via `document.addEventListener('keydown', ...)`. Check `event.metaKey || event.ctrlKey` for cross-platform support.

| Shortcut | Action | Notes |
|----------|--------|-------|
| `Ctrl/Cmd+R` | Refresh data | Re-fetch from daemon |
| `Ctrl/Cmd+F` | Focus filter bar | If panel has filtering |
| `Escape` | Close filter / deselect | Context-dependent |
| `Ctrl/Cmd+Shift+E` | Export visible data | CSV or JSON, match query panel pattern |
| `Ctrl/Cmd+C` | Copy selection | Tables: selected cells as TSV |

### TUI Functional Parity

When designing an extension panel, verify the equivalent TUI screen exposes the same capabilities. This is a functional check, not a visual one — the interfaces look different but should offer equivalent data and actions.

**Before marking a panel complete, confirm:**
- [ ] Same data fields visible (columns in table, fields in detail view)
- [ ] Same filter/search capabilities
- [ ] Same sort options
- [ ] Same export formats available
- [ ] Same drill-down / navigation paths (e.g., solution → components)
- [ ] Same refresh behavior
- [ ] Environment scoping works equivalently

If the TUI screen doesn't exist yet, file or reference the corresponding wave:2 issue. Panels and screens can be built in parallel but should converge on the same RPC methods and data shapes.
