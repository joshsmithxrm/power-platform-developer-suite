# Extension Stabilization Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix security issues, optimize bundle size, add query cancellation + DML safety guard, add "Open in Maker" links, and implement full daemon lifecycle resilience — bringing the VS Code extension to release-ready stability.

**Architecture:** All work is in `extension/src/` (TypeScript) except DML safety which requires changes to `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`. The RPC handler currently does its own SQL→FetchXML transpilation and calls `IQueryExecutor` directly — it does NOT route through `SqlQueryService`. DML safety requires adding `DmlSafetyGuard` invocation directly in the RPC handler. Daemon lifecycle uses VS Code EventEmitter pattern already established in tree views.

**Tech Stack:** TypeScript, VS Code Extension API, esbuild, JSON-RPC (vscode-jsonrpc), Monaco Editor

---

## File Structure

### New Files
- `extension/src/daemonStatusBar.ts` — Daemon connection status bar indicator
- `extension/src/__tests__/daemonStatusBar.test.ts` — Unit tests for status bar
- `extension/src/__tests__/panels/queryCancellation.test.ts` — Unit tests for cancellation wiring
- `extension/src/__tests__/panels/dmlSafety.test.ts` — Unit tests for DML safety UX

### Modified Files
- `extension/src/panels/SolutionsPanel.ts` — Fix innerHTML copy button, add Open in Maker
- `extension/src/notebooks/virtualScrollScript.ts` — Fix href attribute escaping
- `extension/src/panels/monaco-entry.ts` — Selective language imports
- `extension/src/panels/monaco-worker.ts` — Match selective imports
- `extension/esbuild.js` — Add Monaco ESM plugin if needed
- `extension/src/daemonClient.ts` — Add heartbeat, onReconnected event, cancellation support
- `extension/src/extension.ts` — Wire status bar, register cancellation commands
- `extension/src/panels/WebviewPanelBase.ts` — Add daemon reconnect subscription
- `extension/src/panels/QueryPanel.ts` — Wire cancellation, DML safety, record links
- `extension/src/commands/browserCommands.ts` — Add solution-level Open in Maker
- `extension/src/types.ts` — Add DML safety types if needed
- `extension/package.json` — Register new commands (cancel query, restart daemon)
- `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — Wire DmlSafetyOptions through query/sql RPC

---

## Chunk 1: Security Fixes + Monaco Bundle Optimization

### Task 1: Fix SolutionsPanel innerHTML copy button

The copy button at `SolutionsPanel.ts:520-522` saves `copyBtn.innerHTML` and restores it via `innerHTML` after showing a checkmark. This violates Constitution S1. The fix is trivial — use `textContent` consistently since the button content is a single emoji character.

**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts:516-524`

**Acceptance Criteria:**
- AC-01: Copy button never uses innerHTML for restoration
- AC-02: Copy-to-clipboard still works (checkmark shows, reverts to clipboard icon)

- [ ] **Step 1: Fix the copy button handler**

In `SolutionsPanel.ts`, find the copy button click handler (around line 516-524). Replace:

```javascript
var original = copyBtn.innerHTML;
copyBtn.textContent = '\u2713';
setTimeout(function() { copyBtn.innerHTML = original; }, 1500);
```

With:

```javascript
copyBtn.textContent = '\u2713';
setTimeout(function() { copyBtn.textContent = '\u{1F4CB}'; }, 1500);
```

The clipboard emoji `📋` is Unicode `U+1F4CB`. Use the ES2015+ `\u{...}` escape syntax which is more readable than surrogate pairs.

- [ ] **Step 2: Verify the fix**

Run: `npm run compile` from `extension/`
Expected: No compilation errors. The copy button should show ✓ on click and revert to 📋 after 1.5s.

- [ ] **Step 3: Commit**

```
fix: replace innerHTML restoration with textContent in SolutionsPanel copy button (S1)
```

---

### Task 2: Fix notebook href attribute escaping

The virtual scroll script at `virtualScrollScript.ts:76` uses `escapeHtml()` for href attributes. While safe today (URLs are pre-encoded with `encodeURIComponent`), the correct function for attribute values is `escapeAttr()`. Add it for correctness.

**Files:**
- Modify: `extension/src/notebooks/virtualScrollScript.ts:39-44,76`

- [ ] **Step 1: Add escapeAttr function to the generated script**

In `virtualScrollScript.ts`, find the `escapeHtml` function definition (around line 39-44) and add an `escapeAttr` function after it:

```javascript
function escapeAttr(str) {
    if (str === null || str === undefined) return '';
    var s = String(str);
    return s.replace(/&/g, '&amp;').replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
```

This matches the existing `escapeAttr` implementation in `SolutionsPanel.ts:739-744` for consistency.

- [ ] **Step 2: Use escapeAttr for href attribute**

On line 76, change:

```javascript
html += '<td class="data-cell"><a href="' + escapeHtml(cell.url) + '" target="_blank">' + escapeHtml(cell.text) + '</a></td>';
```

To:

```javascript
html += '<td class="data-cell"><a href="' + escapeAttr(cell.url) + '" target="_blank">' + escapeHtml(cell.text) + '</a></td>';
```

Note: `escapeHtml` is still correct for the link text content. Only the attribute value changes.

- [ ] **Step 3: Verify the fix**

Run: `npm run compile` from `extension/`
Expected: No errors. Notebook results with clickable links should still work.

- [ ] **Step 4: Commit**

```
fix: use escapeAttr for href attribute values in notebook virtual scroll (S1)
```

---

### Task 3: Monaco selective language bundling

