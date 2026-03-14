# Data Explorer Monaco Editor — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Data Explorer's raw `<textarea>` with an embedded Monaco Editor providing SQL/FetchXML syntax highlighting, language auto-detection, and IntelliSense completions via the existing daemon `query/complete` endpoint.

**Architecture:** Install `monaco-editor` npm package, add a separate browser-targeted esbuild entry to bundle Monaco for the webview, replace the textarea with a Monaco instance, register completion providers for SQL and XML that bridge to the daemon via postMessage, and add a language auto-detect + manual toggle button.

**Tech Stack:** TypeScript, `monaco-editor` npm package, esbuild (browser build), VS Code webview API

**Spec:** [`specs/vscode-data-explorer-monaco-editor.md`](../../../specs/vscode-data-explorer-monaco-editor.md)

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `extension/package.json` | Add `monaco-editor` dependency |
| Modify | `extension/esbuild.js` | Add browser-targeted Monaco bundle build |
| Create | `extension/src/panels/monaco-entry.ts` | Monaco webview entry point — initializes editor + workers |
| Modify | `extension/src/panels/QueryPanel.ts` | Replace textarea with Monaco div, update CSP, localResourceRoots, add completion bridge + language toggle, update all editor references |
| Create | `extension/src/__tests__/panels/monacoUtils.test.ts` | Unit tests for detectLanguage, mapCompletionKind |
| Create | `extension/src/panels/monacoUtils.ts` | Pure utility functions (testable without Monaco) |

---

## Chunk 1: Monaco Build Infrastructure

### Task 1: Install monaco-editor + Configure esbuild

**Files:**
- Modify: `extension/package.json`
- Modify: `extension/esbuild.js`

- [ ] **Step 1: Install monaco-editor**

Run: `cd extension && npm install monaco-editor`

- [ ] **Step 2: Create Monaco webview entry point**

Create `extension/src/panels/monaco-entry.ts`:

```typescript
/**
 * Monaco Editor entry point for webview bundles.
 * This file is the esbuild entry for the browser-targeted Monaco bundle.
 * It configures the Monaco environment (workers) and re-exports monaco.
 */
import * as monaco from 'monaco-editor';

// Configure Monaco to use inline workers (blob URLs)
// This avoids needing separate worker file serving
(self as any).MonacoEnvironment = {
    getWorker(_moduleId: string, label: string) {
        // SQL and XML use the default editor worker
        const workerUrl = (self as any).__MONACO_WORKER_URL__;
        if (workerUrl) {
            return new Worker(workerUrl);
        }
        // Fallback: create a minimal no-op worker
        const blob = new Blob(
            ['self.onmessage = function() {}'],
            { type: 'application/javascript' },
        );
        return new Worker(URL.createObjectURL(blob));
    },
};

// Expose monaco globally for the webview IIFE to use
(window as any).monaco = monaco;
```

- [ ] **Step 3: Create editor worker entry point**

Create `extension/src/panels/monaco-worker.ts`:

```typescript
/**
 * Monaco editor worker entry point.
 * Bundled separately by esbuild and loaded via blob URL.
 */
import 'monaco-editor/esm/vs/editor/editor.worker';
```

- [ ] **Step 4: Update esbuild.js with Monaco builds**

Replace the entire `extension/esbuild.js`:

```javascript
const esbuild = require('esbuild');
const production = process.argv.includes('--production');

async function main() {
    // Build 1: Extension host (Node.js, CJS) — existing
    const extCtx = await esbuild.context({
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
    });

    // Build 2: Monaco editor bundle (browser, IIFE) — new
    const monacoCtx = await esbuild.context({
        entryPoints: ['src/panels/monaco-entry.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/monaco-editor.js',
        logLevel: 'warning',
        loader: {
            '.ttf': 'file',
        },
    });

    // Build 3: Monaco editor worker (browser, IIFE) — new
    const workerCtx = await esbuild.context({
        entryPoints: ['src/panels/monaco-worker.ts'],
        bundle: true,
        format: 'iife',
        minify: production,
        sourcemap: false,
        sourcesContent: false,
        platform: 'browser',
        outfile: 'dist/editor.worker.js',
        logLevel: 'warning',
    });

    if (process.argv.includes('--watch')) {
        await Promise.all([extCtx.watch(), monacoCtx.watch(), workerCtx.watch()]);
    } else {
        await Promise.all([extCtx.rebuild(), monacoCtx.rebuild(), workerCtx.rebuild()]);
        await Promise.all([extCtx.dispose(), monacoCtx.dispose(), workerCtx.dispose()]);
    }
}
main().catch(e => { console.error(e); process.exit(1); });
```

