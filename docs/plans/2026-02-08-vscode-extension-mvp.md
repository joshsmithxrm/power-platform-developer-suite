# VS Code Extension MVP — Full TUI Parity + Notebooks

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a production-ready VS Code extension with full feature parity to the TUI: authentication, environment management, SQL query execution with results, query history, FetchXML preview, export, and .ppdsnb notebook support — all powered by the `ppds serve` JSON-RPC daemon.

**Architecture:** Thin TypeScript UI shell → `ppds serve` daemon via JSON-RPC over stdio. All business logic stays in the .NET Application Services layer. The extension has zero direct Dataverse access. Webview panels use `@vscode/webview-ui-toolkit` web components for native VS Code look and feel. Notebooks use the VS Code Notebook API with cells executing via daemon RPC calls.

**Tech Stack:** TypeScript 5+, VS Code Extension API, `@vscode/webview-ui-toolkit`, `vscode-jsonrpc`, esbuild (bundler), Jest (tests)

**Reference Implementation:** `C:\VS\ppdsw\ppds-extension-archived\` — archived extension with notebook support, webview patterns, and virtual scrolling. Adapt patterns but route all execution through daemon RPC instead of direct API calls.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                   VS Code Extension                  │
│                                                      │
│  ┌──────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │ Activity  │  │   Webview    │  │   Notebook    │  │
│  │ Bar /     │  │   Panels     │  │   Controller  │  │
│  │ TreeViews │  │  (Toolkit)   │  │   (.ppdsnb)   │  │
│  └─────┬─────┘  └──────┬───────┘  └───────┬───────┘  │
│        │               │                  │          │
│        └───────┬───────┴──────────┬───────┘          │
│                │                  │                   │
│         ┌──────▼──────┐   ┌──────▼──────┐            │
│         │ DaemonClient │   │ Message     │            │
│         │ (JSON-RPC)   │   │ Protocol    │            │
│         └──────┬───────┘   └─────────────┘            │
└────────────────┼─────────────────────────────────────┘
                 │ stdio
         ┌───────▼────────┐
         │  ppds serve     │
         │  (RPC daemon)   │
         │  ┌────────────┐ │
         │  │ RpcMethod   │ │
         │  │ Handler     │ │
         │  └──────┬──────┘ │
         │         │        │
         │  ┌──────▼──────┐ │
         │  │ Application  │ │
         │  │ Services     │ │
         │  └──────────────┘ │
         └──────────────────┘
```

## Phasing Strategy

| Phase | Scope | Tasks |
|-------|-------|-------|
| **Phase 1** | Foundation | Build infrastructure, daemon client, activity bar, profile tree view |
| **Phase 2** | Auth & Environments | Profile management, environment discovery/selection, device code flow |
| **Phase 3** | Query Panel | SQL editor webview, query execution, results table, status bar |
| **Phase 4** | Query Features | History, FetchXML preview, export, EXPLAIN |
| **Phase 5** | Notebooks | .ppdsnb serializer, controller, cell execution, export |
| **Phase 6** | Polish | Error handling, re-auth flow, keyboard shortcuts, settings |

---

## Phase 1: Foundation

### Task 1: Project Setup & Build Infrastructure

**Files:**
- Modify: `extension/package.json`
- Create: `extension/esbuild.js`
- Create: `extension/.eslintrc.json` (update existing eslint config if needed)
- Modify: `extension/tsconfig.json`

**Step 1: Install dependencies**

```bash
cd extension
npm install @vscode/webview-ui-toolkit vscode-jsonrpc@^8.2.0
npm install -D esbuild @types/vscode@^1.85.0 @vscode/test-electron jest ts-jest @types/jest
```

**Step 2: Create esbuild bundler config**

Create `extension/esbuild.js`:
```js
const esbuild = require('esbuild');
const production = process.argv.includes('--production');

async function main() {
    const ctx = await esbuild.context({
        entryPoints: ['src/extension.ts'],
        bundle: true,
        format: 'cjs',
        minify: production,
        sourcemap: !production,
        sourcesContent: false,
        platform: 'node',
        outfile: 'dist/extension.js',
        external: ['vscode'],
        logLevel: 'silent',
    });
    if (process.argv.includes('--watch')) {
        await ctx.watch();
    } else {
        await ctx.rebuild();
        await ctx.dispose();
    }
}
main().catch(e => { console.error(e); process.exit(1); });
```

**Step 3: Update package.json scripts**

Update `extension/package.json` scripts to use esbuild:
```json
{
  "scripts": {
    "vscode:prepublish": "npm run package",
    "compile": "node esbuild.js",
    "watch": "node esbuild.js --watch",
    "package": "node esbuild.js --production",
    "lint": "eslint src",
    "test": "jest",
    "test:watch": "jest --watch"
  }
}
```

**Step 4: Update tsconfig.json**

```json
{
  "compilerOptions": {
    "module": "Node16",
    "target": "ES2022",
    "outDir": "dist",
    "rootDir": "src",
    "lib": ["ES2022"],
    "sourceMap": true,
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noImplicitReturns": true,
    "moduleResolution": "Node16",
    "esModuleInterop": true,
    "skipLibCheck": true
  },
  "include": ["src"],
  "exclude": ["node_modules", "dist", "**/*.test.ts"]
}
```

**Step 5: Verify build works**

```bash
cd extension && npm run compile
```
Expected: `dist/extension.js` created without errors.

**Step 6: Commit**

```bash
git add extension/package.json extension/esbuild.js extension/tsconfig.json extension/package-lock.json
git commit -m "build(extension): switch to esbuild bundler and add toolkit dependency"
```

---

### Task 2: Daemon Client — Full RPC Surface

Expand `DaemonClient` from 1 method to cover all existing daemon RPC methods, plus the new methods needed for TUI parity.

**Files:**
- Modify: `extension/src/daemonClient.ts`
- Create: `extension/src/types.ts` (shared TypeScript interfaces for all RPC responses)
- Create: `extension/src/__tests__/daemonClient.test.ts`

**Step 1: Create shared types file**

Create `extension/src/types.ts` with TypeScript interfaces mirroring the C# response DTOs in `RpcMethodHandler.cs:877-1531`. Every `[JsonPropertyName]` attribute maps to a TS property.

Key interfaces to define:
```typescript
// Auth
export interface AuthListResponse { activeProfile: string | null; activeProfileIndex: number | null; profiles: ProfileInfo[]; }
export interface ProfileInfo { index: number; name: string | null; identity: string; authMethod: string; cloud: string; environment: EnvironmentSummary | null; isActive: boolean; createdAt: string | null; lastUsedAt: string | null; }
export interface EnvironmentSummary { url: string; displayName: string; }
export interface AuthWhoResponse { index: number; name: string | null; authMethod: string; cloud: string; tenantId: string | null; username: string | null; objectId: string | null; applicationId: string | null; tokenExpiresOn: string | null; tokenStatus: string | null; environment: EnvironmentDetails | null; createdAt: string | null; lastUsedAt: string | null; }
export interface EnvironmentDetails { url: string; displayName: string; uniqueName: string | null; environmentId: string | null; organizationId: string | null; type: string | null; region: string | null; }
export interface AuthSelectResponse { index: number; name: string | null; identity: string; environment: string | null; }

// Environment
export interface EnvListResponse { filter: string | null; environments: EnvironmentInfo[]; }
export interface EnvironmentInfo { id: string; environmentId: string | null; friendlyName: string; uniqueName: string; apiUrl: string; url: string | null; type: string | null; state: string; region: string | null; version: string | null; isActive: boolean; }
export interface EnvSelectResponse { url: string; displayName: string; uniqueName: string | null; environmentId: string | null; resolutionMethod: string; }

// Query
export interface QueryResultResponse { success: boolean; entityName: string | null; columns: QueryColumnInfo[]; records: Record<string, unknown>[]; count: number; totalCount: number | null; moreRecords: boolean; pagingCookie: string | null; pageNumber: number; isAggregate: boolean; executedFetchXml: string | null; executionTimeMs: number; }
export interface QueryColumnInfo { logicalName: string; alias: string | null; displayName: string | null; dataType: string; linkedEntityAlias: string | null; }

// Profiles
export interface ProfilesInvalidateResponse { profileName: string; invalidated: boolean; }

// Solutions
export interface SolutionsListResponse { solutions: SolutionInfoDto[]; }
export interface SolutionInfoDto { id: string; uniqueName: string; friendlyName: string; version: string | null; isManaged: boolean; publisherName: string | null; description: string | null; }

// Query History (new — daemon endpoint to be added)
export interface QueryHistoryEntry { sql: string; executedAt: string; rowCount: number; environmentUrl: string; }
export interface QueryHistoryResponse { entries: QueryHistoryEntry[]; }

// Export (new — daemon endpoint to be added)
export interface ExportRequest { sql: string; format: 'csv' | 'tsv' | 'json'; includeHeaders: boolean; }
export interface ExportResponse { content: string; format: string; rowCount: number; }
```

**Step 2: Expand DaemonClient with all RPC methods**

Modify `extension/src/daemonClient.ts` to add:
```typescript
// Auth methods
async authList(): Promise<AuthListResponse>
async authWho(): Promise<AuthWhoResponse>
async authSelect(params: { index?: number; name?: string }): Promise<AuthSelectResponse>

// Environment methods
async envList(filter?: string): Promise<EnvListResponse>
async envSelect(environment: string): Promise<EnvSelectResponse>

// Query methods
async querySql(params: { sql: string; top?: number; page?: number; pagingCookie?: string; count?: boolean; showFetchXml?: boolean }): Promise<QueryResultResponse>
async queryFetch(params: { fetchXml: string; top?: number; page?: number; pagingCookie?: string; count?: boolean }): Promise<QueryResultResponse>

// Profile management
async profilesInvalidate(profileName: string): Promise<ProfilesInvalidateResponse>

// Solutions
async solutionsList(filter?: string, includeManaged?: boolean): Promise<SolutionsListResponse>

// Device code notification handler
onDeviceCode(handler: (params: { userCode: string; verificationUrl: string; message: string }) => void): void
```