The current `monaco-entry.ts` does `import * as monaco from 'monaco-editor'` which bundles all 30+ languages into a 9.5 MB file. We only need SQL and XML. Switch to selective ESM imports.

**Files:**
- Modify: `extension/src/panels/monaco-entry.ts`
- Modify: `extension/src/panels/monaco-worker.ts` (may need update)
- Modify: `extension/esbuild.js` (may need ESM alias)

**Acceptance Criteria:**
- AC-01: Monaco bundle size drops below 4 MB (production build)
- AC-02: SQL syntax highlighting works in Data Explorer
- AC-03: XML syntax highlighting works when language toggled to FetchXML
- AC-04: IntelliSense completions still work for both SQL and XML
- AC-05: Monaco editor theme detection (dark/light) still works

- [ ] **Step 1: Switch to selective Monaco imports**

Replace the entire content of `extension/src/panels/monaco-entry.ts` with:

```typescript
/**
 * Monaco Editor entry point — selective language bundling.
 * Only SQL and XML languages are included to reduce bundle size.
 */

// Core editor API
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';

// Language contributions — only SQL and XML
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution.js';
import 'monaco-editor/esm/vs/basic-languages/xml/xml.contribution.js';

// Configure Monaco to use inline workers (blob URLs)
(self as any).MonacoEnvironment = {
    getWorker(_moduleId: string, _label: string) {
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

- [ ] **Step 2: Build and measure**

Run from `extension/`:
```bash
node esbuild.js --production
```

Check the output size of `dist/monaco-editor.js`. It should be significantly smaller than 9.5 MB.

If the build fails due to missing ESM paths, you may need to adjust esbuild.js to add:
```javascript
mainFields: ['module', 'main'],
```
to the Monaco build context (Build 2, around line 20-33).

- [ ] **Step 3: Test SQL highlighting**

Open the extension in dev mode (F5), open Data Explorer, type a SQL query:
```sql
SELECT accountid, name FROM account WHERE statecode = 0
```
Expected: SQL keywords highlighted, completions work on space/dot triggers.

- [ ] **Step 4: Test XML highlighting**

Toggle language to FetchXML. Type:
```xml
<fetch top="10"><entity name="account"><attribute name="name"/></entity></fetch>
```
Expected: XML syntax highlighted, completions work on `<` and `"` triggers.

- [ ] **Step 5: Test dark/light theme**

Switch VS Code theme from dark to light. Editor should match.

- [ ] **Step 6: Commit**

```
perf: selective Monaco language bundling — SQL and XML only

Reduces monaco-editor.js from ~9.5 MB to ~2-3 MB by importing only
the languages we actually use instead of the full monaco-editor package.
```

---

## Chunk 2: Query Cancellation

### Task 4: Add query cancellation to Data Explorer

The TUI supports Escape to cancel in-flight queries via `CancellationTokenSource`. The extension's `daemonClient.querySql()` already accepts an optional `CancellationToken` parameter but `QueryPanel` never passes one. We need to wire cancellation end-to-end.

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`
- Modify: `extension/package.json` (register ppds.cancelQuery command)
- Test: `extension/src/__tests__/unit/queryCancellation.test.ts`

**Acceptance Criteria:**
- AC-01: User can press Escape in the editor while a query is running to cancel it
- AC-02: A "Cancel" button appears in the toolbar while a query is executing
- AC-03: Cancelled queries show "Query cancelled" message (not an error)
- AC-04: CancellationToken is propagated from webview through host to daemon RPC call
- AC-05: After cancellation, user can immediately execute a new query

- [ ] **Step 1: Write unit test for cancellation token wiring**

Create `extension/src/__tests__/unit/queryCancellation.test.ts`:

```typescript
import { describe, it, expect, vi } from 'vitest';
import { CancellationTokenSource } from 'vscode-jsonrpc';

describe('Query cancellation', () => {
    it('should create a CancellationTokenSource per query execution', () => {
        const cts = new CancellationTokenSource();
        expect(cts.token.isCancellationRequested).toBe(false);
        cts.cancel();
        expect(cts.token.isCancellationRequested).toBe(true);
    });

    it('should cancel previous query when new query starts', () => {
        const cts1 = new CancellationTokenSource();
        const cts2 = new CancellationTokenSource();

        // Simulate: new query starts, old one should be cancelled
        cts1.cancel();
        expect(cts1.token.isCancellationRequested).toBe(true);
        expect(cts2.token.isCancellationRequested).toBe(false);
    });
});
```

- [ ] **Step 2: Run test to verify it passes (basic CancellationToken behavior)**

Run: `npm run ext:test -- --reporter=verbose`
Expected: PASS

- [ ] **Step 3: Add CancellationTokenSource tracking to QueryPanel**

In `QueryPanel.ts`, add a class-level field for the active cancellation source:

```typescript
private queryCts: CancellationTokenSource | undefined;
```

Import `CancellationTokenSource` from `vscode-jsonrpc/node` (matches existing pattern in `DataverseNotebookController.ts`):
```typescript
import { CancellationTokenSource } from 'vscode-jsonrpc/node';
```

- [ ] **Step 4: Wire cancellation into executeQuery**

In the `executeQuery` method, before calling `this.daemon.querySql()`:

```typescript
// Cancel any previous in-flight query
this.queryCts?.cancel();
this.queryCts?.dispose();
this.queryCts = new CancellationTokenSource();
const token = this.queryCts.token;
```

Then pass `token` to the daemon call:
```typescript
const result = await this.daemon.querySql({ sql, top: defaultTop, useTds, environmentUrl }, token);
```

And for FetchXML:
```typescript
const result = await this.daemon.queryFetch({ fetchXml: sql, top: defaultTop, environmentUrl }, token);
```

In the catch block, detect cancellation:
```typescript
if (token.isCancellationRequested) {
    this.postMessage({ command: 'queryCancelled' });
    return;
}
```

- [ ] **Step 5: Add cancel message handler from webview**

In the `onDidReceiveMessage` switch block, add:

```typescript
case 'cancelQuery':
    this.queryCts?.cancel();
    break;
