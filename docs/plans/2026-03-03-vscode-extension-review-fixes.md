# VS Code Extension Review Fixes

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all critical and important issues found during code review of the VS Code extension MVP before creating the PR.

**Architecture:** Targeted fixes to existing files. No new features — only bug fixes, safety improvements, and correctness patches. All work in the `feature/vscode-extension-mvp` worktree.

**Tech Stack:** TypeScript (VS Code Extension), C# (.NET RPC handler), Vitest

**Worktree:** `/c/VS/ppdsw/ppds/.worktrees/vscode-extension-mvp`

---

## Task Summary

| Task | Priority | Description |
|------|----------|-------------|
| 1 | CRITICAL | Remove keybinding conflicts + fix notebook menu `when` clauses |
| 2 | CRITICAL | Fix race condition in DaemonClient.ensureConnected() |
| 3 | CRITICAL | Fix loadMore sending empty SQL in QueryPanel |
| 4 | CRITICAL | Handle useTds parameter in daemon (throw NotSupported) |
| 5 | CRITICAL | Fix Playwright E2E fixture to use @vscode/test-electron |
| 6 | IMPORTANT | Fix device code handler leak + listProfiles not switching |
| 7 | IMPORTANT | Fix tree provider disposal + duplicate command registration |
| 8 | IMPORTANT | Add manual URL entry to environment selector |
| 9 | IMPORTANT | Fix daemon error codes, explain endpoint, and export safety cap |
| 10 | IMPORTANT | Fix webview clipboard to use extension host |
| 11 | IMPORTANT | Add missing daemon client unit tests |
| 12 | MINOR | Extract duplicated helpers + cleanup |

---

## Task 1: Remove Keybinding Conflicts + Fix Notebook Menu `when` Clauses

**Files:**
- Modify: `extension/package.json`

**Step 1: Remove all keybindings**

Remove the entire `keybindings` array (lines 217-238) from `extension/package.json`. All four shortcuts conflict with core VS Code functionality:
- `Ctrl+Shift+D` → VS Code Debug sidebar
- `Ctrl+Shift+E` → VS Code Explorer sidebar
- `Ctrl+Shift+P` → VS Code Command Palette
- `Ctrl+Shift+N` → VS Code New Window

Users access commands via Command Palette, activity bar, status bar, and toolbar buttons. Users who want keyboard shortcuts configure them via VS Code's keybinding editor (`Ctrl+K Ctrl+S`).

Delete this entire block:
```json
"keybindings": [
  {
    "command": "ppds.dataExplorer",
    "key": "ctrl+shift+d",
    "when": "!editorFocus"
  },
  {
    "command": "ppds.selectEnvironment",
    "key": "ctrl+shift+e",
    "when": "!editorFocus"
  },
  {
    "command": "ppds.selectProfile",
    "key": "ctrl+shift+p",
    "when": "!editorFocus"
  },
  {
    "command": "ppds.newNotebook",
    "key": "ctrl+shift+n",
    "when": "!editorFocus"
  }
]
```

**Step 2: Fix notebook cell menu `when` clause quoting**

VS Code `when` clauses do not use quotes around string values. Remove single quotes from all four `notebook/cell/title` menu entries (lines 171-192).

Change:
```json
"when": "notebookType == 'ppdsnb'"
```
To:
```json
"when": "notebookType == ppdsnb"
```

Apply to all four entries:
- `ppds.toggleNotebookCellLanguage`
- `ppds.openCellInDataExplorer`
- `ppds.exportCellResultsCsv`
- `ppds.exportCellResultsJson`

Also fix any other `when` clauses that reference `ppdsnb` with quotes — search the entire `package.json` for `'ppdsnb'`.

**Step 3: Verify build**

```bash
cd src/PPDS.Extension && npm run compile
```
Expected: Builds without errors.

**Step 4: Commit**

```bash
git add src/PPDS.Extension/package.json
git commit -m "fix(extension): remove conflicting keybindings and fix notebook when clause quoting"
```

---