The `onDeviceCode` method should register a JSON-RPC notification handler using `connection.onNotification('auth/deviceCode', handler)`.

Add auto-reconnect logic: if the daemon process dies, the next RPC call should restart it.

**Step 3: Write daemon client unit tests**

Create `extension/src/__tests__/daemonClient.test.ts`:
- Test that `ensureConnected()` is called before each RPC method
- Test that `onDeviceCode` registers notification handler
- Test dispose cleanup

**Step 4: Commit**

```bash
git add extension/src/types.ts extension/src/daemonClient.ts extension/src/__tests__/
git commit -m "feat(extension): expand daemon client with full RPC surface"
```

---

### Task 3: Activity Bar & Profile Tree View

**Files:**
- Modify: `extension/package.json` (contributes section)
- Create: `extension/src/views/profileTreeView.ts`
- Create: `extension/src/views/toolsTreeView.ts`
- Modify: `extension/src/extension.ts`

**Step 1: Update package.json with activity bar contributions**

Add to `extension/package.json` contributes:
```json
{
  "viewsContainers": {
    "activitybar": [{
      "id": "ppds",
      "title": "Power Platform Developer Suite",
      "icon": "images/activity-bar-icon.svg"
    }]
  },
  "views": {
    "ppds": [
      { "id": "ppds.profiles", "name": "Profiles" },
      { "id": "ppds.tools", "name": "Tools" }
    ]
  }
}
```

**Step 2: Create ProfileTreeView**

Create `extension/src/views/profileTreeView.ts`:
- Implements `vscode.TreeDataProvider<ProfileTreeItem>`
- Calls `daemonClient.authList()` to populate tree
- Shows profile name, identity, auth method, environment
- Active profile marked with checkmark icon
- Context menu: Select, Details, Rename, Delete
- Refresh button in title bar

**Step 3: Create ToolsTreeView**

Create `extension/src/views/toolsTreeView.ts`:
- Static tree with items: Data Explorer, Notebooks, Solutions
- Each item opens the corresponding panel/command
- Disabled state if no profile/environment selected

**Step 4: Wire up in extension.ts**

Replace the single-command activation in `extension.ts` with:
- Activity bar views registration
- Tree view providers
- Activation on view open

**Step 5: Commit**

```bash
git add extension/src/views/ extension/src/extension.ts extension/package.json
git commit -m "feat(extension): add activity bar with profile and tools tree views"
```

---

## Phase 2: Auth & Environment Management

### Task 4: Profile Management Commands

**Files:**
- Create: `extension/src/commands/profileCommands.ts`
- Modify: `extension/package.json` (commands section)
- Modify: `extension/src/extension.ts`

**Step 1: Register profile management commands in package.json**

```json
{
  "commands": [
    { "command": "ppds.selectProfile", "title": "PPDS: Select Profile" },
    { "command": "ppds.profileDetails", "title": "PPDS: Profile Details" },
    { "command": "ppds.createProfile", "title": "PPDS: Create Profile", "icon": "$(add)" },
    { "command": "ppds.deleteProfile", "title": "PPDS: Delete Profile", "icon": "$(trash)" },
    { "command": "ppds.renameProfile", "title": "PPDS: Rename Profile", "icon": "$(edit)" },
    { "command": "ppds.refreshProfiles", "title": "PPDS: Refresh Profiles", "icon": "$(refresh)" }
  ]
}
```

**Step 2: Implement profile commands**

Create `extension/src/commands/profileCommands.ts`:

- `selectProfile`: QuickPick with all profiles → calls `daemonClient.authSelect()`
- `profileDetails`: Shows `authWho()` result in an information panel or QuickPick detail view
- `createProfile`:
  1. QuickPick for auth method (Device Code, Interactive Browser, Client Secret, Certificate File, Certificate Store)
  2. InputBox for profile name
  3. For SPN methods: InputBox chain for App ID, Secret/Cert, Tenant, Environment URL
  4. For user methods: trigger device code flow or browser auth
  5. Device code: show notification with user code + verification URL, listen for `auth/deviceCode` notification
- `deleteProfile`: Confirmation dialog → RPC call (needs new daemon endpoint `profiles/delete`)
- `renameProfile`: InputBox for new name → RPC call (needs new daemon endpoint `profiles/rename`)
- `refreshProfiles`: Refresh tree view

**Step 3: Wire commands in extension.ts**

Register all commands and connect to tree view context menu.

**Step 4: Commit**

```bash
git add extension/src/commands/profileCommands.ts extension/package.json extension/src/extension.ts
git commit -m "feat(extension): add profile management commands"
```

---

### Task 5: New Daemon RPC Endpoints — Profile CRUD

The TUI has full profile management. The daemon currently only has `auth/list`, `auth/who`, `auth/select`, and `profiles/invalidate`. We need:

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Add `profiles/create` RPC method**

```csharp
[JsonRpcMethod("profiles/create")]
public async Task<ProfileCreateResponse> ProfilesCreateAsync(
    string authMethod,
    string? name = null,
    string? applicationId = null,
    string? clientSecret = null,
    string? tenantId = null,
    string? environmentUrl = null,
    string? certificatePath = null,
    string? certificatePassword = null,
    string? certificateThumbprint = null,
    string? username = null,
    string? password = null,
    CancellationToken cancellationToken = default)
```

This should:
1. Parse `authMethod` to `AuthenticationMethod` enum
2. Build an `AuthProfile` from parameters
3. For device code / interactive: invoke the credential provider (device code sends `auth/deviceCode` notification via existing `DaemonDeviceCodeHandler`)
4. Save profile to store
5. Return created profile info

**Step 2: Add `profiles/delete` RPC method**

```csharp
[JsonRpcMethod("profiles/delete")]
public async Task<ProfileDeleteResponse> ProfilesDeleteAsync(
    int? index = null,
    string? name = null,
    CancellationToken cancellationToken = default)
```

**Step 3: Add `profiles/rename` RPC method**

```csharp
[JsonRpcMethod("profiles/rename")]
public async Task<ProfileRenameResponse> ProfilesRenameAsync(
    string currentName,
    string newName,
    CancellationToken cancellationToken = default)
```

**Step 4: Add response DTOs**

```csharp
public class ProfileCreateResponse { ... }
public class ProfileDeleteResponse { ... }
public class ProfileRenameResponse { ... }
```

**Step 5: Run existing daemon protocol tests**

```bash
dotnet test tests/PPDS.Cli.DaemonTests --filter Category!=Integration
```

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add profiles/create, profiles/delete, profiles/rename RPC methods"
```

---

### Task 6: Environment Management Commands

**Files:**
- Create: `extension/src/commands/environmentCommands.ts`
- Modify: `extension/package.json`
- Modify: `extension/src/extension.ts`

**Step 1: Register environment commands**

```json
{
  "commands": [
    { "command": "ppds.selectEnvironment", "title": "PPDS: Select Environment" },
    { "command": "ppds.environmentDetails", "title": "PPDS: Environment Details" },
    { "command": "ppds.refreshEnvironments", "title": "PPDS: Refresh Environments", "icon": "$(refresh)" }
  ]
}
```

**Step 2: Implement environment commands**

Create `extension/src/commands/environmentCommands.ts`:

- `selectEnvironment`:
  1. Show progress notification while loading
  2. Call `daemonClient.envList()`
  3. QuickPick with environments, showing: Name [Type] (Region)
  4. Selected → `daemonClient.envSelect(apiUrl)`
  5. Refresh profile tree to show updated environment
  6. Fire `ppds.onEnvironmentChanged` event

- `environmentDetails`:
  1. Call `daemonClient.authWho()`
  2. Show details in an information message or QuickPick with detail sections

- `refreshEnvironments`: Re-fetch environment list

**Step 3: Add environment status bar item**

Show current environment in VS Code status bar (bottom):
```typescript
const envStatusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
envStatusBar.command = 'ppds.selectEnvironment';
envStatusBar.text = '$(cloud) No environment';
```

Update when environment changes.

**Step 4: Commit**

```bash
git add extension/src/commands/environmentCommands.ts extension/package.json extension/src/extension.ts
git commit -m "feat(extension): add environment selection and status bar"
```

---

### Task 7: New Daemon Endpoint — `env/who` (WhoAmI)

The TUI's EnvironmentDetailsDialog calls WhoAmI to show org details. The daemon needs this.

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Add `env/who` RPC method**

```csharp
[JsonRpcMethod("env/who")]
public async Task<EnvWhoResponse> EnvWhoAsync(CancellationToken cancellationToken = default)
```

This should:
1. Get active profile + environment
2. Create service provider via pool
3. Call WhoAmI (like the TUI's EnvironmentDetailsDialog does)
4. Return org name, URL, unique name, version, org ID, user ID, BU ID

**Step 2: Add response DTO**

```csharp
public class EnvWhoResponse
{
    [JsonPropertyName("organizationName")] public string OrganizationName { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("uniqueName")] public string UniqueName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("organizationId")] public Guid OrganizationId { get; set; }
    [JsonPropertyName("userId")] public Guid UserId { get; set; }
    [JsonPropertyName("businessUnitId")] public Guid BusinessUnitId { get; set; }
    [JsonPropertyName("connectedAs")] public string ConnectedAs { get; set; } = "";
    [JsonPropertyName("environmentType")] public string? EnvironmentType { get; set; }
}
```

**Step 3: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add env/who RPC method for WhoAmI details"
```

---

## Phase 3: Query Panel (Webview)

### Task 8: Webview Infrastructure

**Files:**
- Create: `extension/src/panels/WebviewPanelBase.ts`
- Create: `extension/src/panels/getWebviewContent.ts`

**Step 1: Create base webview panel class**

Create `extension/src/panels/WebviewPanelBase.ts`:
```typescript
/**
 * Base class for webview panels with safe messaging (adapted from archived SafeWebviewPanel).
 * Prevents "Webview is disposed" errors from async operations.
 */
export abstract class WebviewPanelBase implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];

    protected postMessage(message: unknown): void {
        // Safe: no-op if panel is disposed
        this.panel?.webview.postMessage(message);
    }

    abstract getHtmlContent(webview: vscode.Webview): string;

    dispose(): void { ... }
}
```