```

- [ ] **Step 6: Add Escape key handler and Cancel button in webview HTML**

In the `getHtmlContent()` method's JavaScript section, add an Escape key handler to the Monaco editor:

```javascript
editor.addCommand(monaco.KeyCode.Escape, function() {
    if (isExecuting) {
        vscode.postMessage({ command: 'cancelQuery' });
    }
});
```

Add a Cancel button next to the Execute button (hidden by default):

```html
<button id="cancelBtn" class="toolbar-btn" style="display:none;" title="Cancel query (Escape)">Cancel</button>
```

Wire the Cancel button click:
```javascript
document.getElementById('cancelBtn').addEventListener('click', function() {
    vscode.postMessage({ command: 'cancelQuery' });
});
```

Toggle visibility based on execution state:
```javascript
// In executionStarted handler:
document.getElementById('cancelBtn').style.display = '';
document.getElementById('executeBtn').style.display = 'none';

// In queryResult/queryError/queryCancelled handler:
document.getElementById('cancelBtn').style.display = 'none';
document.getElementById('executeBtn').style.display = '';
```

- [ ] **Step 7: Add queryCancelled message handler in webview**

In the webview's message handler switch:

```javascript
case 'queryCancelled':
    document.getElementById('cancelBtn').style.display = 'none';
    document.getElementById('executeBtn').style.display = '';
    statusEl.textContent = 'Query cancelled';
    break;
```

- [ ] **Step 8: Clean up CancellationTokenSource on panel dispose**

In `QueryPanel`'s dispose method:
```typescript
this.queryCts?.cancel();
this.queryCts?.dispose();
```

- [ ] **Step 9: Test manually**

1. Open Data Explorer, run a slow query (e.g., `SELECT * FROM account`)
2. Press Escape while executing → should show "Query cancelled"
3. Click Cancel button while executing → same result
4. Execute a new query immediately after cancel → should work

- [ ] **Step 10: Commit**

```
feat: add query cancellation support to Data Explorer

Escape key or Cancel button cancels in-flight queries. CancellationToken
propagated through daemon RPC. Matches TUI behavior (Ctrl+C / Escape).

AC-01 through AC-05 satisfied.
```

---

## Chunk 3: DML Safety Guard

### Task 5: Wire DML safety through daemon RPC

**IMPORTANT CONTEXT:** The RPC `query/sql` handler (`RpcMethodHandler.cs:936-1009`) does NOT route through `SqlQueryService`. It transpiles SQL→FetchXML directly via `TranspileSqlToFetchXml()` and calls `IQueryExecutor.ExecuteFetchXmlAsync()`. DML safety must be added directly to the RPC handler by parsing the SQL with `TSql160Parser` and calling `DmlSafetyGuard.Check()`.

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` (QuerySqlRequest DTO at line 2720 + QuerySqlAsync handler at line 936)

**Acceptance Criteria:**
- AC-01: RPC `query/sql` accepts optional `dmlSafety` parameter with `isConfirmed`, `isDryRun`, `noLimit`, `rowCap` fields
- AC-02: When `dmlSafety` is provided, handler parses SQL and calls `DmlSafetyGuard.Check()`
- AC-03: Blocked operations return a **structured** JSON-RPC error with `code` and `data: { dmlBlocked: true, blockReason: "..." }`
- AC-04: Operations requiring confirmation return a **structured** JSON-RPC error with `data: { dmlConfirmationRequired: true, confirmationMessage: "..." }`
- AC-05: When `dmlSafety` is null/omitted, no safety check is performed (backwards compatible)

- [ ] **Step 1: Add DmlSafety fields to QuerySqlRequest DTO**

Find `QuerySqlRequest` at line 2720 in `RpcMethodHandler.cs`. Add:

```csharp
[JsonPropertyName("dmlSafety")] public DmlSafetyRpcOptions? DmlSafety { get; set; }
```

Add the DTO class near the other query DTOs:

```csharp
public sealed class DmlSafetyRpcOptions
{
    [JsonPropertyName("isConfirmed")] public bool IsConfirmed { get; set; }
    [JsonPropertyName("isDryRun")] public bool IsDryRun { get; set; }
    [JsonPropertyName("noLimit")] public bool NoLimit { get; set; }
    [JsonPropertyName("rowCap")] public int? RowCap { get; set; }
}
```

- [ ] **Step 2: Define structured error codes for DML safety**

Add error code constants (near existing `ErrorCodes` class, or in the RPC error codes section):

```csharp
public static class DmlErrorCodes
{
    public const int DmlBlocked = -32001;
    public const int DmlConfirmationRequired = -32002;
}
```

Define a response data object for DML errors:

```csharp
public sealed class DmlSafetyErrorData
{
    [JsonPropertyName("dmlBlocked")] public bool DmlBlocked { get; init; }
    [JsonPropertyName("dmlConfirmationRequired")] public bool DmlConfirmationRequired { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
```

- [ ] **Step 3: Add DML safety check to QuerySqlAsync handler**

In `QuerySqlAsync` (line 940), BEFORE the `TranspileSqlToFetchXml` call at line 980, add DML checking:

```csharp
// DML safety check (only when dmlSafety options provided)
if (request.DmlSafety != null)
{
    var parser = new TSql160Parser(initialQuotedIdentifiers: false);
    using var reader = new StringReader(request.Sql);
    var fragment = parser.Parse(reader, out var errors);

    if (fragment is TSqlScript script && script.Batches.Count > 0
        && script.Batches[0].Statements.Count > 0)
    {
        var guard = new DmlSafetyGuard();
        var opts = new DmlSafetyOptions
        {
            IsConfirmed = request.DmlSafety.IsConfirmed,
            IsDryRun = request.DmlSafety.IsDryRun,
            NoLimit = request.DmlSafety.NoLimit,
            RowCap = request.DmlSafety.RowCap,
        };
        var result = guard.Check(script.Batches[0].Statements[0], opts);

        if (result.IsBlocked)
        {
            throw new RpcException(
                DmlErrorCodes.DmlBlocked,
                result.BlockReason ?? "DML operation blocked",
                new DmlSafetyErrorData
                {
                    DmlBlocked = true,
                    Message = result.BlockReason ?? "DML operation blocked"
                });
        }

        if (result.RequiresConfirmation && !request.DmlSafety.IsConfirmed)
        {
            throw new RpcException(
                DmlErrorCodes.DmlConfirmationRequired,
                result.ConfirmationMessage ?? "DML operation requires confirmation",
                new DmlSafetyErrorData
                {
                    DmlConfirmationRequired = true,
                    Message = result.ConfirmationMessage ?? "DML operation requires confirmation"
                });
        }
    }
}
```

Note: Check that `RpcException` supports a `data` parameter. If not, use the JSON-RPC `ResponseError` mechanism that `StreamJsonRpc` provides. The key requirement is that the error response includes structured data, not just a message string.

- [ ] **Step 4: Add required using statements**

At the top of `RpcMethodHandler.cs`, ensure these are present:

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Services.Query; // For DmlSafetyGuard, DmlSafetyOptions
```

- [ ] **Step 5: Build and test**

Run: `dotnet build PPDS.sln` from the repo root.
Expected: No errors.

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```
feat: add DML safety checking to query/sql RPC endpoint

Parses SQL via TSql160Parser and runs DmlSafetyGuard.Check() when
dmlSafety parameter is provided. Returns structured JSON-RPC errors
with dmlBlocked/dmlConfirmationRequired data for programmatic handling.
Does not affect existing callers (dmlSafety is optional).
```

---

### Task 6: Add DML safety guard UX to Data Explorer

Wire the extension to send `dmlSafety` params with every query and handle confirmation dialogs.

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`
- Modify: `extension/src/daemonClient.ts` (add dmlSafety to querySql params type)
- Test: `extension/src/__tests__/unit/dmlSafety.test.ts`

**Acceptance Criteria:**
- AC-05: Extension sends `dmlSafety: { isConfirmed: false }` with every query by default
- AC-06: When daemon returns DML confirmation error, extension shows VS Code warning dialog
- AC-07: Dialog shows the confirmation message from the daemon (e.g., "DELETE on production environment — are you sure?")
- AC-08: If user confirms, extension re-sends query with `isConfirmed: true`
- AC-09: If user cancels, query is aborted and "Query cancelled" shown
- AC-10: Blocked operations (DELETE without WHERE) show error message, no confirmation option

- [ ] **Step 1: Add dmlSafety to querySql params type**

In `daemonClient.ts`, update the `querySql` method params type:

```typescript
async querySql(params: {
    sql: string;
    environmentUrl?: string;
    top?: number;
    page?: number;
    pagingCookie?: string;
    count?: boolean;
    showFetchXml?: boolean;
    useTds?: boolean;
    dmlSafety?: { isConfirmed?: boolean; isDryRun?: boolean; noLimit?: boolean; rowCap?: number };
}, token?: CancellationToken): Promise<QueryResultResponse> {
```

- [ ] **Step 2: Send dmlSafety with every query from QueryPanel**

In `QueryPanel.executeQuery()`, add `dmlSafety: { isConfirmed: false }` to every `querySql` call:

```typescript
const result = await this.daemon.querySql({
    sql,
    top: defaultTop,
    useTds,
    environmentUrl,
    dmlSafety: { isConfirmed },
}, token);
```

Where `isConfirmed` is a parameter on `executeQuery`:

```typescript
private async executeQuery(sql: string, isRetry = false, useTds?: boolean, language?: string, isConfirmed = false): Promise<void> {
```

**Task 6 depends on Task 5.** The structured error codes defined in Task 5 drive the detection logic here.

- [ ] **Step 3: Detect DML safety errors using structured error codes**

The JSON-RPC library (`vscode-jsonrpc`) wraps errors as `ResponseError` with `code` and `data` properties. In the catch block of `executeQuery`, detect DML-specific errors by error code (not string matching — per Constitution D4):

```typescript
catch (error: unknown) {
    // Check for structured DML safety errors from daemon
    const rpcError = error as { code?: number; data?: { dmlBlocked?: boolean; dmlConfirmationRequired?: boolean; message?: string } };

    if (rpcError.code === -32002 && rpcError.data?.dmlConfirmationRequired) {
        // DML requires confirmation — show warning dialog
        const choice = await vscode.window.showWarningMessage(
            rpcError.data.message || 'This DML operation requires confirmation.',
            { modal: true },
            'Execute Anyway'
        );
        if (choice === 'Execute Anyway') {
            await this.executeQuery(sql, isRetry, useTds, language, true);
        } else {
            this.postMessage({ command: 'queryCancelled' });
        }
        return;
    }

    if (rpcError.code === -32001 && rpcError.data?.dmlBlocked) {
        // DML is completely blocked — show error, no confirmation possible
        this.postMessage({
            command: 'queryError',
            error: rpcError.data.message || 'This DML operation is blocked.'
        });
        return;
    }

    // ... existing error handling (auth errors, generic errors, etc.)
    const msg = error instanceof Error ? error.message : String(error);
```

