# Extension MVP Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all architectural debt, broken patterns, and polish issues found during the 3-baseline MVP review so the codebase is PR-ready and can scale from 2 panels to 11+ without friction.

**Architecture:** Extract shared utilities (error handling, selection, config, auth-retry) first, then refactor WebviewPanelBase to consolidate duplicated panel lifecycle wiring, then fix infrastructure scaling (esbuild, debug commands, activate grouping, tree view). Each chunk produces working, testable code independently.

**Tech Stack:** TypeScript, VS Code Extension API, Vitest, esbuild

---

## File Structure

**Create:**
- `src/PPDS.Extension/src/panels/webview/shared/error-handler.ts` — shared `window.onerror` + `webviewError` forwarding for all webview scripts
- `src/PPDS.Extension/src/panels/webview/shared/selection-utils.ts` — pure selection geometry functions shared between host tests and webview
- `src/PPDS.Extension/src/utils/config.ts` — typed config accessor for extension settings
- `src/PPDS.Extension/src/__tests__/utils/errorUtils.test.ts` — tests for new `handleAuthError` helper

**Modify:**
- `src/PPDS.Extension/src/panels/WebviewPanelBase.ts` — add `initPanel()`, make `handleMessage` abstract, remove dead no-op, add `logWebviewError` helper
- `src/PPDS.Extension/src/panels/QueryPanel.ts` — use `initPanel()`, move switch into `handleMessage`, use `handleAuthError`, fix `convertQuery` raw `postMessage`
- `src/PPDS.Extension/src/panels/SolutionsPanel.ts` — use `initPanel()`, move switch into `handleMessage`, use `handleAuthError`, set `retainContextWhenHidden: false`
- `src/PPDS.Extension/src/panels/webview/query-panel.ts` — import selection utils from `shared/selection-utils`, use shared error handler
- `src/PPDS.Extension/src/panels/webview/solutions-panel.ts` — add shared error handler, add `webviewError` forwarding
- `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` — add `webviewError` to `SolutionsPanelWebviewToHost`
- `src/PPDS.Extension/src/panels/querySelectionUtils.ts` — re-export from shared (preserves test imports)
- `src/PPDS.Extension/src/utils/errorUtils.ts` — add `handleAuthError()` helper
- `src/PPDS.Extension/src/commands/debugCommands.ts` — change `panelCounts` to `Record<string, () => number>`
- `src/PPDS.Extension/src/extension.ts` — extract `registerPanelCommands()`, use dynamic panel registry, move `moveProfile` into provider
- `src/PPDS.Extension/src/views/profileTreeView.ts` — add `moveProfile()` method, add env list cache
- `src/PPDS.Extension/esbuild.js` — data-driven build config array

**Test files updated:**
- `src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts` — update for dynamic `Record<string, () => number>`
- `src/PPDS.Extension/src/__tests__/panels/querySelectionUtils.test.ts` — no changes needed (re-exports preserve API)

---

## Chunk 1: Shared Utilities & Quick Fixes

### Task 1: Create shared webview error handler

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/shared/error-handler.ts`

**Design note:** The existing `query-panel.ts` has an inline `window.onerror` that records errors to `window.__ppds_errors` but does NOT forward them to the host via `postMessage`. `solutions-panel.ts` has no error handler at all. This shared module improves both: it records errors AND forwards them to the host so `logWebviewError` in the base class receives them.

**Timing note:** These webview scripts are built as IIFE bundles by esbuild, not ESM. In IIFE bundles, `import` statements become synchronous inline code — execution order follows source order. Calling `installErrorHandler()` at the top of the module (after imports) executes before any other module code, which is sufficient for catching runtime errors.

- [ ] **Step 1: Create the shared error handler module**

```ts
// src/PPDS.Extension/src/panels/webview/shared/error-handler.ts

/**
 * Shared error tracking for webview scripts.
 * Sets up window.onerror to record errors and forward them to the extension host.
 *
 * Call at the top of each webview script, right after imports.
 * Safe in IIFE bundles (esbuild) where import resolution is synchronous.
 */

declare global {
    interface Window {
        __ppds_errors: { msg: string; src: string; line: number | null; col: number | null; stack: string }[];
    }
}

/**
 * Install a global error handler that:
 * 1. Tracks errors on `window.__ppds_errors` for diagnostic inspection
 * 2. Forwards each error to the extension host via `postMessage`
 *
 * @param postMessage — the panel's `vscode.postMessage` bound to the correct message type
 */
export function installErrorHandler(
    postMessage: (msg: { command: 'webviewError'; error: string; stack?: string }) => void,
): void {
    window.__ppds_errors = [];
    window.onerror = function (msg, src, line, col, err) {
        const entry = {
            msg: String(msg),
            src: String(src ?? ''),
            line: line ?? null,
            col: col ?? null,
            stack: err?.stack ? err.stack.substring(0, 500) : '',
        };
        window.__ppds_errors.push(entry);
        postMessage({
            command: 'webviewError',
            error: entry.msg,
            stack: entry.stack || undefined,
        });
    };
}
```

- [ ] **Step 2: Verify it compiles**

Run: `cd src/PPDS.Extension && npx tsc --noEmit -p tsconfig.webview.json`
Expected: No errors

- [ ] **Step 3: Commit**

```
git add src/PPDS.Extension/src/panels/webview/shared/error-handler.ts
git commit -m "feat(ext): add shared webview error handler"
```

---

### Task 2: Move selection utils to shared module

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/shared/selection-utils.ts`
- Modify: `src/PPDS.Extension/src/panels/querySelectionUtils.ts`
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts`

**Duplication note:** `sanitizeValue` currently exists in both `shared/dom-utils.ts` (webview) and `querySelectionUtils.ts` (host). After this task, `querySelectionUtils.ts` re-exports `sanitizeValue` from `shared/dom-utils.ts` to eliminate the duplication while preserving the test import path.

- [ ] **Step 1: Create shared selection-utils module**

```ts
// src/PPDS.Extension/src/panels/webview/shared/selection-utils.ts

export interface SelectionRect {
    minRow: number;
    maxRow: number;
    minCol: number;
    maxCol: number;
}

export interface CellCoord {
    row: number;
    col: number;
}