**Step 2: Create webview HTML helper**

Create `extension/src/panels/getWebviewContent.ts`:
- Helper that generates HTML with `@vscode/webview-ui-toolkit` loaded
- Injects VS Code Toolkit `<script>` tag
- Sets Content-Security-Policy
- Includes nonce for script security

```typescript
export function getWebviewContent(webview: vscode.Webview, extensionUri: vscode.Uri, bodyHtml: string, scriptUri?: vscode.Uri): string {
    const toolkitUri = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
    );
    const nonce = getNonce();
    return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
    ${scriptUri ? `<script type="module" nonce="${nonce}" src="${scriptUri}"></script>` : ''}
</head>
<body>${bodyHtml}</body>
</html>`;
}
```

**Step 3: Commit**

```bash
git add extension/src/panels/
git commit -m "feat(extension): add webview panel base infrastructure"
```

---

### Task 9: SQL Query Panel — Editor & Execution

This is the core panel, equivalent to the TUI's `SqlQueryScreen`.

**Files:**
- Create: `extension/src/panels/QueryPanel.ts`
- Create: `extension/src/panels/webview/queryPanel.ts` (client-side script)
- Create: `extension/src/panels/webview/queryPanel.css`
- Modify: `extension/package.json` (add command)
- Modify: `extension/src/extension.ts`

**Step 1: Register Data Explorer command**

Add to `extension/package.json`:
```json
{ "command": "ppds.dataExplorer", "title": "PPDS: Data Explorer", "icon": "$(database)" }
```

**Step 2: Create QueryPanel class**

Create `extension/src/panels/QueryPanel.ts`:

```typescript
export class QueryPanel extends WebviewPanelBase {
    private static instance: QueryPanel | undefined;

    static show(extensionUri: vscode.Uri, daemon: DaemonClient): void {
        if (QueryPanel.instance) {
            QueryPanel.instance.panel?.reveal();
            return;
        }
        QueryPanel.instance = new QueryPanel(extensionUri, daemon);
    }

    constructor(private extensionUri: vscode.Uri, private daemon: DaemonClient) {
        // Create webview panel
        // Register message handlers for:
        //   'executeQuery' → daemon.querySql(params) → post 'queryResult' back
        //   'showFetchXml' → daemon.querySql({ showFetchXml: true }) → post 'fetchXmlResult' back
        //   'loadMore' → daemon.querySql({ page, pagingCookie }) → post 'appendResults' back
    }
}
```

**Step 3: Create client-side webview script**

Create `extension/src/panels/webview/queryPanel.ts`:

The webview HTML layout (using VS Code Toolkit components):
```html
<div class="query-panel">
    <!-- Toolbar -->
    <div class="toolbar">
        <vscode-button id="execute-btn" appearance="primary">
            <span slot="start" class="codicon codicon-play"></span>
            Execute (Ctrl+Enter)
        </vscode-button>
        <vscode-button id="fetchxml-btn" appearance="secondary">FetchXML</vscode-button>
        <vscode-button id="export-btn" appearance="secondary">Export</vscode-button>
        <vscode-button id="history-btn" appearance="secondary">History</vscode-button>
    </div>

    <!-- SQL Editor -->
    <div class="editor-container">
        <textarea id="sql-editor" placeholder="SELECT TOP 10 * FROM account" spellcheck="false"></textarea>
    </div>

    <!-- Results Table -->
    <div class="results-container">
        <vscode-data-grid id="results-grid"></vscode-data-grid>
    </div>

    <!-- Status Bar -->
    <div class="status-bar">
        <span id="status-text">Ready</span>
        <span id="row-count"></span>
        <span id="execution-time"></span>
    </div>
</div>
```

Message protocol between webview ↔ extension:
```typescript
// Webview → Extension
{ command: 'executeQuery', sql: string }
{ command: 'showFetchXml', sql: string }
{ command: 'loadMore', pagingCookie: string, page: number }
{ command: 'exportResults', format: 'csv' | 'tsv' | 'json' }
{ command: 'showHistory' }

// Extension → Webview
{ command: 'queryResult', data: QueryResultResponse }
{ command: 'appendResults', data: QueryResultResponse }
{ command: 'fetchXmlResult', fetchXml: string }
{ command: 'queryError', error: string }
{ command: 'executionStarted' }
{ command: 'executionComplete', rowCount: number, timeMs: number }
```

**Step 4: Handle keyboard shortcuts in webview**

In the webview script:
- `Ctrl+Enter` → execute query
- `Ctrl+Shift+F` → show FetchXML
- `Ctrl+E` → export

**Step 5: Wire up command in extension.ts**

```typescript
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.dataExplorer', () => {
        QueryPanel.show(context.extensionUri, daemonClient);
    })
);
```

**Step 6: Commit**

```bash
git add extension/src/panels/ extension/package.json extension/src/extension.ts
git commit -m "feat(extension): add SQL query panel with execution and results"
```

---

### Task 10: Results Table — Data Grid with Sorting & Copy

**Files:**
- Modify: `extension/src/panels/webview/queryPanel.ts`

**Step 1: Implement data grid rendering**

The `vscode-data-grid` component from the toolkit handles basic table rendering. Enhance with:
- Column headers from `QueryResultResponse.columns`
- Row data from `QueryResultResponse.records`
- Click-to-copy cell value
- Column sorting (client-side)
- Formatted value display (use `formatted` field from lookup values)
- Row count badge

**Step 2: Implement "Load More" pagination**

When `QueryResultResponse.moreRecords === true`:
- Show "Load More" button below grid
- On click: send `loadMore` message with `pagingCookie` and `page + 1`
- Append results to existing grid

**Step 3: Implement client-side filter**

Add filter input above results:
```html
<vscode-text-field id="filter-input" placeholder="Filter results..." type="text">
    <span slot="start" class="codicon codicon-filter"></span>
</vscode-text-field>
```

Filter rows client-side by matching any cell value.

**Step 4: Commit**

```bash
git add extension/src/panels/webview/
git commit -m "feat(extension): add results grid with sorting, pagination, and filtering"
```

---

## Phase 4: Query Features

### Task 11: New Daemon Endpoint — `query/history`

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Add `query/history/list` RPC method**

```csharp
[JsonRpcMethod("query/history/list")]
public async Task<QueryHistoryListResponse> QueryHistoryListAsync(
    string? search = null,
    int limit = 50,
    CancellationToken cancellationToken = default)
```

This should use the existing `IQueryHistoryService` from Application Services.

**Step 2: Add `query/history/delete` RPC method**

```csharp
[JsonRpcMethod("query/history/delete")]
public async Task<QueryHistoryDeleteResponse> QueryHistoryDeleteAsync(
    string id,
    CancellationToken cancellationToken = default)
```

**Step 3: Add response DTOs and commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add query/history RPC methods"
```

---

### Task 12: Query History Dialog in Extension

**Files:**
- Create: `extension/src/commands/queryHistoryCommand.ts`
- Modify: `extension/src/panels/QueryPanel.ts`

**Step 1: Implement history as QuickPick**

When user clicks History button or presses `Ctrl+Shift+H`:
1. Call `daemonClient.queryHistoryList()`
2. Show QuickPick with entries: `[MM/dd HH:mm] (N rows) SELECT...`
3. Search box filters entries
4. Selected entry → load SQL into query editor

**Step 2: Wire to query panel**

Handle `showHistory` message from webview → open QuickPick → send selected SQL back to webview.

**Step 3: Commit**

```bash
git add extension/src/commands/queryHistoryCommand.ts extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): add query history dialog"
```

---

### Task 13: FetchXML Preview

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

**Step 1: Implement FetchXML preview**

When user clicks FetchXML button or presses `Ctrl+Shift+F`:
1. Send current SQL to `daemonClient.querySql({ sql, showFetchXml: true })`
2. Get `executedFetchXml` from response
3. Open a new untitled document with `language: xml` and content = FetchXML
4. Or show in a side-by-side panel

Using VS Code's built-in XML highlighting:
```typescript
const doc = await vscode.workspace.openTextDocument({ content: fetchXml, language: 'xml' });
await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
```

**Step 2: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): add FetchXML preview"
```

---

### Task 14: New Daemon Endpoint — `query/export`

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

**Step 1: Add `query/export` RPC method**

```csharp
[JsonRpcMethod("query/export")]
public async Task<QueryExportResponse> QueryExportAsync(
    string sql,
    string format = "csv",
    bool includeHeaders = true,
    int? top = null,
    CancellationToken cancellationToken = default)
```

This should:
1. Execute the SQL query (reuse existing query pipeline)
2. Use `IExportService` to format results in requested format
3. Return the formatted content as a string

**Step 2: Add response DTO**

```csharp
public class QueryExportResponse
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("rowCount")] public int RowCount { get; set; }
}
```

**Step 3: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add query/export RPC method"
```

---

### Task 15: Export Dialog in Extension

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

**Step 1: Implement export flow**

When user clicks Export button or presses `Ctrl+E`:
1. QuickPick for format: CSV, TSV, JSON, Clipboard
2. For file formats:
   - Call `daemonClient.queryExport({ sql, format })`
   - Show save dialog with appropriate filter
   - Write content to file
3. For clipboard:
   - Call `daemonClient.queryExport({ sql, format: 'tsv' })`
   - Copy to clipboard via `vscode.env.clipboard.writeText()`
   - Show confirmation notification

**Step 2: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): add export dialog with CSV/TSV/JSON/clipboard"
```

---

### Task 16: EXPLAIN Support

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` (add `query/explain`)
- Modify: `extension/src/panels/QueryPanel.ts`

**Step 1: Add `query/explain` daemon endpoint**

```csharp
[JsonRpcMethod("query/explain")]
public async Task<QueryExplainResponse> QueryExplainAsync(
    string sql,
    CancellationToken cancellationToken = default)
```

Use existing `ISqlQueryService.ExplainAsync()`.

**Step 2: Add EXPLAIN button to query panel toolbar**

