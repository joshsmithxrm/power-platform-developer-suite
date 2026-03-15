# Extension Infrastructure Hardening Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the VS Code extension webview infrastructure with typed message protocols, TypeScript webview scripts, extracted CSS, comprehensive linting, and updated skill documentation — preparing for parallel panel development.

**Architecture:** Convert webview scripts from untyped JavaScript to TypeScript with discriminated union message types shared between host and webview. Extract inline CSS to bundled files. Add exhaustive switch checking. Enhance WebviewPanelBase with generic type parameters and AbortSignal support. Add ESLint rules for async safety, type safety, and complexity gates. Add Stylelint for CSS files.

**Tech Stack:** TypeScript, esbuild (CSS entry points), ESLint 9 flat config, Stylelint, Vitest

---

## Chunk 0: Dead Code Cleanup

### Task 0: Remove Dead Code & Unnecessary Exports

**Files:**
- Modify: `extension/src/types.ts` (delete lines 187-234)
- Modify: `extension/src/views/toolsTreeView.ts` (remove `export` from `ToolTreeItem`)
- Modify: `extension/src/views/profileTreeView.ts` (remove `export` from `ProfileTreeItem`, `ManualUrlTreeItem`)
- Modify: `extension/src/notebooks/notebookResultRenderer.ts` (remove `export` from `CellData`)
- Modify: `extension/src/panels/environmentPicker.ts` (remove `export` from `EnvironmentOption`)

- [ ] **Step 1: Delete unused plugin interfaces from types.ts**

Remove `PluginsListResponse`, `PluginPackageInfo`, `PluginAssemblyInfo`, `PluginTypeInfoDto`, `PluginStepInfo`, `PluginImageInfo` and the `// -- Plugins` section header (lines 187-234). These are never imported or used anywhere.

- [ ] **Step 2: Remove unnecessary exports**

In each file, change `export class/interface/type` to just `class/interface/type` for symbols that are only used within their own module:
- `ToolTreeItem` in `views/toolsTreeView.ts`
- `ProfileTreeItem` and `ManualUrlTreeItem` in `views/profileTreeView.ts`
- `CellData` in `notebooks/notebookResultRenderer.ts`
- `EnvironmentOption` in `panels/environmentPicker.ts`

- [ ] **Step 3: Verify build and tests**

Run: `cd extension && node esbuild.js && npx vitest run`
Expected: PASS — these symbols are not imported externally.

- [ ] **Step 4: Commit**

```
git add extension/src/types.ts extension/src/views/ extension/src/notebooks/notebookResultRenderer.ts extension/src/panels/environmentPicker.ts
git commit -m "chore(extension): remove dead plugin interfaces and unnecessary exports"
```

---

## Chunk 1: Foundation & Type System

### Task 1: Infrastructure Setup

**Files:**
- Create: `extension/tsconfig.webview.json`
- Create: `extension/src/panels/styles/` (directory)
- Create: `extension/src/panels/webview/` (directory)
- Create: `extension/src/panels/webview/shared/` (directory)
- Create: `extension/src/panels/webview/shared/assert-never.ts`

- [ ] **Step 1: Create tsconfig.webview.json**

This tsconfig targets browser environment for webview scripts. Separate from the host tsconfig which targets Node.js.

```json
{
  "compilerOptions": {
    "module": "ESNext",
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "moduleResolution": "bundler",
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noImplicitReturns": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "outDir": "dist",
    "sourceMap": true,
    "isolatedModules": true
  },
  "include": ["src/panels/webview/**/*.ts"],
  "exclude": ["node_modules", "dist"]
}
```

- [ ] **Step 2: Create assertNever utility**

Create `extension/src/panels/webview/shared/assert-never.ts`:

```typescript
/**
 * Exhaustive switch helper. If this function is reachable, TypeScript
 * will report a compile error — meaning a case was not handled.
 */
export function assertNever(value: never): never {
    throw new Error(`Unhandled discriminated union member: ${JSON.stringify(value)}`);
}
```

- [ ] **Step 3: Create directory structure**

```
mkdir -p src/panels/webview/shared
mkdir -p src/panels/styles
```

- [ ] **Step 4: Add type-check script to package.json**

Add these scripts to `extension/package.json`:

```json
"typecheck": "tsc --noEmit -p tsconfig.json",
"typecheck:webview": "tsc --noEmit -p tsconfig.webview.json",
"typecheck:all": "npm run typecheck && npm run typecheck:webview"
```

- [ ] **Step 5: Verify setup compiles**

Run: `cd extension && npx tsc --noEmit -p tsconfig.webview.json`
Expected: Success (no .ts files in webview/ yet, so nothing to check — just validates the config)

- [ ] **Step 6: Commit**