## Task 2: Fix Race Condition in DaemonClient.ensureConnected()

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts`
- Modify: `src/PPDS.Extension/src/__tests__/daemonClient.test.ts`

**Step 1: Add connecting promise guard**

In `src/PPDS.Extension/src/daemonClient.ts`, add a private field near the other private fields:

```typescript
private connectingPromise: Promise<void> | null = null;
```

Replace the `ensureConnected()` method (lines 494-503) with:

```typescript
private async ensureConnected(): Promise<void> {
    if (this.connection) return;
    if (!this.connectingPromise) {
        this.connectingPromise = this.start().finally(() => {
            this.connectingPromise = null;
        });
    }
    await this.connectingPromise;
}
```

This ensures that if two RPC calls arrive concurrently during startup, only one daemon process is spawned. The second caller awaits the same promise.

**Step 2: Add test for concurrent connection**

Add to `src/PPDS.Extension/src/__tests__/daemonClient.test.ts`:

```typescript
it('should not spawn multiple daemon processes on concurrent calls', async () => {
    // Call two methods concurrently
    const [result1, result2] = await Promise.all([
        client.authList(),
        client.authWho(),
    ]);

    // start() should only have been called once
    expect(mockConnection.sendRequest).toHaveBeenCalledTimes(2);
    // The spawn should have only happened once (ensureConnected guard)
});
```

**Step 3: Also reset connectingPromise on dispose**

In the `dispose()` method, add:
```typescript
this.connectingPromise = null;
```

**Step 4: Commit**

```bash
git add src/PPDS.Extension/src/daemonClient.ts src/PPDS.Extension/src/__tests__/daemonClient.test.ts
git commit -m "fix(extension): prevent race condition spawning multiple daemon processes"
```

---

## Task 3: Fix loadMore Sending Empty SQL in QueryPanel

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts`

**Step 1: Store the last executed SQL**

The `QueryPanel` class should already have a `lastSql` or similar field. If not, add one:

```typescript
private lastSql: string | undefined;
```

In the `executeQuery` method, save the SQL before executing:
```typescript
this.lastSql = sql;
```

**Step 2: Fix loadMore to use stored SQL**

Replace the `loadMore` method (lines 168-184). Change `sql: ''` to `sql: this.lastSql!`:

```typescript
private async loadMore(pagingCookie: string, page: number): Promise<void> {
    if (!this.lastResult || !this.lastSql) return;
    try {
        this.postMessage({ command: 'executionStarted' });
        const result = await this.daemon.querySql({
            sql: this.lastSql,
            page,
            pagingCookie,
        });
        this.lastResult = result;
        this.allRecords.push(...result.records);
        this.postMessage({ command: 'appendResults', data: result });
    } catch (error) {
        const msg = error instanceof Error ? error.message : String(error);
        this.postMessage({ command: 'queryError', error: msg });
    }
}
```

**Step 3: Commit**

```bash
git add src/PPDS.Extension/src/panels/QueryPanel.ts
git commit -m "fix(extension): pass original SQL to loadMore pagination instead of empty string"
```

---

## Task 4: Handle useTds Parameter in Daemon

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Throw NotSupported when useTds is true**

In `QuerySqlAsync` (line ~845, after the `sql` validation check), add:

```csharp
if (useTds)
{
    throw new RpcException(
        ErrorCodes.Operation.NotSupported,
        "TDS Read Replica mode is not yet implemented. Use the standard FetchXML execution path.");
}
```

This is better than silently ignoring the parameter. The extension UI can catch this error and show a message. When TDS is implemented later, this guard is removed.

**Step 2: Run daemon tests**

```bash
dotnet test tests/PPDS.Cli.DaemonTests --filter Category!=Integration
```
Expected: All pass.

**Step 3: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "fix(daemon): throw NotSupported for useTds parameter until TDS routing is implemented"
```

---

## Task 5: Fix Playwright E2E Fixture

**Files:**
- Modify: `extension/e2e/fixtures.ts`
- Modify: `extension/package.json` (add @vscode/test-electron dev dep)

**Step 1: Replace raw Electron launch with @vscode/test-electron**

The current fixture uses `electron.launch()` with the `code` shell wrapper, which won't work. Replace with `@vscode/test-electron` which is the official VS Code testing approach.

```bash
cd src/PPDS.Extension && npm install -D @vscode/test-electron
```

Replace `extension/e2e/fixtures.ts` entirely:

```typescript
import { test as base } from '@playwright/test';
import { downloadAndUnzipVSCode, resolveCliArgsFromVSCodeExecutablePath } from '@vscode/test-electron';
import { _electron as electron } from '@playwright/test';
import * as path from 'path';

/**
 * Custom test fixtures for VS Code extension E2E testing.
 * Downloads VS Code if needed, launches with extension loaded.
 */