When clicked, show the execution plan in a read-only text document.

**Step 3: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs extension/src/panels/QueryPanel.ts
git commit -m "feat: add EXPLAIN query support to daemon and extension"
```

---

## Phase 5: Notebooks

> **Reference Implementation:** `C:\VS\ppdsw\ppds-extension-archived\src\features\dataExplorer\notebooks\`
> The archived extension has a complete, production-tested notebook implementation. Our version adapts these patterns but routes ALL execution through the `ppds serve` daemon instead of direct Dataverse API calls. This eliminates the need for TypeScript-side SQL parsers, FetchXML transpilers, metadata caches, and API services.

### Task 17: Notebook Serializer (.ppdsnb)

**Files:**
- Create: `extension/src/notebooks/DataverseNotebookSerializer.ts`
- Create: `extension/src/notebooks/__tests__/DataverseNotebookSerializer.test.ts`
- Modify: `extension/package.json` (notebook + language contributions)
- Modify: `extension/src/extension.ts`

**Step 1: Register notebook type and FetchXML language in package.json**

Add to `extension/package.json` contributes:
```json
{
  "notebooks": [{
    "type": "ppdsnb",
    "displayName": "Power Platform Developer Suite Notebook",
    "selector": [{ "filenamePattern": "*.ppdsnb" }]
  }],
  "languages": [{
    "id": "fetchxml",
    "aliases": ["FetchXML", "fetchxml"],
    "extensions": [".fetchxml"]
  }]
}
```

Also add activation event:
```json
{
  "activationEvents": ["onNotebook:ppdsnb"]
}
```

**Step 2: Create serializer**

Create `extension/src/notebooks/DataverseNotebookSerializer.ts`:

The .ppdsnb file format is a JSON document with environment metadata and cells. This is a direct port from the archived reference (`C:\VS\ppdsw\ppds-extension-archived\src\features\dataExplorer\notebooks\DataverseNotebookSerializer.ts`).

```typescript
import * as vscode from 'vscode';

/** JSON format for .ppdsnb notebook files. */
interface NotebookFileData {
    metadata: NotebookMetadata;
    cells: NotebookCellFileData[];
}

interface NotebookMetadata {
    /** Environment ID for reconnection when notebook is reopened */
    environmentId?: string;
    /** Environment display name for status bar */
    environmentName?: string;
    /** Environment URL for clickable record links in results */
    environmentUrl?: string;
}

interface NotebookCellFileData {
    kind: 'sql' | 'fetchxml' | 'markdown';
    source: string;
}

export class DataverseNotebookSerializer implements vscode.NotebookSerializer {

    async deserializeNotebook(content: Uint8Array, _token: vscode.CancellationToken): Promise<vscode.NotebookData> {
        const text = new TextDecoder().decode(content);

        // Handle empty or new files
        if (!text.trim()) {
            return this.createEmptyNotebook();
        }

        try {
            const data = JSON.parse(text) as NotebookFileData;
            return this.parseNotebookData(data);
        } catch {
            return this.createEmptyNotebook();
        }
    }

    async serializeNotebook(data: vscode.NotebookData, _token: vscode.CancellationToken): Promise<Uint8Array> {
        const notebookData: NotebookFileData = {
            metadata: this.extractMetadata(data),
            cells: data.cells.map(cell => this.serializeCell(cell)),
        };
        return new TextEncoder().encode(JSON.stringify(notebookData, null, 2));
    }

    private createEmptyNotebook(): vscode.NotebookData {
        const cell = new vscode.NotebookCellData(
            vscode.NotebookCellKind.Code,
            '-- Write your SQL query here\nSELECT TOP 10 * FROM account',
            'sql'
        );
        const notebookData = new vscode.NotebookData([cell]);
        notebookData.metadata = { environmentId: undefined, environmentName: undefined };
        return notebookData;
    }

    private parseNotebookData(data: NotebookFileData): vscode.NotebookData {
        const cells = data.cells.map(cellData => {
            const kind = cellData.kind === 'markdown'
                ? vscode.NotebookCellKind.Markup
                : vscode.NotebookCellKind.Code;

            const language = cellData.kind === 'markdown' ? 'markdown'
                : cellData.kind === 'fetchxml' ? 'fetchxml'
                : 'sql';

            return new vscode.NotebookCellData(kind, cellData.source, language);
        });

        // Ensure at least one cell exists
        if (cells.length === 0) {
            cells.push(new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code,
                '-- Write your SQL query here\nSELECT TOP 10 * FROM account',
                'sql'
            ));
        }

        const notebookData = new vscode.NotebookData(cells);
        notebookData.metadata = {
            environmentId: data.metadata.environmentId,
            environmentName: data.metadata.environmentName,
            environmentUrl: data.metadata.environmentUrl,
        };
        return notebookData;
    }

    private extractMetadata(data: vscode.NotebookData): NotebookMetadata {
        const metadata = data.metadata as NotebookMetadata | undefined;
        return {
            environmentId: metadata?.environmentId,
            environmentName: metadata?.environmentName,
            environmentUrl: metadata?.environmentUrl,
        };
    }

    private serializeCell(cell: vscode.NotebookCellData): NotebookCellFileData {
        let kind: NotebookCellFileData['kind'];
        if (cell.kind === vscode.NotebookCellKind.Markup) {
            kind = 'markdown';
        } else if (cell.languageId === 'fetchxml' || cell.languageId === 'xml') {
            kind = 'fetchxml';
        } else {
            kind = 'sql';
        }
        return { kind, source: cell.value };
    }
}
```

**Step 3: Register serializer in extension.ts**

```typescript
import { DataverseNotebookSerializer } from './notebooks/DataverseNotebookSerializer';

// In activate():
context.subscriptions.push(
    vscode.workspace.registerNotebookSerializer('ppdsnb', new DataverseNotebookSerializer(), {
        transientOutputs: true  // Don't persist cell outputs — re-execute to get results
    })
);
```

**Step 4: Write serializer round-trip tests**

Create `extension/src/notebooks/__tests__/DataverseNotebookSerializer.test.ts`:

```typescript
describe('DataverseNotebookSerializer', () => {
    it('round-trips SQL cells with metadata', async () => {
        const serializer = new DataverseNotebookSerializer();
        const original = new vscode.NotebookData([
            new vscode.NotebookCellData(vscode.NotebookCellKind.Code, 'SELECT * FROM account', 'sql'),
        ]);
        original.metadata = { environmentId: 'env-1', environmentName: 'Dev', environmentUrl: 'https://dev.crm.dynamics.com' };

        const bytes = await serializer.serializeNotebook(original, token);
        const deserialized = await serializer.deserializeNotebook(bytes, token);

        expect(deserialized.cells).toHaveLength(1);
        expect(deserialized.cells[0].value).toBe('SELECT * FROM account');
        expect(deserialized.cells[0].languageId).toBe('sql');
        expect(deserialized.metadata?.environmentId).toBe('env-1');
        expect(deserialized.metadata?.environmentUrl).toBe('https://dev.crm.dynamics.com');
    });

    it('round-trips FetchXML cells', async () => { /* ... */ });
    it('round-trips markdown cells', async () => { /* ... */ });
    it('handles empty files gracefully', async () => { /* ... */ });
    it('handles corrupt JSON gracefully', async () => { /* ... */ });
    it('ensures at least one cell exists', async () => { /* ... */ });
});
```

**Step 5: Commit**

```bash
git add extension/src/notebooks/ extension/package.json extension/src/extension.ts
git commit -m "feat(extension): add .ppdsnb notebook serializer with round-trip tests"
```

---

### Task 18: Notebook Controller — Cell Execution, Virtual Scrolling, Clickable Links

This is the most complex notebook task. The archived controller is 924 lines. Our version is simpler because execution goes through daemon RPC, but the rendering (virtual scrolling, clickable links, theme-aware CSS) is equally complex.

**Files:**
- Create: `extension/src/notebooks/DataverseNotebookController.ts`
- Create: `extension/src/notebooks/notebookResultRenderer.ts`
- Create: `extension/src/notebooks/virtualScrollScript.ts`
- Modify: `extension/src/extension.ts`

**Reference Files:**
- Controller: `C:\VS\ppdsw\ppds-extension-archived\src\features\dataExplorer\notebooks\DataverseNotebookController.ts`
- Virtual scroll: `C:\VS\ppdsw\ppds-extension-archived\src\shared\infrastructure\ui\virtualScroll\VirtualScrollScriptGenerator.ts`

#### Step 1: Create virtual scroll script generator

Create `extension/src/notebooks/virtualScrollScript.ts`:

This generates inline JavaScript for virtual scrolling in notebook cell output. Only visible rows are rendered in the DOM — spacer rows create visual space above and below.

```typescript
/** Configuration for virtual scroll rendering */
interface VirtualScrollConfig {
    rowHeight: number;       // Fixed height per row (36px)
    overscan: number;        // Extra rows above/below viewport (5)
    scrollContainerId: string;
    tbodyId: string;
    columnCount: number;
}

/**
 * Generates inline JavaScript for virtual scrolling.
 * Uses spacer-row approach: top spacer + visible rows + bottom spacer.
 * Only re-renders when visible range changes (lastStart/lastEnd caching).
 * Uses requestAnimationFrame for smooth scroll handling.
 *
 * @param rowDataJson - JSON.stringify'd string[][] of pre-rendered cell HTML
 * @param config - Scroll configuration
 */
