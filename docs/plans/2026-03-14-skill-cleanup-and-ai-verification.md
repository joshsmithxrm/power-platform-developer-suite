# Skill Cleanup & AI Verification Infrastructure Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clean up and rename Claude Code skills for clarity, add npm root proxy scripts, build diagnostic commands and webview dev mode for AI self-verification, install MCP servers, and validate the full pipeline by running a live SQL query through the extension.

**Architecture:** Three layers of verification — (1) acomagu/vscode-as-mcp-server for VS Code command execution and diagnostics, (2) ppds.debug.* commands for structured extension state inspection, (3) Playwright MCP + webview dev server for visual webview verification. Skills renamed to short verb-oriented names. Root package.json proxies all extension/TUI npm scripts.

**Tech Stack:** TypeScript, Vitest, Vite, VS Code Extension API, MCP (acomagu, mcp-tui-test, MCP Inspector), Playwright

---

## Dependency Graph

```
Parallel Phase (Tasks 1-6)
├── Task 1: npm script cleanup + root package.json
├── Task 2: Skill renames + merge + delete
├── Task 3: Create /verify skill
├── Task 4: Build ppds.debug.* diagnostic commands
├── Task 5: Build webview standalone dev mode
└── Task 6: Slim down repo CLAUDE.md

Sequential Phase (Tasks 7-8, depends on Tasks 2+3+6)
├── Task 7: Update /debug skill content
└── Task 8: Update /implement skill content

Setup Phase (Task 9, depends on Tasks 4+5)
└── Task 9: Install & configure MCP servers

Verification Phase (Task 10, depends on all)
└── Task 10: End-to-end verification test
```

---

## Chunk 1: npm & Skill Reorganization

### Task 1: npm Script Cleanup + Root Package.json

**Files:**
- Modify: `src/PPDS.Extension/package.json` (scripts section)
- Create: `package.json` (repo root)
- Modify: `src/PPDS.Extension/scripts/build-local.js` (if references old script names)
- Modify: `.vscode/tasks.json` (if references old script names)

- [ ] **Step 1: Rename scripts in src/PPDS.Extension/package.json**

Apply these renames in the `"scripts"` section of `src/PPDS.Extension/package.json`:

| Old Name | New Name |
|----------|----------|
| `vsce-package` | `vsce:package` |
| `install-local` | `local:install` |
| `uninstall-local` | `local:uninstall` |
| `revert-to-marketplace` | `local:revert` |
| `test-release` | `release:test` |

Delete the `marketplace` script entirely (duplicate of `revert-to-marketplace`).

Update internal cross-references within package.json scripts:
- `local` script calls `local:install` (was `install-local`)
- `local:revert` calls `local:uninstall` (was `uninstall-local`) then `code --install-extension ...`
- `release:test` calls `local:install` (was `install-local`)

The `local` script runs `node scripts/build-local.js` — check if `build-local.js` references any script names by running `npm run install-local`. If so, update the reference to `local:install`.

- [ ] **Step 2: Update launch.json task references**

Check `.vscode/launch.json` and `src/PPDS.Extension/.vscode/launch.json` for any `preLaunchTask` or task references that use old npm script names. Update if found.

Also check `.vscode/tasks.json` for npm script references.

- [ ] **Step 3: Create root package.json with proxy scripts**

Create `package.json` at repo root:

```json
{
  "private": true,
  "description": "PPDS workspace — root proxy scripts for extension and TUI",
  "scripts": {
    "ext:compile": "npm run compile --prefix src/PPDS.Extension",
    "ext:watch": "npm run watch --prefix src/PPDS.Extension",
    "ext:package": "npm run package --prefix src/PPDS.Extension",
    "ext:lint": "npm run lint --prefix src/PPDS.Extension",
    "ext:test": "npm run test --prefix src/PPDS.Extension",
    "ext:test:watch": "npm run test:watch --prefix src/PPDS.Extension",
    "ext:test:e2e": "npm run test:e2e --prefix src/PPDS.Extension",
    "ext:vsce:package": "npm run vsce:package --prefix src/PPDS.Extension",
    "ext:local": "npm run local --prefix src/PPDS.Extension",
    "ext:local:install": "npm run local:install --prefix src/PPDS.Extension",
    "ext:local:uninstall": "npm run local:uninstall --prefix src/PPDS.Extension",
    "ext:local:revert": "npm run local:revert --prefix src/PPDS.Extension",
    "ext:release:test": "npm run release:test --prefix src/PPDS.Extension",
    "ext:bundle:cli": "npm run bundle:cli --prefix src/PPDS.Extension",
    "ext:package:win32-x64": "npm run package:win32-x64 --prefix src/PPDS.Extension",
    "ext:package:linux-x64": "npm run package:linux-x64 --prefix src/PPDS.Extension",
    "ext:package:darwin-x64": "npm run package:darwin-x64 --prefix src/PPDS.Extension",
    "ext:package:darwin-arm64": "npm run package:darwin-arm64 --prefix src/PPDS.Extension",
    "tui:test": "npm test --prefix tests/tui-e2e",
    "tui:test:update": "npm run test:update --prefix tests/tui-e2e",
    "tui:test:headed": "npm run test:headed --prefix tests/tui-e2e"
  }
}
```

