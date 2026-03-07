# VS Code Extension Review Fixes (v2)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all critical, important, and suggestion-level issues found during the second comprehensive code review of the VS Code extension MVP before creating the PR.

**Architecture:** Targeted fixes to existing files. No new features — only bug fixes, safety improvements, correctness patches, and missing test coverage. All work in the `feature/vscode-extension-mvp` worktree.

**Tech Stack:** TypeScript (VS Code Extension), C# (.NET RPC handler), Vitest

**Worktree:** `/c/VS/ppdsw/ppds/.worktrees/vscode-extension-mvp`

---

## Task Summary

| Task | Priority | Description |
|------|----------|-------------|
| 1 | CRITICAL | Fix query history resolve/hide ordering (C1) |
| 2 | CRITICAL | Remove `shell: true` from daemon spawn (C3) |
| 3 | CRITICAL | Fix dispose lifecycle in QueryPanel + WebviewPanelBase (C4) |
| 4 | CRITICAL | Fix E2E fixture `--disable-extensions` + readiness gate (C8) |
| 5 | CRITICAL | Fix listProfiles authSelect for unnamed profiles (C9) |
| 6 | CRITICAL | Fix renameProfile for unnamed profiles (I6) |
| 7 | CRITICAL | Restructure virtual scroll XSS architecture (C5) |
| 8 | CRITICAL | Add daemon startup handshake + race condition fix (C6) |
| 9 | CRITICAL | Wire cancellation tokens in completionProvider (C7) |
| 10 | CRITICAL | Wire cancellation in notebook controller (C11) |
| 11 | CRITICAL | Refactor WithActiveProfileAsync to use pool manager (C2) |
| 12 | CRITICAL | Fix EnvWhoAsync null-forgiving operator race (C12) |
| 13 | CRITICAL | Document/mitigate plain-text secrets in ProfilesCreateAsync (C10) |
| 14 | IMPORTANT | Add re-auth recursion guard in QueryPanel (I1) |
| 15 | IMPORTANT | Switch to event delegation in QueryPanel results table (I2) |
| 16 | IMPORTANT | Fix Ctrl+C override swallowing native copy (S4) |
| 17 | IMPORTANT | Add error handling for environmentCommands configure button + pass URL (I7) |
| 18 | IMPORTANT | Fix dispose() not awaiting connectingPromise (I4) |
| 19 | IMPORTANT | Add error handling in query history delete + notebook controller selectEnvironment (I8, I10) |
| 20 | IMPORTANT | Fix concurrent cell execution AbortController overwrite (I11) |
| 21 | IMPORTANT | Add structural validation in notebook serializer + warning on corrupt files (I12) |
| 22 | IMPORTANT | Fix isAuthError heuristic false positives (I15) |
| 23 | IMPORTANT | Fix FetchXML sent as sql parameter in completionProvider (I9) |
| 24 | IMPORTANT | Add implements vscode.Disposable to tree providers (I5 from commands review) |
| 25 | IMPORTANT | Fix FireAndForgetHistorySave exception handling + cancellation (I17) |
| 26 | IMPORTANT | Fix TypeScript types to match C# DTOs (I19) |
| 27 | IMPORTANT | Fix Vitest module caching in smokeTest (I20) |
| 28 | IMPORTANT | Add missing .gitignore entries for test artifacts (S13) |
| 29 | SUGGESTION | Cleanup: rename getWebviewContent.ts, extract duplicated helpers, escapeHtml type safety, button constants (S1, S2, S5, I3 from webview, S6) |
| 30 | SUGGESTION | Add daemon client startup timeout + reduce verbose logging for query/complete (S7, S8) |
| 31 | SUGGESTION | Fix test mock data to match TypeScript interfaces (S9) |
| 32 | SUGGESTION | Add unit tests for commands, tree views, and C# DTOs (I7 from commands, S5 from webview, S12) |
| 33 | SUGGESTION | Add E2E tests that verify PPDS-specific elements (S15) |

---

## Task 1: Fix Query History resolve/hide Ordering

**Priority:** CRITICAL
**Files:** `extension/src/commands/queryHistoryCommand.ts`

**Problem:** `quickPick.hide()` fires `onDidHide` synchronously, which calls `resolve(undefined)` before `resolve(value)`. Both "Run this query" button and Enter-to-select are completely non-functional — they always return `undefined`.

**Step 1:** In the `onDidTriggerItemButton` handler, swap the order for the "Run this query" case:

```typescript
// BEFORE (broken):
} else if (buttonTooltip === 'Run this query') {
    quickPick.hide();
    resolve(entry.sql);
}

// AFTER (fixed):
} else if (buttonTooltip === 'Run this query') {
    resolve(entry.sql);
    quickPick.hide();
}
```

**Step 2:** In the `onDidAccept` handler, swap the order:

```typescript
// BEFORE (broken):
disposables.push(quickPick.onDidAccept(() => {
    const selected = quickPick.selectedItems[0];
    quickPick.hide();
    resolve(selected?.entry.sql);
}));

// AFTER (fixed):
disposables.push(quickPick.onDidAccept(() => {
    const selected = quickPick.selectedItems[0];
    resolve(selected?.entry.sql);
    quickPick.hide();
}));
```