export function getSelectionRect(anchor: CellCoord | null, focus: CellCoord | null): SelectionRect | null {
    if (!anchor || !focus) return null;
    return {
        minRow: Math.min(anchor.row, focus.row),
        maxRow: Math.max(anchor.row, focus.row),
        minCol: Math.min(anchor.col, focus.col),
        maxCol: Math.max(anchor.col, focus.col),
    };
}

export function isSingleCell(anchor: CellCoord | null, focus: CellCoord | null): boolean {
    if (!anchor || !focus) return false;
    return anchor.row === focus.row && anchor.col === focus.col;
}
```

- [ ] **Step 2: Update querySelectionUtils.ts to re-export from shared**

Replace the entire file content of `src/PPDS.Extension/src/panels/querySelectionUtils.ts` with:

```ts
/**
 * Re-exports selection and DOM utilities from shared webview modules.
 * Kept as a stable import path for host-side tests.
 */
export { getSelectionRect, isSingleCell } from './webview/shared/selection-utils.js';
export type { SelectionRect, CellCoord } from './webview/shared/selection-utils.js';

import { sanitizeValue } from './webview/shared/dom-utils.js';

// Re-export sanitizeValue from its canonical location in dom-utils
// (eliminates the prior local duplicate that was in this file)
export { sanitizeValue };

// buildTsv is host-only (used for copy/paste in test utilities)
export function buildTsv(
    rows: Record<string, unknown>[],
    columns: { alias: string | null; logicalName: string }[],
    rect: { minRow: number; maxRow: number; minCol: number; maxCol: number },
    withHeaders: boolean,
    getDisplayValue: (row: Record<string, unknown>, colIdx: number) => string,
): string {
    let text = '';
    if (withHeaders) {
        const headers: string[] = [];
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            headers.push(columns[c].alias || columns[c].logicalName);
        }
        text += headers.join('\t') + '\n';
    }
    for (let r = rect.minRow; r <= rect.maxRow; r++) {
        const vals: string[] = [];
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            vals.push(sanitizeValue(getDisplayValue(rows[r], c)));
        }
        text += vals.join('\t') + '\n';
    }
    return text.trimEnd();
}
```

- [ ] **Step 3: Update query-panel.ts to import from shared instead of inline duplicates**

In `src/PPDS.Extension/src/panels/webview/query-panel.ts`:

Add imports after existing imports:
```ts
import { installErrorHandler } from './shared/error-handler.js';
import { getSelectionRect as computeSelectionRect, isSingleCell as checkSingleCell } from './shared/selection-utils.js';
import type { CellCoord } from './shared/selection-utils.js';
```

Replace the inline `window.onerror` block (lines 7-23) with:
```ts
// Error tracking and forwarding is handled by the shared error handler.
// Must be called early — safe in IIFE bundles where imports are synchronous.
```
Then right after `const vscode = getVsCodeApi<QueryPanelWebviewToHost>();` (line 35), add:
```ts
installErrorHandler((msg) => vscode.postMessage(msg as QueryPanelWebviewToHost));
```
Also remove the `declare global { interface Window { ... } }` block since that's now in the shared module.

Remove the inline `CellPosition` interface (around line 148-151):
```ts
// DELETE:
interface CellPosition {
    row: number;
    col: number;
}
```

Change variable type annotations (around line 156-157):
```ts
// BEFORE:
let anchor: CellPosition | null = null;
let focus: CellPosition | null = null;
// AFTER:
let anchor: CellCoord | null = null;
let focus: CellCoord | null = null;
```

Remove inline function definitions (lines 162-175):
```ts
// DELETE entire getSelectionRect() function (lines 162-170)
// DELETE entire isSingleCell() function (lines 172-175)
```

Update all 6 call sites to pass `anchor, focus` as arguments:
- Line 254: `if (isSingleCell())` → `if (checkSingleCell(anchor, focus))`
- Line 262: `const rect = getSelectionRect();` → `const rect = computeSelectionRect(anchor, focus);`
- Line 937: `const rect = getSelectionRect();` → `const rect = computeSelectionRect(anchor, focus);`
- Line 940: `const single = isSingleCell();` → `const single = checkSingleCell(anchor, focus);`
- Line 991: `const rect = getSelectionRect();` → `const rect = computeSelectionRect(anchor, focus);`
- Line 999: `const single = isSingleCell()` → `const single = checkSingleCell(anchor, focus)`

- [ ] **Step 4: Run typecheck to verify (BEFORE committing)**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors

- [ ] **Step 5: Run existing tests to verify re-exports work**

Run: `cd src/PPDS.Extension && npx vitest run src/__tests__/panels/querySelectionUtils.test.ts`
Expected: All 23 tests pass (5 getSelectionRect + 5 isSingleCell + 6 sanitizeValue + 7 buildTsv)

- [ ] **Step 6: Commit**

```
git add src/PPDS.Extension/src/panels/webview/shared/selection-utils.ts src/PPDS.Extension/src/panels/querySelectionUtils.ts src/PPDS.Extension/src/panels/webview/query-panel.ts
git commit -m "refactor(ext): move selection utils to shared module, eliminate duplication"
```

---

### Task 3: Extract auth-error retry to shared utility

**Files:**
- Modify: `src/PPDS.Extension/src/utils/errorUtils.ts`
- Create: `src/PPDS.Extension/src/__tests__/utils/errorUtils.test.ts` (directory `__tests__/utils/` must be created)

**Scope note:** Only `QueryPanel` and `SolutionsPanel` will be migrated to `handleAuthError`. `DataverseNotebookController` has different auth-error semantics (no retry — it shows "Re-authenticated. Please re-execute the cell." and returns). That pattern is intentionally different and stays as-is.

- [ ] **Step 1: Write the failing test**

Create directory `src/PPDS.Extension/src/__tests__/utils/` and file `errorUtils.test.ts`:

```ts
import { describe, it, expect, vi, beforeEach } from 'vitest';

const mockShowErrorMessage = vi.fn();

vi.mock('vscode', () => ({
    window: {
        showErrorMessage: mockShowErrorMessage,
    },
}));