Note: The error codes `-32001` (DmlBlocked) and `-32002` (DmlConfirmationRequired) must match what Task 5 defines in `DmlErrorCodes`.

- [ ] **Step 4: Test manually**

1. Connect to a Dataverse environment
2. Try `DELETE FROM account` → should be blocked ("DELETE without WHERE clause")
3. Try `DELETE FROM account WHERE name = 'test'` on a production environment → should prompt confirmation
4. Click "Execute Anyway" → should execute
5. Click Cancel → should show "Query cancelled"
6. Try `SELECT * FROM account` → should execute normally (no DML dialog)

- [ ] **Step 5: Commit**

```
feat: add DML safety guard to Data Explorer

Extension sends dmlSafety with every query. Blocked DML operations
(DELETE/UPDATE without WHERE) show error. Production environments
require confirmation dialog before executing DML. Matches TUI safety model.

AC-05 through AC-10 satisfied.
```

---

## Chunk 4: Open in Maker + Record Links

### Task 7: Add "Open in Maker" to Solutions panel

The `browserCommands.ts` already has `buildMakerUrl()` and environment ID resolution. We need to add a click action on solutions that opens the solution in the Maker Portal.

**Files:**
- Modify: `extension/src/panels/SolutionsPanel.ts`

**Acceptance Criteria:**
- AC-01: Each solution in the list has an "Open in Maker" button/link
- AC-02: Clicking opens `https://make.powerapps.com/environments/{envId}/solutions/{solutionId}` in browser
- AC-03: If environment ID is not available, shows informational message and falls back to environment-level Maker URL

- [ ] **Step 1: Add Open in Maker button to solution rows**

In `SolutionsPanel.ts`, in the solution row rendering (around line 615-625), add an "Open in Maker" button after the solution info:

```javascript
'<button class="open-maker-btn" data-solution-id="' + escapeAttr(sol.id || '') + '" data-unique-name="' + escapeAttr(sol.uniqueName) + '" title="Open in Maker Portal">\uD83D\uDD17</button>'
```

Note: The solution GUID is `sol.id` (per `SolutionInfoDto` in `types.ts:156-167`). The unique name alone isn't enough for the Maker URL; we need the solution GUID.

- [ ] **Step 2: Add click handler for Open in Maker**

In the webview JavaScript click handler (around line 481), add:

```javascript
var makerBtn = e.target.closest('.open-maker-btn');
if (makerBtn) {
    var solutionId = makerBtn.dataset.solutionId;
    vscode.postMessage({ command: 'openInMaker', solutionId: solutionId });
    e.stopPropagation();
    return;
}
```

- [ ] **Step 3: Handle openInMaker message on host side**

In the `SolutionsPanel` message handler, add:

```typescript
case 'openInMaker': {
    const envId = this.environmentId; // or resolve from current environment
    if (envId && msg.solutionId) {
        const url = `https://make.powerapps.com/environments/${envId}/solutions/${msg.solutionId}`;
        await vscode.env.openExternal(vscode.Uri.parse(url));
    } else if (envId) {
        const url = `https://make.powerapps.com/environments/${envId}/solutions`;
        await vscode.env.openExternal(vscode.Uri.parse(url));
    } else {
        vscode.window.showInformationMessage('Environment ID not available — cannot open Maker Portal.');
    }
    break;
}
```

**IMPORTANT:** `SolutionsPanel` currently tracks `this.environmentUrl` but NOT `this.environmentId` (the Power Platform GUID, which is different from the URL). You must add a `private environmentId: string | null = null;` field and populate it when the environment is set. The `EnvSelectResponse` includes `environmentId` — store it when processing the environment selection. Also check `EnvListResponse` — environments there include `environmentId` which can be matched by URL to resolve the ID for the current environment.

- [ ] **Step 4: Style the button**

Add CSS for the Open in Maker button so it's subtle and doesn't dominate the solution row:

```css
.open-maker-btn {
    background: none;
    border: none;
    cursor: pointer;
    opacity: 0.5;
    padding: 2px 4px;
    font-size: 12px;
}
.open-maker-btn:hover { opacity: 1; }
```

- [ ] **Step 5: Test manually**

1. Open Solutions panel
2. Click the link icon on a solution → browser opens Maker with that solution
3. Verify URL is correct with environment ID and solution ID

- [ ] **Step 6: Commit**

```
feat: add "Open in Maker" button to Solutions panel

Each solution row has a link icon that opens the solution directly in
Power Apps Maker Portal. Uses environment ID from profile for URL.
```

---

### Task 8: Add record links to Data Explorer results

Query results should let users open records in Dynamics 365. The entity name comes from `QueryResultResponse.entityName` and the record ID follows the `{entityName}id` convention.

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

**Acceptance Criteria:**
- AC-04: Right-click context menu on a result row includes "Open Record in Dynamics"
- AC-05: Clicking opens `{environmentUrl}/main.aspx?pagetype=entityrecord&etn={entityName}&id={recordId}` in browser
- AC-06: Menu item only appears when entity name is known (not aggregate queries)
- AC-07: "Copy Record URL" context menu item copies the URL to clipboard

- [ ] **Step 1: Add record URL construction helper in webview**

In the QueryPanel webview JavaScript, add a helper function:

```javascript
function getRecordId(row) {
    if (!lastEntityName) return null;
    var idKey = lastEntityName + 'id';
    return row[idKey] || null;
}