export function generateVirtualScrollScript(rowDataJson: string, config: VirtualScrollConfig): string {
    return `
    (function() {
        const allRows = ${rowDataJson};
        const ROW_HEIGHT = ${config.rowHeight};
        const OVERSCAN = ${config.overscan};
        const COL_COUNT = ${config.columnCount};
        const container = document.getElementById('${config.scrollContainerId}');
        const tbody = document.getElementById('${config.tbodyId}');
        if (!container || !tbody) return;

        let lastStart = -1, lastEnd = -1;
        let rafId = null;

        function render() {
            const scrollTop = container.scrollTop;
            const viewportHeight = container.clientHeight;
            const totalRows = allRows.length;

            let start = Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN;
            let end = Math.ceil((scrollTop + viewportHeight) / ROW_HEIGHT) + OVERSCAN;
            start = Math.max(0, start);
            end = Math.min(totalRows, end);

            if (start === lastStart && end === lastEnd) return;
            lastStart = start;
            lastEnd = end;

            const topSpacerHeight = start * ROW_HEIGHT;
            const bottomSpacerHeight = (totalRows - end) * ROW_HEIGHT;

            let html = '';
            if (topSpacerHeight > 0) {
                html += '<tr class="virtual-spacer"><td colspan="' + COL_COUNT + '" style="height:' + topSpacerHeight + 'px"></td></tr>';
            }
            for (let i = start; i < end; i++) {
                const rowClass = i % 2 === 0 ? 'row-even' : 'row-odd';
                html += '<tr class="data-row ' + rowClass + '">';
                for (let j = 0; j < allRows[i].length; j++) {
                    html += '<td class="data-cell">' + allRows[i][j] + '</td>';
                }
                html += '</tr>';
            }
            if (bottomSpacerHeight > 0) {
                html += '<tr class="virtual-spacer"><td colspan="' + COL_COUNT + '" style="height:' + bottomSpacerHeight + 'px"></td></tr>';
            }
            tbody.innerHTML = html;
        }

        container.addEventListener('scroll', function() {
            if (rafId) cancelAnimationFrame(rafId);
            rafId = requestAnimationFrame(render);
        });
        render();
    })();`;
}
```

#### Step 2: Create result renderer

Create `extension/src/notebooks/notebookResultRenderer.ts`:

Handles HTML generation for notebook cell outputs. Separated from controller for testability.

```typescript
import { generateVirtualScrollScript } from './virtualScrollScript';
import type { QueryResultResponse, QueryColumnInfo } from '../types';

const ROW_HEIGHT = 36;
const OVERSCAN = 5;
const CONTAINER_HEIGHT = 400;

/**
 * Renders query results as HTML with virtual scrolling for notebook cell output.
 *
 * Features:
 * - Virtual scrolling (only visible rows in DOM) for large result sets
 * - Clickable lookup GUIDs that link to Dataverse records
 * - Clickable primary key column values that link to records
 * - VS Code theme-aware CSS using CSS variables
 * - Alternating row striping
 * - Sticky header row
 * - Row count and execution time summary
 */
export function renderResultsHtml(result: QueryResultResponse, environmentUrl: string | undefined): string {
    if (result.records.length === 0) {
        return renderEmptyResults();
    }

    const uniqueId = generateUniqueId();
    const scrollContainerId = `scrollContainer_${uniqueId}`;
    const tbodyId = `tableBody_${uniqueId}`;

    // Build header cells
    const headerCells = result.columns
        .map((col, idx) => {
            const isLast = idx === result.columns.length - 1;
            const label = col.alias ?? col.displayName ?? col.logicalName;
            return `<th class="header-cell${isLast ? ' last' : ''}">${escapeHtml(label)}</th>`;
        })
        .join('');

    // Prepare row data with clickable links
    const rowData = prepareRowData(result, environmentUrl);

    // Summary line
    const summary = `<div class="results-summary">${result.count} row${result.count !== 1 ? 's' : ''} returned in ${result.executionTimeMs}ms${result.moreRecords ? ' (more available)' : ''}</div>`;

    return `
        <style>${getNotebookStyles()}</style>
        ${summary}
        <div class="results-container">
            <div class="virtual-scroll-container" id="${scrollContainerId}">
                <table class="results-table">
                    <thead><tr>${headerCells}</tr></thead>
                    <tbody id="${tbodyId}"></tbody>
                </table>
            </div>
        </div>
        <script>${generateVirtualScrollScript(JSON.stringify(rowData), {
            rowHeight: ROW_HEIGHT,
            overscan: OVERSCAN,
            scrollContainerId,
            tbodyId,
            columnCount: result.columns.length
        })}</script>
    `;
}

/**
 * Prepares row data with pre-rendered HTML for each cell.
 * Lookup values become clickable links to Dataverse records.
 * Primary key columns (entityname + "id") also become clickable.
 */
function prepareRowData(result: QueryResultResponse, environmentUrl: string | undefined): string[][] {
    const primaryKeyColumn = result.entityName ? `${result.entityName}id` : null;

    return result.records.map(record => {
        return result.columns.map(col => {
            const key = col.alias ?? col.logicalName;
            const rawValue = record[key];

            if (rawValue === null || rawValue === undefined) {
                return '';
            }

            // Structured lookup value: { value, formatted, entityType, entityId }
            if (typeof rawValue === 'object' && rawValue !== null && 'entityId' in rawValue) {
                const lookup = rawValue as { value: unknown; formatted: string | null; entityType: string; entityId: string };
                const displayText = String(lookup.formatted ?? lookup.value ?? '');
                if (environmentUrl && lookup.entityType && lookup.entityId) {
                    const url = buildRecordUrl(environmentUrl, lookup.entityType, lookup.entityId);
                    return `<a href="${escapeHtml(url)}" target="_blank">${escapeHtml(displayText)}</a>`;
                }
                return escapeHtml(displayText);
            }

            // Structured formatted value: { value, formatted }
            if (typeof rawValue === 'object' && rawValue !== null && 'formatted' in rawValue) {
                const formatted = rawValue as { value: unknown; formatted: string | null };
                return escapeHtml(String(formatted.formatted ?? formatted.value ?? ''));
            }

            // Primary key column — make GUID clickable
            const stringValue = String(rawValue);
            if (primaryKeyColumn && environmentUrl && result.entityName
                && col.logicalName.toLowerCase() === primaryKeyColumn.toLowerCase()
                && isGuid(stringValue)) {
                const url = buildRecordUrl(environmentUrl, result.entityName, stringValue);
                return `<a href="${escapeHtml(url)}" target="_blank">${escapeHtml(stringValue)}</a>`;
            }

            return escapeHtml(stringValue);
        });
    });
}

/** Builds a Dataverse record URL: {base}/main.aspx?pagetype=entityrecord&etn={entity}&id={id} */
function buildRecordUrl(dataverseUrl: string, entityLogicalName: string, recordId: string): string {
    const baseUrl = dataverseUrl.replace(/\/+$/, '');
    return `${baseUrl}/main.aspx?pagetype=entityrecord&etn=${encodeURIComponent(entityLogicalName)}&id=${encodeURIComponent(recordId)}`;
}

function isGuid(value: string): boolean {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function escapeHtml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#039;');
}

function generateUniqueId(): string {
    return `${Date.now().toString(36)}_${Math.random().toString(36).substring(2, 8)}`;
}

function renderEmptyResults(): string {
    return `<style>.no-results { font-family: var(--vscode-font-family); padding: 20px; text-align: center; color: var(--vscode-descriptionForeground); font-style: italic; }</style><div class="no-results">No results returned</div>`;
}

/**
 * Theme-aware CSS using VS Code CSS variables.
 * Works in both light and dark themes automatically.
 */
function getNotebookStyles(): string {
    return `
        .results-summary {
            font-family: var(--vscode-font-family);
            color: var(--vscode-descriptionForeground);
            padding: 4px 0 8px 0;
            font-size: 12px;
        }
        .results-container {
            font-family: var(--vscode-font-family);
            color: var(--vscode-foreground);
            background: var(--vscode-editor-background);
            margin: 0; padding: 0;
        }
        .virtual-scroll-container {
            max-height: ${CONTAINER_HEIGHT}px;
            overflow-y: auto; overflow-x: auto;
            position: relative; margin: 0; padding: 0;
        }
        .results-table {
            width: max-content; min-width: 100%;
            border-collapse: collapse; margin: 0;
        }
        .header-cell {
            padding: 8px 12px; text-align: left; font-weight: 600;
            background: var(--vscode-button-background);
            color: var(--vscode-button-foreground);
            border-bottom: 2px solid var(--vscode-panel-border);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            white-space: nowrap; position: sticky; top: 0; z-index: 10;
        }
        .header-cell.last { border-right: none; }
        .data-row {
            height: ${ROW_HEIGHT}px;
            border-bottom: 1px solid var(--vscode-panel-border);
        }
        .data-row.row-even { background: var(--vscode-list-inactiveSelectionBackground); }
        .data-row.row-odd { background: transparent; }
        .data-row:hover { background: var(--vscode-list-hoverBackground); }
        .data-cell {
            padding: 8px 12px; white-space: nowrap;
            vertical-align: middle; text-align: left;
        }
        .data-cell a { color: var(--vscode-textLink-foreground); text-decoration: none; }
        .data-cell a:hover { color: var(--vscode-textLink-activeForeground); text-decoration: underline; }
        .virtual-spacer td { padding: 0 !important; border: none !important; }
    `;
}
```

#### Step 3: Create notebook controller

Create `extension/src/notebooks/DataverseNotebookController.ts`:

```typescript
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient';
import type { QueryResultResponse } from '../types';
import { renderResultsHtml } from './notebookResultRenderer';

/** Maximum content length to trigger auto-language-switch. */
const AUTO_SWITCH_THRESHOLD = 30;

export class DataverseNotebookController implements vscode.Disposable {
    private readonly controller: vscode.NotebookController;
    private readonly disposables: vscode.Disposable[] = [];

    // --- Environment state (per notebook) ---
    private selectedEnvironmentUrl: string | undefined;
    private selectedEnvironmentName: string | undefined;
    private statusBarItem: vscode.StatusBarItem;

    // --- Execution tracking ---
    /** Per-cell result cache for export commands. Keyed by cell document URI. */
    private readonly cellResults = new Map<string, QueryResultResponse>();
    /** Active cell executions for cancellation. Keyed by cell document URI. */
    private readonly activeExecutions = new Map<string, AbortController>();
    /** Flag to stop multi-cell execution loop when interrupt is requested. */
    private executionInterrupted = false;
    /** Monotonic execution order counter. */
    private executionOrder = 0;

