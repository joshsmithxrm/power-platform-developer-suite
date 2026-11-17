# Data Explorer

**Status:** Draft
**Last Updated:** 2026-11-17
**Code:** [src/PPDS.Extension/src/panels/QueryPanel.ts](../src/PPDS.Extension/src/panels/QueryPanel.ts), [src/PPDS.Extension/src/utils/dmlSafety.ts](../src/PPDS.Extension/src/utils/dmlSafety.ts), [src/PPDS.Extension/src/notebooks/](../src/PPDS.Extension/src/notebooks/), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/)
**Surfaces:** Extension, TUI, MCP

---

## Overview

The Data Explorer provides interactive query execution and result browsing across all PPDS surfaces (VS Code webview, TUI, MCP). This spec covers the VS Code panel enhancements including Monaco editor integration and rectangular selection/copy UX.

The VS Code panel (`QueryPanel.ts`) hosts a webview that currently uses a raw `<textarea>` for query input and a basic cell-toggle model for result selection. This spec replaces both with production-quality implementations: an embedded Monaco Editor with SQL/FetchXML IntelliSense, and an anchor+focus rectangular selection system with smart copy behavior matching the TUI.

### Goals

- **Monaco editor**: SQL and FetchXML syntax highlighting, IntelliSense via existing daemon `query/complete` endpoint, auto-detection between query languages
- **Live diagnostics**: Inline error squiggles via `setModelMarkers()` before query execution, powered by daemon `query/validate` endpoint
- **Familiar selection UX**: Click, drag, Shift+click rectangular selection matching SSMS/Excel behavior
- **Smart copy defaults**: Single cell = value only; multi-cell = with headers; Ctrl+Shift+C inverts
- **Discoverable shortcuts**: Context menu and status bar hints teach users the available actions
- **Parity**: Match the editing and copy experience of the TUI

### Non-Goals