vi.mock('vscode-jsonrpc/node', () => ({
    ResponseError: class ResponseError extends Error {
        data: unknown;
        constructor(code: number, message: string, data?: unknown) {
            super(message);
            this.data = data;
        }
    },
}));

import { handleAuthError } from '../../utils/errorUtils.js';

describe('handleAuthError', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('returns false for non-auth errors', async () => {
        const error = new Error('Network timeout');
        const daemon = { authWho: vi.fn(), profilesInvalidate: vi.fn() } as any;
        const retry = vi.fn();

        const handled = await handleAuthError(daemon, error, false, retry);

        expect(handled).toBe(false);
        expect(mockShowErrorMessage).not.toHaveBeenCalled();
    });

    it('returns false when isRetry is true (prevents infinite loop)', async () => {
        const error = new Error('unauthorized');
        const daemon = { authWho: vi.fn(), profilesInvalidate: vi.fn() } as any;
        const retry = vi.fn();

        const handled = await handleAuthError(daemon, error, true, retry);

        expect(handled).toBe(false);
        expect(mockShowErrorMessage).not.toHaveBeenCalled();
    });

    it('shows re-auth prompt and retries on success', async () => {
        const error = new Error('unauthorized');
        const daemon = {
            authWho: vi.fn().mockResolvedValue({ name: 'dev', index: 0 }),
            profilesInvalidate: vi.fn().mockResolvedValue(undefined),
        } as any;
        const retry = vi.fn().mockResolvedValue(undefined);
        mockShowErrorMessage.mockResolvedValue('Re-authenticate');

        const handled = await handleAuthError(daemon, error, false, retry);

        expect(handled).toBe(true);
        expect(daemon.profilesInvalidate).toHaveBeenCalledWith('dev');
        expect(retry).toHaveBeenCalled();
    });

    it('returns false when user cancels re-auth prompt', async () => {
        const error = new Error('unauthorized');
        const daemon = { authWho: vi.fn(), profilesInvalidate: vi.fn() } as any;
        const retry = vi.fn();
        mockShowErrorMessage.mockResolvedValue('Cancel');

        const handled = await handleAuthError(daemon, error, false, retry);

        expect(handled).toBe(false);
        expect(retry).not.toHaveBeenCalled();
    });

    it('returns false when retry throws', async () => {
        const error = new Error('unauthorized');
        const daemon = {
            authWho: vi.fn().mockResolvedValue({ name: 'dev', index: 0 }),
            profilesInvalidate: vi.fn().mockResolvedValue(undefined),
        } as any;
        const retry = vi.fn().mockRejectedValue(new Error('still failing'));
        mockShowErrorMessage.mockResolvedValue('Re-authenticate');

        const handled = await handleAuthError(daemon, error, false, retry);

        expect(handled).toBe(false);
    });

    it('uses profile index when name is null', async () => {
        const error = new Error('token expired');
        const daemon = {
            authWho: vi.fn().mockResolvedValue({ name: null, index: 2 }),
            profilesInvalidate: vi.fn().mockResolvedValue(undefined),
        } as any;
        const retry = vi.fn().mockResolvedValue(undefined);
        mockShowErrorMessage.mockResolvedValue('Re-authenticate');

        await handleAuthError(daemon, error, false, retry);

        expect(daemon.profilesInvalidate).toHaveBeenCalledWith('2');
    });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/PPDS.Extension && npx vitest run src/__tests__/utils/errorUtils.test.ts`
Expected: FAIL — `handleAuthError` does not exist

- [ ] **Step 3: Implement handleAuthError**

In `src/PPDS.Extension/src/utils/errorUtils.ts`, add these imports at the top of the file (after the existing `ResponseError` import):

```ts
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
```

Then add this function at the bottom of the file:

```ts
/**
 * Shared auth-error retry handler for panels.
 * Shows a re-authentication prompt when an auth error is detected,
 * invalidates cached tokens, and retries the operation once.
 *
 * Note: DataverseNotebookController has different auth-error semantics
 * (no retry, shows "re-execute" message) and does NOT use this helper.
 *
 * @returns true if the retry succeeded (caller should return early),
 *          false otherwise (caller should show error to user)
 */
export async function handleAuthError(
    daemon: DaemonClient,
    error: unknown,
    isRetry: boolean,
    retry: () => Promise<void>,
): Promise<boolean> {
    if (!isAuthError(error) || isRetry) return false;

    const action = await vscode.window.showErrorMessage(
        'Session expired. Re-authenticate?',
        'Re-authenticate', 'Cancel',
    );
    if (action !== 'Re-authenticate') return false;

    try {
        const who = await daemon.authWho();
        const profileId = who.name ?? String(who.index);
        await daemon.profilesInvalidate(profileId);
    } catch {
        // If authWho fails, proceed with retry anyway
    }

    try {
        await retry();
        return true;
    } catch {
        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd src/PPDS.Extension && npx vitest run src/__tests__/utils/errorUtils.test.ts`
Expected: All 6 tests pass

- [ ] **Step 5: Commit**

```
git add src/PPDS.Extension/src/utils/errorUtils.ts src/PPDS.Extension/src/__tests__/utils/errorUtils.test.ts
git commit -m "feat(ext): extract handleAuthError to shared utility with tests"
```

---

### Task 4: Create centralized config accessor

**Files:**
- Create: `src/PPDS.Extension/src/utils/config.ts`

- [ ] **Step 1: Create the config module**

```ts
// src/PPDS.Extension/src/utils/config.ts
import * as vscode from 'vscode';

/**
 * Typed accessors for PPDS extension configuration.
 * Centralizes all `vscode.workspace.getConfiguration('ppds')` reads
 * so new panels don't scatter inline config reads across the codebase.
 *
 * Each function reads fresh from workspace config (no caching) so
 * configuration changes take effect immediately.
 */

function cfg(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration('ppds');
}

/** Whether to auto-start the daemon on activation (default: true). */
export function autoStartDaemon(): boolean {
    return cfg().get<boolean>('autoStartDaemon', true);
}

/** Default TOP clause for queries (default: 100, range: 1-5000). */
export function queryDefaultTop(): number {
    return cfg().get<number>('queryDefaultTop', 100);
}
```

- [ ] **Step 2: Verify it compiles**

Run: `cd src/PPDS.Extension && npx tsc --noEmit -p tsconfig.json`
Expected: No errors

- [ ] **Step 3: Commit**

```
git add src/PPDS.Extension/src/utils/config.ts
git commit -m "feat(ext): add centralized config accessor"
```

---

## Chunk 2: Panel Architecture Refactoring

### Task 5: Consolidate WebviewPanelBase

**Files:**
- Modify: `src/PPDS.Extension/src/panels/WebviewPanelBase.ts`

- [ ] **Step 1: Add `initPanel`, make `handleMessage` abstract, add `logWebviewError`**

Replace the entire content of `src/PPDS.Extension/src/panels/WebviewPanelBase.ts`:

```ts
import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';

/**
 * Base class for webview panels with safe messaging and lifecycle management.
 *
 * Subclasses create their `WebviewPanel` and call `initPanel(panel)` to wire:
 * - `onDidReceiveMessage` → `handleMessage()` (abstract, subclass implements)
 * - `onDidDispose` → `dispose()`
 *
 * This eliminates per-panel boilerplate for lifecycle wiring.
 */
export abstract class WebviewPanelBase<
    TIncoming extends { command: string } = { command: string },
    TOutgoing extends { command: string } = { command: string; [key: string]: unknown },
> implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];
    private _disposed = false;
    private readonly _abortController = new AbortController();

    /** Fires when the panel is disposed. Pass to async operations so they can bail out early. */
    protected get abortSignal(): AbortSignal {
        return this._abortController.signal;
    }

    /**
     * Wire lifecycle listeners on a newly-created webview panel.
     * Call this from the subclass constructor after creating the panel
     * and setting its HTML content.
     */
    protected initPanel(panel: vscode.WebviewPanel): void {
        this.panel = panel;
        this.disposables.push(
            panel.webview.onDidReceiveMessage((msg: TIncoming) => {
                try {
                    const result = this.handleMessage(msg);
                    if (result instanceof Promise) {
                        result.catch((err: unknown) => {
                            const errMsg = err instanceof Error ? err.message : String(err);
                            // eslint-disable-next-line no-console -- unhandled message handler error
                            console.error(`[PPDS] Unhandled message error: ${errMsg}`);
                        });
                    }
                } catch (err) {
                    const errMsg = err instanceof Error ? err.message : String(err);
                    // eslint-disable-next-line no-console -- unhandled message handler error
                    console.error(`[PPDS] Unhandled message error: ${errMsg}`);
                }
            }),
            panel.onDidDispose(() => this.dispose()),
        );
    }

    protected postMessage(message: TOutgoing): void {
        this.panel?.webview.postMessage(message);
    }

    /**
     * Subscribes to daemon reconnect events. On reconnect, posts a
     * `daemonReconnected` message to the webview (shows the stale-data
     * banner) and calls the overridable `onDaemonReconnected` hook.
     */
    protected subscribeToDaemonReconnect(client: DaemonClient): void {
        this.disposables.push(
            client.onDidReconnect(() => {
                // Cast required: `daemonReconnected` is a shared protocol command that
                // every panel's TOutgoing includes, but TypeScript can't verify that
                // structurally from the base class.
                this.postMessage({ command: 'daemonReconnected' } as TOutgoing);
                this.onDaemonReconnected();
            })
        );
    }

    /** Override in subclasses to handle reconnection (e.g., auto-refresh). */
    protected onDaemonReconnected(): void {
        // Default: no-op
    }

    /**
     * Log a webview-side error and show it to the user.
     * Call from subclass `handleMessage` when receiving a `webviewError` message.
     */
    protected logWebviewError(error: string, stack?: string): void {
        // eslint-disable-next-line no-console -- forwarding webview errors to dev console for diagnostics
        console.error(`[PPDS Webview] ${error}`);
        // eslint-disable-next-line no-console -- forwarding webview errors to dev console for diagnostics
        if (stack) console.error(`[PPDS Webview Stack] ${stack}`);
        vscode.window.showErrorMessage(`PPDS: ${error}`);
    }

    /** Handle an incoming message from the webview. Subclasses implement their message switch here. */
    protected abstract handleMessage(message: TIncoming): Promise<void> | void;

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

- [ ] **Step 2: Verify it compiles**

Run: `cd src/PPDS.Extension && npx tsc --noEmit -p tsconfig.json`
Expected: Compile errors in QueryPanel.ts and SolutionsPanel.ts (handleMessage now abstract) — expected, fixed in next tasks

- [ ] **Step 3: Commit**

```
git add src/PPDS.Extension/src/panels/WebviewPanelBase.ts
git commit -m "refactor(ext): consolidate WebviewPanelBase — initPanel, abstract handleMessage, logWebviewError"
```

---

### Task 6: Update QueryPanel to use new base class

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts`

- [ ] **Step 1: Replace constructor wiring with `initPanel` and extract `handleMessage`**

In `src/PPDS.Extension/src/panels/QueryPanel.ts`:

**Remove** the `isAuthError` import (line 7) — will use `handleAuthError` instead:
```ts
// REMOVE: import { isAuthError } from '../utils/errorUtils.js';
// ADD:
import { handleAuthError } from '../utils/errorUtils.js';
```

**Add** config import:
```ts
import { queryDefaultTop } from '../utils/config.js';
```

In the constructor, **replace** the block that creates the panel and wires listeners (lines 73-236) with:

```ts
        this.panelId = QueryPanel.nextId++;
        QueryPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.dataExplorer',
            'Data Explorer #' + this.panelId,
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [
                    vscode.Uri.joinPath(extensionUri, 'node_modules'),
                    vscode.Uri.joinPath(extensionUri, 'dist'),
                ],
            }
        );

        panel.webview.html = this.getHtmlContent(panel.webview);
        this.initPanel(panel);
        this.subscribeToDaemonReconnect(this.daemon);