export const test = base.extend<{ vscodeApp: any }>({
    // eslint-disable-next-line no-empty-pattern
    vscodeApp: async ({}, use) => {
        const extensionPath = path.resolve(__dirname, '..');

        // Download VS Code if not cached
        const vscodeExecutablePath = await downloadAndUnzipVSCode();
        const [cliPath] = resolveCliArgsFromVSCodeExecutablePath(vscodeExecutablePath);

        // Resolve actual Electron binary (not the CLI wrapper)
        const electronPath = process.platform === 'win32'
            ? path.resolve(path.dirname(vscodeExecutablePath), 'Code.exe')
            : vscodeExecutablePath;

        const electronApp = await electron.launch({
            executablePath: electronPath,
            args: [
                '--extensionDevelopmentPath=' + extensionPath,
                '--disable-extensions',
                '--no-sandbox',
            ],
        });
        const window = await electronApp.firstWindow();
        await use(window);
        await electronApp.close();
    },
});

export { expect } from '@playwright/test';
```

**Step 2: Fix Playwright config trace setting**

In `extension/playwright.config.ts`, change `trace: 'on-first-retry'` to `trace: 'retain-on-failure'` since retries are set to 0:

```typescript
use: {
    trace: 'retain-on-failure',
},
```

**Step 3: Commit**

```bash
git add extension/e2e/fixtures.ts extension/playwright.config.ts src/PPDS.Extension/package.json src/PPDS.Extension/package-lock.json
git commit -m "fix(extension): use @vscode/test-electron for Playwright E2E fixtures"
```

---

## Task 6: Fix Device Code Handler Leak + listProfiles Not Switching

**Files:**
- Modify: `src/PPDS.Extension/src/commands/profileCommands.ts`

**Step 1: Make device code handler registration idempotent**

The `onDeviceCode` handler is registered inside `createProfile`, which means every profile creation adds another handler. Fix by registering once during command setup and making it idempotent.

Move the device code handler registration OUT of `createProfile` and into the command registration setup (top of the file, after daemon client is available). The `onDeviceCode` method should be called once:

```typescript
// Register device code handler ONCE (not per createProfile call)
daemonClient.onDeviceCode(async ({ userCode, verificationUrl, message }) => {
    const action = await vscode.window.showInformationMessage(
        message || `Enter code: ${userCode}`,
        { modal: false },
        'Open Browser', 'Copy Code'
    );
    if (action === 'Open Browser') {
        await vscode.env.openExternal(vscode.Uri.parse(verificationUrl));
    } else if (action === 'Copy Code') {
        await vscode.env.clipboard.writeText(userCode);
    }
});
```

Remove the `daemonClient.onDeviceCode(...)` block from inside the `createProfile` function (lines 355-370).

**Step 2: Fix listProfiles to actually switch profile**

In the `ppds.listProfiles` command handler (lines 49-85), after the user selects a profile from the QuickPick, call `authSelect` and refresh:

Replace:
```typescript
if (selected) {
    vscode.window.showInformationMessage(`Selected profile: ${selected.label}`);
}
```

With:
```typescript
if (selected) {
    try {
        await daemonClient.authSelect({ name: selected.label });
        refreshProfiles();
        vscode.window.showInformationMessage(`Switched to profile: ${selected.label}`);
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        vscode.window.showErrorMessage(`Failed to switch profile: ${message}`);
    }
}
```

**Step 3: Commit**

```bash
git add src/PPDS.Extension/src/commands/profileCommands.ts
git commit -m "fix(extension): register device code handler once and wire listProfiles to authSelect"
```

---

## Task 7: Fix Tree Provider Disposal + Duplicate Command

**Files:**
- Modify: `src/PPDS.Extension/src/extension.ts`

**Step 1: Add tree providers to subscriptions**

After the tree provider creation (lines 34-48), add both providers to subscriptions:

```typescript
context.subscriptions.push(profileTreeProvider, toolsTreeProvider);
```

**Step 2: Remove duplicate openDataExplorer command**

Remove the duplicate `ppds.openDataExplorer` registration (lines 123-126):

```typescript
// DELETE these lines:
const openDataExplorerCmd = vscode.commands.registerCommand('ppds.openDataExplorer', () => {
    QueryPanel.show(context.extensionUri, daemonClient);
});
context.subscriptions.push(openDataExplorerCmd);
```

If the tools tree view references `ppds.openDataExplorer`, update it to use `ppds.dataExplorer` instead (check `src/PPDS.Extension/src/views/toolsTreeView.ts` for the command reference and update it).

**Step 3: Verify no other references to ppds.openDataExplorer**

Search the codebase for `ppds.openDataExplorer` and update any remaining references to `ppds.dataExplorer`. Check `extension/package.json` commands array too.

**Step 4: Commit**

```bash
git add src/PPDS.Extension/src/extension.ts src/PPDS.Extension/src/views/toolsTreeView.ts src/PPDS.Extension/package.json
git commit -m "fix(extension): add tree provider disposal and remove duplicate dataExplorer command"
```

---

## Task 8: Add Manual URL Entry to Environment Selector

**Files:**
- Modify: `src/PPDS.Extension/src/commands/environmentCommands.ts`

**Step 1: Add "Enter URL manually..." option to environment QuickPick**

In the `selectEnvironment` command, add a special item at the end of the QuickPick items list:

```typescript
const manualEntry = {
    label: '$(link) Enter URL manually...',
    description: 'Connect to an environment not in the list',
    detail: '',
    picked: false,
    apiUrl: '__manual__',
    buttons: [],
};