```
git add extension/tsconfig.webview.json extension/src/panels/webview/ extension/src/panels/styles/ extension/package.json
git commit -m "feat(extension): add webview tsconfig, directory structure, assertNever utility"
```

---

### Task 2: Message Protocol Types

**Files:**
- Create: `extension/src/panels/webview/shared/message-types.ts`

Define discriminated union types for ALL webview-to-host and host-to-webview messages. These types are imported by both the host panel classes (via the host tsconfig) and the webview scripts (via tsconfig.webview.json).

**Important:** This file must also be included in the host tsconfig. Add it to the host tsconfig `include` or ensure it's importable from `src/panels/`.

- [ ] **Step 1: Create message-types.ts with Query Panel messages**

Create `extension/src/panels/webview/shared/message-types.ts`. Extract every command string from `QueryPanel.ts:88-177` (the switch statement) and `query-panel-webview.js:619-681` (the message handler).

```typescript
import type { QueryResultResponse, CompletionItemDto } from '../../../types.js';

// ── Query Panel ─────────────────────────────────────────────────────

/** Messages the Query Panel webview sends to the extension host. */
export type QueryPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'executeQuery'; sql: string; useTds?: boolean; language?: string }
    | { command: 'showFetchXml'; sql: string }
    | { command: 'loadMore'; pagingCookie: string; page: number }
    | { command: 'explainQuery'; sql: string }
    | { command: 'exportResults' }
    | { command: 'openInNotebook'; sql: string }
    | { command: 'showHistory' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openRecordUrl'; url: string }
    | { command: 'requestClipboard' }
    | { command: 'requestCompletions'; requestId: number; sql: string; cursorOffset: number; language: string }
    | { command: 'webviewError'; error: string; stack?: string }
    | { command: 'cancelQuery' }
    | { command: 'refresh' }
    | { command: 'requestEnvironmentList' };

/** Messages the extension host sends to the Query Panel webview. */
export type QueryPanelHostToWebview =
    | { command: 'queryResult'; data: QueryResultResponse }
    | { command: 'queryError'; error: string }
    | { command: 'executionStarted' }
    | { command: 'queryCancelled' }
    | { command: 'loadQuery'; sql: string }
    | { command: 'updateEnvironment'; name: string; url: string | null }
    | { command: 'clipboardContent'; text: string }
    | { command: 'completionResult'; requestId: number; items: CompletionItemDto[] }
    | { command: 'appendResults'; data: QueryResultResponse }
    | { command: 'daemonReconnected' };
```

- [ ] **Step 2: Add Solutions Panel messages**

Append to the same file. Extract from `SolutionsPanel.ts:88-127` and `solutions-panel-webview.js:191-225`.

```typescript
// ── Solutions Panel ─────────────────────────────────────────────────

/** Serialized solution data sent to the webview. */
export interface SolutionViewDto {
    id: string;
    uniqueName: string;
    friendlyName: string;
    version: string;
    publisherName: string;
    isManaged: boolean;
    description: string;
    createdOn: string | null;
    modifiedOn: string | null;
    installedOn: string | null;
}

/** Component group sent after expanding a solution. */
export interface ComponentGroupDto {
    typeName: string;
    components: {
        objectId: string;
        isMetadata: boolean;
        logicalName?: string;
        schemaName?: string;
        displayName?: string;
        rootComponentBehavior: number;
    }[];
}

/** Messages the Solutions Panel webview sends to the extension host. */
export type SolutionsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'requestEnvironmentList' }
    | { command: 'refresh' }
    | { command: 'toggleManaged' }
    | { command: 'expandSolution'; uniqueName: string }
    | { command: 'collapseSolution'; uniqueName: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openInMaker'; solutionId?: string };

/** Messages the extension host sends to the Solutions Panel webview. */
export type SolutionsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string }
    | { command: 'solutionsLoaded'; solutions: SolutionViewDto[]; managedCount: number; includeManaged: boolean }
    | { command: 'componentsLoading'; uniqueName: string }
    | { command: 'componentsLoaded'; uniqueName: string; groups: ComponentGroupDto[] }
    | { command: 'updateManagedState'; includeManaged: boolean }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

- [ ] **Step 3: Verify the types compile against both tsconfigs**

Run: `cd extension && npx tsc --noEmit -p tsconfig.webview.json`
Expected: PASS

The host tsconfig needs access too. Update `extension/tsconfig.json` exclude to NOT exclude the shared message types. Add a path mapping or simply ensure the import path works. The file is under `src/` so it's already included by `"include": ["src"]`.

Run: `cd extension && npx tsc --noEmit -p tsconfig.json`
Expected: PASS (the file is under `src/` so the host tsconfig includes it)

- [ ] **Step 4: Commit**

```
git add extension/src/panels/webview/shared/message-types.ts
git commit -m "feat(extension): define typed message protocols for Query and Solutions panels"
```

---

### Task 3: Enhanced WebviewPanelBase

**Files:**
- Modify: `extension/src/panels/WebviewPanelBase.ts`

Add generic type parameters for message types, typed `postMessage`, and AbortSignal for async cancellation on disposal.

- [ ] **Step 1: Write test for typed message posting**

Create `extension/src/__tests__/panels/WebviewPanelBase.test.ts`. If this file already exists, add to it.

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock vscode before import
vi.mock('vscode', () => ({
    Disposable: { from: vi.fn() },
}));
vi.mock('../../daemonClient.js', () => ({}));

describe('WebviewPanelBase', () => {
    it('should exist and export the base class', async () => {
        const mod = await import('../../panels/WebviewPanelBase.js');
        expect(mod.WebviewPanelBase).toBeDefined();
    });
});
```