- [ ] **Step 5: Verify builds succeed**

Run: `cd extension && node esbuild.js`
Expected: Three files created in `dist/`: `extension.js`, `monaco-editor.js`, `editor.worker.js`

- [ ] **Step 6: Commit**

```bash
git add extension/package.json extension/package-lock.json extension/esbuild.js extension/src/panels/monaco-entry.ts extension/src/panels/monaco-worker.ts
git commit -m "build(extension): add Monaco Editor bundle infrastructure"
```

---

## Chunk 2: Monaco Editor Integration in QueryPanel

### Task 2: Utility Functions + Tests

**Files:**
- Create: `extension/src/panels/monacoUtils.ts`
- Create: `extension/src/__tests__/panels/monacoUtils.test.ts`

- [ ] **Step 1: Create pure utility functions**

Create `extension/src/panels/monacoUtils.ts`:

```typescript
/**
 * Pure utility functions for Monaco Editor integration.
 * Extracted for testability — no Monaco dependency.
 */

/**
 * Detect whether content is SQL or FetchXML based on first non-whitespace character.
 * SQL never starts with '<'. FetchXML always starts with '<fetch' or '<?xml'.
 */
export function detectLanguage(content: string): 'sql' | 'xml' {
    const trimmed = content.trimStart();
    return trimmed.startsWith('<') ? 'xml' : 'sql';
}

/**
 * Map daemon completion item kind to Monaco CompletionItemKind numeric value.
 * Monaco kinds: Method=0, Function=1, Constructor=2, Field=3, Variable=4,
 *               Class=5, Struct=6, Interface=7, Module=8, Property=9,
 *               Event=10, Operator=11, Unit=12, Value=13, Constant=14,
 *               Enum=15, EnumMember=16, Keyword=17, Text=18, Color=19,
 *               File=20, Reference=21, Customcolor=22, Folder=23, TypeParameter=24, Snippet=27
 */
export function mapCompletionKind(daemonKind: string): number {
    switch (daemonKind) {
        case 'entity': return 5;    // Class
        case 'attribute': return 3; // Field
        case 'keyword': return 17;  // Keyword
        default: return 18;         // Text
    }
}

/**
 * Map daemon CompletionItemDto to Monaco-compatible suggestion shape.
 * Returned objects match monaco.languages.CompletionItem structure.
 */
export function mapCompletionItems(
    items: Array<{ label: string; insertText: string; kind: string; detail: string | null; description: string | null; sortOrder: number }>,
): Array<{ label: string; insertText: string; kind: number; detail: string; sortText: string }> {
    return items.map(item => ({
        label: item.label,
        insertText: item.insertText,
        kind: mapCompletionKind(item.kind),
        detail: item.detail ?? '',
        sortText: String(item.sortOrder).padStart(5, '0'),
    }));
}
```

- [ ] **Step 2: Write tests**