- [ ] **Step 4: Verify npm scripts work**

Run from repo root:
```bash
npm run ext:compile
npm run ext:test
npm run ext:lint
```
Expected: all pass with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Extension/package.json package.json
# Also add any modified launch.json, tasks.json, or build scripts
git commit -m "chore: rename npm scripts to colon convention + add root proxy scripts

- Rename: vsce-package → vsce:package, install-local → local:install,
  uninstall-local → local:uninstall, revert-to-marketplace → local:revert,
  test-release → release:test
- Delete duplicate 'marketplace' script
- Add root package.json with ext:* and tui:* proxy scripts

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Skill File Renames + Merge + Delete

**Files:**
- Rename: `.claude/commands/automated-quality-gates.md` → `.claude/commands/gates.md`
- Rename: `.claude/commands/impartial-code-review.md` → `.claude/commands/review.md`
- Rename: `.claude/commands/review-fix-converge.md` → `.claude/commands/converge.md`
- Delete: `.claude/commands/extension-dev.md`
- Delete: `.claude/commands/install-cli.md`
- Modify: `.claude/commands/setup.md` (absorb install-cli content)
- Modify: `CLAUDE.md` (update skill references table)

- [ ] **Step 1: Rename skill files**

```bash
cd <repo-root>
git mv .claude/commands/automated-quality-gates.md .claude/commands/gates.md
git mv .claude/commands/impartial-code-review.md .claude/commands/review.md
git mv .claude/commands/review-fix-converge.md .claude/commands/converge.md
```

- [ ] **Step 2: Update internal references in renamed files**

In `.claude/commands/gates.md`: Change the title from `# Automated Quality Gates` to `# Gates`. No other changes needed — content stays the same.

In `.claude/commands/review.md`: Change the title from `# Impartial Code Review` to `# Review`. No other changes needed.

In `.claude/commands/converge.md`: Change the title from `# Review Fix Converge` to `# Converge`. Update internal references:
- Change `Invoke \`/automated-quality-gates\`` to `Invoke \`/gates\``
- Change `Invoke \`/impartial-code-review\`` to `Invoke \`/review\``

- [ ] **Step 3: Merge install-cli into setup**

Read `.claude/commands/install-cli.md` content. Add a new section to `.claude/commands/setup.md` after the existing "Execute Setup" step:

```markdown
### Step 4b: Install CLI as Global Tool (if ppds selected)

Run the existing installation script:

\`\`\`powershell
.\scripts\Install-LocalCli.ps1
\`\`\`

This script packs PPDS.Cli to `nupkgs/`, finds the latest package, uninstalls any existing version, and installs globally.

If installation fails, **stop and prompt the user** — common causes are TUI running in another terminal (file lock), another CLI process active, or antivirus blocking.

Verify: `ppds --version`
```

Update the Summary step to include CLI installation status.

- [ ] **Step 4: Delete extension-dev and install-cli**

```bash
git rm .claude/commands/extension-dev.md
git rm .claude/commands/install-cli.md
```

- [ ] **Step 5: Update CLAUDE.md skill references**

In `CLAUDE.md`, update the Commands table to reflect new names:

| Command | Purpose |
|---------|---------|
| `/spec` | Create or update a specification |
| `/implement` | Execute implementation plan with spec-aware subagents |
| `/debug` | Interactive feedback loop for CLI/TUI/Extension/MCP |
| `/setup` | Set up development environment (includes CLI install) |
| `/gates` | Mechanical pass/fail build/test/lint checks |
| `/review` | Bias-free code review against specs |
| `/converge` | Gates, review, fix loop with convergence tracking |
| `/verify` | AI self-verification with MCP tools |

- [ ] **Step 6: Update cross-references in all skill files**

Search all `.claude/commands/*.md` files for references to old names:
- `/automated-quality-gates` → `/gates`
- `/impartial-code-review` → `/review`
- `/review-fix-converge` → `/converge`
- `/extension-dev` → (remove or replace with `/debug extension`)
- `/install-cli` → `/setup`

Files to check: `debug.md`, `implement.md`, `converge.md`, `spec.md`, `spec-audit.md`.

- [ ] **Step 7: Commit**

```bash
git add .claude/commands/ CLAUDE.md
git commit -m "chore: rename skills for clarity + merge setup/install-cli

Renames: automated-quality-gates → gates, impartial-code-review → review,
review-fix-converge → converge.
Merge: install-cli absorbed into setup.
Delete: extension-dev (replaced by /debug + /verify).
Update all cross-references.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 2: New /verify Skill

### Task 3: Create /verify Skill

**Files:**
- Create: `.claude/commands/verify.md`

- [ ] **Step 1: Write the /verify skill**

Create `.claude/commands/verify.md` with the following content:

```markdown
# Verify