    constructor(private readonly daemon: DaemonClient) {
        // --- Controller setup ---
        this.controller = vscode.notebooks.createNotebookController(
            'ppdsnb-controller', 'ppdsnb', 'Power Platform Developer Suite'
        );
        this.controller.supportedLanguages = ['sql', 'fetchxml'];
        this.controller.supportsExecutionOrder = true;
        this.controller.executeHandler = this.executeHandler.bind(this);
        this.controller.interruptHandler = this.interruptHandler.bind(this);

        // --- Status bar (notebook-scoped environment picker) ---
        this.statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
        this.statusBarItem.command = 'ppds.selectNotebookEnvironment';
        this.updateStatusBar();

        // --- Event listeners ---
        // Show/hide status bar when notebook editor changes
        this.disposables.push(
            vscode.window.onDidChangeActiveNotebookEditor(editor => {
                this.updateStatusBarVisibility(editor);
            })
        );

        // Load environment from metadata when notebook opens
        this.disposables.push(
            vscode.workspace.onDidOpenNotebookDocument(notebook => {
                if (notebook.notebookType === 'ppdsnb') {
                    this.loadEnvironmentFromNotebook(notebook);
                    this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
                }
            })
        );

        // Abort active queries when notebook closes
        this.disposables.push(
            vscode.workspace.onDidCloseNotebookDocument(notebook => {
                if (notebook.notebookType === 'ppdsnb') {
                    this.interruptHandler(notebook);
                }
            })
        );

        // Auto-switch cell language based on content
        this.registerAutoSwitchListener();

        // Check if a ppdsnb notebook is already open
        this.checkOpenNotebooks();
    }

    // ========== ENVIRONMENT MANAGEMENT ==========

    /** Prompts user to select environment via QuickPick. Updates notebook metadata. */
    async selectEnvironment(): Promise<void> {
        const envResult = await this.daemon.envList();
        if (envResult.environments.length === 0) {
            vscode.window.showErrorMessage('No environments found. Create a profile and select an environment first.');
            return;
        }

        const items = envResult.environments.map(env => ({
            label: env.friendlyName,
            description: `${env.type ?? ''} ${env.region ?? ''}`.trim(),
            detail: env.apiUrl,
            apiUrl: env.apiUrl,
            picked: env.isActive,
        }));

        const selected = await vscode.window.showQuickPick(items, {
            placeHolder: 'Select Dataverse environment for this notebook',
        });

        if (selected) {
            // Select environment in daemon (so queries use it)
            await this.daemon.envSelect(selected.apiUrl);
            this.selectedEnvironmentUrl = selected.apiUrl;
            this.selectedEnvironmentName = selected.label;
            this.updateStatusBar();

            // Persist to notebook metadata so it survives save/reopen
            const activeEditor = vscode.window.activeNotebookEditor;
            if (activeEditor?.notebook.notebookType === 'ppdsnb') {
                const edit = new vscode.WorkspaceEdit();
                edit.set(activeEditor.notebook.uri, [
                    vscode.NotebookEdit.updateNotebookMetadata({
                        ...activeEditor.notebook.metadata,
                        environmentName: this.selectedEnvironmentName,
                        environmentUrl: this.selectedEnvironmentUrl,
                    }),
                ]);
                await vscode.workspace.applyEdit(edit);
            }
        }
    }

    /** Loads environment from notebook metadata when notebook opens. */
    loadEnvironmentFromNotebook(notebook: vscode.NotebookDocument): void {
        const metadata = notebook.metadata as { environmentName?: string; environmentUrl?: string } | undefined;
        if (metadata?.environmentUrl) {
            this.selectedEnvironmentUrl = metadata.environmentUrl;
            this.selectedEnvironmentName = metadata.environmentName;
            this.updateStatusBar();
        }
    }

    /** Show/hide status bar based on whether a ppdsnb notebook is active. */
    updateStatusBarVisibility(editor: vscode.NotebookEditor | undefined): void {
        if (editor?.notebook.notebookType === 'ppdsnb') {
            this.loadEnvironmentFromNotebook(editor.notebook);
            this.statusBarItem.show();
        } else {
            this.statusBarItem.hide();
        }
    }

    private updateStatusBar(): void {
        this.statusBarItem.text = this.selectedEnvironmentName
            ? `$(database) ${this.selectedEnvironmentName}`
            : '$(database) Select Environment';
        this.statusBarItem.tooltip = this.selectedEnvironmentName
            ? `Dataverse: ${this.selectedEnvironmentName}\nClick to change`
            : 'Click to select Dataverse environment';
    }

    private checkOpenNotebooks(): void {
        for (const notebook of vscode.workspace.notebookDocuments) {
            if (notebook.notebookType === 'ppdsnb') {
                this.loadEnvironmentFromNotebook(notebook);
                this.statusBarItem.show();
                if (!this.selectedEnvironmentUrl) {
                    this.promptForEnvironment();
                }
                return;
            }
        }
    }

    private promptForEnvironment(): void {
        vscode.window.showInformationMessage(
            'Select a Dataverse environment to run queries.',
            'Select Environment'
        ).then(selection => {
            if (selection === 'Select Environment') {
                void this.selectEnvironment();
            }
        });
    }

    // ========== CELL EXECUTION ==========

    /** Execute handler — runs cells sequentially, respecting interrupt flag. */
    private async executeHandler(
        cells: vscode.NotebookCell[],
        _notebook: vscode.NotebookDocument,
        _controller: vscode.NotebookController
    ): Promise<void> {
        this.executionInterrupted = false;
        for (const cell of cells) {
            if (this.executionInterrupted) break;
            await this.executeCell(cell);
        }
    }

    /** Interrupt handler — aborts all active executions for the notebook. */
    private interruptHandler(notebook: vscode.NotebookDocument): void {
        this.executionInterrupted = true;
        for (const cell of notebook.getCells()) {
            const cellUri = cell.document.uri.toString();
            const abort = this.activeExecutions.get(cellUri);
            if (abort) {
                abort.abort();
                this.activeExecutions.delete(cellUri);
            }
        }
    }

    /** Executes a single cell — SQL or FetchXML — via daemon RPC. */
    private async executeCell(cell: vscode.NotebookCell): Promise<void> {
        const execution = this.controller.createNotebookCellExecution(cell);
        execution.executionOrder = ++this.executionOrder;
        execution.start(Date.now());

        const cellUri = cell.document.uri.toString();
        const abortController = new AbortController();
        this.activeExecutions.set(cellUri, abortController);

        try {
            // Validate environment
            if (!this.selectedEnvironmentUrl) {
                await this.selectEnvironment();
                if (!this.selectedEnvironmentUrl) {
                    execution.replaceOutput([new vscode.NotebookCellOutput([
                        vscode.NotebookCellOutputItem.text('No environment selected. Click the environment selector in the status bar.', 'text/plain'),
                    ])]);
                    execution.end(false, Date.now());
                    return;
                }
            }

            // Validate cell content
            const content = cell.document.getText().trim();
            if (!content) {
                execution.replaceOutput([new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text('Empty query', 'text/plain'),
                ])]);
                execution.end(true, Date.now());
                return;
            }

            // Detect language and execute via daemon
            const language = cell.document.languageId;
            const isFetchXml = language === 'fetchxml' || language === 'xml' || this.looksLikeFetchXml(content);

            let result: QueryResultResponse;
            if (isFetchXml) {
                result = await this.daemon.queryFetch({ fetchXml: content });
            } else {
                result = await this.daemon.querySql({ sql: content });
            }

            // Check for cancellation after query completes
            if (abortController.signal.aborted) {
                execution.replaceOutput([new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text('Query cancelled', 'text/plain'),
                ])]);
                execution.end(false, Date.now());
                return;
            }

            // Cache results for export commands
            this.cellResults.set(cellUri, result);

            // Render with virtual scrolling and clickable links
            const html = renderResultsHtml(result, this.selectedEnvironmentUrl);
            execution.replaceOutput([new vscode.NotebookCellOutput([
                vscode.NotebookCellOutputItem.text(html, 'text/html'),
            ])]);
            execution.end(true, Date.now());

        } catch (error) {
            if (abortController.signal.aborted) {
                execution.replaceOutput([new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text('Query cancelled', 'text/plain'),
                ])]);
                execution.end(false, Date.now());
                return;
            }

            // Show both structured error and plain text for visibility
            execution.replaceOutput([
                new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.error(error instanceof Error ? error : new Error(String(error))),
                ]),
            ]);
            execution.appendOutput([
                new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text(this.formatError(error), 'text/plain'),
                ]),
            ]);
            execution.end(false, Date.now());
        } finally {
            this.activeExecutions.delete(cellUri);
        }
    }

    private looksLikeFetchXml(content: string): boolean {
        const trimmed = content.trimStart().toLowerCase();
        return trimmed.startsWith('<fetch') || trimmed.startsWith('<?xml');
    }

    private formatError(error: unknown): string {
        if (error instanceof Error) return `Error: ${error.message}`;
        return `Error: ${String(error)}`;
    }

    // ========== AUTO LANGUAGE SWITCHING ==========

    /**
     * Detects cell content and auto-switches language:
     * - First non-whitespace char is '<' → FetchXML
     * - Otherwise → SQL
     * Only triggers for fresh cells (content < 30 chars) or paste operations (> 30 chars inserted at once).
     */
    private registerAutoSwitchListener(): void {
        this.disposables.push(
            vscode.workspace.onDidChangeTextDocument(event => {
                if (event.document.uri.scheme !== 'vscode-notebook-cell') return;

                // Verify this is one of our notebook cells
                const notebook = vscode.workspace.notebookDocuments.find(
                    nb => nb.notebookType === 'ppdsnb' &&
                          nb.getCells().some(c => c.document.uri.toString() === event.document.uri.toString())
                );
                if (!notebook) return;

                const content = event.document.getText().trim();
                if (!content) return;

                // Determine what to analyze: pasted content or typed content
                let contentToAnalyze: string;
                if (event.contentChanges.length === 1) {
                    const change = event.contentChanges[0];
                    if (change && change.text.length > AUTO_SWITCH_THRESHOLD && change.text.trim().length > 0) {
                        // Paste operation — analyze pasted content
                        contentToAnalyze = change.text.trim();
                    } else if (content.length <= AUTO_SWITCH_THRESHOLD) {
                        // Short typed content — analyze full cell
                        contentToAnalyze = content;
                    } else {
                        return; // Content too long for auto-switch
                    }
                } else if (content.length <= AUTO_SWITCH_THRESHOLD) {
                    contentToAnalyze = content;
                } else {
                    return;
                }

                const currentLanguage = event.document.languageId;
                const shouldBeFetchXml = contentToAnalyze.charAt(0) === '<';

                if (shouldBeFetchXml && currentLanguage !== 'fetchxml') {
                    void vscode.languages.setTextDocumentLanguage(event.document, 'fetchxml');
                } else if (!shouldBeFetchXml && currentLanguage === 'fetchxml') {
                    void vscode.languages.setTextDocumentLanguage(event.document, 'sql');
                }
            })
        );
    }

    // ========== EXPORT SUPPORT ==========

    /** Gets cached results for a cell (used by export commands). */
    getCellResults(cellUri: string): QueryResultResponse | undefined {
        return this.cellResults.get(cellUri);
    }

    /** Checks if a cell has cached results. */
    hasCellResults(cellUri: string): boolean {
        return this.cellResults.has(cellUri);
    }

    // ========== DISPOSAL ==========

    dispose(): void {
        this.controller.dispose();
        this.statusBarItem.dispose();
        for (const d of this.disposables) d.dispose();
    }
}
```

#### Step 4: Register controller in extension.ts

```typescript
import { DataverseNotebookController } from './notebooks/DataverseNotebookController';