Create `extension/src/__tests__/panels/monacoUtils.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { detectLanguage, mapCompletionKind, mapCompletionItems } from '../../panels/monacoUtils.js';

describe('detectLanguage', () => {
    it('returns sql for plain SQL', () => {
        expect(detectLanguage('SELECT * FROM account')).toBe('sql');
    });

    it('returns xml for FetchXML', () => {
        expect(detectLanguage('<fetch><entity name="account"/></fetch>')).toBe('xml');
    });

    it('returns xml for FetchXML with xml declaration', () => {
        expect(detectLanguage('<?xml version="1.0"?><fetch/>')).toBe('xml');
    });

    it('handles leading whitespace before SQL', () => {
        expect(detectLanguage('  \n  SELECT 1')).toBe('sql');
    });

    it('handles leading whitespace before FetchXML', () => {
        expect(detectLanguage('  \n  <fetch/>')).toBe('xml');
    });

    it('returns sql for empty string', () => {
        expect(detectLanguage('')).toBe('sql');
    });

    it('returns sql for whitespace-only', () => {
        expect(detectLanguage('   \n  ')).toBe('sql');
    });
});

describe('mapCompletionKind', () => {
    it('maps entity to Class (5)', () => {
        expect(mapCompletionKind('entity')).toBe(5);
    });

    it('maps attribute to Field (3)', () => {
        expect(mapCompletionKind('attribute')).toBe(3);
    });

    it('maps keyword to Keyword (17)', () => {
        expect(mapCompletionKind('keyword')).toBe(17);
    });

    it('maps unknown to Text (18)', () => {
        expect(mapCompletionKind('something')).toBe(18);
    });
});

describe('mapCompletionItems', () => {
    it('maps daemon items to Monaco format', () => {
        const items = [
            { label: 'account', insertText: 'account', kind: 'entity', detail: 'Entity', description: null, sortOrder: 1 },
            { label: 'name', insertText: 'name', kind: 'attribute', detail: 'String', description: null, sortOrder: 10 },
        ];
        const result = mapCompletionItems(items);
        expect(result).toHaveLength(2);
        expect(result[0]).toEqual({ label: 'account', insertText: 'account', kind: 5, detail: 'Entity', sortText: '00001' });
        expect(result[1]).toEqual({ label: 'name', insertText: 'name', kind: 3, detail: 'String', sortText: '00010' });
    });

    it('handles null detail', () => {
        const items = [
            { label: 'SELECT', insertText: 'SELECT', kind: 'keyword', detail: null, description: null, sortOrder: 0 },
        ];
        const result = mapCompletionItems(items);
        expect(result[0].detail).toBe('');
    });
});
```

- [ ] **Step 3: Run tests**

Run: `cd extension && npx vitest run src/__tests__/panels/monacoUtils.test.ts`
Expected: All PASS

- [ ] **Step 4: Commit**

```bash
git add extension/src/panels/monacoUtils.ts extension/src/__tests__/panels/monacoUtils.test.ts
git commit -m "feat(extension): add Monaco utility functions with tests"
```

### Task 3: Replace Textarea with Monaco Editor

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

This is the largest task. It modifies the webview HTML, CSS, JS, CSP, and the host-side message handler.

- [ ] **Step 1: Update localResourceRoots and add Monaco URI**

In `QueryPanel.ts`, update the constructor's `localResourceRoots` (~line 72):

```typescript
localResourceRoots: [
    vscode.Uri.joinPath(extensionUri, 'node_modules'),
    vscode.Uri.joinPath(extensionUri, 'dist'),
],
```

In `getHtmlContent()`, add Monaco URIs after `toolkitUri` (~line 341):

```typescript
const monacoUri = webview.asWebviewUri(
    vscode.Uri.joinPath(this.extensionUri, 'dist', 'monaco-editor.js')
);
const workerUri = webview.asWebviewUri(
    vscode.Uri.joinPath(this.extensionUri, 'dist', 'editor.worker.js')
);
```

- [ ] **Step 2: Update CSP**

Replace the existing CSP meta tag (~line 351):

```html
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource}; worker-src blob:;">
```

Add the Monaco script tag after the toolkit script (~line 353):

```html
<script nonce="${nonce}">self.__MONACO_WORKER_URL__ = '${workerUri}';</script>
<script nonce="${nonce}" src="${monacoUri}"></script>
```

- [ ] **Step 3: Replace textarea CSS + HTML**

Replace the editor-container CSS (~line 360-362):

```css
    .editor-container { padding: 0; flex-shrink: 0; border-bottom: 1px solid var(--vscode-panel-border); }
    .editor-wrapper { height: 150px; min-height: 120px; max-height: 300px; overflow: hidden; resize: vertical; }
```

Remove the `#sql-editor` and `#sql-editor:focus` CSS rules.

Replace the editor HTML (~line 412-414):

```html
<div class="editor-container">
    <div class="editor-wrapper">
        <div id="sql-editor"></div>
    </div>
</div>
```

- [ ] **Step 4: Add language toggle button to toolbar**

Add after the TDS button (~line 404):

```html
    <vscode-button id="lang-btn" appearance="secondary" title="Toggle SQL / FetchXML language">SQL</vscode-button>
```

- [ ] **Step 5: Replace webview JS editor initialization**

Replace the `sqlEditor` variable declaration and all textarea-based references. After the `vscode` acquireVsCodeApi line (~line 439):