**Step 3:** Commit.

---

## Task 2: Remove `shell: true` from Daemon Spawn

**Priority:** CRITICAL
**Files:** `extension/src/daemonClient.ts`

**Problem:** `shell: true` in `spawn()` passes through the system shell. Unnecessary attack surface — if `ppds.daemonPath` becomes user-configurable, this is command injection. `spawn` resolves PATH without a shell.

**Step 1:** Remove `shell: true` from the spawn options:

```typescript
// BEFORE:
this.process = spawn('ppds', ['serve'], {
    stdio: ['pipe', 'pipe', 'pipe'],
    shell: true
});

// AFTER:
this.process = spawn('ppds', ['serve'], {
    stdio: ['pipe', 'pipe', 'pipe'],
});
```

**Step 2:** Commit.

---

## Task 3: Fix Dispose Lifecycle in QueryPanel + WebviewPanelBase

**Priority:** CRITICAL
**Files:** `extension/src/panels/QueryPanel.ts`, `extension/src/panels/WebviewPanelBase.ts`

**Problem:** `onDidDispose` callback never calls `super.dispose()`, so `this.disposables` (holding the `onDidReceiveMessage` subscription) is never cleaned up. The `onDidDispose` registration itself isn't tracked. Every panel close leaks its message listener.

**Step 1:** Add a double-dispose guard to `WebviewPanelBase.dispose()`:

```typescript
private _disposed = false;

dispose(): void {
    if (this._disposed) return;
    this._disposed = true;

    this.panel?.dispose();

    for (const d of this.disposables) {
        d.dispose();
    }
    this.disposables = [];
}
```

**Step 2:** Override `dispose()` in `QueryPanel` to handle cleanup and call `super.dispose()`:

```typescript
dispose(): void {
    const idx = QueryPanel.instances.indexOf(this);
    if (idx >= 0) QueryPanel.instances.splice(idx, 1);
    this.lastSql = undefined;
    this.lastResult = undefined;
    this.allRecords = [];
    super.dispose();
}
```

**Step 3:** Wire `onDidDispose` to call `this.dispose()` and track the registration:

```typescript
this.disposables.push(
    this.panel.onDidDispose(() => this.dispose())
);
```

Remove the old inline `onDidDispose` callback.

**Step 4:** Commit.

---

## Task 4: Fix E2E Fixture + Readiness Gate

**Priority:** CRITICAL
**Files:** `extension/e2e/fixtures.ts`, `extension/e2e/smoke.spec.ts`, `extension/playwright.config.ts`

**Problem:** `--disable-extensions` prevents the extension under test from loading. No readiness gate after `firstWindow()`. Tests verify nothing PPDS-specific. Timeout too short.

**Step 1:** Fix fixture args — remove `--disable-extensions`, add temp user data dir:

```typescript
import * as os from 'os';
import * as fs from 'fs';

// In fixture setup:
const tmpUserDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ppds-e2e-'));

const electronApp = await electron.launch({
    executablePath: vscodeExecutablePath,
    args: [
        '--extensionDevelopmentPath=' + extensionPath,
        '--no-sandbox',
        '--disable-gpu',
        '--user-data-dir=' + tmpUserDataDir,
    ],
});
```

**Step 2:** Add a readiness gate after `firstWindow()`:

```typescript
const window = await electronApp.firstWindow();
await window.waitForSelector('[id="workbench.parts.editor"]', { timeout: 60000 });
await use(window);
```

**Step 3:** Add cleanup in try/finally:

```typescript
const electronApp = await electron.launch({...});
try {
    const window = await electronApp.firstWindow();
    await window.waitForSelector('[id="workbench.parts.editor"]', { timeout: 60000 });
    await use(window);
} finally {
    await electronApp.close();
    fs.rmSync(tmpUserDataDir, { recursive: true, force: true });
}
```

**Step 4:** Fix Electron binary resolution — remove the fragile platform-specific `Code.exe` resolution. Use `vscodeExecutablePath` directly. Remove unused `cliPath` variable.

**Step 5:** Increase Playwright timeout in `playwright.config.ts`:

```typescript
timeout: 120_000,
retries: 0,
workers: 1,
use: {
    trace: 'retain-on-failure',
},
outputDir: 'test-results',
```

**Step 6:** Commit.

---

## Task 5: Fix listProfiles authSelect for Unnamed Profiles

**Priority:** CRITICAL
**Files:** `extension/src/commands/profileCommands.ts`

**Problem:** When `p.name` is null, the QuickPick label becomes `"Profile ${p.index}"` — a display string. Then `authSelect({ name: "Profile 0" })` is called, which fails because that's not a real profile name.

**Step 1:** Store the original profile data on the QuickPick item:

```typescript
interface ProfileQuickPickItem extends vscode.QuickPickItem {
    profile: ProfileInfo;
}

const items: ProfileQuickPickItem[] = result.profiles.map(p => ({
    label: p.name ?? `Profile ${p.index}`,
    description: p.identity,
    detail: p.environment
        ? `${p.environment.displayName} (${p.authMethod})`
        : p.authMethod,
    picked: p.isActive,
    profile: p,
}));
```