const allItems = [...items, manualEntry];
```

Use `allItems` instead of `items` for the QuickPick.

**Step 2: Handle manual URL selection**

After the QuickPick resolves, check if the manual entry was selected:

```typescript
if (selected?.apiUrl === '__manual__') {
    const url = await vscode.window.showInputBox({
        title: 'Dataverse Environment URL',
        prompt: 'Enter the full URL (e.g., https://myorg.crm.dynamics.com)',
        placeHolder: 'https://myorg.crm.dynamics.com',
        validateInput: (value) => {
            if (!value.trim()) return 'URL is required';
            try {
                new URL(value.trim());
                return undefined;
            } catch {
                return 'Enter a valid URL';
            }
        },
    });
    if (!url) return;
    await daemonClient.envSelect(url.trim());
    // Update status bar and tree view...
    return;
}
```

**Step 3: Commit**

```bash
git add src/PPDS.Extension/src/commands/environmentCommands.ts
git commit -m "fix(extension): add manual URL entry option to environment selector"
```

---

## Task 9: Fix Daemon Error Codes, Explain Endpoint, and Export Safety

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Fix error codes in env/config/set**

At lines ~437 and ~449, change `ErrorCodes.Validation.RequiredField` to `ErrorCodes.Validation.InvalidValue` (or `ErrorCodes.Validation.InvalidArguments` — use whichever exists in `ErrorCodes.cs`):

```csharp
// For type validation:
throw new RpcException(
    ErrorCodes.Validation.InvalidArguments,
    $"Invalid environment type '{type}'. Valid values: {string.Join(", ", Enum.GetNames<EnvironmentType>())}");

// For color validation:
throw new RpcException(
    ErrorCodes.Validation.InvalidArguments,
    $"Invalid environment color '{color}'. Valid values: {string.Join(", ", Enum.GetNames<EnvironmentColor>())}");
```

First check what error codes exist — read `src/PPDS.Cli/Commands/Serve/ErrorCodes.cs` to find the correct code.

**Step 2: Add safety cap to query/export pagination**

In `QueryExportAsync`, add a max record constant and a guard in the pagination loop (lines ~1083-1099):

```csharp
const int MaxExportRecords = 100_000;

// Inside the do-while loop, after allRecords.AddRange:
if (allRecords.Count >= MaxExportRecords)
{
    break; // Safety cap to prevent OOM on very large exports
}
```

**Step 3: Extract SQL transpile helper to reduce duplication**

Create a private helper method used by `QuerySqlAsync`, `QueryExportAsync`, and `QueryExplainAsync`:

```csharp
private string TranspileSqlToFetchXml(string sql, int? top = null)
{
    TSqlStatement stmt;
    try
    {
        var parser = new QueryParser();
        stmt = parser.ParseStatement(sql);
    }
    catch (QueryParseException ex)
    {
        throw new RpcException(ErrorCodes.Query.ParseError, ex);
    }

    if (top.HasValue && stmt is SelectStatement selectStmt
        && selectStmt.QueryExpression is QuerySpecification querySpec)
    {
        querySpec.TopRowFilter = new TopRowFilter
        {
            Expression = new IntegerLiteral { Value = top.Value.ToString() }
        };
    }

    return new FetchXmlGenerator().Generate(stmt);
}
```

Then replace the duplicated parse+generate blocks in all three methods with calls to this helper.

**Step 4: Run daemon tests**

```bash
dotnet test tests/PPDS.Cli.DaemonTests --filter Category!=Integration
```

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "fix(daemon): correct error codes, add export safety cap, and extract SQL transpile helper"
```