- Multi-cursor editing (Monaco supports it natively, not worth customizing)
- Custom SQL grammar (Monaco's built-in SQL mode is sufficient)
- FetchXML schema validation (the daemon handles validation at execution time)
- Replacing the notebook editor (notebooks already have native VS Code IntelliSense)
- Virtual scrolling (current record limits make this unnecessary)
- Column resize or reorder
- Cell editing
- Multi-region selection (Ctrl+click to add disjoint ranges)
- Keyboard arrow navigation for selection (Shift+Arrow to extend by one cell) — potential future enhancement

---

## Architecture

```
QueryPanel.ts (webview)
├── Monaco Editor (replaces textarea)
│   ├── SQL language mode (built-in)
│   ├── XML language mode (built-in, for FetchXML)
│   ├── CompletionItemProvider (registered for both)
│   │   └── postMessage → host → daemon.queryComplete() → postMessage back
│   └── Diagnostic Markers (setModelMarkers)
│       └── postMessage → host → daemon.queryValidate() → postMessage back
├── Validation debounce (300ms, request ID cancellation)
├── Language auto-detect (content change listener)
├── Language toggle button (manual override)
├── Selection State: anchor {row, col}, focus {row, col}
├── Mouse Handlers: mousedown → set anchor, mousemove → update focus, mouseup → finalize
├── Keyboard Handlers: Ctrl+C, Ctrl+Shift+C, Ctrl+A, Escape
├── Context Menu: smart copy options based on selection state
├── Visual Rendering: CSS classes for selection background + edge borders
└── Status Bar Hints: dynamic text based on current selection
```

### Monaco Components

| Component | Responsibility |
|-----------|----------------|
| Monaco Editor instance | Code editing with syntax highlighting |
| Language auto-detector | Switches Monaco language mode based on content prefix |
| Language toggle button | Manual override for auto-detected language |
| Completion bridge | postMessage round-trip between webview and host for daemon completions |
| Validation bridge | Debounced validation round-trip between webview and host for live diagnostics |
| Marker renderer | Maps diagnostic DTOs to Monaco `IMarkerData` and calls `setModelMarkers()` |
| esbuild config | Bundle Monaco workers + core for webview consumption |

### Selection Components

| Component | Responsibility |
|-----------|----------------|
| Selection state (`anchor`, `focus`) | Track rectangular selection as two corner coordinates |
| Mouse handlers | Click sets anchor, drag updates focus, Shift+click extends |
| `getSelectionRect()` | Normalize anchor/focus into {minRow, maxRow, minCol, maxCol} |
| `copySelection(withHeaders)` | Extract selected data as TSV, send to clipboard |
| `updateSelectionVisuals()` | Apply CSS classes for background + edge borders |
| Context menu | Right-click options with smart labeling |
| Status bar hint | Show copy shortcut hint based on selection size |

### Monaco Dependencies

- `monaco-editor` npm package (~2-3MB bundled)
- Existing: `daemon.queryComplete()` in `daemonClient.ts`
- Existing: `SqlCompletionEngine` and `FetchXmlCompletionEngine` in daemon
- Existing: `SqlValidator` in `PPDS.Query.Intellisense` (reused for `query/validate`)

---

## Specification

### Monaco Editor Integration

#### Monaco Setup

Install `monaco-editor` as a dependency. The existing `esbuild.js` builds the extension host bundle (Node.js, CJS). Monaco requires a **separate browser-targeted build** for the webview:

- Add a second esbuild entry point that bundles Monaco's editor core + SQL + XML language support into a single IIFE/ESM file (e.g., `dist/monaco-editor.js`), targeting `platform: 'browser'`
- Bundle Monaco's editor worker (`editor.worker.js`) as a separate output file in `dist/`
- The main extension host bundle (`dist/extension.js`) is unchanged — Monaco is webview-only
- The webview loads the Monaco bundle via a `<script>` tag using `webview.asWebviewUri()`
- Monaco's vendored DOMPurify import is redirected at build time to the explicit, patched `dompurify` npm dependency. The production bundle must include that package and exclude Monaco's vendored sanitizer; dependency overrides alone do not replace vendored source. The redirect applies only to Monaco's sanitizer import.

Update `localResourceRoots` in the webview panel creation to include the `dist/` directory alongside the existing `node_modules`:

```typescript
localResourceRoots: [
    vscode.Uri.joinPath(extensionUri, 'node_modules'),
    vscode.Uri.joinPath(extensionUri, 'dist'),
]
```

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

#### Language Auto-Detection

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

#### Completion Provider

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

Completion flow:
1. Monaco triggers completion
2. Webview computes `cursorOffset` from Monaco's `position` (line/column → character offset)
3. Webview sends `postMessage({ command: 'requestCompletions', sql: model.getValue(), cursorOffset, language })`
4. Host receives message, calls `daemon.queryComplete({ sql, cursorOffset, language })`
5. Host sends response back via `postMessage({ command: 'completionResult', requestId, items })`
6. Webview resolves the pending Promise with mapped Monaco `CompletionItem[]`

Kind mapping:

| Daemon kind | Monaco CompletionItemKind |
|-------------|--------------------------|
| `entity` | `Class` |
| `attribute` | `Field` |
| `keyword` | `Keyword` |
| (default) | `Text` |

The daemon also returns `sortOrder` (number) and `detail` (string) on each `CompletionItemDto`. Map `sortOrder` to Monaco's `sortText` (zero-padded string) for consistent ordering, matching the existing notebook completion provider in `completionProvider.ts:44`.

Since postMessage is asynchronous, use a request ID to correlate responses. Completions time out after 3 seconds returning empty suggestions with no user-visible error.

#### Integration with Existing Features

| Feature | Current (textarea) | New (Monaco) |
|---------|-------------------|--------------|
| Get query text | `sqlEditor.value` | `editor.getValue()` |
| Set query text | `sqlEditor.value = sql` | `editor.setValue(sql)` |
| Ctrl+Enter execute | textarea keydown listener | `editor.addAction({ keybindings: [monaco.KeyMod.CtrlCmd \| monaco.KeyCode.Enter], run: ... })` |
| Focus editor | `sqlEditor.focus()` | `editor.focus()` |
| Check if editor focused | `document.activeElement === sqlEditor` | `editor.hasTextFocus()` |

#### Content Security Policy

The existing CSP needs updating to allow Monaco's worker scripts:
```
script-src 'nonce-${nonce}' ${webview.cspSource};
worker-src blob:;
```

Monaco creates workers via blob URLs, so `worker-src blob:` is required.

#### API Contract: Webview ↔ Host Messages (Monaco)

| Direction | Command | Payload | Purpose |
|-----------|---------|---------|---------|
| Webview → Host | `executeQuery` | `{ sql, useTds, language }` | Execute query (language: `'sql'` or `'xml'`) |
| Webview → Host | `requestCompletions` | `{ requestId, sql, cursorOffset, language }` | Request IntelliSense completions |
| Host → Webview | `completionResult` | `{ requestId, items }` | Return completion items |
| Host → Webview | `loadQuery` | `{ sql }` | Load query text (from history, notebook) |
| Webview → Host | `requestValidation` | `{ requestId, sql, language }` | Request syntax validation (debounced, 300ms) |
| Host → Webview | `validationResult` | `{ requestId, diagnostics }` | Return diagnostic markers |

`queryError` is enriched with optional diagnostics: `{ error, diagnostics?: DiagnosticItem[] }`. All other existing messages (`queryResult`, `executionStarted`, etc.) are unchanged.

#### Error Handling

| Error | Condition | Recovery |
|-------|-----------|----------|
| Monaco fails to load | Script load error, CSP block | Show fallback textarea with error message in status bar |
| Worker blob creation fails | CSP missing `worker-src blob:` | Monaco degrades gracefully (no worker = synchronous tokenization, slower but functional) |
| Completion timeout | Daemon slow or unresponsive | 3-second timeout returns empty suggestions, no user-visible error |
| Completion returns malformed response | Daemon bug | Catch in message handler, resolve with empty suggestions |
| `queryFetch` not available | FetchXML execution path missing | Falls back to `querySql` which auto-transpiles FetchXML |
| Validation timeout | Daemon slow or unresponsive | 3-second timeout clears markers, no user-visible error |

#### Diagnostic Markers

Live syntax validation powered by a new `query/validate` RPC endpoint. The webview calls validation on a debounced timer as the user types; the daemon runs `SqlValidator` (parse-only, no Dataverse connection) and returns structured diagnostics. The webview maps these to Monaco markers via `setModelMarkers()`, producing inline error squiggles with hover messages.

##### Validation Endpoint

**RPC Method:** `query/validate`

**Request:**
```json
{ "sql": "SELECT name FORM account", "language": "sql" }
```

**Response:**
```json
{
  "diagnostics": [
    { "start": 12, "length": 4, "severity": "error", "message": "Incorrect syntax near 'FORM'. Did you mean 'FROM'?" }
  ]
}
```

The endpoint reuses the existing `SqlValidator` which wraps `TSql170Parser`. It returns `SqlDiagnostic[]` mapped to a JSON DTO:

| Field | Type | Description |
|-------|------|-------------|
| `start` | `number` | 0-based character offset in the query text |
| `length` | `number` | Number of characters the diagnostic spans |
| `severity` | `"error" \| "warning" \| "info"` | Maps to `monaco.MarkerSeverity` |
| `message` | `string` | Human-readable description shown on hover |

FetchXML validation is a non-goal for this iteration — the endpoint returns an empty diagnostics array for `language: "xml"`.

##### Live Validation Flow

1. User types in Monaco editor
2. `onDidChangeModelContent` clears existing markers immediately (stale)
3. Content change resets 300ms debounce timer
4. Debounce fires → webview sends `requestValidation` with incrementing `requestId`
5. Host calls `daemon.queryValidate({ sql, language })`
6. Host sends `validationResult` back with `{ requestId, diagnostics }` (host re-attaches the `requestId` from the original webview request)
7. Webview checks `requestId` matches latest request — stale results are discarded
8. Webview converts `start`/`length` to Monaco line/column positions via `model.getPositionAt(start)` and `model.getPositionAt(start + length)`
9. Webview calls `monaco.editor.setModelMarkers(model, 'ppds', markers)`

Skip validation when content is fewer than 3 characters (avoids noise on empty/partial queries).

##### Execution-Time Markers

When query execution fails with a parse error (`ErrorCode: Query.ParseError`), the daemon includes structured diagnostics in the RPC error data. The host enriches the `queryError` webview message:

```typescript
// Current shape
{ command: 'queryError', error: string }

// Enriched shape
{ command: 'queryError', error: string, diagnostics?: DiagnosticItem[] }
```

The daemon normalizes `QueryParseException` line/column coordinates to 0-based `start`/`length` offsets matching the `DiagnosticDto` shape, so the webview uses a single code path for both live and execution-time markers. FetchXML execution errors do not produce markers (FetchXML validation is a non-goal for this iteration).

The C# RPC response uses `DiagnosticDto` (with `start`, `length`, `severity`, `message`). The TypeScript webview maps these to `DiagnosticItem` — same shape, language-specific type.

The error display div still shows the full error text (including line/column) for accessibility and visibility.

##### Marker Clearing

| Event | Action |
|-------|--------|
| Content changes | Clear markers immediately, debounce triggers re-validation |
| Successful query execution | Clear markers |
| Language switch (SQL ↔ FetchXML) | Clear markers |
| Empty/trivial content (< 3 chars) | Skip validation, clear markers |
| Editor disposed | Markers cleared automatically by Monaco |

---

### Selection & Copy

All changes are within the webview JS/CSS in `QueryPanel.ts`. No daemon or host-side changes needed.

#### Selection Model

Selection is always a rectangle defined by `anchor` and `focus` coordinates (`{row, col}`). Replace the current `selectedCells` Set with `anchor` and `focus` objects (or null when nothing selected). `getSelectionRect()` normalizes anchor/focus into `{minRow, maxRow, minCol, maxCol}` regardless of drag direction.

Mouse interactions:

| Action | Behavior |
|--------|----------|
| Click cell | Set anchor = focus = clicked cell. Clear previous selection. |
| Click + drag | mousedown sets anchor. mousemove updates focus (rectangular highlight follows). mouseup finalizes. |
| Shift+click | Keep existing anchor, set focus = clicked cell (extends/shrinks rectangle). |
| Click header (th) | Existing sort behavior (unchanged). |

Keyboard interactions:

| Shortcut | Behavior |
|----------|----------|
| Ctrl+A | Set anchor = {0, 0}, focus = {lastRow, lastCol}. Full table selected. For performance on large tables (5,000 rows), use a CSS class on tbody (`tbody.all-selected td`) instead of per-cell class application. |
| Escape | If filter bar is open: close filter bar only. If filter bar is closed and selection exists: clear selection. (Filter bar takes precedence — matches existing behavior.) |

Drag details:
- On `mousedown` on a `td[data-row]`: set anchor, begin tracking. Add `selecting` class to tbody (changes cursor to `cell`, disables text selection).
- On `mousemove` (while tracking): update focus to the cell under the mouse. Call `updateSelectionVisuals()`.
- On `mouseup`: stop tracking. Remove `selecting` class from tbody. **Remove the mousemove listener** (attach it on mousedown, remove on mouseup — do not leave a permanent global mousemove handler).
- If mouse leaves the results wrapper during drag, clamp focus to nearest edge cell.

#### Copy Behavior

Smart defaults matching the TUI:

| Selection | Ctrl+C (default) | Ctrl+Shift+C (inverted) |
|-----------|-------------------|--------------------------|
| Single cell | Copy displayed (formatted) value only | Copy value with column header above |
| Multi-cell | Copy selected rectangle WITH column headers | Copy selected rectangle WITHOUT headers |
| Full table (Ctrl+A) | Copy all data WITH headers | Copy all data WITHOUT headers |

Format:
- Tab-separated values (TSV) with `\n` row separators
- Values sanitized: tabs replaced with spaces, newlines replaced with spaces
- All copy uses the **displayed (formatted) value** — same as what `renderTable` shows in the grid. For cells with `{value, formatted}` objects, the formatted string is used.
- Only columns within the selection rectangle are included (not all columns)
- **Selection coordinates are relative to the currently rendered view** (which may be filtered or sorted), not the raw `allRows` array

Post-copy feedback:
- Single cell: `"Copied: {truncated_value}"`
- Multi-cell: `"Copied {rowCount} rows x {colCount} cols with headers"` (or "without headers")

The confirmation replaces the copy hint for 2 seconds, then the hint returns.

Clipboard: Use `vscode.postMessage({ command: 'copyToClipboard', text })` (existing pattern). Host side calls `vscode.env.clipboard.writeText(text)` (existing).

#### Context Menu

Right-click on a cell shows:

```
Copy                    Ctrl+C
Copy (no headers)       Ctrl+Shift+C     ← when multi-cell selected
  OR
Copy (with header)      Ctrl+Shift+C     ← when single cell selected
─────────────────────────────────────
Copy Cell Value
Copy Row
Copy All Results
```

- **Copy**: Smart default (same as Ctrl+C)
- **Copy (no headers)** / **Copy (with header)**: The inverse of the default, labeled dynamically
- **Copy Cell Value**: Always the raw value of the right-clicked cell (ignores selection)
- **Copy Row**: Full row of the right-clicked cell as TSV (no headers)
- **Copy All Results**: Entire table with headers

If right-clicking a cell outside the current selection, the selection moves to that cell first (single-cell select), then the menu opens.

#### Visual Styling

Selection background:
```css
td.cell-selected {
    background: var(--vscode-editor-selectionBackground) !important;
}
```

Selection edge borders (applied only to cells on the perimeter; interior cells get `cell-selected` background only):
```css
td.cell-selected-top {
    border-top: 2px solid var(--vscode-focusBorder) !important;
}
td.cell-selected-bottom {
    border-bottom: 2px solid var(--vscode-focusBorder) !important;
}
td.cell-selected-left {
    border-left: 2px solid var(--vscode-focusBorder) !important;
}
td.cell-selected-right {
    border-right: 2px solid var(--vscode-focusBorder) !important;
}
```

Drag cursor:
```css
tbody.selecting {
    cursor: cell !important;
    user-select: none !important;
}
```

#### Status Bar Hints

The status bar already has `statusText`, `rowCountEl`, and `executionTimeEl` spans. Add a `copyHintEl` span that shows context-sensitive hints:

| Selection State | Hint Text |
|-----------------|-----------|
| No selection | (empty) |
| Single cell selected | `Ctrl+C: copy value | Ctrl+Shift+C: with header` |
| Multi-cell selected | `Ctrl+C: copy with headers | Ctrl+Shift+C: values only` |

---

## Acceptance Criteria

### Notebook environment isolation

Notebook bindings persist only environment ID, name, and URL. Missing or partial
metadata remains compatible with existing files. The picker binds the originating
notebook, even if focus changes while it is open, and offers “Use session default.”
The session default is resolved when an unbound notebook begins a run; another
notebook's binding is never used as a fallback.

Each run captures profile identity and environment name, URL, and type. Queries,
result links, completion, and SQL-to-FetchXML conversion use the relevant captured
context. Output headers include the executed target and timestamp, including
errors and cancellation after resolution. Cached results retain that context.
Binding changes during a run apply to the next run and are visible in status.

Runs in one notebook queue; distinct notebooks run concurrently. Interrupting or
closing a notebook cancels its active run and pending queue only. A cancelled old
request cannot overwrite a newer result or remove its execution state. All timers,
token subscriptions, and notebook state are disposed with their owners.

Production/Unknown execution requires explicit in-memory acknowledgement scoped
to notebook, target, authenticated identity, and classification. Target/profile
changes, close/reopen, and classification changes require reevaluation. This
acknowledgement never sets DML confirmation and is never serialized. Per-cell
environment overrides are out of scope. DML confirmation is a separate, per-execution
step described in [DML confirmation and failure display](#dml-confirmation-and-failure-display).

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-NB-01 | Concurrent bound notebooks issue SQL/FetchXML against their respective targets. | `DataverseNotebookController.test.ts`: `executes SQL and FetchXML concurrently against their notebook targets` | ✅ |
| AC-NB-02 | Focus, binding, or default changes do not redirect an active run or its output/links. | `DataverseNotebookController.test.ts`: `captures one target and profile for the entire run despite focus binding and default changes` | ✅ |
| AC-NB-03 | Unbound notebooks capture the session default; missing defaults prompt before execution. | `DataverseNotebookController.test.ts`: `uses the session default for an unbound notebook without inheriting another binding`, `prompts once for a missing default and runs every cell on the selected binding`, `does not inherit another run default while its missing-default picker is open` | ✅ |
| AC-NB-04 | Cancellation, close, queues, and late completions are notebook/run scoped. | `DataverseNotebookController.test.ts`: `queues runs within a notebook and clears queued work on interrupt`, `does not let a closed notebook late response overwrite a reopened notebook result`, `individual-cell cancellation allows later cells without cancelling another notebook`, `disposal cancels active requests and clears queued runs` | ✅ |
| AC-NB-05 | Production/Unknown cancellation sends no query; acknowledgements cannot enable DML. | `DataverseNotebookController.test.ts`: `requires acknowledgement for %s and cancellation sends no query`, `scopes acknowledgement to notebook target profile and type without approving DML` | ✅ |
| AC-NB-06 | Legacy/partial metadata loads, binding roundtrips, and no credentials/acknowledgements persist. | `DataverseNotebookSerializer.test.ts`: `preserves cells with missing or partial metadata %j`, `preserves environment metadata through round-trip`, `never serializes credentials acknowledgement or a claimed environment type` | ✅ |
| AC-NB-07 | Picker only changes notebook metadata; safely rendered output identifies its actual target. | `DataverseNotebookController.test.ts`: `binds the originating notebook when focus changes while its picker is open`, `renders target labels as plain text and does not interpolate them into HTML`, `labels error and empty outputs with the resolved target and execution time` | ✅ |
| AC-NB-08 | Minimum and stable VS Code hosts execute the isolated two-notebook journey. | `e2e/notebook-host.ts`: `runScenarios`; `e2e/notebook-live.ts`: `run` through `PPDS.NotebookLiveHost`, with two authorized Dataverse organizations on VS Code 1.116.0 and 1.144.0 (2026-11-01) | ✅ |
| AC-NB-09 | Query RPC binds the captured opaque profile identity, survives rename/reorder, rejects removed/changed/ambiguous identities, and creates pools from a cloned profile snapshot. Callers omitting identity retain existing name/index behavior. | `RpcProfileIdentityTests`: `ResolveSnapshot_UsesIdentityAfterRenameAndIndexReuse`, `ResolveSnapshot_RejectsRemovedOrChangedIdentity`, `ResolveSnapshot_RejectsAmbiguousIdentity`, `CapturedProvider_DoesNotReloadMutableProfileName_AndSeparatesIdentities`, `QueryFetch_OmittedIdentityRetainsNameResolution`, `QueryMethods_RejectMissingCapturedIdentityBeforePoolAccess` | ✅ |
| AC-NB-10 | Opening a notebook cell in Data Explorer transfers its executed target/profile (or current captured context before execution), including completion, conversion, and subsequent query pages. A full panel limit never loads the query into a different target/profile. | `notebookQueryTransfer.test.ts`: `preserves executed context when the notebook has since changed binding`, `retains the transferred profile across initialization, completion, conversion, SQL/FetchXML and paging`, `refuses mismatched %s reuse at the panel cap` | ✅ |
| AC-NB-11 | The blocking minimum/stable VS Code CI matrix runs the native notebook suite under xvfb with bounded execution and failure diagnostics; tests use isolated application directories without changing the process HOME. | `test_extension_e2e_workflow.py`: `test_native_notebook_suite_is_a_bounded_blocking_matrix_step`, `test_native_notebook_runner_isolates_application_state_without_overriding_home`, `test_failed_e2e_run_always_uploads_diagnostics` | ✅ |

The daemon returns an opaque `profileIdentity` in `auth/who`, derived from
non-secret authentication identity and profile creation fields, excluding mutable
name/index. Notebook SQL, FetchXML, completion, and explain requests send it.
Identity lookup must match exactly one profile; failure never falls back to a
name/index. Pool creation uses the captured profile snapshot directly. Older
clients can omit the field; notebooks require a daemon that supplies it.

Native host coverage uses the production controller and serializer with deterministic
in-memory RPC responses and isolated home, application-data, VS Code, and PPDS
directories. It verifies execution, cancellation, output/link attribution, and
saved-binding reload. Unit tests cover dialog choices and close/late-result races.
The authorized two-organization live demonstration passed on 2026-11-01 using
product commit `1cba826c6ad6233e41b0d4d5197d0c7c618cd4c7`, on both VS Code
1.116.0 and 1.144.0. Each host made six fixed one-row organization reads through
the production RPC/query pipeline and verified distinct organization IDs,
captured-target output links, notebook-scoped cancellation and queue clearing,
the unbound session default, and persisted binding isolation. Original profile
and environment metadata hashes were unchanged, and temporary local resources
were removed. Both targets used the same existing profile; credential-identity
switching remains covered by deterministic tests. The live helper uses an
isolated metadata/DI bootstrap, not the shipped `serve` entrypoint. Cancellation
delays real response delivery at the client and does not measure server-side
cancellation timing. See `tests/PPDS.NotebookLiveHost/README.md` for the harness
contract.

### DML confirmation and failure display

Notebook SQL cells and the Data Explorer share one flow (`src/PPDS.Extension/src/utils/dmlSafety.ts`).
The extension never decides whether SQL is DML: the daemon's `DmlSafetyGuard` does.

**Request rule.** Every `query/sql` request carrying user SQL sends `dmlSafety`. The first
attempt, every refresh or re-run, every re-authentication retry and every load-more page send
`{ isConfirmed: false }`, so the guard's no-WHERE block, row cap, environment protection and
cross-environment policy always apply, against both a daemon that skips the guard when
`dmlSafety` is missing and one that refuses such DML. Each execution captures its statement
text and target once; the modal and the one confirmed request use exactly that pair.
`{ isConfirmed: true }` is sent at most once per confirmation and never by any retry: a
confirmation never outlives the request it was given for.

**Target pinning.** The confirmed request names its profile by the daemon's opaque profile
identity, never by a mutable name. Notebooks capture the identity when the run starts. The
Data Explorer uses its notebook-captured identity when it has one; otherwise, before showing the
modal, it resolves the identity of the active profile with `auth/who` and pins it only when that
profile is the one the panel shows. When the environment URL is not known, the profile is not
known, is not the active profile, or the daemon reports no identity, the panel does not offer
"Execute Anyway": it shows why, and nothing is sent. If the Data Explorer's environment,
profile name or profile identity changes while the confirmation (modal or review step) is open,
nothing is sent and the panel says the statement must be run again. If a notebook cell, its
binding or the session default changes while the modal is open, the confirmed request still
uses the captured text and target.

**Confirmation rule.** Only a `Query.DmlConfirmationRequired` answer (`data.dmlConfirmationRequired`
or `data.code`) opens the modal. The modal names the target environment and type ("type unknown"
when not resolved), its URL, the profile, and the statement's size, and says that one
confirmation covers the whole text, every statement in the script. When the daemon sends
`data.dmlTargets` (`[{ label, url, type, protection }]`, additive), the modal lists every
environment the request writes to, marks each one that is not the bound environment
("OTHER ENVIRONMENT") and each production or protected one ("PRODUCTION / PROTECTED"), and its
title counts the other environments. Without that field (today's daemon) the modal states that
SQL naming another environment (`[LABEL].entity`) also writes there and that other named
environments are not listed, because the daemon does not report them before execution. The
daemon's confirmation text is shown only when it is not the CLI's `--confirm` wording.

**Buttons.** The VS Code API documents that a modal message is modal and resolves to the chosen
item, or `undefined` when it is dismissed; it does not document which button is the default.
"Show Full Statement" is listed first because current VS Code makes the first action the
primary one, and "Execute Anyway" is never listed first. The rules that do not depend on that:
a statement longer than the modal shows cannot be executed before it has been opened, and
Escape or closing the dialog never executes. "Show Full Statement" opens the exact text that
will run in a read-only virtual document (`ExplainDocumentProvider`); if that provider is not
available, an untitled copy, and the review step then says it is a copy for reading only whose
edits are not executed. The review step is a quick pick (`ignoreFocusOut: true`) that stays open
until answered while the document can be read, and is closed when the execution is cancelled.
"Continue to Confirmation" shows the modal again; "Cancel" or dismissing the quick pick counts
as declining and says so.

**Statement display.** A statement of up to 24 lines and 1500 characters is shown whole. A
longer one shows its first 14 lines (at most 900 characters) and last 8 lines (at most 500
characters). Counts treat CRLF, LF and CR each as one line break. The modal states the total
lines and characters and exactly how many characters on how many lines are hidden; the shown
start, the hidden part, the shown end and the line breaks between them add up to the total.
Control, zero-width and bidirectional formatting characters in the statement and in
environment, profile and target names are shown as `<U+XXXX>`, and the modal says when that
happened; the text sent is still the captured text. Names are never placed in a message that
renders Markdown links.

**Outcomes.** Escape, close, Cancel, a cancelled or dismissed review step, or an abort run
nothing. A dialog that cannot be shown or a full statement that cannot be opened is reported as
that failure ("Not executed: … could not be …"), not as a decline, and nothing runs. A confirmed
request that is still answered confirmation-required is shown as an error, never re-prompted.
Confirmation is never remembered across cells, runs or panel executions, and the
Production/Unknown notebook acknowledgement never grants it. A confirmation that a newer Data
Explorer execution replaced sends nothing and posts nothing over the newer execution.

**After the confirmed request was sent.** A cancel then says "Cancelled after the confirmed
statement was submitted: records may already have been changed" and keeps any result or
failed-row information that came back. A confirmed request that fails says it may have partly
applied. When the Data Explorer retries after re-authentication, the new attempt starts
unconfirmed and its modal says the earlier confirmed attempt may have partly applied.

**Refusals.** `Query.DmlBlocked`, `Query.DmlRowCapExceeded`, `Safety.ShakedownActive` and
`data.dmlBlocked` render as errors with the daemon's reason. With today's daemon only the
no-WHERE block arrives before the prompt. Shakedown and the cross-environment policy are
checked after planning, so they refuse the confirmed request, after "Execute Anyway". Today's
daemon stops DML at the row cap without an error (`affected_rows` is the cap), so no row-cap
refusal is shown; `Query.DmlRowCapExceeded` is handled for a daemon that raises it.

**Failed rows.** A daemon that reports DML outcomes sends `success: false` with `affectedRows`,
`failedRows` and `dmlError`; these are read whenever present, and only they carry a reason.
For the result of a confirmed request only, the one-row summary (`affected_rows`,
`failed_rows`, exactly these two columns) with `failed_rows > 0` is also read as a failure,
shown with the counts and "The daemon did not report a failure reason." Results of
unconfirmed requests are never read as a DML summary, so a SELECT cannot be flagged. The
notebook shows the failure as an error output above the table and the cell ends failed; the
Data Explorer shows an error banner above the summary row.

**Notebook runs.** A cell counts as DML once the daemon has answered it with
`Query.DmlConfirmationRequired`. When such a cell is declined, refused, cannot be confirmed,
fails, does not succeed, has failed rows, or is cancelled through its own cell execution, the
cell ends failed with a message naming the cause and the run stops: every remaining cell of
that run (Run All included) is marked "Not run: this run stopped because …" with the cause and
is not sent. Each skipped cell is checked for ownership, always ends its execution, and a
failure marking one cell never touches another cell's output. Interrupting or closing the
notebook ends the whole run instead: nothing further is sent, and the remaining cells are left
as they were, not marked. VS Code does not fire a cell's own cancellation token when the
controller has an interrupt handler, so in practice a notebook-level interrupt is the cancel
path. A cell that fails before the daemon classifies it (parse, connection or authentication
error on the first, unconfirmed request) is treated like any other failing cell: the run
continues, and later DML cells still ask for their own confirmation. Queued separate runs are
unaffected and prompt for their own DML.

**Load More** never runs DML: page requests send `{ isConfirmed: false }`, and the result of a
confirmed execution is never paged; the panel says so in its warning banner instead of offering
Load More.

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-DML-01 | A notebook DML cell runs only after a modal naming target, profile and statement; the confirmed request carries the same text and target as the first, unconfirmed one, even if the cell or binding changed while the modal was open. | `DataverseNotebookController.test.ts`: `asks once in a modal naming target, profile and statement, then sends the same text and target confirmed`, `sends the confirmed request for the shown text and target even if the cell and binding change while the modal is open` | ✅ |
| AC-DML-02 | Confirmation is asked on every execution and never remembered; refresh and re-authentication in the Data Explorer start unconfirmed and ask again for the target of that attempt, saying the earlier attempt may have partly applied. | `DataverseNotebookController.test.ts`: `asks again on every DML execution and never remembers a confirmation`; `queryPanelDmlSafety.test.ts`: `refresh of a confirmed DML asks again`, `after re-authentication sends the statement unconfirmed again and asks again for the target of that attempt` | ✅ |
| AC-DML-03 | Declining, cancelling or dismissing the review, interrupting, closing or disposing runs nothing confirmed; declining marks the cell not executed and stops Run All; an interrupt closes the review prompt and leaves later cells unmarked. | `DataverseNotebookController.test.ts`: `declining runs nothing, marks the cell not executed and stops Run All`, `treats cancelling the review step as declining`, `keeps the review prompt open until answered and treats dismissing it as declining`, `closes the review prompt when the run is interrupted`, `does not execute when the run is interrupted while the confirmation is open`, `sends nothing when the notebook is %s while the confirmation is open`, `leaves later cells untouched when the notebook is interrupted during a DML confirmation`; `queryPanelDmlSafety.test.ts`: `a dismissed review step says so and runs nothing` | ✅ |
| AC-DML-04 | SELECT cells run without a DML prompt and send unconfirmed safety options; the environment acknowledgement never confirms DML. | `DataverseNotebookController.test.ts`: `runs SELECT cells without a DML prompt and still sends unconfirmed safety options`, `scopes acknowledgement to notebook target profile and type without approving DML` | ✅ |
| AC-DML-05 | A missing-WHERE refusal is shown before any prompt; shakedown, cross-environment and row-cap refusals of the confirmed request are shown after it; a still-unconfirmed retry is an error. Each refusal shows the reason and stops the notebook run. | `DataverseNotebookController.test.ts`: `refuses a missing WHERE before any prompt, shows the reason and stops the run`, `shows a %s refusal of the confirmed request as an error and stops the run`, `shows a confirmed request that is still confirmation-required as an error instead of prompting again`; `queryPanelDmlSafety.test.ts`: `shows a refusal as an error without prompting`, `shows a refusal of the confirmed request as an error` | ✅ |
| AC-DML-06 | Failed DML rows show counts (and a reason when the daemon sends one) and stop the notebook run; only a confirmed request's result is read as a DML summary. | `DataverseNotebookController.test.ts`: `renders failed DML rows with counts and the reason a daemon reports, and stops the run`, `reads the one-row DML summary of a confirmed request as failed rows even when success is true`, `never flags a SELECT whose columns look like the DML summary`; `queryPanelDmlSafety.test.ts`: `posts the failure reason and counts for a DML result with failed rows`, `never flags a SELECT, even one whose columns look like the DML summary`; `dmlSafety.test.ts`: `ignores successful DML and rows that merely resemble the summary`, `reads formatted summary values and additive fields` | ✅ |
| AC-DML-07 | "Show Full Statement" is listed first and never executes: it opens the exact text (read-only through the provider; otherwise a copy described as such) and asks again. A long statement shows its start and end with exact hidden counts, and Execute is offered only after the full text was opened. A failed open or dialog is reported as a failure. | `DataverseNotebookController.test.ts`: `choosing Show Full Statement opens the exact text and asks again before anything runs`, `opens the full statement through the read-only document provider when it is registered`, `says the fallback editor is a copy whose edits are not executed`, `requires a long statement to be opened in full before Execute is offered, and shows its start and end`, `never executes a long statement that was not opened in full, whatever the modal returns`, `reports a failure to open the full statement as that failure, and runs nothing`, `reports a rejected confirmation dialog as a failure, and runs nothing`; `queryPanelDmlSafety.test.ts`: `runs the review loop: Show Full Statement, continue, then Execute`; `dmlSafety.test.ts`: `shows a short statement whole`, `counts exactly what is hidden in a long %s statement`, `cuts a single very long line by characters at both ends`; `explainDocumentProvider.test.ts`: `serves the exact statement as a virtual, read-only .sql document`, `still shows the statement when the SQL language is not available` | ✅ |
| AC-DML-08 | Load More sends unconfirmed safety options and is neither offered nor performed for a confirmed DML result. | `queryPanelDmlSafety.test.ts`: `load more after a SELECT sends unconfirmed safety options`, `never offers or performs load more for a confirmed DML result` | ✅ |
| AC-DML-09 | The Data Explorer sends the confirmed request only for the target the modal named, with the profile pinned by identity: an environment, profile-name or identity change, a change during the review step, panel disposal or a newer execution sends nothing; an unresolvable URL or profile is refused without offering Execute. | `queryPanelDmlSafety.test.ts`: `confirms DML with statement and target, and sends the confirmed request for the same pair`, `pins a non-captured profile by identity for the confirmed request`, `refuses without offering Execute when %s`, `does not send the confirmed request when the environment changed while the modal was open`, `does not send the confirmed request when the %s changed while the modal was open`, `sends nothing when the target changes during the review step`, `sends nothing confirmed when the panel is disposed while the modal is open`, `a superseded confirmation neither sends nor posts over the newer execution`, `declining sends no confirmed request` | ✅ |
| AC-DML-10 | Cells skipped after a stop are marked one by one; a cell that cannot be marked does not affect any other cell; generic, authentication, unsuccessful and cell-cancelled DML cells stop the run. | `DataverseNotebookController.test.ts`: `marks skipped cells without touching another cell when one cannot be marked`, `stops the run when the confirmed request ends with %s`, `stops the run when a DML cell is cancelled through its own execution token during the confirmation` | ✅ |
| AC-DML-11 | Write targets from `dmlTargets` are listed with other and production environments marked; without them the modal says other named environments are not listed. | `DataverseNotebookController.test.ts`: `lists every reported write target, marking other and production environments`, `says other named environments are not listed when the daemon does not report write targets`; `dmlSafety.test.ts`: `reads dmlTargets defensively` | ✅ |
| AC-DML-12 | A cancel after the confirmed request was sent says records may have changed and keeps what came back. | `DataverseNotebookController.test.ts`: `keeps the result and says records may have changed when cancelled after the confirmed request returned`, `says records may have changed when the confirmed request is cancelled in flight`; `queryPanelDmlSafety.test.ts`: `says records may have changed when cancelled after the confirmed request was sent` | ✅ |
| AC-DML-13 | Hidden characters in the statement and names are shown as `<U+XXXX>` with a note; the captured text is sent unchanged. | `DataverseNotebookController.test.ts`: `shows control and bidirectional characters visibly but sends the captured text unchanged`; `dmlSafety.test.ts`: `makes control and bidirectional characters visible and keeps line breaks` | ✅ |

### Monaco Editor Integration

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| ME-01 | Monaco editor replaces textarea with SQL syntax highlighting | Manual | |
| ME-02 | FetchXML content auto-detected and highlighted as XML | Manual | |
| ME-03 | Language toggle button switches between SQL and FetchXML | Manual | |
| ME-04 | Ctrl+Space triggers IntelliSense completion popup | Manual | |
| ME-05 | Entity names appear in completions after FROM/JOIN | Manual | |
| ME-06 | Attribute names appear in completions after SELECT/WHERE | Manual | |
| ME-07 | FetchXML element/attribute completions work in XML mode | Manual | |
| ME-08 | Ctrl+Enter executes the query | Manual | |
| ME-09 | History loads query into Monaco editor | Manual | |
| ME-10 | Monaco theme matches VS Code theme (dark/light) | Manual | |
| ME-11 | Editor resizes vertically (120px min, 300px max) | Manual | |
| ME-12 | Pasting FetchXML auto-switches to XML language mode | Manual | |
| ME-13 | Manual language toggle overrides auto-detection | Manual | |
| ME-14 | Completion timeout (3s) prevents hanging on slow daemon | `detectLanguage` / `mapCompletionKind` unit tests | |
| ME-15 | The production Monaco bundle uses the patched npm sanitizer and excludes the vendored copy; unrelated imports are unaffected | `bundles the npm sanitizer instead of Monaco's vendored copy` / `leaves the same relative import outside Monaco unchanged` | ✅ |

Monaco edge cases:

| Scenario | Expected Behavior |
|----------|-------------------|
| Daemon not running | Completions return empty list, no error shown |
| Empty editor | SQL mode (default), no completions triggered |
| Paste FetchXML into SQL content | Auto-detects XML if content starts with `<` after paste |
| Toggle to FetchXML, type SQL | Manual override stays until content cleared or loadQuery |
| Very long query (5000+ chars) | Monaco handles efficiently (built-in virtualization) |
| Multiple Data Explorer panels | Each has independent Monaco instance + completion state |

### Diagnostic Markers

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| DM-01 | SQL syntax errors show red squiggles under the error location as the user types | `QueryValidateHandler.ValidSql_ReturnsEmpty` / `QueryValidateHandler.InvalidSql_ReturnsDiagnostics` | ❌ |
| DM-02 | Hovering over a squiggle shows the error message in a tooltip | Manual | ❌ |
| DM-03 | Markers clear when content changes and re-validate after 300ms debounce | Manual | ❌ |
| DM-04 | Stale validation responses (outdated requestId) are discarded | `requestId staleness` unit test | ❌ |
| DM-05 | Execution errors with position info set markers in the editor | `QueryPanel.executeQuery error enrichment` | ❌ |
| DM-06 | Markers clear on successful query execution | Manual | ❌ |
| DM-07 | Markers clear on language switch | Manual | ❌ |
| DM-08 | FetchXML content returns empty diagnostics (no false errors) | `QueryValidateHandler.FetchXml_ReturnsEmpty` | ❌ |
| DM-09 | Daemon not running returns empty diagnostics, no error shown | Manual | ❌ |
| DM-10 | Validation timeout (3s) clears markers silently | Manual | ❌ |

Diagnostic markers edge cases:

| Scenario | Expected Behavior |
|----------|-------------------|
| Rapid typing (< 300ms between keystrokes) | Debounce resets; only one validation request sent after typing stops |
| Daemon not running | Validation returns empty diagnostics, no error shown |
| Very large query (5000+ chars) | Validation still runs; parser handles large input efficiently |
| Multiple parse errors | All errors shown as separate markers |
| Error spans multiple lines | Marker covers the full span using `getPositionAt()` |
| Content cleared while validation in-flight | Stale response discarded; markers cleared |
| Switch to FetchXML while SQL validation in-flight | Stale response discarded; markers cleared on language switch |
| Valid SQL referencing nonexistent table/column | No markers shown (parse-only validation, no semantic checking) |

### Selection & Copy

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| SC-01 | Click cell selects single cell with anchor = focus | Manual | |
| SC-02 | Click + drag creates rectangular selection from anchor to current cell | Manual | |
| SC-03 | Shift+click extends selection from existing anchor | Manual | |
| SC-04 | Ctrl+A selects entire table | Manual | |
| SC-05 | Escape clears selection | Manual | |
| SC-06 | Ctrl+C on single cell copies displayed (formatted) value (no header) | Manual | |
| SC-07 | Ctrl+C on multi-cell copies with headers as TSV | Manual | |
| SC-08 | Ctrl+Shift+C inverts header behavior | Manual | |
| SC-09 | Context menu shows smart copy options with correct labels | Manual | |
| SC-10 | Context menu "Copy Cell Value" copies right-clicked cell value | Manual | |
| SC-11 | Selection shows background highlight + edge borders on perimeter | Manual | |
| SC-12 | Cursor changes to cell during drag | Manual | |
| SC-13 | Status bar shows copy hint based on selection state | Manual | |
| SC-14 | Right-clicking outside selection moves selection to that cell first | Manual | |
| SC-15 | Only columns within selection rectangle are copied (not all columns) | Manual | |
| SC-16 | Values with tabs/newlines are sanitized (replaced with spaces) in copy output | `getSelectionRect` / `copySelection` unit tests | |
| SC-17 | Copy on filtered/sorted view uses displayed row order, not raw allRows | Manual | |
| SC-18 | Post-copy confirmation appears in status bar for 2 seconds | Manual | |
| SC-19 | Escape with filter bar open closes filter bar only (does not clear selection) | Manual | |

Selection & Copy edge cases:

| Scenario | Expected Behavior |
|----------|-------------------|
| Drag starts on header (th) | No selection started — header click triggers sort |
| Drag outside results wrapper | Clamp focus to nearest edge cell |
| Copy with no selection | No-op (existing behavior) |
| Copy single cell with null/undefined value | Copy empty string |
| Ctrl+A with no results | No-op |
| Selection persists after re-sort | Selection cleared on sort (anchor/focus reset) |
| Selection persists after filter | Selection cleared on filter change |
| Selection persists after new query | Selection cleared on new results |
| Selection persists after Load More | Selection cleared on append results |
| Copy while filtered | Copies from filtered/sorted view, not raw allRows |
| Escape with filter open + selection | Closes filter bar only; second Escape clears selection |

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

### Why anchor+focus model over arbitrary cell Set?

**Context:** The current implementation uses a `Set<string>` of cell IDs (`"row:col"`), allowing arbitrary non-rectangular selections via Ctrl+click toggling.

**Decision:** Replace with anchor+focus coordinates defining a rectangle.

**Alternatives considered:**
- Keep Set model and add drag: Would need to enumerate all cells in the rectangle on every mousemove, creating O(rows*cols) operations per frame. The anchor+focus model only stores two points and computes the rectangle on demand.
- Multi-region selection (Ctrl+click adds disjoint rectangles): Over-engineering for a data explorer. SSMS doesn't support it. Excel does, but it's rarely used for copy-paste workflows.

**Consequences:**
- Positive: Simpler state, faster updates, natural drag/Shift+click behavior
- Negative: Can't select non-contiguous cells (acceptable — not a real use case for query results)

### Why smart defaults matching TUI?

**Context:** Need to decide when headers are included in copy.

**Decision:** Single cell = no header by default; multi-cell = headers by default. Ctrl+Shift+C inverts.

**Rationale:** When you copy a single cell, you want the value (to paste into a filter, a URL, a variable). When you copy a range, you're building a dataset (paste into Excel, share with someone) and headers provide context. This matches the TUI's design in `TableCopyHelper.cs` and the self-teaching status bar hint pattern.

### Why a dedicated `query/validate` endpoint over parsing error strings?

**Context:** Need inline diagnostic markers in Monaco. The daemon already has structured parse error info (`QueryParseException` with `Line`/`Column`, `SqlValidator` with `Start`/`Length`), but the RPC layer currently flattens errors into a plain string.

**Decision:** New `query/validate` RPC endpoint returning structured `DiagnosticDto[]`, plus enriched `queryError` responses with optional diagnostics.

**Alternatives considered:**
- **Client-side regex parsing of error strings**: Parse "Line X, Column Y" from `queryError` messages. No daemon changes, but fragile (breaks if message format changes), can't support as-you-type validation, and violates A2 (other surfaces would need their own parsing).
- **Execution-time markers only**: Enrich `queryError` but no live validation. Simpler but doesn't satisfy the "before execution" requirement. Also implemented as part of this design, but not sufficient alone.
- **Full LSP server**: Expose the daemon as a Language Server Protocol server. Provides diagnostics, completions, hover via standard protocol. But the incremental value over `query/validate` + `query/complete` doesn't justify the protocol overhaul. `query/validate` is a stepping stone — the diagnostic shape is already LSP-compatible.

**Consequences:**
- Positive: Reusable by all surfaces (A2), extends naturally to semantic validation (unknown tables/columns), clean separation of parse-only vs execute paths
- Negative: New RPC endpoint to maintain (minimal — `SqlValidator` already exists, endpoint is a thin wrapper)

---

## Related Specs

- TUI completion: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`
- TUI copy implementation: `src/PPDS.Cli/Tui/Helpers/TableCopyHelper.cs`
- Daemon endpoint: `query/complete` in `RpcMethodHandler.cs`
- Extension client: `daemonClient.queryComplete()` in `daemonClient.ts`
- [rpc-contracts.md](./rpc-contracts.md) — Contract tests covering `query/validate` shape
- [solutions.md](./solutions.md) — Solutions panel (absorbed from persistence-and-solutions-polish spec)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-11-17 | Added DML confirmation and failure display for notebooks and Data Explorer (AC-DML-01 to AC-DML-13) |
| 2026-04-25 | Added Diagnostic Markers section: `query/validate` endpoint, live validation flow, execution-time markers, marker clearing rules (#650) |
| 2026-03-18 | Created from vscode-data-explorer-monaco-editor.md and vscode-data-explorer-selection-copy.md per SL1 |