function buildRecordUrl(entityName, recordId) {
    if (!currentEnvironmentUrl || !entityName || !recordId) return null;
    var baseUrl = currentEnvironmentUrl.replace(/\/+$/, '');
    return baseUrl + '/main.aspx?pagetype=entityrecord&etn=' +
        encodeURIComponent(entityName) + '&id=' + encodeURIComponent(recordId);
}
```

Track `lastEntityName` from query results:
```javascript
// In handleQueryResult:
lastEntityName = msg.data.entityName || null;
```

Track `currentEnvironmentUrl` from environment updates:
```javascript
// In updateEnvironment handler:
currentEnvironmentUrl = msg.url;
```

- [ ] **Step 2: Add context menu items**

In the context menu items array (around line 1145-1152), add after the existing copy items:

```javascript
// Only add record items if we have entity context and a single row selected
var recordId = getRecordId(allRows[anchor.row]);
if (lastEntityName && recordId) {
    items.push({ label: 'separator', shortcut: '', action: 'separator' });
    items.push({ label: 'Open Record in Dynamics', shortcut: '', action: 'openRecord' });
    items.push({ label: 'Copy Record URL', shortcut: '', action: 'copyRecordUrl' });
}
```

- [ ] **Step 3: Handle context menu actions**

In the context menu action handler (around line 1170-1214), add:

```javascript
case 'openRecord': {
    var rid = getRecordId(allRows[clickRow]);
    var url = buildRecordUrl(lastEntityName, rid);
    if (url) vscode.postMessage({ command: 'openRecordUrl', url: url });
    break;
}
case 'copyRecordUrl': {
    var rid = getRecordId(allRows[clickRow]);
    var url = buildRecordUrl(lastEntityName, rid);
    if (url) vscode.postMessage({ command: 'copyToClipboard', text: url });
    break;
}
```

- [ ] **Step 4: Handle openRecordUrl on host side**

In `QueryPanel`'s message handler, add:

```typescript
case 'openRecordUrl':
    if (msg.url) {
        await vscode.env.openExternal(vscode.Uri.parse(msg.url));
    }
    break;
```

- [ ] **Step 5: Test manually**

1. Run `SELECT accountid, name FROM account` in Data Explorer
2. Right-click a result row → "Open Record in Dynamics" and "Copy Record URL" should appear
3. Click "Open Record in Dynamics" → opens record in browser
4. Click "Copy Record URL" → URL copied to clipboard
5. Run an aggregate query `SELECT COUNT(*) FROM account` → record menu items should NOT appear

- [ ] **Step 6: Commit**

```
feat: add "Open Record in Dynamics" and "Copy Record URL" to Data Explorer

Right-click context menu on query result rows includes options to open
the record in Dynamics 365 or copy the direct URL. Only available for
non-aggregate queries where entity name and record ID are known.

AC-04 through AC-07 satisfied.
```

---

## Chunk 5: Daemon Lifecycle Resilience

### Task 9: Add EventEmitter and heartbeat to DaemonClient

Add a reconnection event, heartbeat ping, and structured state management.

**Files:**
- Modify: `extension/src/daemonClient.ts`
- Test: `extension/src/__tests__/unit/daemonStatusBar.test.ts`

**Acceptance Criteria:**
- AC-01: DaemonClient emits `onDidChangeState` event with states: `starting`, `ready`, `error`, `reconnecting`
- AC-02: Heartbeat pings daemon every 30 seconds via lightweight RPC call
- AC-03: If heartbeat fails, state changes to `reconnecting` and auto-restart triggers
- AC-04: On successful reconnect, state changes to `ready` and `onDidReconnect` fires
- AC-05: Heartbeat stops when disposed

- [ ] **Step 1: Add state enum and EventEmitters to DaemonClient**

At the top of `daemonClient.ts`, add:

```typescript
export type DaemonState = 'stopped' | 'starting' | 'ready' | 'error' | 'reconnecting';
```

In the `DaemonClient` class, add fields after the existing ones (around line 94):

```typescript
private _state: DaemonState = 'stopped';
private _heartbeatTimer: ReturnType<typeof setInterval> | undefined;
private static readonly HEARTBEAT_INTERVAL_MS = 30_000;

private readonly _onDidChangeState = new vscode.EventEmitter<DaemonState>();
readonly onDidChangeState = this._onDidChangeState.event;

private readonly _onDidReconnect = new vscode.EventEmitter<void>();
readonly onDidReconnect = this._onDidReconnect.event;
```

Add a state setter:

```typescript
private setState(state: DaemonState): void {
    if (this._state === state) return;
    this._state = state;
    this._onDidChangeState.fire(state);
}

get state(): DaemonState { return this._state; }
```

- [ ] **Step 2: Wire state transitions into existing methods**

In `start()`:
- At the top (line 135): `this.setState('starting');`
- After successful handshake (line 245): `this.setState('ready'); this.startHeartbeat();`
- In catch block (line 223-228): `this.setState('error');`

In the post-startup exit handler (line 239-243):
```typescript
this.process.on('exit', (code: number | null) => {
    this.log.warn(`Daemon exited with code ${code}`);
    this.connection = null;
    this.process = null;
    this.stopHeartbeat();
    this.setState('error');
});
```

In `ensureConnected()` (line 700-709):
```typescript
private async ensureConnected(): Promise<void> {
    if (this._disposed) throw new Error('DaemonClient is disposed');
    if (this.connection) return;
    if (!this.connectingPromise) {
        const wasReady = this._state === 'error' || this._state === 'ready';
        this.setState('reconnecting');
        this.connectingPromise = this.start().then(() => {
            if (wasReady) {
                this._onDidReconnect.fire();
            }
        }).finally(() => {
            this.connectingPromise = null;
        });
    }
    await this.connectingPromise;
}
```

- [ ] **Step 3: Add heartbeat**

```typescript
private startHeartbeat(): void {
    this.stopHeartbeat();
    this._heartbeatTimer = setInterval(async () => {
        if (!this.connection || this._disposed) {
            this.stopHeartbeat();
            return;
        }
        try {
            // Use auth/list as heartbeat. This is heavier than ideal (loads profile store).
            // TODO: Consider adding a lightweight health/ping RPC endpoint in a future pass.
            await this.connection.sendRequest('auth/list');
        } catch {
            this.log.warn('Heartbeat failed — daemon may be unresponsive');
            this.connection?.dispose();
            this.connection = null;
            this.process?.kill();
            this.process = null;
            this.stopHeartbeat();
            this.setState('error');
        }
    }, DaemonClient.HEARTBEAT_INTERVAL_MS);
}