```

**Add** a `handleMessage` method (the existing switch body extracted into a method):

```ts
    protected async handleMessage(message: QueryPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'executeQuery':
                await this.executeQuery(message.sql, false, message.useTds, message.language);
                break;
            case 'showFetchXml':
                await this.showFetchXml(message.sql);
                break;
            case 'loadMore':
                await this.loadMore(message.pagingCookie, message.page);
                break;
            case 'explainQuery':
                await this.explainQuery(message.sql);
                break;
            case 'exportResults':
                await this.exportResults(message.format);
                break;
            case 'saveQuery':
                await this.saveQuery(message.sql, message.language);
                break;
            case 'loadQueryFromFile':
                await this.loadQueryFromFile();
                break;
            case 'openInNotebook':
                await vscode.commands.executeCommand('ppds.openQueryInNotebook', message.sql);
                break;
            case 'showHistory': {
                const sql = await showQueryHistory(this.daemon);
                if (sql) {
                    this.postMessage({ command: 'loadQuery', sql });
                }
                break;
            }
            case 'copyToClipboard':
                await vscode.env.clipboard.writeText(message.text);
                break;
            case 'openRecordUrl': {
                const parsed = vscode.Uri.parse(message.url);
                if (parsed.scheme === 'https' || parsed.scheme === 'http') {
                    await vscode.env.openExternal(parsed);
                }
                break;
            }
            case 'requestClipboard': {
                const clipText = await vscode.env.clipboard.readText();
                this.postMessage({ command: 'clipboardContent', text: clipText });
                break;
            }
            case 'requestCompletions': {
                const requestId = message.requestId;
                try {
                    const result = await this.daemon.queryComplete({
                        sql: message.sql,
                        cursorOffset: message.cursorOffset,
                        language: message.language,
                    });
                    this.postMessage({ command: 'completionResult', requestId, items: result.items });
                } catch {
                    this.postMessage({ command: 'completionResult', requestId, items: [] });
                }
                break;
            }
            case 'ready':
                if (this.initialSql) {
                    this.postMessage({ command: 'loadQuery', sql: this.initialSql });
                }
                void this.initEnvironment();
                break;
            case 'webviewError':
                this.logWebviewError(message.error, message.stack);
                break;
            case 'cancelQuery':
                this.queryCts?.cancel();
                break;
            case 'convertQuery': {
                const { sql, toLanguage } = message;
                try {
                    let converted: string;
                    if (toLanguage === 'xml') {
                        const result = await this.daemon.queryExplain({
                            sql,
                            environmentUrl: this.environmentUrl ?? undefined,
                        });
                        converted = result.fetchXml ?? result.plan;
                    } else {
                        const { FetchXmlToSqlTranspiler } = await import('../utils/fetchXmlToSql.js');
                        const transpiler = new FetchXmlToSqlTranspiler();
                        const result = transpiler.transpile(sql);
                        if (!result.success) {
                            throw new Error(result.error || 'Transpilation failed');
                        }
                        converted = result.sql;
                    }
                    this.postMessage({
                        command: 'queryConverted',
                        content: converted,
                        language: toLanguage,
                    });
                } catch (error) {
                    const msg = error instanceof Error ? error.message : String(error);
                    vscode.window.showWarningMessage(`Conversion failed: ${msg}`);
                    this.postMessage({
                        command: 'conversionFailed',
                        error: msg,
                        language: toLanguage,
                    });
                }
                break;
            }
            case 'refresh':
                if (this.lastSql) {
                    await this.executeQuery(this.lastSql, false, this.lastUseTds, this.lastLanguage);
                }
                break;
            case 'requestEnvironmentList': {
                const env = await showEnvironmentPicker(this.daemon, this.environmentUrl);
                if (env) {
                    this.environmentUrl = env.url;
                    this.environmentDisplayName = env.displayName;
                    this.postMessage({ command: 'updateEnvironment', name: env.displayName, url: env.url });
                    this.updateTitle();
                }
                break;
            }
            default:
                assertNever(message);
        }
    }