**Step 2:** Use the stored profile data for authSelect:

```typescript
if (selected) {
    const p = (selected as ProfileQuickPickItem).profile;
    await daemonClient.authSelect(p.name ? { name: p.name } : { index: p.index });
}
```

**Step 3:** Commit.

---

## Task 6: Fix renameProfile for Unnamed Profiles

**Priority:** CRITICAL
**Files:** `extension/src/commands/profileCommands.ts`

**Problem:** `renameProfile` passes `index.toString()` as `currentName` for unnamed profiles — the daemon may not recognize a stringified index as a profile identifier.

**Step 1:** Update `runRenameProfile` to use index-based identification. Check the daemon's `profiles/rename` RPC signature. If it only accepts `currentName: string`, add an `index` parameter to the daemon client method and RPC handler. If the daemon already accepts index as string, add a comment documenting that behavior.

**Step 2:** If the daemon needs changes, update `RpcMethodHandler.cs` `ProfilesRenameAsync` to accept an optional `index` parameter alongside `currentName`, and look up by index when `currentName` is null.

**Step 3:** Update the TypeScript call site to handle both cases:

```typescript
const currentName = name ?? index.toString();
// If daemon supports index lookup:
await daemonClient.profilesRename(name ?? null, newName.trim(), name ? undefined : index);
```

**Step 4:** Commit.

---

## Task 7: Restructure Virtual Scroll XSS Architecture

**Priority:** CRITICAL
**Files:** `extension/src/notebooks/notebookResultRenderer.ts`, `extension/src/notebooks/virtualScrollScript.ts`

**Problem:** `prepareRowData` mixes escaped plain-text with raw HTML anchor tags in the same `string[][]`. The virtual scroll script injects all values via `innerHTML`. Any future unescaped code path becomes XSS. Additionally, `dataverseUrl` in anchor tags could contain `</script>`.

**Step 1:** Change `prepareRowData` to return structured cell data instead of pre-built HTML:

```typescript
interface CellData {
    text: string;       // Always plain text (NOT pre-escaped)
    url?: string;       // Optional link URL
}

function prepareRowData(
    records: Record<string, unknown>[],
    columns: QueryColumnInfo[],
    environmentUrl?: string
): CellData[][] {
    // ... return { text: displayText, url: linkUrl } for lookups
    // ... return { text: stringValue } for plain values
}
```

**Step 2:** Update `virtualScrollScript.ts` to handle escaping at render time:

```javascript
function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// In render():
for (let j = 0; j < cols; j++) {
    const cell = allRows[i][j];
    if (cell.url) {
        html += '<td class="data-cell"><a href="' + escapeHtml(cell.url) +
            '" target="_blank">' + escapeHtml(cell.text) + '</a></td>';
    } else {
        html += '<td class="data-cell">' + escapeHtml(cell.text) + '</td>';
    }
}
```

**Step 3:** Update `generateVirtualScrollScript` to pass row data safely. Use a `<script type="application/json">` data element instead of inlining JSON in a script tag to prevent `</script>` breakout:

```html
<script type="application/json" id="${containerId}-data">${JSON.stringify(rowData)}</script>
<script nonce="...">
(function() {
    const allRows = JSON.parse(document.getElementById('${containerId}-data').textContent);
    // ... virtual scroll logic
})();
</script>
```

**Step 4:** Update `notebookResultRenderer.ts` callers to work with the new `CellData` structure.

**Step 5:** Commit.

---

## Task 8: Add Daemon Startup Handshake + Race Condition Fix

**Priority:** CRITICAL
**Files:** `extension/src/daemonClient.ts`

**Problem:** If the daemon process exits immediately after spawn (binary not found, crash on startup), the `exit` handler fires asynchronously while `start()` continues, creating a connection object on a dead pipe. Requests hang until timeout.

**Step 1:** Add a startup handshake after `connection.listen()`. Race a ping request against the process exit event:

```typescript
private async start(): Promise<void> {
    // ... spawn process ...

    // Create a promise that rejects if the process exits during startup
    const exitPromise = new Promise<never>((_, reject) => {
        this.process!.on('exit', (code) => {
            reject(new Error(`Daemon exited during startup with code ${code}`));
        });
    });

    // ... create connection, listen ...

    // Race a health check against process exit
    try {
        await Promise.race([
            this.connection.sendRequest('auth/list', {}, /* timeout */ 10_000),
            exitPromise,
        ]);
    } catch (err) {
        // Cleanup on failure
        this.connection?.dispose();
        this.connection = null;
        this.process?.kill();
        this.process = null;
        throw err;
    }

    this.outputChannel.appendLine('Daemon connection established and verified.');
}
```

**Step 2:** Ensure the `exit` handler sets a flag that `ensureConnected` can check:

```typescript
this.process.on('exit', (code) => {
    this.outputChannel.appendLine(`Daemon exited with code ${code}`);
    this.connection?.dispose();
    this.connection = null;
    this.process = null;
    this.connectingPromise = null;  // Allow reconnection on next call
});
```

**Step 3:** Commit.

---

## Task 9: Wire Cancellation Tokens in completionProvider

**Priority:** CRITICAL
**Files:** `extension/src/providers/completionProvider.ts`

**Problem:** `CancellationToken` is received but never used. Every keystroke sends a concurrent RPC request with no cancellation or deduplication.

**Step 1:** Check the cancellation token before and after the RPC call:

```typescript
async provideCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    token: vscode.CancellationToken,
    _context: vscode.CompletionContext
): Promise<vscode.CompletionItem[] | null> {
    if (token.isCancellationRequested) return null;

    const text = document.getText();
    const offset = document.offsetAt(position);

    try {
        const result = await this.daemon.queryComplete({ sql: text, cursorOffset: offset });

        if (token.isCancellationRequested) return null;

        return result.items.map((item, index) => {
            // ... existing mapping
        });
    } catch {
        return null;
    }
}
```

**Step 2:** Commit. (Full RPC-level cancellation propagation via `vscode-jsonrpc` CancellationToken is deferred — the pre/post check eliminates most stale responses.)

---

## Task 10: Wire Cancellation in Notebook Controller

**Priority:** CRITICAL
**Files:** `extension/src/notebooks/DataverseNotebookController.ts`

**Problem:** `AbortController.signal` is never passed to daemon RPC calls. `execution.token` from VS Code notebook API is ignored. Interrupt only takes effect after the query finishes.

**Step 1:** Wire `execution.token` to the `AbortController`:

```typescript
const abortController = new AbortController();
this.activeExecutions.set(cellUri, abortController);

// Wire VS Code's execution cancellation token
const tokenDisposable = execution.token.onCancellationRequested(() => {
    abortController.abort();
});
```

**Step 2:** Check cancellation token before the RPC call and after it returns:

```typescript
if (abortController.signal.aborted) {
    execution.end(false, Date.now());
    return;
}

let result: QueryResultResponse;
if (isFetchXml) {
    result = await this.daemon.queryFetch({ fetchXml: content });
} else {
    result = await this.daemon.querySql({ sql: content });
}

if (abortController.signal.aborted) {
    execution.end(false, Date.now());
    tokenDisposable.dispose();
    return;
}
```

**Step 3:** Dispose the token listener in the `finally` block:

```typescript
finally {
    this.activeExecutions.delete(cellUri);
    tokenDisposable.dispose();
}
```

**Step 4:** Commit. (Full RPC-level cancellation propagation is deferred — same reasoning as Task 9.)

---

## Task 11: Refactor WithActiveProfileAsync to Use Pool Manager

**Priority:** CRITICAL
**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Problem:** Every call to `WithActiveProfileAsync` creates a new `ServiceProvider` with a new `DataverseConnectionPool`. The `_poolManager` exists for caching pools but is only used by `PluginsListAsync`. This violates connection pooling rules and is especially severe for `query/complete` called on every keystroke.

**Step 1:** Refactor `WithActiveProfileAsync` to use `_poolManager`:

```csharp
private async Task<T> WithActiveProfileAsync<T>(
    Func<IServiceProvider, CancellationToken, Task<T>> action,
    CancellationToken cancellationToken)
{
    var store = _authServices.GetRequiredService<ProfileStore>();
    var collection = await store.LoadAsync(cancellationToken);
    var profile = collection.ActiveProfile
        ?? throw new RpcException(-32001, "No active profile. Run 'ppds auth login' first.");
    var environment = profile.Environment
        ?? throw new RpcException(-32001, $"No environment selected for profile '{profile.Name ?? profile.Index.ToString()}'.");

    var pool = await _poolManager.GetOrCreatePoolAsync(
        profile.Name ?? profile.Index.ToString(),
        environment.Url,
        DaemonDeviceCodeHandler.CreateCallback(_rpc),
        cancellationToken);

    var serviceProvider = pool.ServiceProvider;
    return await action(serviceProvider, cancellationToken);
}
```

**Step 2:** Update `PluginsListAsync` to use `WithActiveProfileAsync` instead of its custom pool management, eliminating the inconsistent pattern.

**Step 3:** Verify `IDaemonConnectionPoolManager.GetOrCreatePoolAsync` returns a `ServiceProvider` or equivalent that provides `IDataverseConnectionPool`, `ICachedMetadataProvider`, `SqlCompletionEngine`, etc. If the pool manager returns a different abstraction, adapt the code to resolve services from it.

**Step 4:** Run existing integration tests to verify no regressions.

**Step 5:** Commit.

---

## Task 12: Fix EnvWhoAsync Null-Forgiving Operator Race

**Priority:** CRITICAL
**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Problem:** `collection.ActiveProfile!` and `profile.Environment!` use `!` to suppress null warnings. Between validation in `WithActiveProfileAsync` and the lambda body, concurrent RPC calls could modify the active profile on disk.

**Step 1:** Refactor `WithActiveProfileAsync` to pass the validated profile and environment into the lambda:

```csharp
private async Task<T> WithActiveProfileAsync<T>(
    Func<IServiceProvider, ProfileEntry, EnvironmentEntry, CancellationToken, Task<T>> action,
    CancellationToken cancellationToken)
{
    // ... validation ...
    return await action(serviceProvider, profile, environment, cancellationToken);
}
```

**Step 2:** Update all callers to accept the profile and environment from the lambda parameters instead of re-loading from the store.

**Step 3:** For `EnvWhoAsync` specifically, remove the redundant `ProfileStore.LoadAsync` call inside the lambda and use the passed-in profile/environment.

**Step 4:** Update the overload that doesn't need profile/environment to call the new signature and discard those params.

**Step 5:** Commit.

---

## Task 13: Document/Mitigate Plain-Text Secrets in ProfilesCreateAsync

**Priority:** CRITICAL
**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Problem:** `clientSecret`, `password`, and `certificatePassword` are plain-text JSON-RPC parameters. If trace logging is enabled, these are logged in plaintext.

**Step 1:** Add security documentation comments to the method:

```csharp
/// <summary>
/// Creates a new authentication profile.
/// </summary>
/// <remarks>
/// SECURITY: clientSecret, password, and certificatePassword are transmitted as plain-text
/// JSON-RPC parameters over the local stdio pipe. This is acceptable for local-only IPC
/// but these values MUST NOT be logged. The extension should prefer device code or
/// browser-based auth flows when possible.
/// </remarks>
```

**Step 2:** Ensure StreamJsonRpc trace logging (if any is configured) does NOT capture request parameters for `profiles/create`. Check if there is any request/response logging middleware and add an exclusion for this method.

**Step 3:** Add a `[SensitiveData]` attribute or equivalent marker to the parameters for future audit tooling.

**Step 4:** Commit.

---

## Task 14: Add Re-Auth Recursion Guard in QueryPanel

**Priority:** IMPORTANT
**Files:** `extension/src/panels/QueryPanel.ts`

**Step 1:** Add an `isRetry` parameter to `executeQuery`:

```typescript
private async executeQuery(sql: string, useTds?: boolean, isRetry = false): Promise<void> {
    // ... existing code ...

    // In the catch block for auth errors:
    if (isAuthError(error) && !isRetry) {
        // Show re-auth prompt
        // ...
        await this.executeQuery(sql, useTds, true);
        return;
    }
    // If isRetry, fall through to show error normally
}
```

**Step 2:** Commit.

---

## Task 15: Switch to Event Delegation in QueryPanel Results Table

**Priority:** IMPORTANT
**Files:** `extension/src/panels/QueryPanel.ts`

**Problem:** `renderTable` attaches click handlers to every `td` and `th` element. For 10K rows x 15 cols = 150K listener registrations.

**Step 1:** Replace per-element listeners with a single delegated handler on `resultsWrapper`:

```javascript
resultsWrapper.addEventListener('click', (e) => {
    const td = e.target.closest('td[data-row]');
    if (td) {
        // Handle cell click (select/deselect)
        const row = parseInt(td.dataset.row);
        const col = parseInt(td.dataset.col);
        // ... existing cell selection logic
        return;
    }
    const th = e.target.closest('th[data-col]');
    if (th) {
        // Handle header click (sort)
        const col = parseInt(th.dataset.col);
        // ... existing sort logic
        return;
    }
});

resultsWrapper.addEventListener('contextmenu', (e) => {
    const td = e.target.closest('td[data-row]');
    if (td) {
        e.preventDefault();
        // ... existing context menu logic
    }
});
```

**Step 2:** Add `data-row` and `data-col` attributes to `td` and `th` elements in the `renderTable` HTML generation.

**Step 3:** Remove the per-element `querySelectorAll('td').forEach(...)` and `querySelectorAll('th').forEach(...)` event binding loops.

**Step 4:** Commit.

---

## Task 16: Fix Ctrl+C Override Swallowing Native Copy

**Priority:** IMPORTANT
**Files:** `extension/src/panels/QueryPanel.ts`

**Step 1:** Only call `preventDefault()` when cells are actually selected, and exclude the filter input:

```javascript
if ((e.ctrlKey || e.metaKey) && e.key === 'c' &&
    document.activeElement !== sqlEditor &&
    document.activeElement !== filterInput) {
    if (selectedCells.size > 0) {
        e.preventDefault();
        copySelectedCells(e.shiftKey);
    }
}
```

**Step 2:** Commit.

---

## Task 17: Fix Environment Configure Button + Pass URL

**Priority:** IMPORTANT
**Files:** `extension/src/commands/environmentCommands.ts`

**Step 1:** Store the environment API URL on the QuickPick item and pass it to the configure command:

```typescript
interface EnvQuickPickItem extends vscode.QuickPickItem {
    apiUrl: string;
}

// In onDidTriggerItemButton:
disposables.push(quickPick.onDidTriggerItemButton(async (e) => {
    const envItem = e.item as EnvQuickPickItem;
    quickPick.hide();
    await vscode.commands.executeCommand('ppds.configureEnvironment', envItem.apiUrl);
}));
```