Run: `cd extension && npx vitest run src/__tests__/panels/WebviewPanelBase.test.ts`
Expected: PASS (basic smoke test)

- [ ] **Step 2: Rewrite WebviewPanelBase with generics**

Modify `extension/src/panels/WebviewPanelBase.ts`:

```typescript
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';

/**
 * Base class for webview panels with typed messaging, safe disposal,
 * and an AbortSignal that fires when the panel is disposed.
 *
 * @template TIncoming - Discriminated union of messages FROM the webview
 * @template TOutgoing - Discriminated union of messages TO the webview
 */
export abstract class WebviewPanelBase<
    TIncoming extends { command: string } = { command: string },
    TOutgoing extends { command: string } = { command: string },
> implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];
    private _disposed = false;
    private readonly _abortController = new AbortController();

    /**
     * Fires when the panel is disposed. Pass to async operations
     * so they can bail out early when the panel closes.
     */
    protected get abortSignal(): AbortSignal {
        return this._abortController.signal;
    }

    /** Type-safe message posting. No-ops if the panel is already disposed. */
    protected postMessage(message: TOutgoing): void {
        this.panel?.webview.postMessage(message);
    }

    /**
     * Subscribes to daemon reconnect events. On reconnect, posts a
     * `daemonReconnected` message to the webview and calls the
     * overridable `onDaemonReconnected` hook.
     */
    protected subscribeToDaemonReconnect(client: DaemonClient): void {
        this.disposables.push(
            client.onDidReconnect(() => {
                // Cast is safe: every panel's TOutgoing includes daemonReconnected
                this.postMessage({ command: 'daemonReconnected' } as TOutgoing);
                this.onDaemonReconnected();
            })
        );
    }

    /** Override in subclasses to handle reconnection (e.g., auto-refresh). */
    protected onDaemonReconnected(): void {
        // Default: no-op
    }

    abstract getHtmlContent(webview: vscode.Webview): string;

    dispose(): void {
        if (this._disposed) return;
        this._disposed = true;

        this._abortController.abort();
        this.panel?.dispose();

        for (const d of this.disposables) {
            d.dispose();
        }
        this.disposables = [];
    }
}
```

- [ ] **Step 3: Verify host tsconfig compiles**

Run: `cd extension && npx tsc --noEmit -p tsconfig.json`
Expected: PASS (existing panels use the default generic params so no breakage)

- [ ] **Step 4: Run existing tests**

Run: `cd extension && npx vitest run`
Expected: All existing tests pass (the generics default to `{ command: string }` so no existing code breaks)

- [ ] **Step 5: Commit**

```
git add extension/src/panels/WebviewPanelBase.ts
git commit -m "feat(extension): add generic type params and AbortSignal to WebviewPanelBase"
```

---

## Chunk 2: Webview Migration

### Task 4: Shared Webview Utilities

**Files:**
- Create: `extension/src/panels/webview/shared/dom-utils.ts`
- Create: `extension/src/panels/webview/shared/vscode-api.ts`

Extract duplicated utilities from both webview scripts: `escapeHtml`, `escapeAttr`, `cssEscape`, `formatDate`. Both `query-panel-webview.js:961-967` and `solutions-panel-webview.js:359-384` have identical copies.

- [ ] **Step 1: Create dom-utils.ts**

Create `extension/src/panels/webview/shared/dom-utils.ts`:

```typescript
/** Escape HTML entities for safe insertion into innerHTML. */
export function escapeHtml(str: unknown): string {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

/** Escape for use in HTML attribute values. */
export function escapeAttr(str: unknown): string {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
}

/** Escape for use in CSS selectors (wraps CSS.escape). */
export function cssEscape(str: unknown): string {
    if (str === null || str === undefined) return '';
    return CSS.escape(String(str));
}

/** Format an ISO date string for display. */
export function formatDate(isoString: string | null | undefined): string {
    if (!isoString) return '';
    try {
        return new Date(isoString).toLocaleDateString(undefined, {
            year: 'numeric',
            month: 'short',
            day: 'numeric',
        });
    } catch {
        return isoString;
    }
}

/** Strip tabs and newlines from a value for clipboard copy. */
export function sanitizeValue(val: string): string {
    return val.replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
}
```