```

**Key changes in `handleMessage`:**
- `convertQuery` case now uses `this.postMessage(...)` instead of raw `this.panel?.webview.postMessage(...)` (fixes type safety issue #4)
- `webviewError` case uses `this.logWebviewError()` from base class

**Required field promotion:** `initialSql` was a constructor parameter captured by closure in the inline handler. Since `handleMessage` is now a method, add this field to the class:

```ts
    private readonly initialSql: string | undefined;
```

And in the constructor body, before `this.panelId = ...`:
```ts
        this.initialSql = initialSql;
```

**Replace** the auth-error block in `executeQuery` (lines 340-362) with:

```ts
            // Check for auth errors and offer re-authentication (only on first attempt)
            if (await handleAuthError(this.daemon, error, isRetry, () =>
                this.executeQuery(sql, true, useTds, language, isConfirmed)
            )) {
                return;
            }
```

**Replace** the inline config read (line 287) with:

```ts
            const defaultTop = queryDefaultTop();
```

- [ ] **Step 2: Verify it compiles**

Run: `cd src/PPDS.Extension && npx tsc --noEmit -p tsconfig.json`
Expected: Compile errors in SolutionsPanel.ts only (fixed next task)

- [ ] **Step 3: Commit**

```
git add src/PPDS.Extension/src/panels/QueryPanel.ts
git commit -m "refactor(ext): QueryPanel uses initPanel, handleMessage, handleAuthError, typed postMessage"
```

---

### Task 7: Update SolutionsPanel to use new base class

**Files:**
- Modify: `src/PPDS.Extension/src/panels/SolutionsPanel.ts`
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`
- Modify: `src/PPDS.Extension/src/panels/webview/solutions-panel.ts`

- [ ] **Step 1: Add `webviewError` to SolutionsPanelWebviewToHost**

In `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`, add to the `SolutionsPanelWebviewToHost` union:

```ts
export type SolutionsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'requestEnvironmentList' }
    | { command: 'refresh' }
    | { command: 'expandSolution'; uniqueName: string }
    | { command: 'collapseSolution'; uniqueName: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openInMaker'; solutionId?: string }
    | { command: 'webviewError'; error: string; stack?: string };
```

- [ ] **Step 2: Add error handler to solutions-panel.ts webview script**

At the top of `src/PPDS.Extension/src/panels/webview/solutions-panel.ts`, before all other imports, add:

```ts
import { installErrorHandler } from './shared/error-handler.js';
```

And right after the `const vscode = getVsCodeApi<...>()` line, add:

```ts
installErrorHandler((msg) => vscode.postMessage(msg as SolutionsPanelWebviewToHost));
```