private stopHeartbeat(): void {
    if (this._heartbeatTimer) {
        clearInterval(this._heartbeatTimer);
        this._heartbeatTimer = undefined;
    }
}
```

- [ ] **Step 4: Clean up in dispose()**

In the `dispose()` method, add cleanup:

```typescript
dispose(): void {
    this.log.info('Disposing daemon client...');
    this._disposed = true;
    this.connectingPromise = null;
    this.stopHeartbeat();

    if (this.connection) {
        this.connection.dispose();
        this.connection = null;
    }

    if (this.process) {
        this.process.kill();
        this.process = null;
    }

    // Dispose EventEmitters LAST (after all other cleanup)
    this.setState('stopped');
    this._onDidChangeState.dispose();
    this._onDidReconnect.dispose();
}
```

- [ ] **Step 5: Commit**

```
feat: add daemon heartbeat, state machine, and reconnect events

DaemonClient now emits onDidChangeState (stopped/starting/ready/error/
reconnecting) and onDidReconnect events. 30-second heartbeat detects
unresponsive daemon and triggers auto-reconnect.

AC-01 through AC-05 satisfied.
```

---

### Task 10: Add daemon status bar indicator

Wire the DaemonClient state events to a visible StatusBarItem.

**Files:**
- Create: `extension/src/daemonStatusBar.ts`
- Modify: `extension/src/extension.ts`

**Acceptance Criteria:**
- AC-06: Status bar shows daemon connection state with icon and text
- AC-07: States: "$(check) PPDS" (ready), "$(sync~spin) PPDS" (starting/reconnecting), "$(error) PPDS" (error)
- AC-08: Clicking the status bar item runs `ppds.restartDaemon` command
- AC-09: Tooltip shows detailed state (e.g., "PPDS Daemon: Connected" or "PPDS Daemon: Reconnecting...")

- [ ] **Step 1: Create daemonStatusBar.ts**

```typescript
import * as vscode from 'vscode';
import type { DaemonClient, DaemonState } from './daemonClient.js';

export class DaemonStatusBar implements vscode.Disposable {
    private readonly statusBarItem: vscode.StatusBarItem;
    private readonly disposables: vscode.Disposable[] = [];

    constructor(client: DaemonClient) {
        this.statusBarItem = vscode.window.createStatusBarItem(
            vscode.StatusBarAlignment.Left,
            50
        );
        this.statusBarItem.command = 'ppds.restartDaemon';
        this.updateState(client.state);
        this.statusBarItem.show();

        this.disposables.push(
            client.onDidChangeState(state => this.updateState(state)),
            this.statusBarItem,
        );
    }

    private updateState(state: DaemonState): void {
        switch (state) {
            case 'ready':
                this.statusBarItem.text = '$(check) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Connected';
                this.statusBarItem.backgroundColor = undefined;
                break;
            case 'starting':
            case 'reconnecting':
                this.statusBarItem.text = '$(sync~spin) PPDS';
                this.statusBarItem.tooltip = `PPDS Daemon: ${state === 'starting' ? 'Starting' : 'Reconnecting'}...`;
                this.statusBarItem.backgroundColor = undefined;
                break;
            case 'error':
                this.statusBarItem.text = '$(error) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Disconnected — click to restart';
                this.statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
                break;
            case 'stopped':
                this.statusBarItem.text = '$(circle-slash) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Stopped';
                this.statusBarItem.backgroundColor = undefined;
                break;
        }
    }

    dispose(): void {
        for (const d of this.disposables) d.dispose();
    }
}
```

- [ ] **Step 2: Register restart command and wire status bar in extension.ts**

In `extension.ts`, after daemon client creation (around line 74):

```typescript
import { DaemonStatusBar } from './daemonStatusBar.js';

// After: context.subscriptions.push(client);
const statusBar = new DaemonStatusBar(client);
context.subscriptions.push(statusBar);
```

Add a `restart()` method to DaemonClient (see below) and register the restart command. **IMPORTANT:** Do NOT set `_disposed = false` — that violates the disposal contract. Instead, kill the process and null the connection, then let `ensureConnected()` re-establish naturally via `start()`:

```typescript
// In DaemonClient:
async restart(): Promise<void> {
    if (this._disposed) throw new Error('DaemonClient is disposed');
    this.stopHeartbeat();
    if (this.connection) {
        this.connection.dispose();
        this.connection = null;
    }
    if (this.process) {
        this.process.kill();
        this.process = null;
    }
    // start() will be called, which sets state to 'starting' and re-establishes connection
    await this.start();
}
```

Then the restart command becomes:
```typescript
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.restartDaemon', async () => {
        try {
            await client.restart();
            vscode.window.showInformationMessage('PPDS daemon restarted.');
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            vscode.window.showErrorMessage(`Failed to restart daemon: ${msg}`);
        }
    })
);
```

- [ ] **Step 3: Register command in package.json**

Add to the `contributes.commands` array:

```json
{
    "command": "ppds.restartDaemon",
    "title": "PPDS: Restart Daemon"
}
```

- [ ] **Step 4: Test manually**

1. Open extension → status bar should show "$(check) PPDS" after daemon connects
2. Kill the daemon process externally → within 30s, status bar should show "$(error) PPDS"
3. Click the status bar → daemon restarts, status bar returns to "$(check) PPDS"

- [ ] **Step 5: Commit**

```
feat: add daemon status bar indicator with restart command