- [ ] **Step 2: Create vscode-api.ts**

Create `extension/src/panels/webview/shared/vscode-api.ts`:

```typescript
/**
 * Type-safe wrapper around acquireVsCodeApi().
 * The generic param restricts what messages can be sent.
 */

interface VsCodeApi<TOutgoing> {
    postMessage(message: TOutgoing): void;
    getState(): unknown;
    setState(state: unknown): void;
}

declare function acquireVsCodeApi<T>(): VsCodeApi<T>;

/** Acquire the VS Code webview API with typed message sending. */
export function getVsCodeApi<TOutgoing extends { command: string }>(): VsCodeApi<TOutgoing> {
    return acquireVsCodeApi<TOutgoing>();
}
```

- [ ] **Step 3: Write tests for dom-utils**

Create `extension/src/__tests__/panels/webview/dom-utils.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { escapeHtml, escapeAttr, sanitizeValue, formatDate } from '../../../panels/webview/shared/dom-utils.js';

describe('escapeHtml', () => {
    it('escapes angle brackets', () => {
        expect(escapeHtml('<script>alert("xss")</script>')).toBe('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;');
    });

    it('returns empty string for null/undefined', () => {
        expect(escapeHtml(null)).toBe('');
        expect(escapeHtml(undefined)).toBe('');
    });
});

describe('sanitizeValue', () => {
    it('strips tabs and newlines', () => {
        expect(sanitizeValue('a\tb\nc')).toBe('a b c');
    });
});

describe('formatDate', () => {
    it('returns empty for null', () => {
        expect(formatDate(null)).toBe('');
    });

    it('formats a valid ISO string', () => {
        const result = formatDate('2026-01-15T00:00:00Z');
        expect(result).toContain('2026');
    });
});
```

- [ ] **Step 4: Run tests**

Run: `cd extension && npx vitest run src/__tests__/panels/webview/dom-utils.test.ts`
Expected: PASS

- [ ] **Step 5: Commit**

```
git add extension/src/panels/webview/shared/
git commit -m "feat(extension): add shared webview utilities — dom-utils, vscode-api wrapper"
```

---

### Task 5: Extract CSS & Convert Query Panel to TypeScript

**Files:**
- Create: `extension/src/panels/styles/shared.css`
- Create: `extension/src/panels/styles/query-panel.css`
- Create: `extension/src/panels/webview/query-panel.ts`
- Modify: `extension/src/panels/QueryPanel.ts` (use typed base, CSS link, new script path)
- Modify: `extension/esbuild.js` (add CSS entry points, change JS→TS entry)
- Delete: `extension/src/panels/query-panel-webview.js`

This is the largest task. It converts the Query Panel webview from untyped JS with inline CSS to typed TypeScript with external CSS.

- [ ] **Step 1: Extract shared CSS**

Create `extension/src/panels/styles/shared.css` with styles common to both panels. Extract from `QueryPanel.ts:481-519` and `SolutionsPanel.ts:327-424` — the overlapping rules.

Shared styles include: body reset, toolbar, toolbar-spacer, status-bar, empty-state, error-state, spinner, keyframes, reconnect-banner, environment-picker.

```css
/* shared.css — common webview panel styles */

body {
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    height: 100vh;
    font-family: var(--vscode-font-family);
    color: var(--vscode-foreground);
    background: var(--vscode-editor-background);
}

.toolbar {
    display: flex;
    gap: 8px;
    padding: 8px 12px;
    border-bottom: 1px solid var(--vscode-panel-border);
    flex-shrink: 0;
    align-items: center;
}

.toolbar-spacer { flex: 1; }

.status-bar {
    display: flex;
    gap: 16px;
    padding: 4px 12px;
    border-top: 1px solid var(--vscode-panel-border);
    font-size: 12px;
    color: var(--vscode-descriptionForeground);
    flex-shrink: 0;
}

.empty-state {
    padding: 40px;
    text-align: center;
    color: var(--vscode-descriptionForeground);
    font-style: italic;
}

.error-state {
    padding: 12px;
    background: var(--vscode-inputValidation-errorBackground, rgba(255,0,0,0.1));
    border: 1px solid var(--vscode-inputValidation-errorBorder, red);
    border-radius: 4px;
    margin: 8px 12px;
    color: var(--vscode-errorForeground);
}

.spinner {
    display: inline-block;
    width: 16px;
    height: 16px;
    border: 2px solid var(--vscode-descriptionForeground);
    border-top-color: transparent;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
}

@keyframes spin { to { transform: rotate(360deg); } }

.loading-state {
    padding: 40px;
    text-align: center;
    color: var(--vscode-descriptionForeground);
}

.loading-state .spinner {
    width: 24px;
    height: 24px;
    margin-bottom: 12px;
}

/* Environment picker — previously generated by getEnvironmentPickerCss() */
.env-picker-btn {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 3px 8px;
    background: var(--vscode-input-background);
    color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent);
    border-radius: 2px;
    cursor: pointer;
    font-size: 12px;
    max-width: 280px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.env-picker-btn:hover {
    background: var(--vscode-list-hoverBackground);
}
```

