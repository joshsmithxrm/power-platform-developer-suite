# Data Explorer: Monaco Editor with SQL/FetchXML IntelliSense

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-14
**Code:** [extension/src/panels/QueryPanel.ts](../extension/src/panels/QueryPanel.ts)

---

## Overview

The Data Explorer's SQL input is a raw `<textarea>` with no syntax highlighting or autocomplete. The daemon already has a fully functional `query/complete` RPC endpoint with `SqlCompletionEngine` and `FetchXmlCompletionEngine`, used by both the TUI and notebooks. This spec replaces the textarea with an embedded Monaco Editor instance, adds auto-detection between SQL and FetchXML, and wires up the existing completion infrastructure.

### Goals

- **Syntax highlighting**: SQL and FetchXML with proper tokenization and color themes
- **IntelliSense**: Entity/attribute/keyword completions via the existing daemon `query/complete` endpoint
- **Auto-detection**: Automatically switch between SQL and FetchXML mode based on content
- **Parity**: Match the editing experience of the TUI's `SyntaxHighlightedTextView` and notebooks

### Non-Goals

- Multi-cursor editing (Monaco supports it natively, but not worth customizing)
- Custom SQL grammar (Monaco's built-in SQL mode is sufficient)
- FetchXML schema validation (the daemon handles validation at execution time)
- Replacing the notebook editor (notebooks already have native VS Code IntelliSense)

---

## Architecture

```
QueryPanel.ts (webview)
├── Monaco Editor (replaces textarea)
│   ├── SQL language mode (built-in)
│   ├── XML language mode (built-in, for FetchXML)
│   └── CompletionItemProvider (registered for both)
│       └── postMessage → host → daemon.queryComplete() → postMessage back
├── Language auto-detect (content change listener)
└── Language toggle button (manual override)
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Monaco Editor instance | Code editing with syntax highlighting |
| Language auto-detector | Switches Monaco language mode based on content prefix |
| Language toggle button | Manual override for auto-detected language |
| Completion bridge | postMessage round-trip between webview and host for daemon completions |
| esbuild config | Bundle Monaco workers + core for webview consumption |

### Dependencies

- `monaco-editor` npm package (~2-3MB bundled)
- Existing: `daemon.queryComplete()` in `daemonClient.ts`
- Existing: `SqlCompletionEngine` and `FetchXmlCompletionEngine` in daemon

---

## Specification

### 1. Monaco Setup

#### npm + esbuild

Install `monaco-editor` as a dependency. The existing `esbuild.js` builds the extension host bundle (Node.js, CJS). Monaco requires a **separate browser-targeted build** for the webview:

- Add a second esbuild entry point that bundles Monaco's editor core + SQL + XML language support into a single IIFE/ESM file (e.g., `dist/monaco-editor.js`), targeting `platform: 'browser'`
- Bundle Monaco's editor worker (`editor.worker.js`) as a separate output file in `dist/`
- The main extension host bundle (`dist/extension.js`) is unchanged — Monaco is webview-only
- The webview loads the Monaco bundle via a `<script>` tag using `webview.asWebviewUri()`

#### localResourceRoots

Update `localResourceRoots` in the webview panel creation to include the `dist/` directory alongside the existing `node_modules`:
```typescript
localResourceRoots: [
    vscode.Uri.joinPath(extensionUri, 'node_modules'),
    vscode.Uri.joinPath(extensionUri, 'dist'),
]
```

#### Webview Integration

Replace the `<textarea>` with a `<div id="sql-editor">` container. Initialize Monaco:

```javascript
const editor = monaco.editor.create(document.getElementById('sql-editor'), {
    language: 'sql',
    theme: 'vs-dark',  // or detect from VS Code theme
    minimap: { enabled: false },
    lineNumbers: 'on',
    scrollBeyondLastLine: false,
    wordWrap: 'on',
    fontSize: 13,
    automaticLayout: true,  // auto-resize when container changes
    suggestOnTriggerCharacters: true,
});
```

#### Theme Sync

Monaco should match the VS Code theme. Detect the current theme from the webview's body class:
- `vscode-dark` / `vscode-high-contrast` → `vs-dark`
- `vscode-light` / `vscode-high-contrast-light` → `vs`

#### Editor Sizing

The editor container should match the current textarea sizing:
- Min height: 120px
- Max height: 300px
- Resizable vertically: wrap the Monaco container in a `<div>` with `overflow: hidden; resize: vertical;` — CSS `resize` requires `overflow` to not be `visible`. Monaco's `automaticLayout: true` will detect the container resize and adjust.

**Panel visibility:** The current panel uses `retainContextWhenHidden: true`. Monaco's `automaticLayout` handles re-layout when the panel becomes visible again — no explicit `editor.layout()` call needed.

### 2. Language Auto-Detection

#### Detection Logic

On every content change (`editor.onDidChangeModelContent`):

```javascript
function detectLanguage(content) {
    const trimmed = content.trimStart();
    return trimmed.startsWith('<') ? 'xml' : 'sql';
}
```

When detected language differs from current Monaco language mode, call:
```javascript
monaco.editor.setModelLanguage(editor.getModel(), detectedLanguage);
```

#### Manual Override

- A toggle button in the toolbar shows the current language: `SQL` or `FetchXML`
- Clicking it forces the opposite mode and sets a `manualOverride` flag
- While `manualOverride` is true, auto-detection is suppressed
- When the user clears the editor or the content changes significantly (e.g., full text replacement via History/loadQuery), reset `manualOverride` to false

#### Execution-Time Routing

At query execution, the webview includes the current Monaco language mode in the execute message:

```javascript
vscode.postMessage({ command: 'executeQuery', sql: editor.getValue(), useTds, language: currentLanguage });
```

The host-side `executeQuery` method routes based on `language`:
- `'sql'` → calls `daemon.querySql(...)` (existing path)
- `'xml'` → calls `daemon.queryFetch(...)` for direct FetchXML execution

The `executeQuery` message handler in the host must accept the new `language` field and branch accordingly. The webview's `currentLanguage` variable reflects either auto-detection or manual override.

### 3. Completion Provider

#### Registration

Register a `monaco.languages.registerCompletionItemProvider` for both `'sql'` and `'xml'`:

```javascript
monaco.languages.registerCompletionItemProvider('sql', {
    triggerCharacters: [' ', ',', '.'],
    provideCompletionItems: async (model, position) => {
        return await requestCompletions(model, position, 'sql');
    }
});

monaco.languages.registerCompletionItemProvider('xml', {
    triggerCharacters: [' ', '<', '"'],
    provideCompletionItems: async (model, position) => {
        return await requestCompletions(model, position, 'fetchxml');
    }
});
```

#### Completion Flow

1. Monaco triggers completion
2. Webview computes `cursorOffset` from Monaco's `position` (line/column → character offset)
3. Webview sends `postMessage({ command: 'requestCompletions', sql: model.getValue(), cursorOffset, language })`
4. Host receives message, calls `daemon.queryComplete({ sql, cursorOffset, language })`
5. Host sends response back via `postMessage({ command: 'completionResult', requestId, items })`
6. Webview resolves the pending Promise with mapped Monaco `CompletionItem[]`

#### Kind Mapping

| Daemon kind | Monaco CompletionItemKind |
|-------------|--------------------------|
| `entity` | `Class` |
| `attribute` | `Field` |
| `keyword` | `Keyword` |
| (default) | `Text` |

The daemon also returns `sortOrder` (number) and `detail` (string) on each `CompletionItemDto`. Map `sortOrder` to Monaco's `sortText` (zero-padded string) for consistent ordering, matching the existing notebook completion provider in `completionProvider.ts:44`.

#### Request ID

Since postMessage is asynchronous, use a request ID to correlate responses:
```javascript
let completionRequestId = 0;
const pendingCompletions = new Map();

function requestCompletions(model, position, language) {
    return new Promise((resolve) => {
        const id = ++completionRequestId;
        const cursorOffset = model.getOffsetAt(position);
        pendingCompletions.set(id, resolve);
        vscode.postMessage({ command: 'requestCompletions', requestId: id, sql: model.getValue(), cursorOffset, language });
        // Timeout after 3 seconds
        setTimeout(() => {
            if (pendingCompletions.has(id)) {
                pendingCompletions.delete(id);
                resolve({ suggestions: [] });
            }
        }, 3000);
    });
}
```

### 4. Integration with Existing Features

| Feature | Current (textarea) | New (Monaco) |
|---------|-------------------|--------------|
| Get query text | `sqlEditor.value` | `editor.getValue()` |
| Set query text | `sqlEditor.value = sql` | `editor.setValue(sql)` |
| Ctrl+Enter execute | textarea keydown listener | `editor.addAction({ keybindings: [monaco.KeyMod.CtrlCmd \| monaco.KeyCode.Enter], run: ... })` |
| Focus editor | `sqlEditor.focus()` | `editor.focus()` |
| Check if editor focused | `document.activeElement === sqlEditor` | `editor.hasTextFocus()` |

The results grid selection/copy system is unaffected — it operates on the results table, not the editor.

### 5. Content Security Policy

The existing CSP needs updating to allow Monaco's worker scripts:
```
script-src 'nonce-${nonce}' ${webview.cspSource};
worker-src blob:;
```

Monaco creates workers via blob URLs, so `worker-src blob:` is required.

---

## API Contract: Webview ↔ Host Messages

| Direction | Command | Payload | Purpose |
|-----------|---------|---------|---------|
| Webview → Host | `executeQuery` | `{ sql, useTds, language }` | Execute query (language: `'sql'` or `'xml'`) |
| Webview → Host | `requestCompletions` | `{ requestId, sql, cursorOffset, language }` | Request IntelliSense completions |
| Host → Webview | `completionResult` | `{ requestId, items }` | Return completion items |
| Host → Webview | `loadQuery` | `{ sql }` | Load query text (from history, notebook) |

All other existing messages (`queryResult`, `queryError`, `executionStarted`, etc.) are unchanged.

---

## Error Handling

| Error | Condition | Recovery |
|-------|-----------|----------|
| Monaco fails to load | Script load error, CSP block | Show fallback textarea with error message in status bar |
| Worker blob creation fails | CSP missing `worker-src blob:` | Monaco degrades gracefully (no worker = synchronous tokenization, slower but functional) |
| Completion timeout | Daemon slow or unresponsive | 3-second timeout returns empty suggestions, no user-visible error |
| Completion returns malformed response | Daemon bug | Catch in message handler, resolve with empty suggestions |
| `queryFetch` not available | FetchXML execution path missing | Falls back to `querySql` which auto-transpiles FetchXML |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Monaco editor replaces textarea with SQL syntax highlighting | Manual | 🔲 |
| AC-02 | FetchXML content auto-detected and highlighted as XML | Manual | 🔲 |
| AC-03 | Language toggle button switches between SQL and FetchXML | Manual | 🔲 |
| AC-04 | Ctrl+Space triggers IntelliSense completion popup | Manual | 🔲 |
| AC-05 | Entity names appear in completions after FROM/JOIN | Manual | 🔲 |
| AC-06 | Attribute names appear in completions after SELECT/WHERE | Manual | 🔲 |
| AC-07 | FetchXML element/attribute completions work in XML mode | Manual | 🔲 |
| AC-08 | Ctrl+Enter executes the query | Manual | 🔲 |
| AC-09 | History loads query into Monaco editor | Manual | 🔲 |
| AC-10 | Monaco theme matches VS Code theme (dark/light) | Manual | 🔲 |
| AC-11 | Editor resizes vertically (120px min, 300px max) | Manual | 🔲 |
| AC-12 | Pasting FetchXML auto-switches to XML language mode | Manual | 🔲 |
| AC-13 | Manual language toggle overrides auto-detection | Manual | 🔲 |
| AC-14 | Completion timeout (3s) prevents hanging on slow daemon | `detectLanguage` / `mapCompletionKind` unit tests | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Daemon not running | Completions return empty list, no error shown |
| Empty editor | SQL mode (default), no completions triggered |
| Paste FetchXML into SQL content | Auto-detects XML if content starts with `<` after paste |
| Toggle to FetchXML, type SQL | Manual override stays until content cleared or loadQuery |
| Very long query (5000+ chars) | Monaco handles efficiently (built-in virtualization) |
| Multiple Data Explorer panels | Each has independent Monaco instance + completion state |

---

## Design Decisions

### Why Monaco over CodeMirror or custom highlighting?

**Context:** Need syntax highlighting + completion in a webview textarea.

**Decision:** Monaco Editor (`monaco-editor` npm package).

**Alternatives considered:**
- **CodeMirror 6**: Lighter weight (~200KB vs ~2MB), but no built-in SQL completion infrastructure. Would need a custom completion source anyway, and the UI would look different from VS Code's native editors.
- **Custom textarea overlay**: Syntax highlighting via a transparent `<pre>` overlay on the textarea. Fragile, no completion support, breaks on scroll/resize. The old extension didn't attempt this.
- **Monaco via `@codingame/monaco-vscode-api`**: Deeper VS Code integration but unstable API, version coupling, and complex setup. Overkill.

**Consequences:**
- Positive: Familiar VS Code editing experience, built-in SQL/XML modes, completion API, theme support, accessibility
- Negative: ~2-3MB added to extension size (acceptable for a developer tool)

### Why auto-detect + manual toggle over explicit-only?

**Context:** Users paste both SQL and FetchXML into the Data Explorer.

**Decision:** Auto-detect based on `<` prefix with manual override toggle.

**Rationale:** The old extension used this approach successfully. SQL never starts with `<`, FetchXML always does. The detection is reliable for this domain. The toggle provides an escape hatch.

---

## Related Specs

- [vscode-data-explorer-selection-copy.md](./vscode-data-explorer-selection-copy.md) — Results grid selection (unaffected by this change)
- TUI completion: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`
- Daemon endpoint: `query/complete` in `RpcMethodHandler.cs`
- Extension client: `daemonClient.queryComplete()` in `daemonClient.ts`