// In activate():
const notebookController = new DataverseNotebookController(daemonClient);
context.subscriptions.push(notebookController);

// Register environment selection command for notebooks
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.selectNotebookEnvironment', () => notebookController.selectEnvironment())
);
```

#### Step 5: Commit

```bash
git add extension/src/notebooks/ extension/src/extension.ts
git commit -m "feat(extension): add notebook controller with virtual scrolling, clickable links, and cancellation"
```

---

### Task 19: Notebook Commands — New Notebook, Toggle Language, Export, Open in Data Explorer

**Files:**
- Create: `extension/src/commands/notebookCommands.ts`
- Modify: `extension/package.json` (commands + menus)
- Modify: `extension/src/extension.ts`

**Reference:** `C:\VS\ppdsw\ppds-extension-archived\src\features\dataExplorer\notebooks\registerNotebooks.ts:198-502`

#### Step 1: Register commands and menus in package.json

Add to `extension/package.json` contributes.commands:
```json
[
    { "command": "ppds.newNotebook", "title": "PPDS: New Notebook", "icon": "$(notebook)" },
    { "command": "ppds.toggleNotebookCellLanguage", "title": "PPDS: Toggle SQL/FetchXML", "icon": "$(arrow-swap)" },
    { "command": "ppds.openCellInDataExplorer", "title": "PPDS: Open in Data Explorer", "icon": "$(table)" },
    { "command": "ppds.exportCellResultsCsv", "title": "PPDS: Export Cell Results to CSV", "icon": "$(file)" },
    { "command": "ppds.exportCellResultsJson", "title": "PPDS: Export Cell Results to JSON", "icon": "$(json)" },
    { "command": "ppds.selectNotebookEnvironment", "title": "PPDS: Select Notebook Environment", "icon": "$(database)" }
]
```

Add to contributes.menus:
```json
{
    "notebook/cell/title": [
        { "command": "ppds.toggleNotebookCellLanguage", "when": "notebookType == 'ppdsnb'", "group": "inline@1" },
        { "command": "ppds.openCellInDataExplorer", "when": "notebookType == 'ppdsnb' && notebookCellType == 'code'", "group": "inline@2" },
        { "command": "ppds.exportCellResultsCsv", "when": "notebookType == 'ppdsnb' && notebookCellType == 'code'", "group": "export@1" },
        { "command": "ppds.exportCellResultsJson", "when": "notebookType == 'ppdsnb' && notebookCellType == 'code'", "group": "export@2" }
    ]
}
```

#### Step 2: Implement notebook commands

Create `extension/src/commands/notebookCommands.ts`:

```typescript
import * as vscode from 'vscode';
import type { DataverseNotebookController } from '../notebooks/DataverseNotebookController';
import type { DaemonClient } from '../daemonClient';

/**
 * Creates a new .ppdsnb notebook with example cells.
 * Includes: markdown header, SQL example, FetchXML example.
 */
export async function createNewNotebook(): Promise<void> {
    const notebook = await vscode.workspace.openNotebookDocument(
        'ppdsnb',
        new vscode.NotebookData([
            new vscode.NotebookCellData(
                vscode.NotebookCellKind.Markup,
                '# Power Platform Developer Suite Notebook\n\nSelect an environment using the status bar picker, then write SQL or FetchXML queries.\n\n**Tip:** Use the toggle button in the cell toolbar to convert between SQL and FetchXML.',
                'markdown'
            ),
            new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code,
                '-- SQL Example\nSELECT TOP 10\n    accountid,\n    name,\n    createdon\nFROM account\nORDER BY createdon DESC',
                'sql'
            ),
            new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code,
                '<!-- FetchXML Example -->\n<fetch top="10">\n  <entity name="account">\n    <attribute name="accountid" />\n    <attribute name="name" />\n    <attribute name="createdon" />\n    <order attribute="createdon" descending="true" />\n  </entity>\n</fetch>',
                'fetchxml'
            ),
        ])
    );
    await vscode.window.showNotebookDocument(notebook);
}

/**
 * Toggles active cell between SQL and FetchXML.
 * Uses daemon RPC to transpile content when toggling:
 * - SQL → FetchXML: calls query/sql with showFetchXml=true
 * - FetchXML → SQL: switches language only (no server-side FetchXML→SQL transpiler in daemon yet)
 *
 * Falls back to language-only switch if transpilation fails.
 */
export async function toggleCellLanguage(daemon: DaemonClient): Promise<void> {
    const editor = vscode.window.activeNotebookEditor;
    if (editor?.notebook.notebookType !== 'ppdsnb') return;

    const selections = editor.selections;
    const firstSelection = selections[0];
    if (!firstSelection) return;

    const cell = editor.notebook.cellAt(firstSelection.start);
    if (cell.kind !== vscode.NotebookCellKind.Code) return;

    const currentLanguage = cell.document.languageId;
    const content = cell.document.getText().trim();

    // Empty cell — just switch language
    if (!content) {
        const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
        await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
        return;
    }

    try {
        if (currentLanguage !== 'fetchxml' && !content.startsWith('<')) {
            // SQL → FetchXML: use daemon to transpile
            const result = await daemon.querySql({ sql: content, showFetchXml: true });
            if (result.executedFetchXml) {
                const edit = new vscode.WorkspaceEdit();
                const fullRange = new vscode.Range(
                    cell.document.positionAt(0),
                    cell.document.positionAt(cell.document.getText().length)
                );
                edit.replace(cell.document.uri, fullRange, result.executedFetchXml);
                await vscode.workspace.applyEdit(edit);
                await vscode.languages.setTextDocumentLanguage(cell.document, 'fetchxml');
                return;
            }
        }
        // FetchXML → SQL: no daemon transpiler for this direction yet, just switch language
        const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
        await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
    } catch {
        // Transpilation failed — still switch language
        const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
        await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
    }
}

/**
 * Opens a query from a notebook cell in the Data Explorer webview panel.
 */
export function openCellInDataExplorer(openQueryPanel: (sql: string) => void): void {
    const editor = vscode.window.activeNotebookEditor;
    if (editor?.notebook.notebookType !== 'ppdsnb') return;

    const selections = editor.selections;
    const firstSelection = selections[0];
    if (!firstSelection) return;

    const cell = editor.notebook.cellAt(firstSelection.start);
    if (cell.kind !== vscode.NotebookCellKind.Code) return;

    openQueryPanel(cell.document.getText());
}

/**
 * Exports cell results to CSV or JSON file.
 * Reads cached results from the controller (populated during cell execution).
 */
export async function exportCellResults(
    controller: DataverseNotebookController,
    format: 'csv' | 'json'
): Promise<void> {
    const editor = vscode.window.activeNotebookEditor;
    if (!editor || editor.notebook.notebookType !== 'ppdsnb') {
        vscode.window.showWarningMessage('This command is only available for Dataverse notebooks.');
        return;
    }

    const firstSelection = editor.selections[0];
    if (!firstSelection) { vscode.window.showWarningMessage('No cell selected.'); return; }

    const cell = editor.notebook.cellAt(firstSelection.start);
    if (cell.kind !== vscode.NotebookCellKind.Code) { vscode.window.showWarningMessage('Select a code cell.'); return; }

    const cellUri = cell.document.uri.toString();
    const results = controller.getCellResults(cellUri);
    if (!results) { vscode.window.showWarningMessage('No results to export. Execute the cell first.'); return; }
    if (results.records.length === 0) { vscode.window.showWarningMessage('Query returned no results.'); return; }

    // Generate content
    const entityName = results.entityName ?? 'query_results';
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    let content: string;
    let filename: string;
    let filterName: string;

    if (format === 'csv') {
        // Build CSV manually (no dependency needed for simple CSV)
        const headers = results.columns.map(c => c.alias ?? c.logicalName);
        const rows = results.records.map(record =>
            results.columns.map(col => {
                const key = col.alias ?? col.logicalName;
                const val = record[key];
                if (val === null || val === undefined) return '';
                if (typeof val === 'object' && 'formatted' in val) return String((val as { formatted: string }).formatted ?? '');
                return String(val);
            })
        );
        content = [headers, ...rows].map(row => row.map(cell => `"${cell.replace(/"/g, '""')}"`).join(',')).join('\n');
        filename = `${entityName}_${timestamp}.csv`;
        filterName = 'CSV Files';
    } else {
        const jsonArray = results.records.map(record => {
            const obj: Record<string, unknown> = {};
            for (const col of results.columns) {
                obj[col.alias ?? col.logicalName] = record[col.alias ?? col.logicalName];
            }
            return obj;
        });
        content = JSON.stringify(jsonArray, null, 2);
        filename = `${entityName}_${timestamp}.json`;
        filterName = 'JSON Files';
    }

    // Save dialog
    const uri = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.file(filename),
        filters: { [filterName]: [format] },
    });

    if (uri) {
        await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(content));
        vscode.window.showInformationMessage(`Exported ${results.records.length} rows to ${uri.fsPath}`);
    }
}