NOTE: The exact environment picker CSS should be extracted from `environmentPicker.ts:getEnvironmentPickerCss()`. Read that file to get the exact rules. The above is a representative starting point — adjust to match the actual output.

- [ ] **Step 2: Create query-panel.css**

Create `extension/src/panels/styles/query-panel.css` with Query Panel specific styles:

```css
@import './shared.css';

/* Query Panel specific styles */
.editor-container { flex-shrink: 0; border-bottom: 1px solid var(--vscode-panel-border); }
.editor-wrapper { height: 150px; min-height: 120px; max-height: 300px; overflow: hidden; resize: vertical; position: relative; }
#sql-editor { position: absolute; top: 0; left: 0; right: 0; bottom: 0; }

.results-wrapper { flex: 1; overflow: auto; position: relative; min-height: 0; }
.results-table { width: max-content; min-width: 100%; border-collapse: collapse; }
.results-table thead { position: sticky; top: 0; z-index: 1; }
/* ... rest of table, selection, filter, context-menu styles from QueryPanel.ts:489-519 */
```

Full CSS content: extract ALL style rules from `QueryPanel.ts:481-519` that are NOT in shared.css.

- [ ] **Step 3: Convert query-panel-webview.js to TypeScript**

Create `extension/src/panels/webview/query-panel.ts`. This replaces `src/panels/query-panel-webview.js`.

Key changes from the JS version:
1. Import types: `import type { QueryPanelWebviewToHost, QueryPanelHostToWebview } from './shared/message-types.js';`
2. Import utilities: `import { escapeHtml, escapeAttr, sanitizeValue } from './shared/dom-utils.js';`
3. Import vscode API wrapper: `import { getVsCodeApi } from './shared/vscode-api.js';`
4. Import assertNever: `import { assertNever } from './shared/assert-never.js';`
5. Replace `const vscode = acquireVsCodeApi();` with `const vscode = getVsCodeApi<QueryPanelWebviewToHost>();`
6. Add type annotations to all functions
7. Add exhaustive switch in the message handler:
   ```typescript
   window.addEventListener('message', (event: MessageEvent<QueryPanelHostToWebview>) => {
       const msg = event.data;
       switch (msg.command) {
           case 'queryResult': handleQueryResult(msg.data); break;
           // ... all cases ...
           default: assertNever(msg);
       }
   });
   ```
8. Remove duplicated `escapeHtml`, `escapeAttr` functions (now imported)
9. Keep the IIFE wrapper — esbuild will handle bundling

The full content is a direct translation of `query-panel-webview.js` (977 lines) to TypeScript with the above changes. Do NOT restructure the logic — this is a type-safe conversion, not a rewrite.

- [ ] **Step 4: Update esbuild.js**

Modify `extension/esbuild.js`:

1. Change query panel entry point from `.js` to `.ts`:
   ```javascript
   // Build 4: Query panel webview script (browser, IIFE)
   const queryPanelCtx = await esbuild.context({
       entryPoints: ['src/panels/webview/query-panel.ts'],
       bundle: true,
       format: 'iife',
       minify: production,
       sourcemap: !production,
       sourcesContent: false,
       platform: 'browser',
       outfile: 'dist/query-panel.js',
       logLevel: 'warning',
   });
   ```

2. Add CSS entry points:
   ```javascript
   // Build 6: Query panel CSS
   const queryPanelCssCtx = await esbuild.context({
       entryPoints: ['src/panels/styles/query-panel.css'],
       bundle: true,
       minify: production,
       outfile: 'dist/query-panel.css',
       logLevel: 'warning',
   });

   // Build 7: Solutions panel CSS
   const solutionsPanelCssCtx = await esbuild.context({
       entryPoints: ['src/panels/styles/solutions-panel.css'],
       bundle: true,
       minify: production,
       outfile: 'dist/solutions-panel.css',
       logLevel: 'warning',
   });
   ```

3. Add all new contexts to the watch/rebuild/dispose arrays.