---

## Task 10: Fix Webview Clipboard to Use Extension Host

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts`

**Step 1: Replace navigator.clipboard with message-based clipboard**

In the webview HTML script, `navigator.clipboard` may not work in all VS Code webview contexts. Replace all instances of `navigator.clipboard.writeText(...)` with a message post to the extension host:

In the webview script, replace clipboard calls (lines ~649, 686, 695, 708):

```javascript
// Instead of:
navigator.clipboard.writeText(text);

// Use:
vscode.postMessage({ command: 'copyToClipboard', text: text });
```

**Step 2: Handle the message in the extension host**

In the QueryPanel message handler, add a case for `copyToClipboard`:

```typescript
case 'copyToClipboard':
    await vscode.env.clipboard.writeText(message.text as string);
    break;
```

`vscode.env.clipboard.writeText()` is reliable in all contexts (local, remote, WSL, SSH).

**Step 3: Commit**

```bash
git add src/PPDS.Extension/src/panels/QueryPanel.ts
git commit -m "fix(extension): use extension host clipboard instead of navigator.clipboard in webview"
```

---

## Task 11: Add Missing Daemon Client Unit Tests

**Files:**
- Modify: `src/PPDS.Extension/src/__tests__/daemonClient.test.ts`

**Step 1: Add tests for all untested RPC methods**

The test file currently covers ~7 of 20 methods. Add tests for the remaining 13:

```typescript
// Environment methods
it('should call env/who', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ organizationName: 'Test Org' });
    const result = await client.envWho();
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/who');
    expect(result.organizationName).toBe('Test Org');
});

it('should call env/config/get', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ label: 'Dev' });
    const result = await client.envConfigGet('https://test.crm.dynamics.com');
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/config/get', { environmentUrl: 'https://test.crm.dynamics.com' });
});

it('should call env/config/set', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ saved: true });
    await client.envConfigSet({ environmentUrl: 'https://test.crm.dynamics.com', label: 'Dev' });
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/config/set', expect.objectContaining({ environmentUrl: 'https://test.crm.dynamics.com' }));
});

// Query feature methods
it('should call query/complete', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ items: [] });
    const result = await client.queryComplete({ sql: 'SELECT ', cursorOffset: 7 });
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/complete', { sql: 'SELECT ', cursorOffset: 7 });
});

it('should call query/history/list', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ entries: [] });
    const result = await client.queryHistoryList();
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/history/list', expect.any(Object));
});

it('should call query/history/delete', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ deleted: true });
    await client.queryHistoryDelete('id-123');
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/history/delete', { id: 'id-123' });
});

it('should call query/export', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ content: 'a,b\n1,2', format: 'csv', rowCount: 1 });
    const result = await client.queryExport({ sql: 'SELECT * FROM account', format: 'csv' });
    expect(result.format).toBe('csv');
});

it('should call query/explain', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ plan: '<fetch/>', format: 'fetchxml' });
    const result = await client.queryExplain('SELECT * FROM account');
    expect(result.plan).toBe('<fetch/>');
});

// Profile CRUD methods
it('should call profiles/create', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ index: 0, name: 'test' });
    await client.profilesCreate({ authMethod: 'devicecode' });
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/create', expect.objectContaining({ authMethod: 'devicecode' }));
});

it('should call profiles/delete', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ deleted: true });
    await client.profilesDelete({ name: 'test' });
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/delete', { name: 'test' });
});

it('should call profiles/rename', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ renamed: true });
    await client.profilesRename({ currentName: 'old', newName: 'new' });
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/rename', { currentName: 'old', newName: 'new' });
});

// Schema methods
it('should call schema/entities', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ entities: [] });
    const result = await client.schemaEntities();
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('schema/entities');
});