- [ ] **Step 3: Update SolutionsPanel.ts to use initPanel, handleMessage, handleAuthError**

In `src/PPDS.Extension/src/panels/SolutionsPanel.ts`:

**Replace** imports:
```ts
// REMOVE: import { isAuthError } from '../utils/errorUtils.js';
// ADD:
import { handleAuthError } from '../utils/errorUtils.js';
```

**Replace** constructor body (panel creation + wiring) with:

```ts
        this.panelId = SolutionsPanel.nextId++;
        SolutionsPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.solutionsPanel',
            `Solutions #${this.panelId}`,
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                retainContextWhenHidden: false,
                localResourceRoots: [
                    vscode.Uri.joinPath(extensionUri, 'node_modules'),
                    vscode.Uri.joinPath(extensionUri, 'dist'),
                ],
            }
        );

        panel.webview.html = this.getHtmlContent(panel.webview);
        this.initPanel(panel);
        this.subscribeToDaemonReconnect(this.daemon);
```

Note `retainContextWhenHidden: false` — SolutionsPanel is a static list, the `ready` → `initialize()` flow handles re-fetching on re-show. Verified: `solutions-panel.ts` line 339 sends `vscode.postMessage({ command: 'ready' })` at script load, which re-fires when VS Code recreates the webview DOM on re-show.

**Add** `handleMessage` method:

```ts
    protected async handleMessage(message: SolutionsPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initialize();
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPicker();
                break;
            case 'refresh':
                await this.loadSolutions();
                break;
            case 'expandSolution':
                await this.loadComponents(message.uniqueName);
                break;
            case 'collapseSolution':
                // No-op on host side; collapse is handled in webview JS
                break;
            case 'copyToClipboard':
                await vscode.env.clipboard.writeText(message.text);
                break;
            case 'openInMaker': {
                if (this.environmentId) {
                    let url = buildMakerUrl(this.environmentId);
                    if (message.solutionId) {
                        url = `${url}/${message.solutionId}`;
                    }
                    await vscode.env.openExternal(vscode.Uri.parse(url));
                } else {
                    vscode.window.showInformationMessage('Environment ID not available \u2014 cannot open Maker Portal.');
                }
                break;
            }
            case 'webviewError':
                this.logWebviewError(message.error, message.stack);
                break;
            default:
                assertNever(message);
        }
    }
```

**Replace** auth-error block in `loadSolutions` (lines 218-239) with:

```ts
            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadSolutions(true))) {
                return;
            }
```

- [ ] **Step 4: Verify everything compiles**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors

- [ ] **Step 5: Run all tests**

Run: `cd src/PPDS.Extension && npx vitest run`
Expected: All tests pass

- [ ] **Step 6: Commit**

```
git add src/PPDS.Extension/src/panels/SolutionsPanel.ts src/PPDS.Extension/src/panels/webview/solutions-panel.ts src/PPDS.Extension/src/panels/webview/shared/message-types.ts
git commit -m "refactor(ext): SolutionsPanel uses initPanel, handleMessage, handleAuthError, onerror, retainContextWhenHidden:false"
```

---

## Chunk 3: Infrastructure & Polish

### Task 8: Refactor esbuild.js for scalability

**Files:**
- Modify: `src/PPDS.Extension/esbuild.js`

- [ ] **Step 1: Replace individual context variables with data-driven array**

Replace entire content of `src/PPDS.Extension/esbuild.js`:

```js
const esbuild = require('esbuild');
const production = process.argv.includes('--production');

// ── Build definitions ────────────────────────────────────────────────────────
// To add a new panel: add JS + CSS entries below. No other changes needed.