/**
 * Opens a query in a new notebook (called from Data Explorer "Open in Notebook").
 */
export async function openQueryInNotebook(
    sql: string,
    environmentName?: string,
    environmentUrl?: string
): Promise<void> {
    const isFetchXml = sql.trim().startsWith('<');
    const language = isFetchXml ? 'fetchxml' : 'sql';

    const notebookData = new vscode.NotebookData([
        new vscode.NotebookCellData(
            vscode.NotebookCellKind.Markup,
            `# Query Notebook\n\n**Environment:** ${environmentName ?? 'Not selected'}`,
            'markdown'
        ),
        new vscode.NotebookCellData(vscode.NotebookCellKind.Code, sql.trim(), language),
    ]);

    notebookData.metadata = { environmentName, environmentUrl };

    const notebook = await vscode.workspace.openNotebookDocument('ppdsnb', notebookData);
    await vscode.window.showNotebookDocument(notebook);
}
```

#### Step 3: Wire commands in extension.ts

```typescript
import {
    createNewNotebook, toggleCellLanguage, openCellInDataExplorer,
    exportCellResults, openQueryInNotebook
} from './commands/notebookCommands';

// In activate():
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.newNotebook', createNewNotebook),
    vscode.commands.registerCommand('ppds.toggleNotebookCellLanguage', () => toggleCellLanguage(daemonClient)),
    vscode.commands.registerCommand('ppds.openCellInDataExplorer', () => openCellInDataExplorer(sql => QueryPanel.show(context.extensionUri, daemonClient, sql))),
    vscode.commands.registerCommand('ppds.exportCellResultsCsv', () => exportCellResults(notebookController, 'csv')),
    vscode.commands.registerCommand('ppds.exportCellResultsJson', () => exportCellResults(notebookController, 'json')),
);
```

#### Step 4: Commit

```bash
git add extension/src/commands/notebookCommands.ts extension/package.json extension/src/extension.ts
git commit -m "feat(extension): add notebook commands — new, toggle language, export, open in data explorer"
```

---

## Phase 6: Polish

### Task 20: Device Code Flow UI

**Files:**
- Modify: `extension/src/commands/profileCommands.ts`

**Step 1: Implement device code notification handling**

When creating a profile with device code auth:
1. Register `onDeviceCode` handler on daemon client
2. When notification received, show VS Code notification with:
   - User code (large, copyable)
   - "Open Browser" button → opens verification URL
   - "Copy Code" button → copies to clipboard
3. Show progress notification while waiting for auth completion

```typescript
daemon.onDeviceCode(async ({ userCode, verificationUrl, message }) => {
    const action = await vscode.window.showInformationMessage(
        `Enter code: ${userCode}`,
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

**Step 2: Commit**

```bash
git add extension/src/commands/profileCommands.ts
git commit -m "feat(extension): add device code flow UI with browser open and clipboard"
```

---

### Task 21: Re-Authentication Flow

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`
- Modify: `extension/src/notebooks/DataverseNotebookController.ts`

**Step 1: Handle auth errors in query panel**

When a query fails with an auth error (RPC error code matching `Auth.*`):
1. Show error notification: "Session expired. Re-authenticate?"
2. If user clicks "Re-authenticate":
   - Invalidate profile cache: `daemon.profilesInvalidate(profileName)`
   - Retry the query
3. If user cancels: show error in results area

**Step 2: Handle auth errors in notebook controller**

Same flow but using cell execution error output.

**Step 3: Commit**

```bash
git add extension/src/panels/QueryPanel.ts extension/src/notebooks/DataverseNotebookController.ts
git commit -m "feat(extension): add re-authentication flow for expired sessions"
```

---

### Task 22: Keyboard Shortcuts & Keybindings

**Files:**
- Modify: `extension/package.json` (keybindings contribution)

**Step 1: Register keybindings**

```json
{
  "keybindings": [
    { "command": "ppds.dataExplorer", "key": "ctrl+shift+d", "when": "!editorFocus" },
    { "command": "ppds.selectEnvironment", "key": "ctrl+shift+e", "when": "!editorFocus" },
    { "command": "ppds.selectProfile", "key": "ctrl+shift+p", "when": "!editorFocus" },
    { "command": "ppds.newNotebook", "key": "ctrl+shift+n", "when": "!editorFocus" }
  ]
}
```

Note: `Ctrl+Enter` for query execution is handled inside the webview, not as a VS Code keybinding (webviews have their own keyboard handling).

**Step 2: Commit**

```bash
git add extension/package.json
git commit -m "feat(extension): add keyboard shortcuts"
```

---

### Task 23: Extension Settings

**Files:**
- Modify: `extension/package.json` (configuration contribution)

**Step 1: Add settings**

```json
{
  "configuration": {
    "title": "Power Platform Developer Suite",
    "properties": {
      "ppds.queryDefaultTop": {
        "type": "number",
        "default": 100,
        "minimum": 1,
        "maximum": 5000,
        "description": "Default TOP value for SQL queries when none specified"
      },
      "ppds.autoStartDaemon": {
        "type": "boolean",
        "default": true,
        "description": "Automatically start the ppds serve daemon when the extension activates"
      },
      "ppds.showEnvironmentInStatusBar": {
        "type": "boolean",
        "default": true,
        "description": "Show the active environment in the VS Code status bar"
      }
    }
  }
}
```

**Step 2: Read settings in extension code**

```typescript
const config = vscode.workspace.getConfiguration('ppds');
const defaultTop = config.get<number>('queryDefaultTop', 100);
```

**Step 3: Commit**

```bash
git add extension/package.json extension/src/extension.ts
git commit -m "feat(extension): add configurable settings"
```

---

### Task 24: Update DaemonClient TypeScript Types

Update `extension/src/daemonClient.ts` and `extension/src/types.ts` to include all new daemon RPC methods added in Tasks 5, 7, 11, 14, and 16.

**Files:**
- Modify: `extension/src/daemonClient.ts`
- Modify: `extension/src/types.ts`

**Step 1: Add TypeScript methods for new RPC endpoints**

```typescript
// Profile CRUD
async profilesCreate(params: ProfileCreateParams): Promise<ProfileCreateResponse>
async profilesDelete(params: { index?: number; name?: string }): Promise<ProfileDeleteResponse>
async profilesRename(params: { currentName: string; newName: string }): Promise<ProfileRenameResponse>

// Environment
async envWho(): Promise<EnvWhoResponse>

// Query features
async queryHistoryList(search?: string, limit?: number): Promise<QueryHistoryListResponse>
async queryHistoryDelete(id: string): Promise<QueryHistoryDeleteResponse>
async queryExport(params: ExportParams): Promise<QueryExportResponse>
async queryExplain(sql: string): Promise<QueryExplainResponse>
```

**Step 2: Add corresponding TypeScript interfaces to types.ts**

**Step 3: Commit**

```bash
git add extension/src/daemonClient.ts extension/src/types.ts
git commit -m "feat(extension): add TypeScript types for all new daemon RPC methods"
```

---

### Task 25: End-to-End Smoke Test

**Files:**
- Create: `extension/src/__tests__/integration/smokeTest.test.ts`

**Step 1: Write smoke test**

Test that:
1. Extension activates without error
2. Activity bar views register
3. Commands are registered
4. Notebook serializer handles round-trip
5. Empty query panel renders without crash

**Step 2: Commit**

```bash
git add extension/src/__tests__/
git commit -m "test(extension): add end-to-end smoke tests"
```

---

## Daemon RPC Surface Summary

### Existing (no changes needed)
| Method | Status |
|--------|--------|
| `auth/list` | Ready |
| `auth/who` | Ready |
| `auth/select` | Ready |
| `env/list` | Ready |
| `env/select` | Ready |
| `query/sql` | Ready |
| `query/fetch` | Ready |
| `plugins/list` | Ready |
| `solutions/list` | Ready |
| `solutions/components` | Ready |
| `profiles/invalidate` | Ready |
| `auth/deviceCode` (notification) | Ready |

### New (to be added in this plan)
| Method | Task | Purpose |
|--------|------|---------|
| `profiles/create` | Task 5 | Create new auth profile |
| `profiles/delete` | Task 5 | Delete auth profile |
| `profiles/rename` | Task 5 | Rename auth profile |
| `env/who` | Task 7 | WhoAmI environment details |
| `query/history/list` | Task 11 | List query history entries |
| `query/history/delete` | Task 11 | Delete history entry |
| `query/export` | Task 14 | Export query results |
| `query/explain` | Task 16 | Show query execution plan |

---

## TUI ↔ VS Code Parity Matrix

| TUI Feature | VS Code Equivalent | Task |
|-------------|-------------------|------|
| SqlQueryScreen | QueryPanel webview | Task 9-10 |
| ProfileSelectorDialog | Profile tree view + QuickPick | Task 3-4 |
| ProfileCreationDialog | Create profile command chain | Task 4-5 |
| ProfileDetailsDialog | auth/who QuickPick details | Task 4 |
| EnvironmentSelectorDialog | Environment QuickPick | Task 6 |
| EnvironmentDetailsDialog | env/who display | Task 6-7 |
| PreAuthenticationDialog | Device code notification | Task 20 |
| ReAuthenticationDialog | Re-auth notification | Task 21 |
| QueryHistoryDialog | History QuickPick | Task 12 |
| ExportDialog | Export command chain | Task 15 |
| FetchXmlPreviewDialog | Side-by-side XML document | Task 13 |
| TuiStatusBar | VS Code status bar items | Task 6, 9 |
| Keyboard shortcuts | VS Code keybindings + webview | Task 22 |
| N/A (new) | .ppdsnb notebooks | Task 17-19 |