AI self-verification of implemented work using MCP tools. Goes beyond unit tests to verify that code actually works in its runtime environment.

## Usage

`/verify` - Auto-detect component from recent changes
`/verify cli` - Verify CLI command behavior
`/verify tui` - Verify TUI rendering and interaction
`/verify extension` - Verify VS Code extension behavior
`/verify mcp` - Verify MCP server tools

## Prerequisites

Each mode requires specific MCP servers. If a prerequisite is missing, tell the user what to install and stop.

| Mode | Required MCPs / Tools |
|------|----------------------|
| cli | None (uses Bash tool directly) |
| tui | `mcp-tui-test` configured in Claude Code |
| extension | `acomagu/vscode-as-mcp-server` installed in VS Code + Playwright MCP for webview |
| mcp | MCP Inspector CLI (`npx @modelcontextprotocol/inspector`) |

## Process

### 1. Detect Component

Based on $ARGUMENTS or recent changes:
- `src/PPDS.Cli/Commands/` → CLI mode
- `src/PPDS.Cli/Tui/` → TUI mode
- `src/PPDS.Extension/` → Extension mode
- `src/PPDS.Mcp/` → MCP mode
- No clear match → Ask user

### 2. Run Unit Tests First

Always run the relevant unit tests before interactive verification. If tests fail, fix them first — don't waste MCP verification cycles on broken code.

- CLI/TUI: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- Extension: `npm run test --prefix src/PPDS.Extension`
- MCP: `dotnet test --filter "FullyQualifiedName~Mcp" -v q`

### 3. CLI Mode

Run the CLI command and verify output:

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```

Verify:
- Command executes without error
- Output format is correct (JSON where expected, table where expected)
- Exit code is 0
- Edge cases: empty input, invalid args, missing auth

### 4. TUI Mode

Use `mcp-tui-test` MCP tools:

1. **Launch:** Start the TUI app with configured dimensions
2. **Wait:** Wait for initial render (look for expected text)
3. **Interact:** Send keyboard input to navigate menus, trigger actions
4. **Capture:** Read screen output after each interaction
5. **Verify:** Check captured output contains expected elements

```
→ launch_tui_app("ppds tui", rows=40, cols=120)
→ wait_for_text("PPDS", timeout=10000)
→ capture_screen() → verify menu items visible
→ send_keys("Enter") → navigate into menu
→ capture_screen() → verify expected view loaded
```

Also run TUI snapshot tests for visual regression:
```bash
npm test --prefix tests/tui-e2e
```

### 5. Extension Mode

**Phase A: Functional Verification (acomagu MCP + ppds.debug.*)**

Use `execute_vscode_command` to run diagnostic commands:

```
→ execute_vscode_command("ppds.debug.daemonStatus")
  Verify: daemon is "ready", process ID present

→ execute_vscode_command("ppds.debug.extensionState")
  Verify: activation succeeded, no errors

→ execute_vscode_command("ppds.debug.treeViewState")
  Verify: profiles tree populated (if auth configured)

→ execute_vscode_command("ppds.dataExplorer")
  Opens Data Explorer panel

→ execute_vscode_command("ppds.debug.panelState")
  Verify: QueryPanel instance exists
```

Use `code_checker` to read VS Code diagnostics:
```
→ code_checker()
  Verify: no errors from PPDS extension
```

**Phase B: Webview Visual Verification (Playwright MCP)**

Start the webview dev server:
```bash
npm run dev:webview --prefix src/PPDS.Extension
```

Use Playwright MCP:
```
→ browser_navigate("http://localhost:5173/query-panel.html")
→ browser_snapshot() → verify query input, execute button, results area
→ browser_fill_form("#sql-input", "SELECT TOP 5 name FROM account")
→ browser_click("#execute-btn")
→ browser_snapshot() → verify results table rendered
```

### 6. MCP Mode

Use MCP Inspector CLI to test tools:

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```

For each tool:
1. Call with valid input → verify success response shape
2. Call with edge case input → verify error handling
3. Verify response matches expected schema

### 7. Report

Present structured results:

```
## Verification Results — [component]

| Check | Status | Details |
|-------|--------|---------|
| Unit tests | PASS | 12/12 passing |
| Daemon connection | PASS | PID 12345, uptime 30s |
| Tree view state | PASS | 2 profiles, 3 environments |
| Data Explorer open | PASS | Panel created |
| SQL query execution | PASS | 5 rows returned |
| Webview rendering | PASS | Query panel layout correct |

### Verdict: PASS — all checks green
```

## Rules

1. **Unit tests first** — always. Don't waste interactive cycles on broken code.
2. **Structured data over screenshots** — prefer ppds.debug.* JSON over visual inspection.
3. **Report exact state** — include actual values, not just pass/fail.
4. **Prerequisites are hard gates** — if MCP not configured, stop and say so.
5. **Don't fix during verify** — report problems, don't fix them. That's for /debug or /converge.
```

- [ ] **Step 2: Commit**

```bash
git add .claude/commands/verify.md
git commit -m "feat: add /verify skill for AI self-verification with MCP tools