const builds = [
    // Extension host (Node.js, CJS)
    {
        entryPoints: ['src/extension.ts'],
        bundle: true,
        format: 'cjs',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'node',
        outfile: 'dist/extension.js',
        external: ['vscode'],
        logLevel: 'warning',
    },
    // Monaco editor bundle (browser, IIFE)
    {
        entryPoints: ['src/panels/monaco-entry.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/monaco-editor.js',
        logLevel: 'warning',
        loader: { '.ttf': 'file' },
    },
    // Monaco editor worker (browser, IIFE)
    {
        entryPoints: ['src/panels/monaco-worker.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: false,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/editor.worker.js',
        logLevel: 'warning',
    },
    // ── Panel webview scripts (browser, IIFE) ────────────────────────────────
    {
        entryPoints: ['src/panels/webview/query-panel.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/query-panel.js',
        logLevel: 'warning',
    },
    {
        entryPoints: ['src/panels/webview/solutions-panel.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/solutions-panel.js',
        logLevel: 'warning',
    },
    // ── Panel CSS bundles ────────────────────────────────────────────────────
    {
        entryPoints: ['src/panels/styles/query-panel.css'],
        bundle: true,
        minify: production,
        outfile: 'dist/query-panel.css',
        logLevel: 'warning',
    },
    {
        entryPoints: ['src/panels/styles/solutions-panel.css'],
        bundle: true,
        minify: production,
        outfile: 'dist/solutions-panel.css',
        logLevel: 'warning',
    },
];

async function main() {
    const contexts = await Promise.all(builds.map(b => esbuild.context(b)));

    if (process.argv.includes('--watch')) {
        await Promise.all(contexts.map(c => c.watch()));
    } else {
        await Promise.all(contexts.map(c => c.rebuild()));
        await Promise.all(contexts.map(c => c.dispose()));
    }
}

main().catch(e => { console.error(e); process.exit(1); });
```

- [ ] **Step 2: Verify build works**

Run: `cd src/PPDS.Extension && node esbuild.js`
Expected: Builds successfully, produces same dist/ files

- [ ] **Step 3: Commit**

```
git add src/PPDS.Extension/esbuild.js
git commit -m "refactor(ext): data-driven esbuild config — adding panels is now config-only"
```

---

### Task 9: Make debugCommands panel registry dynamic

**Files:**
- Modify: `src/PPDS.Extension/src/commands/debugCommands.ts`
- Modify: `src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts`
- Modify: `src/PPDS.Extension/src/extension.ts`

- [ ] **Step 1: Update debugCommands to use dynamic Record**

In `src/PPDS.Extension/src/commands/debugCommands.ts`:

Change the `PanelState` interface:
```ts
interface PanelState {
    [panelName: string]: number;
}
```

Change `getPanelState` signature:
```ts
export function getPanelState(counts: Record<string, () => number>): PanelState {
    const state: PanelState = {};
    for (const [name, countFn] of Object.entries(counts)) {
        state[name] = countFn();
    }
    return state;
}
```

Change `registerDebugCommands` parameter:
```ts
export function registerDebugCommands(
    context: { subscriptions: { push: (d: vscode.Disposable) => void } },
    daemon: DaemonClient,
    profileTreeProvider: ProfileTreeDataProvider,
    extensionState: { daemonState: string; profileCount: number },
    panelCounts: Record<string, () => number>,
): void {
```

The `panelState` command handler now just passes through:
```ts
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.debug.panelState', () => {
            return getPanelState(panelCounts);
        }),
    );
```

- [ ] **Step 2: Update extension.ts call site**

In `src/PPDS.Extension/src/extension.ts`, the `registerDebugCommands` call (line 336-339):

```ts
    registerDebugCommands(context, client, profileTreeProvider, extensionState, {
        queryPanels: () => QueryPanel.instanceCount,
        solutionsPanels: () => SolutionsPanel.instanceCount,
    });
```

No change needed — the object literal `{ queryPanels: ..., solutionsPanels: ... }` already satisfies `Record<string, () => number>`.

- [ ] **Step 3: Update tests**

In `src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts`:

Update `getPanelState` tests to use the new signature:

```ts
    describe('getPanelState', () => {
        it('returns panel instance counts', () => {
            const result = getPanelState({ queryPanels: () => 2, solutionsPanels: () => 1 });
            expect(result).toEqual({ queryPanels: 2, solutionsPanels: 1 });
        });

        it('returns zero counts when no panels are open', () => {
            const result = getPanelState({ queryPanels: () => 0, solutionsPanels: () => 0 });
            expect(result).toEqual({ queryPanels: 0, solutionsPanels: 0 });
        });

        it('handles dynamic panel types', () => {
            const result = getPanelState({
                queryPanels: () => 1,
                solutionsPanels: () => 2,
                metadataPanels: () => 3,
            });
            expect(result).toEqual({ queryPanels: 1, solutionsPanels: 2, metadataPanels: 3 });
        });
    });
```

Update `registerDebugCommands` tests — change the `panelCounts` parameter from `{ queryPanelCount, solutionsPanelCount }` to `{ queryPanels, solutionsPanels }` (matching the Record keys):

Update ALL five `registerDebugCommands` call sites in the test file to use the new key names.
There are calls at approximately lines 146-151, 166-178, 196-209, 229-242, and 245-257.
Change the fifth argument from:
```ts
{ queryPanelCount: () => N, solutionsPanelCount: () => N },
```
to:
```ts
{ queryPanels: () => N, solutionsPanels: () => N },
```

And update the "panelState command handler returns live counts" test:
```ts
        it('panelState command handler returns live counts', () => {
            // ...
            let qCount = 0;
            let sCount = 0;

            registerDebugCommands(
                mockContext as any,
                mockDaemon as any,
                mockProvider as any,
                { daemonState: 'error', profileCount: 0 },
                { queryPanels: () => qCount, solutionsPanels: () => sCount },
            );

            // ... handler lookup ...
            expect(handler()).toEqual({ queryPanels: 0, solutionsPanels: 0 });

            qCount = 3;
            sCount = 2;
            expect(handler()).toEqual({ queryPanels: 3, solutionsPanels: 2 });
        });
```

- [ ] **Step 4: Run tests**

Run: `cd src/PPDS.Extension && npx vitest run src/__tests__/commands/debugCommands.test.ts`
Expected: All tests pass

- [ ] **Step 5: Commit**

```
git add src/PPDS.Extension/src/commands/debugCommands.ts src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts src/PPDS.Extension/src/extension.ts
git commit -m "refactor(ext): dynamic panel registry in debugCommands — no manual additions for new panels"
```

---

### Task 10: Group activate() panel commands

**Files:**
- Modify: `src/PPDS.Extension/src/extension.ts`

- [ ] **Step 1: Extract panel commands into registerPanelCommands**

In `src/PPDS.Extension/src/extension.ts`, add a new function before `activate`:

```ts
/**
 * Registers commands for opening panel-based views (Data Explorer, Solutions, etc.).
 * Add new panel commands here when adding panels.
 */
function registerPanelCommands(
    context: vscode.ExtensionContext,
    client: DaemonClient,
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.dataExplorer', () => {
            QueryPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openSolutions', () => {
            SolutionsPanel.show(context.extensionUri, client, undefined, undefined, context.globalState);
        }),
        vscode.commands.registerCommand('ppds.openQueryInNotebook', (sql?: string) => {
            void openQueryInNotebook(sql ?? '');
        }),
        vscode.commands.registerCommand('ppds.openNotebooks', () => {
            void createNewNotebook();
        }),
        vscode.commands.registerCommand('ppds.showLogs', () => {
            logChannel?.show();
        }),
    );
}
```

Then replace the inline registrations in `activate` (lines 366-393) with:

```ts
    // ── Panel Commands ───────────────────────────────────────────────────
    registerPanelCommands(context, client);