it('should call schema/attributes', async () => {
    mockConnection.sendRequest.mockResolvedValueOnce({ entityName: 'account', attributes: [] });
    const result = await client.schemaAttributes('account');
    expect(mockConnection.sendRequest).toHaveBeenCalledWith('schema/attributes', { entity: 'account' });
});
```

Adapt the exact mock setup and method signatures to match the actual `DaemonClient` implementation. Read `src/PPDS.Extension/src/daemonClient.ts` to verify parameter names.

**Step 2: Run tests**

```bash
cd src/PPDS.Extension && npx vitest run
```
Expected: All tests pass.

**Step 3: Commit**

```bash
git add src/PPDS.Extension/src/__tests__/daemonClient.test.ts
git commit -m "test(extension): add unit tests for all daemon client RPC methods"
```

---

## Task 12: Minor Cleanup

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts`
- Modify: `src/PPDS.Extension/src/panels/getWebviewContent.ts`
- Modify: `extension/esbuild.js`
- Modify: `src/PPDS.Extension/src/notebooks/DataverseNotebookController.ts`
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Fix escapeHtml consistency in QueryPanel webview**

In the webview script's `escapeHtml` function (line ~720), add single-quote escaping to match `notebookResultRenderer.ts`:

```javascript
function escapeHtml(str) {
    if (!str) return '';
    return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}
```

**Step 2: Change esbuild logLevel from 'silent' to 'warning'**

In `extension/esbuild.js` line 16:

```javascript
logLevel: 'warning',
```

**Step 3: Remove dead getWebviewContent function if unused**

Check if `getWebviewContent()` in `src/PPDS.Extension/src/panels/getWebviewContent.ts` is imported and used anywhere other than `getNonce()`. If only `getNonce` is used, either:
- Export only `getNonce` and remove the unused `getWebviewContent` function, OR
- Have `QueryPanel` use `getWebviewContent` instead of building HTML inline

**Step 4: Add comment about per-notebook environment limitation**

In `src/PPDS.Extension/src/notebooks/DataverseNotebookController.ts`, add a comment near `selectedEnvironmentUrl`:

```typescript
/**
 * Current environment URL. Shared across all notebooks in this session.
 * Each notebook persists its preference in metadata, and the controller
 * loads it when the notebook gains focus. However, queries always run
 * against this single active environment.
 * TODO: Support true per-notebook environment isolation.
 */
private selectedEnvironmentUrl: string | undefined;
```

**Step 5: Remove stale schema/list stub from daemon**

In `RpcMethodHandler.cs`, remove the `SchemaListAsync` method that still throws `NotSupported` (lines ~647-665). The `schema/entities` and `schema/attributes` endpoints replace it. If any client references `schema/list`, they should be updated to use the new endpoints.

**Step 6: Extract auto-save history helper in daemon**

Extract the duplicated fire-and-forget history save logic from `QuerySqlAsync` and `QueryFetchAsync` into a private helper:

```csharp
private void FireAndForgetHistorySave(string queryText, QueryResultResponse response)
{
    _ = Task.Run(async () =>
    {
        try
        {
            var historyService = _authServices.GetService<IQueryHistoryService>();
            if (historyService != null)
            {
                var store = _authServices.GetRequiredService<ProfileStore>();
                var collection = await store.LoadAsync(CancellationToken.None);
                var envUrl = collection.ActiveProfile?.Environment?.Url;
                if (envUrl != null)
                {
                    await historyService.AddQueryAsync(
                        envUrl, queryText,
                        rowCount: response.Count,
                        executionTimeMs: response.ExecutionTimeMs);
                }
            }
        }
        catch { /* silently ignore history save failures */ }
    });
}
```

Then replace the duplicated blocks in both methods with `FireAndForgetHistorySave(sql, response)`.

**Step 7: Run all tests**

```bash
cd src/PPDS.Extension && npx vitest run
dotnet test tests/PPDS.Cli.DaemonTests --filter Category!=Integration
```

**Step 8: Commit**

```bash
git add extension/ src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "fix: minor cleanup — escapeHtml consistency, esbuild logging, remove dead code, extract helpers"
```

---

## Verification

After all 12 tasks are complete:

1. **Build check:** `cd src/PPDS.Extension && npm run compile` — should succeed
2. **Extension tests:** `cd src/PPDS.Extension && npx vitest run` — all pass
3. **Daemon tests:** `dotnet test tests/PPDS.Cli.DaemonTests --filter Category!=Integration` — all pass
4. **Manual verification:** Launch extension dev host (`F5` in VS Code) and verify:
   - No keybinding conflicts (Ctrl+Shift+P opens Command Palette, not PPDS)
   - Notebook cell toolbar shows toggle/export buttons
   - Profile QuickPick switches active profile
   - Environment selector shows "Enter URL manually..." option
   - Query pagination (Load More) returns next page of results
   - TDS toggle shows "not yet implemented" error message