```javascript
    // ── Monaco Editor initialization ──
    const monacoTheme = document.body.classList.contains('vscode-light') || document.body.classList.contains('vscode-high-contrast-light') ? 'vs' : 'vs-dark';
    const editor = monaco.editor.create(document.getElementById('sql-editor'), {
        language: 'sql',
        theme: monacoTheme,
        value: '',
        minimap: { enabled: false },
        lineNumbers: 'on',
        scrollBeyondLastLine: false,
        wordWrap: 'on',
        fontSize: 13,
        automaticLayout: true,
        suggestOnTriggerCharacters: true,
        placeholder: 'SELECT TOP 10 * FROM account',
    });

    let currentLanguage = 'sql';
    let manualOverride = false;
    const langBtn = document.getElementById('lang-btn');
```

Remove the old `const sqlEditor = document.getElementById('sql-editor');` line.

- [ ] **Step 6: Add language auto-detection**

Add after the Monaco initialization:

```javascript
    // ── Language auto-detection ──
    function detectLang(content) {
        const trimmed = content.trimStart();
        return trimmed.startsWith('<') ? 'xml' : 'sql';
    }

    function updateLanguage(lang) {
        if (lang !== currentLanguage) {
            currentLanguage = lang;
            monaco.editor.setModelLanguage(editor.getModel(), lang);
            langBtn.textContent = lang === 'xml' ? 'FetchXML' : 'SQL';
        }
    }

    editor.onDidChangeModelContent(() => {
        if (!manualOverride) {
            const detected = detectLang(editor.getValue());
            updateLanguage(detected);
        }
    });

    langBtn.addEventListener('click', () => {
        manualOverride = true;
        updateLanguage(currentLanguage === 'sql' ? 'xml' : 'sql');
    });
```

- [ ] **Step 7: Add Ctrl+Enter action to Monaco**

Add after language detection:

```javascript
    // ── Monaco keybinding: Ctrl+Enter to execute ──
    editor.addAction({
        id: 'ppds.executeQuery',
        label: 'Execute Query',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
        run: () => {
            const sql = editor.getValue().trim();
            if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds, language: currentLanguage });
        },
    });
```

- [ ] **Step 8: Update all editor references in webview JS**

Search and replace throughout the webview JS IIFE:

| Old | New |
|-----|-----|
| `sqlEditor.value.trim()` | `editor.getValue().trim()` |
| `sqlEditor.value = msg.sql` | `editor.setValue(msg.sql); manualOverride = false;` |
| `document.activeElement !== sqlEditor` | `!editor.hasTextFocus()` |

For the `executeQuery` button handler:
```javascript
    executeBtn.addEventListener('click', () => {
        const sql = editor.getValue().trim();
        if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds, language: currentLanguage });
    });
```

Remove the old `sqlEditor.addEventListener('keydown', ...)` block for Ctrl+Enter (Monaco handles it now).

For the `loadQuery` message handler:
```javascript
            case 'loadQuery':
                editor.setValue(msg.sql);
                manualOverride = false;
                break;
```

- [ ] **Step 9: Update host-side executeQuery to handle language**

In the host-side message handler (~line 82-84), update the executeQuery case:

```typescript
case 'executeQuery':
    await this.executeQuery(
        message.sql as string,
        false,
        message.useTds as boolean | undefined,
        message.language as string | undefined,
    );
    break;
```

Update the `executeQuery` method signature and add FetchXML routing:

```typescript
private async executeQuery(sql: string, isRetry = false, useTds?: boolean, language?: string): Promise<void> {
    try {
        this.postMessage({ command: 'executionStarted' });
        const defaultTop = vscode.workspace.getConfiguration('ppds').get<number>('queryDefaultTop', 100);

        let result: QueryResultResponse;
        if (language === 'xml') {
            result = await this.daemon.queryFetch({ fetchXml: sql, top: defaultTop, environmentUrl: this.environmentUrl });
        } else {
            const tds = useTds ?? false;
            result = await this.daemon.querySql({ sql, top: defaultTop, useTds: tds, environmentUrl: this.environmentUrl });
        }
```

The rest of the method (lastSql, lastResult, error handling) stays the same. Store `language` alongside `lastSql` for loadMore support:

Add `private lastLanguage: string | undefined;` to the class fields.
Set `this.lastLanguage = language;` alongside `this.lastSql = sql;`.