```

**Important:** Remove ALL of the following from `activate()`:
- The `ppds.dataExplorer` command registration (line 368)
- The `ppds.openQueryInNotebook` command registration (line 371)
- The standalone `openNotebooksCmd` variable declaration and `context.subscriptions.push(openNotebooksCmd)` (lines 376-379)
- The `ppds.openSolutions` command registration (line 383)
- The `ppds.showLogs` command registration (line 390)

Note: `logChannel` is module-level, so the extracted function can reference it.

- [ ] **Step 2: Verify build and tests**

Run: `cd src/PPDS.Extension && npm run compile && npx vitest run`
Expected: Build succeeds, all tests pass

- [ ] **Step 3: Commit**

```
git add src/PPDS.Extension/src/extension.ts
git commit -m "refactor(ext): extract registerPanelCommands from activate()"
```

---

### Task 11: Consolidate moveProfile sort logic

**Files:**
- Modify: `src/PPDS.Extension/src/views/profileTreeView.ts`
- Modify: `src/PPDS.Extension/src/extension.ts`

- [ ] **Step 1: Add moveProfile method to ProfileTreeDataProvider**

In `src/PPDS.Extension/src/views/profileTreeView.ts`, add method to `ProfileTreeDataProvider`:

```ts
    /**
     * Swaps the sort position of a profile with its neighbor.
     * Consolidates the sort logic that was duplicated in extension.ts.
     */
    async moveProfile(profileId: string, direction: 'up' | 'down'): Promise<void> {
        const sortOrder = this.globalState?.get<Record<string, number>>('ppds.profiles.sortOrder') ?? {};
        const profiles = await this.daemonClient.authList();
        const sorted = profiles.profiles.map(p => ({ id: getProfileId(p), profile: p }));

        sorted.sort((a, b) => {
            const orderA = sortOrder[a.id] ?? a.profile.index;
            const orderB = sortOrder[b.id] ?? b.profile.index;
            return orderA - orderB;
        });

        const targetIdx = sorted.findIndex(i => i.id === profileId);
        const swapIdx = direction === 'up' ? targetIdx - 1 : targetIdx + 1;

        if (targetIdx < 0 || swapIdx < 0 || swapIdx >= sorted.length) return;

        const newOrder: Record<string, number> = {};
        sorted.forEach((it, idx) => { newOrder[it.id] = idx; });
        newOrder[sorted[targetIdx].id] = swapIdx;
        newOrder[sorted[swapIdx].id] = targetIdx;

        await this.globalState?.update('ppds.profiles.sortOrder', newOrder);
        this.refresh();
    }
```

- [ ] **Step 2: Simplify moveProfile in extension.ts**

Replace the `moveProfile` function in `src/PPDS.Extension/src/extension.ts` (lines 148-181) with:

```ts
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.moveProfileUp', cmd(async (item: { profile: ProfileInfo }) => {
            if (!item?.profile) return;
            const profileId = getProfileId(item.profile);
            await profileTreeProvider.moveProfile(profileId, 'up');
        })),
        vscode.commands.registerCommand('ppds.moveProfileDown', cmd(async (item: { profile: ProfileInfo }) => {
            if (!item?.profile) return;
            const profileId = getProfileId(item.profile);
            await profileTreeProvider.moveProfile(profileId, 'down');
        })),
    );
```

Remove the old `moveProfile` function and the two `eslint-disable-next-line` comments on lines 184-187.

Note: `ProfileInfo` is already imported at the top of `extension.ts` (line 4). No import changes needed.

- [ ] **Step 3: Run tests**

Run: `cd src/PPDS.Extension && npx vitest run`
Expected: All tests pass

- [ ] **Step 4: Commit**

```
git add src/PPDS.Extension/src/views/profileTreeView.ts src/PPDS.Extension/src/extension.ts
git commit -m "refactor(ext): consolidate moveProfile sort logic into ProfileTreeDataProvider"
```

---

### Task 12: Add env list caching to ProfileTreeDataProvider

**Files:**
- Modify: `src/PPDS.Extension/src/views/profileTreeView.ts`

- [ ] **Step 1: Add a short-lived cache for envList results**

In `src/PPDS.Extension/src/views/profileTreeView.ts`, add cache fields to `ProfileTreeDataProvider`:

```ts
    /** Cached env list to avoid re-fetching on every tree expand. Cleared on refresh(). */
    private envCache: Awaited<ReturnType<DaemonClient['envList']>> | null = null;
    private envCacheTime = 0;
    private static readonly ENV_CACHE_TTL_MS = 30_000; // 30 seconds
```

Add a cache accessor method:

```ts
    private async getCachedEnvList(): Promise<Awaited<ReturnType<DaemonClient['envList']>>> {
        const now = Date.now();
        if (this.envCache && (now - this.envCacheTime) < ProfileTreeDataProvider.ENV_CACHE_TTL_MS) {
            return this.envCache;
        }
        const result = await this.daemonClient.envList();
        this.envCache = result;
        this.envCacheTime = now;
        return result;
    }
```

Update `refresh()` to clear the cache:

```ts
    refresh(): void {
        this.envCache = null;
        this._onDidChangeTreeData.fire();
    }
```

In `getEnvironments()`, replace `await this.daemonClient.envList()` with `await this.getCachedEnvList()`.

- [ ] **Step 2: Verify build**

Run: `cd src/PPDS.Extension && npm run typecheck`
Expected: No errors

- [ ] **Step 3: Run tests**

Run: `cd src/PPDS.Extension && npx vitest run src/__tests__/views/profileTreeView.test.ts`
Expected: All tests pass (cache is transparent)

- [ ] **Step 4: Commit**

```
git add src/PPDS.Extension/src/views/profileTreeView.ts
git commit -m "perf(ext): cache envList in ProfileTreeDataProvider (30s TTL)"
```

---

### Task 13: Run quality gates

- [ ] **Step 1: Typecheck**

Run: `cd src/PPDS.Extension && npm run typecheck:all`
Expected: No errors

- [ ] **Step 2: Lint**

Run: `cd src/PPDS.Extension && npm run lint`
Expected: No errors (or only pre-existing warnings)

- [ ] **Step 3: Build**

Run: `cd src/PPDS.Extension && npm run compile`
Expected: Build succeeds

- [ ] **Step 4: Run all unit tests**

Run: `cd src/PPDS.Extension && npx vitest run`
Expected: All tests pass

- [ ] **Step 5: Check for dead code**

Run: `cd src/PPDS.Extension && npm run dead-code` (runs `knip` — script verified in package.json)
Expected: No new dead code introduced

---

## Out of Scope

These items from the review are not addressed in this plan:

| Item | Reason |
|------|--------|
| Panel state persistence across VS Code restarts | Feature addition, not a fix. Decide per-panel during implementation. |
| Lightweight heartbeat endpoint | Requires daemon C# changes. Separate PR. |
| Notification handler generalization | YAGNI — generalize when a second notification type is needed. |
| Environment picker string coupling | Minor fragility, churn outweighs benefit at 2 panels. |