Four modes: cli, tui, extension, mcp. Each uses appropriate
tooling to verify work beyond unit tests.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 3: Extension Diagnostic Commands

### Task 4: Build ppds.debug.* Diagnostic Commands

**Files:**
- Create: `src/PPDS.Extension/src/commands/debugCommands.ts`
- Create: `src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts`
- Modify: `src/PPDS.Extension/src/extension.ts` (register debug commands)
- Modify: `src/PPDS.Extension/package.json` (add command contributions)

- [ ] **Step 1: Write the failing tests**

Create `src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts`:

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';

const { mockLogChannel } = vi.hoisted(() => ({
    mockLogChannel: { info: vi.fn(), error: vi.fn(), appendLine: vi.fn() },
}));

vi.mock('vscode', () => ({
    window: {
        showInformationMessage: vi.fn(),
    },
}));

import {
    getDaemonStatus,
    getExtensionState,
    getTreeViewState,
    getPanelState,
} from '../../commands/debugCommands.js';
import type { DaemonClient } from '../../daemonClient.js';

function makeMockDaemon(overrides: Partial<DaemonClient> = {}): DaemonClient {
    return {
        isReady: () => true,
        getProcessId: () => 12345,
        ...overrides,
    } as unknown as DaemonClient;
}

describe('debugCommands', () => {
    beforeEach(() => vi.clearAllMocks());

    describe('getDaemonStatus', () => {
        it('returns ready status with PID when daemon is running', () => {
            const daemon = makeMockDaemon();
            const result = getDaemonStatus(daemon);
            expect(result.state).toBe('ready');
            expect(result.processId).toBe(12345);
        });

        it('returns not-running when daemon is null', () => {
            const result = getDaemonStatus(null);
            expect(result.state).toBe('not-running');
            expect(result.processId).toBeNull();
        });
    });

    describe('getExtensionState', () => {
        it('returns state summary with profile count', () => {
            const result = getExtensionState({
                daemonState: 'ready',
                profileCount: 3,
            });
            expect(result.daemonState).toBe('ready');
            expect(result.profileCount).toBe(3);
        });
    });

    describe('getTreeViewState', () => {
        it('returns empty array when provider has no items', async () => {
            const mockProvider = {
                getChildren: vi.fn().mockResolvedValue([]),
            };
            const result = await getTreeViewState(mockProvider as any);
            expect(result.items).toEqual([]);
            expect(result.count).toBe(0);
        });

        it('returns serialized tree items', async () => {
            const mockProvider = {
                getChildren: vi.fn().mockResolvedValue([
                    { label: 'Profile 1', contextValue: 'profile' },
                    { label: 'Profile 2', contextValue: 'profile' },
                ]),
            };
            const result = await getTreeViewState(mockProvider as any);
            expect(result.count).toBe(2);
            expect(result.items[0].label).toBe('Profile 1');
        });
    });

    describe('getPanelState', () => {
        it('returns panel counts', () => {
            const result = getPanelState({
                queryPanelCount: 2,
                solutionsPanelCount: 1,
            });
            expect(result.queryPanels).toBe(2);
            expect(result.solutionsPanels).toBe(1);
        });
    });
});
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npm run test --prefix src/PPDS.Extension -- --run src/__tests__/commands/debugCommands.test.ts
```
Expected: FAIL — module `../../commands/debugCommands.js` not found.

- [ ] **Step 3: Implement debugCommands.ts**

Create `src/PPDS.Extension/src/commands/debugCommands.ts`:

```typescript
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { ProfileTreeDataProvider } from '../views/profileTreeView.js';

export interface DaemonStatusResult {
    state: 'ready' | 'not-running' | 'error';
    processId: number | null;
}

export interface ExtensionStateResult {
    daemonState: string;
    profileCount: number;
}

export interface TreeViewStateResult {
    count: number;
    items: Array<{ label: string; contextValue?: string; description?: string }>;
}

export interface PanelStateResult {
    queryPanels: number;
    solutionsPanels: number;
}

export function getDaemonStatus(daemon: DaemonClient | null | undefined): DaemonStatusResult {
    if (!daemon || !daemon.isReady()) {
        return { state: 'not-running', processId: null };
    }
    return {
        state: 'ready',
        processId: daemon.getProcessId() ?? null,
    };
}

export function getExtensionState(state: {
    daemonState: string;
    profileCount: number;
}): ExtensionStateResult {
    return {
        daemonState: state.daemonState,
        profileCount: state.profileCount,
    };
}

export async function getTreeViewState(
    provider: ProfileTreeDataProvider,
): Promise<TreeViewStateResult> {
    const children = await provider.getChildren();
    const items = (children ?? []).map((item: any) => ({
        label: typeof item.label === 'string' ? item.label : item.label?.label ?? '',
        contextValue: item.contextValue,
        description: typeof item.description === 'string' ? item.description : undefined,
    }));
    return { count: items.length, items };
}