- [ ] **Step 5: Update QueryPanel.ts to use typed base and CSS link**

Modify `extension/src/panels/QueryPanel.ts`:

1. Change class declaration:
   ```typescript
   import type { QueryPanelWebviewToHost, QueryPanelHostToWebview } from './webview/shared/message-types.js';

   export class QueryPanel extends WebviewPanelBase<QueryPanelWebviewToHost, QueryPanelHostToWebview> {
   ```

2. Type the message handler — remove all `as string` casts:
   ```typescript
   this.disposables.push(
       this.panel.webview.onDidReceiveMessage(
           async (message: QueryPanelWebviewToHost) => {
               switch (message.command) {
                   case 'executeQuery':
                       await this.executeQuery(message.sql, false, message.useTds, message.language);
                       break;
                   // ... all cases — no casts needed, properties are typed ...
                   default:
                       assertNever(message);
               }
           }
       )
   );
   ```

3. Update `getHtmlContent()`:
   - Remove the inline `<style>...</style>` block
   - Add CSS link: `<link rel="stylesheet" href="${queryPanelCssUri}">`
   - Remove `getEnvironmentPickerCss()` call (now in shared.css)
   - Keep `getEnvironmentPickerHtml()` (still generates HTML)
   - Add `localResourceRoots` entry for styles directory if needed

4. Add `dist/` path for CSS:
   ```typescript
   const queryPanelCssUri = webview.asWebviewUri(
       vscode.Uri.joinPath(this.extensionUri, 'dist', 'query-panel.css')
   );
   ```

- [ ] **Step 6: Delete old JS file**

Delete `extension/src/panels/query-panel-webview.js`.

- [ ] **Step 7: Build and verify**

Run: `cd extension && node esbuild.js`
Expected: All builds succeed. `dist/query-panel.js` and `dist/query-panel.css` exist.

Run: `cd extension && npx tsc --noEmit -p tsconfig.webview.json`
Expected: PASS

Run: `cd extension && npx vitest run`
Expected: All existing tests pass.

- [ ] **Step 8: Commit**

```
git add -A extension/src/panels/
git add extension/esbuild.js
git commit -m "feat(extension): convert Query Panel webview to TypeScript with typed messages and extracted CSS"
```

---

### Task 6: Extract CSS & Convert Solutions Panel to TypeScript

**Files:**
- Create: `extension/src/panels/styles/solutions-panel.css`
- Create: `extension/src/panels/webview/solutions-panel.ts`
- Modify: `extension/src/panels/SolutionsPanel.ts`
- Modify: `extension/esbuild.js` (solutions entry point)
- Delete: `extension/src/panels/solutions-panel-webview.js`

Same treatment as Task 5 but for the Solutions Panel. Follow the identical pattern.

- [ ] **Step 1: Create solutions-panel.css**

Create `extension/src/panels/styles/solutions-panel.css`:

```css
@import './shared.css';

/* Solutions Panel specific styles — extract from SolutionsPanel.ts:336-442 */
/* Only include rules NOT already in shared.css */
```

Extract: `.solution-list`, `.solution-row`, `.chevron`, `.components-container`, `.component-group`, `.component-items`, `.component-item`, `.detail-card`, `.component-detail-card`, `.managed-badge`, `.open-maker-btn`, `.copy-btn`, `.components-loading`, `.content`, and all their variants.

- [ ] **Step 2: Convert solutions-panel-webview.js to TypeScript**

Create `extension/src/panels/webview/solutions-panel.ts`. Same conversion pattern as Task 5 Step 3:

1. Import types, dom-utils, vscode-api, assert-never
2. Type the `getVsCodeApi<SolutionsPanelWebviewToHost>()`
3. Type the message handler with `MessageEvent<SolutionsPanelHostToWebview>`
4. Add exhaustive switch with `assertNever`
5. Remove duplicated utility functions (imported from shared)

- [ ] **Step 3: Update SolutionsPanel.ts**

Same pattern as Task 5 Step 5:
1. Extend `WebviewPanelBase<SolutionsPanelWebviewToHost, SolutionsPanelHostToWebview>`
2. Type the message handler — remove all `as string` casts
3. Add exhaustive switch with `assertNever`
4. Replace inline CSS with `<link>` tag
5. Remove `getEnvironmentPickerCss()` call
6. Add CSS URI for `dist/solutions-panel.css`

- [ ] **Step 4: Update esbuild.js — solutions entry point**

Change solutions panel entry from `.js` to `.ts`:
```javascript
const solutionsPanelCtx = await esbuild.context({
    entryPoints: ['src/panels/webview/solutions-panel.ts'],
    // ... rest same as before
});
```

The CSS entry point was already added in Task 5 Step 4.

- [ ] **Step 5: Delete old JS file**