**Step 2:** Commit.

---

## Task 18: Fix dispose() Not Awaiting connectingPromise

**Priority:** IMPORTANT
**Files:** `extension/src/daemonClient.ts`

**Step 1:** Add a `_disposed` flag and make cleanup handle in-flight connections:

```typescript
private _disposed = false;

dispose(): void {
    this._disposed = true;
    this.outputChannel.appendLine('Disposing daemon client...');
    this.connectingPromise = null;

    if (this.connection) {
        this.connection.dispose();
        this.connection = null;
    }

    if (this.process) {
        this.process.kill();
        this.process = null;
    }

    this.outputChannel.dispose();
}
```

**Step 2:** Check `_disposed` in `start()` before assigning the connection:

```typescript
private async start(): Promise<void> {
    // ... spawn, create connection ...

    if (this._disposed) {
        connection.dispose();
        this.process?.kill();
        this.process = null;
        return;
    }

    this.connection = connection;
    this.connection.listen();
}
```

**Step 3:** Commit.

---

## Task 19: Add Error Handling in Query History Delete + selectEnvironment

**Priority:** IMPORTANT
**Files:** `extension/src/commands/queryHistoryCommand.ts`, `extension/src/notebooks/DataverseNotebookController.ts`

**Step 1:** Wrap query history delete in try/catch:

```typescript
if (confirm === 'Delete') {
    try {
        await daemon.queryHistoryDelete(entry.id);
        const refreshed = await daemon.queryHistoryList(undefined, 50);
        quickPick.items = refreshed.entries.map(e2 => { /* ... */ });
    } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        vscode.window.showErrorMessage(`Failed to delete history entry: ${msg}`);
    }
}
```

**Step 2:** Wrap `selectEnvironment` in try/catch in the notebook controller:

```typescript
private async selectEnvironment(): Promise<void> {
    try {
        // ... existing environment selection logic ...
        await this.daemon.envSelect(selected.apiUrl);
        this.selectedEnvironmentUrl = selected.apiUrl;
        // ... metadata update ...
    } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        vscode.window.showErrorMessage(`Failed to select environment: ${msg}`);
    }
}
```

**Step 3:** Commit.

---

## Task 20: Fix Concurrent Cell Execution AbortController Overwrite

**Priority:** IMPORTANT
**Files:** `extension/src/notebooks/DataverseNotebookController.ts`

**Step 1:** Abort existing execution before starting a new one for the same cell:

```typescript
// Before creating new AbortController:
const existing = this.activeExecutions.get(cellUri);
if (existing) {
    existing.abort();
}

const abortController = new AbortController();
this.activeExecutions.set(cellUri, abortController);
```

**Step 2:** Commit.

---

## Task 21: Add Structural Validation in Notebook Serializer

**Priority:** IMPORTANT
**Files:** `extension/src/notebooks/DataverseNotebookSerializer.ts`, `extension/src/notebooks/__tests__/DataverseNotebookSerializer.test.ts`

**Step 1:** Add structural guard before `parseNotebookData`:

```typescript
const data = JSON.parse(text);
if (!data || !Array.isArray(data.cells)) {
    vscode.window.showWarningMessage(
        'Could not parse notebook file. Starting with empty notebook.'
    );
    return this.createEmptyNotebook();
}
return this.parseNotebookData(data as NotebookFileData);
```

**Step 2:** Move the warning into the catch block as well:

```typescript
catch {
    vscode.window.showWarningMessage(
        'Could not parse notebook file. Starting with empty notebook.'
    );
    return this.createEmptyNotebook();
}
```

**Step 3:** Add test cases for valid-JSON-wrong-shape:

```typescript
it('handles valid JSON with unexpected structure', async () => {
    const notebook = await serializer.deserializeNotebook(
        encode('{"cells": "not-an-array", "metadata": 42}'), token
    );
    expect(notebook.cells).toHaveLength(1);
    expect(notebook.cells[0].languageId).toBe('sql');
});

it('handles JSON with null cells', async () => {
    const notebook = await serializer.deserializeNotebook(
        encode('{"cells": null}'), token
    );
    expect(notebook.cells).toHaveLength(1);
});
```

**Step 4:** Commit.

---

## Task 22: Fix isAuthError Heuristic False Positives

**Priority:** IMPORTANT
**Files:** `extension/src/utils/errorUtils.ts`

**Step 1:** Use word-boundary regex instead of `includes()`:

```typescript
export function isAuthError(error: unknown): boolean {
    const msg = error instanceof Error ? error.message : String(error);
    const lower = msg.toLowerCase();
    return (
        lower.includes('unauthorized') ||
        /\b401\b/.test(lower) ||
        lower.includes('token expired') ||
        lower.includes('session expired') ||
        lower.includes('authentication required') ||
        lower.includes('not authenticated')
    );
}
```

**Step 2:** Add a comment noting the daemon should return structured error codes long-term.

**Step 3:** Commit.

---

## Task 23: Fix FetchXML Sent as sql Parameter in completionProvider