export function getPanelState(counts: {
    queryPanelCount: number;
    solutionsPanelCount: number;
}): PanelStateResult {
    return {
        queryPanels: counts.queryPanelCount,
        solutionsPanels: counts.solutionsPanelCount,
    };
}

/**
 * Register all ppds.debug.* commands.
 * Called from extension.ts activate().
 */
export function registerDebugCommands(
    context: vscode.ExtensionContext,
    getDaemon: () => DaemonClient | undefined,
    getProfileProvider: () => ProfileTreeDataProvider,
    getState: () => { daemonState: string; profileCount: number },
    getPanelCounts: () => { queryPanelCount: number; solutionsPanelCount: number },
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.debug.daemonStatus', () => {
            return getDaemonStatus(getDaemon());
        }),
        vscode.commands.registerCommand('ppds.debug.extensionState', () => {
            return getExtensionState(getState());
        }),
        vscode.commands.registerCommand('ppds.debug.treeViewState', async () => {
            return getTreeViewState(getProfileProvider());
        }),
        vscode.commands.registerCommand('ppds.debug.panelState', () => {
            return getPanelState(getPanelCounts());
        }),
    );
}
```

**Important:** This implementation requires two small additions to `DaemonClient`:
- `isReady(): boolean` — returns whether daemon connection is active
- `getProcessId(): number | undefined` — returns the spawned process PID

Read `src/PPDS.Extension/src/daemonClient.ts` to check if these already exist. If not, add them as thin wrappers over existing internal state (the daemon already tracks its child process).

- [ ] **Step 4: Run tests to verify they pass**

```bash
npm run test --prefix src/PPDS.Extension -- --run src/__tests__/commands/debugCommands.test.ts
```
Expected: PASS — all tests green.

- [ ] **Step 5: Add command contributions to package.json**

In `src/PPDS.Extension/package.json`, add to `contributes.commands` array:

```json
{ "command": "ppds.debug.daemonStatus", "title": "PPDS Debug: Daemon Status" },
{ "command": "ppds.debug.extensionState", "title": "PPDS Debug: Extension State" },
{ "command": "ppds.debug.treeViewState", "title": "PPDS Debug: Tree View State" },
{ "command": "ppds.debug.panelState", "title": "PPDS Debug: Panel State" }
```

- [ ] **Step 6: Register debug commands in extension.ts**

In `src/PPDS.Extension/src/extension.ts`, import and call `registerDebugCommands` near the end of `activate()`:

```typescript
import { registerDebugCommands } from './commands/debugCommands.js';

// Inside activate(), after all other registrations:
registerDebugCommands(
    context,
    () => daemonClient,
    () => profileTreeProvider,
    () => ({
        daemonState: /* current daemon state variable */,
        profileCount: /* current profile count variable */,
    }),
    () => ({
        queryPanelCount: QueryPanel.instanceCount,
        solutionsPanelCount: SolutionsPanel.instanceCount,
    }),
);
```

**Note:** `QueryPanel` and `SolutionsPanel` may not expose `instanceCount` yet. Check existing code — they track instances via static `Map` or `Set`. Add a static getter if needed:
```typescript
static get instanceCount(): number { return SolutionsPanel.panels.size; }
```

- [ ] **Step 7: Run full extension tests**

```bash
npm run lint --prefix src/PPDS.Extension && npm run compile --prefix src/PPDS.Extension && npm run test --prefix src/PPDS.Extension
```
Expected: 0 errors, all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/PPDS.Extension/src/commands/debugCommands.ts src/PPDS.Extension/src/__tests__/commands/debugCommands.test.ts src/PPDS.Extension/src/extension.ts src/PPDS.Extension/src/daemonClient.ts src/PPDS.Extension/package.json src/PPDS.Extension/src/panels/QueryPanel.ts src/PPDS.Extension/src/panels/SolutionsPanel.ts
git commit -m "feat(extension): add ppds.debug.* diagnostic commands for AI verification

Commands: daemonStatus, extensionState, treeViewState, panelState.
Returns structured JSON for MCP-based inspection.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 4: Webview Dev Mode

### Task 5: Build Webview Standalone Dev Mode

**Files:**
- Create: `src/PPDS.Extension/dev/index.html`
- Create: `src/PPDS.Extension/dev/mock-vscode-api.js`
- Create: `src/PPDS.Extension/dev/query-panel.html`
- Create: `src/PPDS.Extension/dev/vite.config.ts`
- Modify: `src/PPDS.Extension/package.json` (add `dev:webview` script)

The goal is a minimal standalone page that renders the QueryPanel webview HTML in a browser, with a mock VS Code API that returns sample data. Playwright MCP can then navigate to it for visual verification.

- [ ] **Step 1: Read QueryPanel.getHtmlContent() to understand the HTML structure**

Read `src/PPDS.Extension/src/panels/QueryPanel.ts`, specifically the `getHtmlContent()` method. Understand:
- What CSS is inlined
- What JS is inlined
- What DOM structure is created
- How `acquireVsCodeApi()` is used
- What `postMessage` types the webview sends/receives

- [ ] **Step 2: Create the mock VS Code API**

Create `src/PPDS.Extension/dev/mock-vscode-api.js`:

```javascript
/**
 * Mock acquireVsCodeApi() for standalone webview development.
 * Intercepts postMessage calls and returns sample data.
 */