Delete `extension/src/panels/solutions-panel-webview.js`.

- [ ] **Step 6: Build and verify**

Run: `cd extension && node esbuild.js`
Expected: All builds succeed.

Run: `cd extension && npx tsc --noEmit -p tsconfig.webview.json`
Expected: PASS

Run: `cd extension && npx vitest run`
Expected: All existing tests pass.

- [ ] **Step 7: Commit**

```
git add -A extension/src/panels/
git add extension/esbuild.js
git commit -m "feat(extension): convert Solutions Panel webview to TypeScript with typed messages and extracted CSS"
```

---

### Task 7: Update environmentPicker.ts

**Files:**
- Modify: `extension/src/panels/environmentPicker.ts`

After CSS extraction, the `getEnvironmentPickerCss()` function is no longer needed (its styles are in `shared.css`). Remove it and update any callers.

- [ ] **Step 1: Remove getEnvironmentPickerCss**

The function is no longer called from `getHtmlContent()` in either panel (CSS is now external). Remove the export and the function body. Keep `getEnvironmentPickerHtml()` and `showEnvironmentPicker()` — they're still used.

- [ ] **Step 2: Verify no remaining callers**

Run: `grep -r "getEnvironmentPickerCss" extension/src/`
Expected: No matches (all callers were in inline `<style>` blocks that are now deleted)

- [ ] **Step 3: Build and test**

Run: `cd extension && node esbuild.js && npx vitest run`
Expected: PASS

- [ ] **Step 4: Commit**

```
git add extension/src/panels/environmentPicker.ts
git commit -m "refactor(extension): remove getEnvironmentPickerCss — styles moved to shared.css"
```

---

## Chunk 3: Quality Gates

### Task 8: ESLint Overhaul

**Files:**
- Modify: `extension/eslint.config.mjs`
- Modify: `extension/package.json` (add eslint-plugin-import dependency + scripts)

- [ ] **Step 1: Install additional ESLint dependencies**

Run: `cd extension && npm install --save-dev eslint-plugin-import`

Note: `typescript-eslint` is already installed. We just need the import ordering plugin.

- [ ] **Step 2: Rewrite eslint.config.mjs**

Replace `extension/eslint.config.mjs` with the comprehensive config. Model it on the legacy config (`ppds-extension-archived/eslint.config.mjs`) but adapted for the MVP's simpler architecture (no Clean Architecture layers, no local-rules plugin).

Key rules to add (all discussed and agreed):

**High priority:**
- `@typescript-eslint/no-floating-promises`: error
- `@typescript-eslint/no-misused-promises`: error
- `@typescript-eslint/no-explicit-any`: error
- `@typescript-eslint/no-unsafe-return`: error
- `@typescript-eslint/no-unsafe-assignment`: error
- `@typescript-eslint/no-unsafe-member-access`: error
- `@typescript-eslint/no-unsafe-call`: error
- `@typescript-eslint/no-unsafe-argument`: error
- `no-console`: error (host only)
- `complexity`: warn 15
- `max-lines`: warn 500
- `max-lines-per-function`: warn 100

**Medium priority:**
- `@typescript-eslint/explicit-function-return-type`: error
- `import/order`: error (with groups)
- `@typescript-eslint/consistent-type-definitions`: error (interface)

**Overrides:**
- Test files: relax complexity, allow `any`, no max-lines
- Panel files (`src/panels/*Panel*.ts`): max-lines 700, max-lines-per-function 150, complexity 20
- Webview TypeScript (`src/panels/webview/**/*.ts`): allow `console`, add browser globals declaration
- Composition root (`src/extension.ts`): no max-lines

The full config should be ~150-200 lines. Include `parserOptions.project` pointing to both tsconfigs for type-aware rules.

- [ ] **Step 3: Add lint:fix and lint:webview scripts**

Add to `extension/package.json` scripts:
```json
"lint": "eslint src",
"lint:fix": "eslint src --fix",
"lint:webview": "eslint src/panels/webview"
```

- [ ] **Step 4: Commit the config (before fixing violations)**

```
git add extension/eslint.config.mjs extension/package.json package-lock.json
git commit -m "feat(extension): comprehensive ESLint config — type safety, async, complexity gates"
```

- [ ] **Step 5: Run lint and capture violations**

Run: `cd extension && npx eslint src --format stylish 2>&1 | head -200`

Report the violations to the user for discussion. Do NOT auto-fix without review — some violations may be intentional or need discussion (e.g., `no-floating-promises` on fire-and-forget patterns).

- [ ] **Step 6: Fix violations**

Fix violations in batches:
1. Auto-fixable: `cd extension && npx eslint src --fix`
2. Manual fixes: address remaining violations one by one
3. Add `// eslint-disable-next-line` with justification for intentional violations