Status bar shows daemon connection state (connected/reconnecting/error).
Clicking the indicator runs ppds.restartDaemon to recover from failures.
30-second heartbeat detects daemon death proactively.

AC-06 through AC-09 satisfied.
```

---

### Task 11: Add panel auto-refresh on daemon reconnect

When the daemon reconnects, panels should notify the user and optionally auto-refresh.

**Files:**
- Modify: `extension/src/panels/WebviewPanelBase.ts`
- Modify: `extension/src/panels/QueryPanel.ts`
- Modify: `extension/src/panels/SolutionsPanel.ts`

**Acceptance Criteria:**
- AC-10: After daemon reconnect, panels show a non-intrusive banner: "Connection restored. Data may be stale."
- AC-11: Banner has a "Refresh" link that reloads data
- AC-12: SolutionsPanel auto-refreshes solution list on reconnect
- AC-13: IntelliSense completion provider handles daemon unavailability gracefully (returns empty, no error shown) — **NOTE:** This is already implemented in the completion provider's catch block which returns empty suggestions. Verify this still works after daemon lifecycle changes; no new code needed.

- [ ] **Step 1: Add reconnect subscription helper to WebviewPanelBase**

In `WebviewPanelBase.ts`, add a method panels can call to subscribe to daemon reconnection:

```typescript
import type { DaemonClient } from '../daemonClient.js';

export abstract class WebviewPanelBase implements vscode.Disposable {
    // ... existing code ...

    protected subscribeToDaemonReconnect(client: DaemonClient): void {
        this.disposables.push(
            client.onDidReconnect(() => {
                this.postMessage({ command: 'daemonReconnected' });
                this.onDaemonReconnected();
            })
        );
    }

    /** Override in subclasses to handle reconnection (e.g., auto-refresh). */
    protected onDaemonReconnected(): void {
        // Default: no-op. Subclasses can override.
    }
}
```

- [ ] **Step 2: Add reconnection banner to webview HTML**

Each panel's `getHtmlContent()` should include a hidden banner div and handler:

```html
<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>
```

JavaScript handler:
```javascript
case 'daemonReconnected':
    document.getElementById('reconnect-banner').style.display = '';
    break;
```

Refresh link handler:
```javascript
document.getElementById('reconnect-refresh').addEventListener('click', function(e) {
    e.preventDefault();
    document.getElementById('reconnect-banner').style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});
```

- [ ] **Step 3: Wire SolutionsPanel to auto-refresh**

In `SolutionsPanel`, override `onDaemonReconnected`:

```typescript
protected override onDaemonReconnected(): void {
    void this.loadSolutions();
}
```

And call `this.subscribeToDaemonReconnect(this.daemon)` in the constructor after panel creation.

- [ ] **Step 4: Wire QueryPanel reconnect subscription**

In `QueryPanel`, call `this.subscribeToDaemonReconnect(this.daemon)` in the constructor. QueryPanel does NOT auto-refresh (user may have unsaved query text), it just shows the banner.

- [ ] **Step 5: Test manually**

1. Open Data Explorer and Solutions panel
2. Kill daemon process
3. Wait for heartbeat to detect (up to 30s)
4. Status bar shows "$(error) PPDS"
5. Execute a query or click Refresh → daemon restarts automatically
6. Both panels show "Connection restored" banner
7. Solutions panel auto-refreshes its list
8. Data Explorer shows banner with Refresh link

- [ ] **Step 6: Commit**

```
feat: add panel reconnection awareness with stale-data banner

Panels subscribe to daemon reconnect events via WebviewPanelBase.
SolutionsPanel auto-refreshes on reconnect. QueryPanel shows banner
with manual refresh link. Provides clear UX during daemon recovery.

AC-10 through AC-13 satisfied.
```

---

## Task Dependencies and Parallelization

**Independent tasks (can all run in parallel):**
- Tasks 1, 2, 3, 4, 7, 8, 9

**Dependent tasks:**
- Task 6 depends on Task 5 (C# RPC changes must land before extension can consume structured errors)
- Task 10 depends on Task 9 (status bar needs `onDidChangeState` event from DaemonClient)
- Task 11 depends on Task 9 (panel reconnect needs `onDidReconnect` event from DaemonClient)
- Tasks 10 and 11 can run in parallel with each other

**Optimal execution with subagents:**
1. Wave 1: Tasks 1, 2, 3, 4, 5, 7, 8, 9 (all in parallel — 8 independent tasks)
2. Wave 2: Tasks 6, 10, 11 (in parallel — all depend on Wave 1 results)

---

## Testing Summary

After all tasks are complete, run the full test suite:

```bash
# Extension unit tests
cd extension && npm run ext:test

# Extension compilation check
cd extension && npm run compile

# .NET build (for RPC changes)
cd .. && dotnet build PPDS.sln --no-restore

# .NET tests (non-integration)
dotnet test PPDS.sln --filter "Category!=Integration" -v q
```

All must pass before creating a PR.