window.acquireVsCodeApi = function () {
    const state = {};
    return {
        postMessage(msg) {
            console.log('[mock] postMessage:', msg);
            // Simulate extension host responses
            if (msg.type === 'executeQuery') {
                setTimeout(() => {
                    window.dispatchEvent(new MessageEvent('message', {
                        data: {
                            type: 'queryResult',
                            columns: ['name', 'accountid', 'createdon'],
                            records: [
                                { name: 'Contoso Ltd', accountid: 'abc-123', createdon: '2024-01-15' },
                                { name: 'Fabrikam Inc', accountid: 'def-456', createdon: '2024-02-20' },
                                { name: 'Adventure Works', accountid: 'ghi-789', createdon: '2024-03-10' },
                                { name: 'Northwind Traders', accountid: 'jkl-012', createdon: '2024-04-05' },
                                { name: 'Trey Research', accountid: 'mno-345', createdon: '2024-05-12' },
                            ],
                            totalCount: 5,
                            hasMore: false,
                        },
                    }));
                }, 300);
            }
            if (msg.type === 'ready') {
                // No-op for initial ready signal
            }
        },
        getState() { return state; },
        setState(s) { Object.assign(state, s); },
    };
};
```

- [ ] **Step 3: Create the query panel dev page**

Create `src/PPDS.Extension/dev/query-panel.html`. This needs to replicate the HTML that `QueryPanel.getHtmlContent()` produces, but loading the mock API first.

The exact HTML depends on what you find in Step 1. The structure should be:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>PPDS Query Panel — Dev Mode</title>
    <!-- Load mock API before any panel code -->
    <script src="./mock-vscode-api.js"></script>
    <style>
        /* Copy the CSS from QueryPanel.getHtmlContent() */
        /* Adapt VS Code CSS variables to sensible defaults */
        :root {
            --vscode-editor-background: #1e1e1e;
            --vscode-editor-foreground: #d4d4d4;
            --vscode-button-background: #0e639c;
            --vscode-button-foreground: #ffffff;
            --vscode-input-background: #3c3c3c;
            --vscode-input-foreground: #cccccc;
            --vscode-input-border: #3c3c3c;
            --vscode-focusBorder: #007fd4;
        }
        body { background: var(--vscode-editor-background); color: var(--vscode-editor-foreground); font-family: -apple-system, BlinkMacSystemFont, sans-serif; margin: 0; padding: 16px; }
    </style>
</head>
<body>
    <!-- Copy the body HTML from QueryPanel.getHtmlContent() -->
    <!-- The inline <script> at the bottom should work as-is since acquireVsCodeApi() is mocked -->
</body>
</html>
```

**Key:** The inline `<script>` from `getHtmlContent()` calls `acquireVsCodeApi()` at the top. Since we loaded the mock first, it will get the mock object. The panel should render and respond to the mock data.

- [ ] **Step 4: Create Vite config for dev server**

Create `src/PPDS.Extension/dev/vite.config.ts`:

```typescript
import { defineConfig } from 'vite';

export default defineConfig({
    root: __dirname,
    server: {
        port: 5173,
        open: false,
    },
});
```

- [ ] **Step 5: Add dev:webview npm script**

In `src/PPDS.Extension/package.json`, add to scripts:

```json
"dev:webview": "npx vite dev/",
```

In root `package.json`, add proxy:

```json
"ext:dev:webview": "npm run dev:webview --prefix src/PPDS.Extension"
```

- [ ] **Step 6: Test the dev server**

```bash
npm run dev:webview --prefix src/PPDS.Extension &
# Wait for server to start, then verify
curl -s http://localhost:5173/query-panel.html | head -20
```

Expected: HTML content of the query panel page.

Kill the dev server when done.

- [ ] **Step 7: Commit**

```bash
git add src/PPDS.Extension/dev/ src/PPDS.Extension/package.json package.json
git commit -m "feat(extension): add webview standalone dev mode for Playwright verification

Creates dev server at localhost:5173 with mock VS Code API.
QueryPanel renders with sample data for visual testing.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 5: CLAUDE.md Cleanup + Skill Content Updates

### Task 6: Slim Down Repo CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Remove Commands table and Spec Workflow section**

Delete the `## Commands` section (lines 35-48) and the `## Spec Workflow` section (lines 49-57). Skills are auto-discovered from `.claude/commands/` — listing them in CLAUDE.md creates maintenance burden and drift risk.