- [ ] **Step 10: Verify compilation**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

Run: `cd extension && node esbuild.js`
Expected: All three builds succeed

- [ ] **Step 11: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): replace textarea with Monaco Editor + language auto-detection"
```

### Task 4: Completion Provider Bridge

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

- [ ] **Step 1: Add completion request handling in host-side message handler**

In the message handler switch statement, add after `copyToClipboard`:

```typescript
case 'requestCompletions': {
    const requestId = message.requestId as number;
    try {
        const result = await this.daemon.queryComplete({
            sql: message.sql as string,
            cursorOffset: message.cursorOffset as number,
            language: message.language as string,
        });
        this.postMessage({
            command: 'completionResult',
            requestId,
            items: result.items,
        });
    } catch {
        this.postMessage({
            command: 'completionResult',
            requestId,
            items: [],
        });
    }
    break;
}
```

- [ ] **Step 2: Add completion providers in webview JS**

Add after the language toggle handlers:

```javascript
    // ── Completion provider bridge ──
    let completionRequestId = 0;
    const pendingCompletions = new Map();

    function requestCompletions(model, position, language) {
        return new Promise((resolve) => {
            const id = ++completionRequestId;
            const cursorOffset = model.getOffsetAt(position);
            pendingCompletions.set(id, resolve);
            vscode.postMessage({ command: 'requestCompletions', requestId: id, sql: model.getValue(), cursorOffset, language });
            setTimeout(() => {
                if (pendingCompletions.has(id)) {
                    pendingCompletions.delete(id);
                    resolve({ suggestions: [] });
                }
            }, 3000);
        });
    }

    function mapKind(kind) {
        switch (kind) {
            case 'entity': return monaco.languages.CompletionItemKind.Class;
            case 'attribute': return monaco.languages.CompletionItemKind.Field;
            case 'keyword': return monaco.languages.CompletionItemKind.Keyword;
            default: return monaco.languages.CompletionItemKind.Text;
        }
    }

    monaco.languages.registerCompletionItemProvider('sql', {
        triggerCharacters: [' ', ',', '.'],
        provideCompletionItems: async (model, position) => {
            return await requestCompletions(model, position, 'sql');
        },
    });

    monaco.languages.registerCompletionItemProvider('xml', {
        triggerCharacters: [' ', '<', '"'],
        provideCompletionItems: async (model, position) => {
            return await requestCompletions(model, position, 'fetchxml');
        },
    });
```

- [ ] **Step 3: Add completionResult message handler**

In the webview's `window.addEventListener('message', ...)`, add a case:

```javascript
            case 'completionResult': {
                const resolver = pendingCompletions.get(msg.requestId);
                if (resolver) {
                    pendingCompletions.delete(msg.requestId);
                    const suggestions = (msg.items || []).map(item => ({
                        label: item.label,
                        insertText: item.insertText,
                        kind: mapKind(item.kind),
                        detail: item.detail || '',
                        sortText: String(item.sortOrder || 0).padStart(5, '0'),
                    }));
                    resolver({ suggestions });
                }
                break;
            }
```

- [ ] **Step 4: Verify compilation + build**

Run: `cd extension && npx tsc --noEmit && node esbuild.js`
Expected: No errors, three files built

- [ ] **Step 5: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): wire Monaco IntelliSense to daemon query/complete endpoint"
```

---

## Chunk 3: Final Verification

### Task 5: Full Build + Test Pass

- [ ] **Step 1: Run all extension tests**

Run: `cd extension && npx vitest run`
Expected: All new tests PASS, pre-existing failures unchanged

- [ ] **Step 2: Run esbuild**

Run: `cd extension && node esbuild.js`
Expected: Three output files in `dist/`

- [ ] **Step 3: Compile TypeScript**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 4: Verify dist files exist**

Run: `ls -la extension/dist/monaco-editor.js extension/dist/editor.worker.js extension/dist/extension.js`
Expected: All three files present

- [ ] **Step 5: Add dist/ patterns to .vscodeignore if needed**

Check `extension/.vscodeignore` — ensure `dist/` files are included in the packaged extension (they should NOT be ignored). Monaco files must ship.

- [ ] **Step 6: Final commit if any fixes needed**

```bash
git add -A
git commit -m "chore: final Monaco integration cleanup"
```