**Priority:** IMPORTANT
**Files:** `extension/src/providers/completionProvider.ts`

**Step 1:** Check `document.languageId` and skip completions for FetchXML (or pass language context):

```typescript
async provideCompletionItems(
    document: vscode.TextDocument,
    position: vscode.Position,
    token: vscode.CancellationToken,
    _context: vscode.CompletionContext
): Promise<vscode.CompletionItem[] | null> {
    // IntelliSense only works for SQL — FetchXML completions not yet supported
    if (document.languageId !== 'sql') return null;

    if (token.isCancellationRequested) return null;
    // ... rest of implementation
}
```

**Step 2:** Alternatively, if the daemon's `query/complete` supports FetchXML, pass the language ID:

```typescript
const result = await this.daemon.queryComplete({
    sql: text,
    cursorOffset: offset,
    language: document.languageId,
});
```

Determine which approach is correct by checking the daemon's `QueryCompleteAsync` implementation. If it only handles SQL, use the early-return approach.

**Step 3:** Commit.

---

## Task 24: Add `implements vscode.Disposable` to Tree Providers

**Priority:** IMPORTANT
**Files:** `extension/src/views/profileTreeView.ts`, `extension/src/views/toolsTreeView.ts`

**Step 1:** Add the interface to both class declarations:

```typescript
export class ProfileTreeDataProvider
    implements vscode.TreeDataProvider<ProfileTreeItem>, vscode.Disposable {
```

```typescript
export class ToolsTreeDataProvider
    implements vscode.TreeDataProvider<ToolTreeItem>, vscode.Disposable {
```

**Step 2:** Commit.

---

## Task 25: Fix FireAndForgetHistorySave Exception Handling

**Priority:** IMPORTANT
**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1:** Accept a `CancellationToken` tied to daemon lifetime (not per-request) and log failures:

```csharp
private void FireAndForgetHistorySave(
    string queryText,
    QueryResultResponse response,
    CancellationToken daemonLifetime)
{
    _ = Task.Run(async () =>
    {
        try
        {
            var store = _authServices.GetRequiredService<ProfileStore>();
            var collection = await store.LoadAsync(daemonLifetime);
            // ... save logic ...
        }
        catch (OperationCanceledException) { /* daemon shutting down — expected */ }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to save query history entry");
        }
    }, daemonLifetime);
}
```

**Step 2:** Pass a daemon-scoped `CancellationToken` from the caller. If `RpcMethodHandler` has access to a host lifetime token, use it. Otherwise, create a `CancellationTokenSource` in the constructor that is cancelled on `Dispose()`.

**Step 3:** Commit.

---

## Task 26: Fix TypeScript Types to Match C# DTOs

**Priority:** IMPORTANT
**Files:** `extension/src/types.ts`

**Step 1:** Add missing fields to `SolutionInfoDto`:

```typescript
export interface SolutionInfoDto {
    id: string;
    uniqueName: string;
    friendlyName: string;
    version: string | null;
    isManaged: boolean;
    publisherName: string | null;
    description: string | null;
    createdOn: string | null;
    modifiedOn: string | null;
    installedOn: string | null;
}
```

**Step 2:** Add missing interfaces for `SolutionComponentsResponse`, `SolutionComponentInfoDto`, `PluginsListResponse`, and related plugin DTOs. Cross-reference against the C# DTOs in `RpcMethodHandler.cs`.

**Step 3:** Commit.

---

## Task 27: Fix Vitest Module Caching in smokeTest

**Priority:** IMPORTANT
**Files:** `extension/src/__tests__/integration/smokeTest.test.ts`

**Step 1:** Add `vi.resetModules()` in `beforeEach` to force fresh imports:

```typescript
beforeEach(() => {
    vi.clearAllMocks();
    vi.resetModules();
});
```

**Step 2:** Verify all tests still pass after the change.

**Step 3:** Commit.

---

## Task 28: Add Missing .gitignore Entries

**Priority:** IMPORTANT
**Files:** `.gitignore`

**Step 1:** Add test artifact directories:

```
# VS Code Extension test artifacts
extension/test-results/
extension/playwright-report/
extension/.vscode-test/
```

**Step 2:** Commit.

---

## Task 29: Cleanup — Rename, Extract Helpers, Type Safety

**Priority:** SUGGESTION
**Files:** `extension/src/panels/getWebviewContent.ts`, `extension/src/commands/queryHistoryCommand.ts`, `extension/src/panels/QueryPanel.ts`, `extension/src/commands/notebookCommands.ts`

**Step 1:** Rename `getWebviewContent.ts` to `webviewUtils.ts` (or move `getNonce()` into `WebviewPanelBase.ts` as a `protected static` method). Update imports.

**Step 2:** Extract duplicated date formatting and QuickPick item building in `queryHistoryCommand.ts` into a `buildHistoryItem` helper function.

**Step 3:** Harden `escapeHtml` in `QueryPanel.ts` to handle non-string input:

```javascript
function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    const s = String(str);
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
```

**Step 4:** Extract button constants in `queryHistoryCommand.ts` and use identity comparison:

```typescript
const RUN_BUTTON: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('play'), tooltip: 'Run this query' };
const COPY_BUTTON: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('copy'), tooltip: 'Copy SQL' };
const DELETE_BUTTON: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('trash'), tooltip: 'Delete' };

// Dispatch on identity:
if (e.button === RUN_BUTTON) { ... }
```

**Step 5:** Fix `toggleCellLanguage` in `notebookCommands.ts` to show a warning when transpilation fails:

```typescript
} catch {
    vscode.window.showWarningMessage('Could not transpile. Language toggled without conversion.');
    const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
    await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
}
```

**Step 6:** Commit.

---

## Task 30: Daemon Client Startup Timeout + Reduce Verbose Logging

**Priority:** SUGGESTION
**Files:** `extension/src/daemonClient.ts`

**Step 1:** Add a configurable startup timeout (default 30 seconds):

```typescript
private static readonly STARTUP_TIMEOUT_MS = 30_000;

// In start():
const timeoutPromise = new Promise<never>((_, reject) =>
    setTimeout(() => reject(new Error('Daemon startup timed out after 30s')), DaemonClient.STARTUP_TIMEOUT_MS)
);

await Promise.race([
    healthCheck,
    exitPromise,
    timeoutPromise,
]);
```

**Step 2:** Reduce logging verbosity for high-frequency methods:

```typescript
// In queryComplete():
// Don't log individual calls for high-frequency IntelliSense requests
const result = await this.sendRequest('query/complete', params);
return result;

// Add a helper for quiet calls:
private async sendRequestQuiet<T>(method: string, params: object): Promise<T> {
    await this.ensureConnected();
    return this.connection!.sendRequest(method, params) as Promise<T>;
}
```

**Step 3:** Commit.

---

## Task 31: Fix Test Mock Data to Match TypeScript Interfaces

**Priority:** SUGGESTION
**Files:** `extension/src/__tests__/daemonClient.test.ts`

**Step 1:** Update mock data objects to include all required fields from their respective TypeScript interfaces. Use `satisfies` operator where possible:

```typescript
const mockResult = {
    connectedAs: 'user@example.com',
    organizationName: 'Test Org',
    organizationId: 'org-123',
    url: 'https://test.crm.dynamics.com',
    uniqueName: 'testorg',
    version: '9.2.0.0',
    userId: 'user-789',
    businessUnitId: 'bu-001',
} satisfies EnvWhoResponse;
```

**Step 2:** Review all mock objects in the test file and add missing required fields.

**Step 3:** Commit.

---

## Task 32: Add Unit Tests for Commands, Tree Views, and C# DTOs

**Priority:** SUGGESTION
**Files:**
- Create: `extension/src/__tests__/commands/queryHistoryCommand.test.ts`
- Create: `extension/src/__tests__/views/profileTreeView.test.ts`
- Create: `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerDtoTests.cs`

**Step 1:** Add unit tests for `queryHistoryCommand.ts` covering:
- Correct resolve ordering (verifies Task 1 fix)
- Delete error handling (verifies Task 19 fix)
- Button dispatch logic

**Step 2:** Add unit tests for `profileTreeView.ts` covering:
- Tree item generation from profile data
- Refresh behavior
- Empty state

**Step 3:** Add C# DTO serialization round-trip tests for all new response types:
- `SchemaEntitiesResponse`
- `SchemaAttributesResponse`
- `QueryCompleteResponse`
- `QueryHistoryListResponse`
- `QueryExportResponse`
- `QueryExplainResponse`
- `EnvWhoResponse`
- `EnvConfigGetResponse`/`EnvConfigSetResponse`
- `ProfileCreateResponse`/`ProfileDeleteResponse`/`ProfileRenameResponse`

Follow the existing pattern in `RpcMethodHandlerTests.cs`.

**Step 4:** Commit.

---

## Task 33: Add PPDS-Specific E2E Tests

**Priority:** SUGGESTION
**Files:** `extension/e2e/smoke.spec.ts`

**Step 1:** Add tests that verify PPDS-specific elements (depends on Task 4 fixture fix):

```typescript
test('PPDS extension is active', async ({ page }) => {
    // Open Extensions view and verify PPDS appears
    await page.keyboard.press('Control+Shift+X');
    await page.waitForSelector('text=Power Platform Developer Suite', { timeout: 30000 });
});

test('PPDS commands are registered', async ({ page }) => {
    // Open Command Palette and search for PPDS commands
    await page.keyboard.press('Control+Shift+P');
    await page.type('.quick-input-widget input', 'PPDS');
    await expect(page.locator('text=PPDS: Open Data Explorer')).toBeVisible({ timeout: 10000 });
});

test('PPDS tree view is registered', async ({ page }) => {
    // Click PPDS activity bar icon and verify tree view renders
    const ppdsIcon = page.locator('[id="workbench.view.extension.ppds-explorer"]');
    if (await ppdsIcon.isVisible()) {
        await ppdsIcon.click();
        await expect(page.locator('text=Profiles')).toBeVisible({ timeout: 10000 });
    }
});
```

**Step 2:** Commit.