- [ ] **Step 2: Update Testing section with extension and TUI commands**

Replace the current `## Testing` section with:

```markdown
## Testing

- .NET unit: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- .NET integration: `dotnet test PPDS.sln --filter "Category=Integration" -v q`
- .NET TUI: `dotnet test --filter "Category=TuiUnit"`
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
- TUI snapshots: `npm run tui:test`
```

- [ ] **Step 3: Slim the Specs section**

Replace the removed Spec Workflow with a minimal pointer:

```markdown
## Specs

- Constitution: `specs/CONSTITUTION.md` — read before any work
- Template: `specs/SPEC-TEMPLATE.md`
- Index: `specs/README.md`
```

- [ ] **Step 4: Verify line count is under 100**

Count lines in the updated CLAUDE.md. Must be under 100 per governance rules.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: slim CLAUDE.md — remove auto-discovered skill listings, update testing

Remove Commands table and Spec Workflow (auto-discovered from .claude/commands/).
Add extension and TUI test commands to Testing section.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Update /debug Skill Content

**Files:**
- Modify: `.claude/commands/debug.md`

Depends on: Task 2 (skill renames), Task 3 (/verify exists)

- [ ] **Step 1: Rewrite the Extension Mode section**

Replace the current "4. Extension Mode" section (which just says "use `/extension-dev`") with:

```markdown
### 4. Extension Mode

**Build & install for manual testing:**

```bash
cd <root>/extension && npm run lint && npm run compile && npm run test && npm run local
```

Then reload VS Code (Ctrl+Shift+P → Reload Window) and test manually.

**Useful npm scripts** (run from repo root or use `--prefix src/PPDS.Extension`):

| Request | Command |
|---------|---------|
| Build + install | `npm run ext:local` |
| Just install existing build | `npm run ext:local:install` |
| Revert to marketplace version | `npm run ext:local:revert` |
| Uninstall local | `npm run ext:local:uninstall` |
| Run unit tests only | `npm run ext:test` |
| Run E2E tests | `npm run ext:test:e2e` |
| Watch mode (hot reload) | `npm run ext:watch` |
| Full release test | `npm run ext:release:test` |

**F5 Launch Configurations** (from VS Code debug panel):

| Configuration | When to use |
|---------------|-------------|
| Run Extension | Default — full build, then launch debug host |
| Run Extension (Watch Mode) | Iterating — hot reloads on file changes |
| Run Extension (No Build) | Quick — skip build, use existing compiled code |
| Run Extension (Open Folder) | Testing with a specific project folder |
| Run Extension Tests | Run VS Code extension integration tests |

**For AI self-verification:** Use `/verify extension` to exercise commands,
inspect state, and verify webview rendering via MCP tools.
```

- [ ] **Step 2: Update cross-references in other sections**

Replace any remaining references to `/extension-dev` with the appropriate alternative.

- [ ] **Step 3: Commit**

```bash
git add .claude/commands/debug.md
git commit -m "docs: update /debug extension mode with current npm scripts and F5 configs

Replace /extension-dev delegation with inline content. Add npm script
table, F5 launch config table, and /verify reference.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Update /implement Skill

**Files:**
- Modify: `.claude/commands/implement.md`

Depends on: Task 2 (skill renames), Task 3 (/verify exists)

- [ ] **Step 1: Add /verify to phase gates**

In `.claude/commands/implement.md`, update **Step 5 Section C (Verify Phase Gate)** to add after the test suite run:

```markdown
- If the phase touches extension code (`src/PPDS.Extension/` directory):
  Invoke `/verify extension` to check daemon status, tree views, and panel state.
- If the phase touches TUI code (`src/PPDS.Cli/Tui/`):
  Invoke `/verify tui` to check TUI rendering.
- If the phase touches MCP code (`src/PPDS.Mcp/`):
  Invoke `/verify mcp` to check tool responses.
```

- [ ] **Step 2: Update skill references**

Replace all references to old skill names:
- `/automated-quality-gates` → `/gates`
- `/impartial-code-review` → `/review`
- `/review-fix-converge` → `/converge`

- [ ] **Step 3: Commit**

```bash
git add .claude/commands/implement.md
git commit -m "docs: update /implement with /verify at phase gates + renamed skill refs

Add src/PPDS.Extension/tui/mcp verification steps to phase gates.
Update all skill cross-references to new short names.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 6: MCP Server Installation & End-to-End Verification

### Task 9: Install & Configure MCP Servers

**Files:**
- Modify: User-level Claude Code settings (`~/.claude/settings.json`)
- Modify: Project-level `.claude/settings.json` (if MCP config goes here)

This task is a manual/interactive setup — the agent installs and configures the MCP servers, then verifies each one works.

- [ ] **Step 1: Install acomagu/vscode-as-mcp-server in VS Code**

```bash
code --install-extension acomagu.vscode-as-mcp-server
```

Verify installation:
```bash
code --list-extensions | grep acomagu
```
Expected: `acomagu.vscode-as-mcp-server` in the list.