- [ ] **Step 7: Verify clean lint**

Run: `cd extension && npx eslint src`
Expected: 0 errors, 0 warnings (or only intentional suppressions)

- [ ] **Step 8: Commit fixes**

```
git add -A extension/src/
git commit -m "fix(extension): resolve ESLint violations — type safety, floating promises, complexity"
```

---

### Task 9: Stylelint Setup

**Files:**
- Create: `extension/.stylelintrc.json`
- Modify: `extension/package.json` (add stylelint dependency + script)

- [ ] **Step 1: Install stylelint**

Run: `cd extension && npm install --save-dev stylelint stylelint-config-standard`

- [ ] **Step 2: Create .stylelintrc.json**

Create `extension/.stylelintrc.json`:

```json
{
  "extends": "stylelint-config-standard",
  "rules": {
    "selector-class-pattern": null,
    "custom-property-pattern": null,
    "no-descending-specificity": null,
    "declaration-empty-line-before": null,
    "alpha-value-notation": "number",
    "color-function-notation": "legacy"
  }
}
```

The relaxed rules avoid false positives on VS Code CSS variable patterns (`--vscode-*`) and the BEM-like class naming used in the extension.

- [ ] **Step 3: Add lint:css script**

Add to `extension/package.json` scripts:
```json
"lint:css": "stylelint \"src/panels/styles/**/*.css\""
```

- [ ] **Step 4: Run stylelint and fix violations**

Run: `cd extension && npx stylelint "src/panels/styles/**/*.css"`
Expected: Fix any violations. These will be formatting issues since the CSS was just extracted from template literals.

- [ ] **Step 5: Install knip for dead code detection**

Run: `cd extension && npm install --save-dev knip`

Add to `extension/package.json` scripts:
```json
"dead-code": "knip"
```

Run: `cd extension && npx knip`
Expected: Review output. Suppress any false positives (entry points, VS Code API). The goal is to have this available as a maintenance check, not to gate CI.

- [ ] **Step 6: Commit**

```
git add extension/.stylelintrc.json extension/package.json package-lock.json extension/src/panels/styles/
git commit -m "feat(extension): add Stylelint for CSS files and knip for dead code detection"
```

---

### Task 10: Update Webview Panels Skill

**Files:**
- Modify: `extension/../.agents/skills/webview-panels/SKILL.md`

Update the skill to reflect the new patterns established in this plan.

- [ ] **Step 1: Rewrite SKILL.md**

The skill must document:
1. TypeScript for webview scripts (not JS)
2. Message protocol types (discriminated unions, exhaustive switch, assertNever)
3. CSS in external files (not inline `<style>`)
4. File naming conventions
5. Shared utilities (dom-utils, vscode-api)
6. esbuild registration steps
7. Testing expectations
8. WebviewPanelBase generic type params

Key changes from current skill:
- Remove "Plain JavaScript (not TypeScript)" guidance
- Remove "CSS styles: Inline `<style>` in `getHtmlContent()`" guidance
- Add TypeScript conversion instructions
- Add CSS extraction instructions
- Add message protocol type creation instructions
- Add `assertNever` exhaustive switch requirement
- Add shared utility imports
- Update file paths to reflect new structure

- [ ] **Step 2: Commit**

```
git add .agents/skills/webview-panels/SKILL.md
git commit -m "docs(extension): update webview-panels skill for TypeScript, typed messages, CSS extraction"
```

---

### Task 11: Final Verification

**Files:** None (verification only)

- [ ] **Step 1: Full build**

Run: `cd extension && node esbuild.js`
Expected: All 7+ contexts build without errors. All outputs in `dist/`.

- [ ] **Step 2: Type check both tsconfigs**

Run: `cd extension && npm run typecheck:all`
Expected: PASS for both host and webview tsconfigs.

- [ ] **Step 3: Run all unit tests**

Run: `cd extension && npx vitest run`
Expected: All tests pass.

- [ ] **Step 4: Lint clean**

Run: `cd extension && npx eslint src && npx stylelint "src/panels/styles/**/*.css"`
Expected: No errors.

- [ ] **Step 5: Visual verification**

Use the webview-cdp skill to verify panels still render and function correctly. Launch the extension, open both Data Explorer and Solutions panels, and verify:
- CSS loads correctly (no unstyled content)
- Execute a query (or verify UI responds to button clicks)
- Environment picker works
- No console errors

If the webview-cdp tooling has issues, STOP and report to the user.

- [ ] **Step 6: Package test**

Run: `cd extension && npm run release:test`
Expected: `.vsix` packages successfully. No missing files.

- [ ] **Step 7: Final commit if any cleanup needed**

```
git add -A
git commit -m "chore(extension): final cleanup after infrastructure hardening"
```