- [ ] **Step 2: Configure acomagu MCP in Claude Code settings**

Read the existing Claude Code settings to understand current MCP configuration. Then add acomagu to the MCP servers config. The exact location depends on whether the project uses project-level or user-level MCP config.

Add to the appropriate settings file:
```json
{
  "mcpServers": {
    "vscode": {
      "command": "code",
      "args": ["--ms-enable-electron-run-as-node", "--mcp-server"]
    }
  }
}
```

**Note:** The exact args depend on the acomagu extension's documentation. Read the extension's README after installation to get the correct configuration.

- [ ] **Step 3: Install mcp-tui-test**

```bash
pip install mcp-tui-test
```

Or if it's an npm package:
```bash
npm install -g mcp-tui-test
```

Check the [mcp-tui-test repo](https://github.com/GeorgePearse/mcp-tui-test) for current installation method.

Add to Claude Code MCP settings:
```json
{
  "mcpServers": {
    "tui-test": {
      "command": "mcp-tui-test",
      "args": []
    }
  }
}
```

- [ ] **Step 4: Verify MCP Inspector CLI**

MCP Inspector runs via npx — no installation needed:
```bash
npx @modelcontextprotocol/inspector --help
```
Expected: Help text showing CLI options.

- [ ] **Step 5: Verify each MCP server responds**

Test acomagu:
```
→ Use execute_vscode_command tool to run a simple VS Code command
→ Verify response received
```

Test mcp-tui-test (if installed):
```
→ Use launch tool to start a simple terminal app (e.g., "echo hello")
→ Verify screen capture works
```

- [ ] **Step 6: Commit settings changes**

```bash
git add .claude/settings.json
git commit -m "chore: configure MCP servers for AI verification (acomagu, tui-test)

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: End-to-End Verification Test

Depends on: ALL previous tasks complete.

This is the validation task — use the MCP tools we just installed to actually test the VS Code extension. The goal is to run a SQL query through Data Explorer and verify the results.

- [ ] **Step 1: Verify the extension is running**

Use acomagu MCP:
```
→ execute_vscode_command("ppds.debug.daemonStatus")
→ Verify: { state: "ready", processId: <number> }
```

If daemon is not ready, check if extension is loaded:
```
→ execute_vscode_command("ppds.debug.extensionState")
→ Verify: { daemonState: "ready", profileCount: > 0 }
```

- [ ] **Step 2: Check profile and environment state**

```
→ execute_vscode_command("ppds.debug.treeViewState")
→ Verify: at least one profile with at least one environment
→ Note the environment URL for the SQL query
```

- [ ] **Step 3: Open Data Explorer and run a SQL query**

```
→ execute_vscode_command("ppds.dataExplorer")
→ Verify: panel opens (check ppds.debug.panelState shows queryPanels > 0)
```

The Data Explorer panel is a webview — we can't directly type into it via acomagu MCP. But we CAN test the RPC layer directly:

```
→ execute_command("ppds query sql \"SELECT TOP 5 name, accountid FROM account\"")
→ Verify: JSON output with rows
```

Or use the debug commands to inspect panel state after the query.

- [ ] **Step 4: Verify webview rendering via Playwright**

Start the dev server:
```bash
npm run dev:webview --prefix src/PPDS.Extension
```

Use Playwright MCP:
```
→ browser_navigate("http://localhost:5173/query-panel.html")
→ browser_snapshot()
→ Verify: SQL input area visible, execute button present
→ browser_fill_form("#sql-input", "SELECT TOP 5 name FROM account")
→ browser_click("#execute-btn")
→ Wait for mock response (300ms)
→ browser_snapshot()
→ Verify: results table with 5 rows visible (Contoso, Fabrikam, etc.)
```

- [ ] **Step 5: Run the full /verify extension workflow**

Now that everything is wired up, invoke the `/verify extension` skill end-to-end to validate that the skill itself works correctly.

- [ ] **Step 6: Report results**

Present a full verification report showing:
- Each check and its status
- Any issues found
- Screenshots or snapshots if relevant
- Verdict: PASS or FAIL with details

---

## Summary

| Task | What | Parallel? | Commit message prefix |
|------|------|-----------|----------------------|
| 1 | npm script cleanup + root proxy | Yes | `chore:` |
| 2 | Skill renames + merge + delete | Yes | `chore:` |
| 3 | Create /verify skill | Yes | `feat:` |
| 4 | ppds.debug.* diagnostic commands | Yes | `feat(extension):` |
| 5 | Webview standalone dev mode | Yes | `feat(extension):` |
| 6 | Slim down repo CLAUDE.md | Yes | `docs:` |
| 7 | Update /debug skill content | After 2+3+6 | `docs:` |
| 8 | Update /implement skill | After 2+3 | `docs:` |
| 9 | Install MCP servers | After 4+5 | `chore:` |
| 10 | E2E verification test | After all | (no commit — validation only) |
