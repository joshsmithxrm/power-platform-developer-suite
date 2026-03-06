# Extension UX Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix VS Code extension UX issues: structured logging, input persistence, inline environment picker, tree welcome states, token invalidation, and show logs command.

**Architecture:** All changes are in the `extension/` directory except no C# changes. LogOutputChannel is created once in `extension.ts` and threaded through to consumers. Context keys drive declarative `viewsWelcome` content. Profile creation flow is reworked for user-based auth to inline environment discovery.

**Tech Stack:** TypeScript (VS Code Extension API), Vitest

**Worktree:** `.worktrees/vscode-extension-mvp/`

---

## Task 1: Switch to LogOutputChannel

**Files:**
- Modify: `extension/src/extension.ts`
- Modify: `extension/src/daemonClient.ts`
- Modify: `extension/src/__tests__/daemonClient.test.ts`
- Modify: `extension/src/__tests__/integration/smokeTest.test.ts`

**Step 1: Update `extension.ts` — create LogOutputChannel and pass to DaemonClient**

Replace:
```ts
daemonClient = new DaemonClient(context.extensionPath);
```

With:
```ts
const log = vscode.window.createOutputChannel('PPDS', { log: true });
context.subscriptions.push(log);
daemonClient = new DaemonClient(context.extensionPath, log);
```

Store `log` in a module-level variable so Task 6 (showLogs command) can reference it.

```ts
let logChannel: vscode.LogOutputChannel | undefined;
```

And in `activate()`:
```ts
logChannel = vscode.window.createOutputChannel('PPDS', { log: true });
context.subscriptions.push(logChannel);
daemonClient = new DaemonClient(context.extensionPath, logChannel);
```

**Step 2: Update `daemonClient.ts` — accept LogOutputChannel, replace all appendLine calls**

Change constructor signature:
```ts
constructor(extensionPath: string, private readonly log: vscode.LogOutputChannel) {
    this.extensionPath = extensionPath;
}
```

Remove the `outputChannel` field and its `createOutputChannel` call and `dispose()` call.

Replace all `this.outputChannel.appendLine(...)` calls with appropriate log levels:

| Pattern | Level | Example |
|---------|-------|---------|
| `Starting ppds serve...` | `this.log.info(...)` | Lifecycle events |
| `Calling auth/list...` | `this.log.debug(...)` | RPC call starts |
| `Got N profiles` | `this.log.debug(...)` | RPC call results |
| `Daemon error:` | `this.log.error(...)` | Errors |
| `Daemon exited` | `this.log.warn(...)` | Unexpected exits |
| `Disposing daemon client` | `this.log.info(...)` | Cleanup |
| `Registered auth/deviceCode` | `this.log.debug(...)` | Registration |
| `[daemon stderr]` | `this.log.warn(...)` | Daemon stderr output |

Remove `this.outputChannel.dispose()` from the `dispose()` method (the channel is now owned by `extension.ts` via `context.subscriptions`).

**Step 3: Update `daemonClient.test.ts` — mock LogOutputChannel**

Replace the `mockOutputChannel` mock:
```ts
const mockOutputChannel = {
    appendLine: vi.fn(),
    dispose: vi.fn(),
};
```

With:
```ts
const mockLogChannel: Record<string, ReturnType<typeof vi.fn>> = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    append: vi.fn(),
    appendLine: vi.fn(),
    clear: vi.fn(),
    show: vi.fn(),
    hide: vi.fn(),
    dispose: vi.fn(),
    replace: vi.fn(),
};
```

Update `createOutputChannel` mock to not be used (it's no longer called in constructor).
Remove the `createOutputChannel` mock from the vscode mock, since DaemonClient no longer calls it.

Update all `new DaemonClient('/test/extension')` calls to `new DaemonClient('/test/extension', mockLogChannel as unknown as vscode.LogOutputChannel)`.

**Step 4: Update `smokeTest.test.ts` — pass log channel to activate context**

The smoke test mocks `vscode.window.createOutputChannel`. Update it to return a mock LogOutputChannel when called with the `{ log: true }` options object. The simplest approach: make the existing `createOutputChannel` mock return an object with all the log methods.

Update the mock return value in the `vi.mock('vscode', ...)` block:
```ts
createOutputChannel: vi.fn(() => ({
    appendLine: vi.fn(),
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    append: vi.fn(),
    clear: vi.fn(),
    show: vi.fn(),
    hide: vi.fn(),
    dispose: vi.fn(),
    replace: vi.fn(),
})),
```

**Step 5: Run lint, compile, and tests**

```bash
cd extension && npm run lint && npm run compile && npm run test
```

Expected: All pass (0 lint errors, esbuild succeeds, 85 tests pass).

**Step 6: Commit**

```bash
git add extension/src/extension.ts extension/src/daemonClient.ts \
       extension/src/__tests__/daemonClient.test.ts \
       extension/src/__tests__/integration/smokeTest.test.ts
git commit -m "refactor(extension): switch to LogOutputChannel for structured logging

Replace raw OutputChannel with LogOutputChannel for automatic timestamps
and log level filtering. Channel is created once in extension.ts and
passed to DaemonClient. All appendLine calls replaced with appropriate
log levels (info/debug/warn/error)."
```

---

## Task 2: Add ignoreFocusOut to All Inputs

**Files:**
- Modify: `extension/src/commands/profileCommands.ts`
- Modify: `extension/src/commands/environmentCommands.ts`
- Modify: `extension/src/commands/environmentConfigCommand.ts`
- Modify: `extension/src/notebooks/DataverseNotebookController.ts`

**Step 1: `profileCommands.ts` — add `ignoreFocusOut: true` to all inputs**

There are 15 `showInputBox` / `showQuickPick` calls in this file. Add `ignoreFocusOut: true` to each:

- Line 91: `showQuickPick` in `listProfiles` (QuickPick for profile selection)
- Line 258: `showQuickPick` in `showProfileDetails`
- Line 348: `showQuickPick` in `runCreateProfileWizard` (auth method selection)
- Line 362: `showInputBox` in `runCreateProfileWizard` (profile name)
- Line 428: `showInputBox` in `collectAuthMethodParams` (environment URL)
- Line 461: `showInputBox` (Application ID — clientSecret)
- Line 470: `showInputBox` (Client Secret)
- Line 479: `showInputBox` (Tenant ID — clientSecret)
- Line 491: `showInputBox` (Application ID — certificateFile)
- Line 500: `showInputBox` (Certificate Path)
- Line 509: `showInputBox` (Certificate Password)
- Line 519: `showInputBox` (Tenant ID — certificateFile)
- Line 531: `showInputBox` (Application ID — certificateStore)
- Line 540: `showInputBox` (Thumbprint)
- Line 549: `showInputBox` (Tenant ID — certificateStore)
- Line 561: `showInputBox` (Username)
- Line 570: `showInputBox` (Password)
- Line 637: `showInputBox` in `runRenameProfile` (new name)

For each `showInputBox`, add `ignoreFocusOut: true` to the options object.
For each `showQuickPick`, add `ignoreFocusOut: true` to the options object.

**Step 2: `environmentCommands.ts` — add `ignoreFocusOut: true`**

- Line 107: `createQuickPick` in `selectEnvironment` — set `quickPick.ignoreFocusOut = true` after creation (line ~112, before `quickPick.show()`)
- Line 143: `showInputBox` for manual URL entry — add `ignoreFocusOut: true`

**Step 3: `environmentConfigCommand.ts` — add `ignoreFocusOut: true`**

- Line 84: `showInputBox` (Label)
- Line 102: `showQuickPick` (Type)
- Line 127: `showQuickPick` (Color)

Add `ignoreFocusOut: true` to each options object.

**Step 4: `DataverseNotebookController.ts` — add `ignoreFocusOut: true`**

- Line 92: `showQuickPick` (notebook environment selector)

Add `ignoreFocusOut: true` to the options object.

**Step 5: Run lint, compile, and tests**

```bash
cd extension && npm run lint && npm run compile && npm run test
```

Expected: All pass.

**Step 6: Commit**

```bash
git add extension/src/commands/profileCommands.ts \
       extension/src/commands/environmentCommands.ts \
       extension/src/commands/environmentConfigCommand.ts \
       extension/src/notebooks/DataverseNotebookController.ts
git commit -m "fix(extension): prevent input dismissal on focus loss

Add ignoreFocusOut: true to all showQuickPick and showInputBox calls
so users can alt-tab to Azure Portal to copy IDs/secrets without
losing their input progress. Users can still cancel with Escape."
```

---

## Task 3: Rework Profile Creation — Inline Environment Picker

**Files:**
- Modify: `extension/src/commands/profileCommands.ts`

**Step 1: Rework `runCreateProfileWizard` for user-based auth flow**

Replace the section after `collectAuthMethodParams` (lines ~384-406) with logic that:

1. Creates the profile via daemon (same as before)
2. For user-based auth only (`deviceCode` or `interactive`): after creation, discover and show environment picker inline
3. For SPN: no post-creation picker (URL already set)
4. Remove the unconditional `ppds.selectEnvironment` auto-launch at line 406

Replace lines 384-406 with:

```ts
    // Create the profile via daemon with progress indicator
    await vscode.window.withProgress(
        {
            location: vscode.ProgressLocation.Notification,
            title: 'Creating authentication profile...',
            cancellable: false,
        },
        async () => {
            await daemonClient.profilesCreate({
                name: profileName || undefined,
                authMethod: selectedMethod.authMethodId,
                ...params,
            });
        },
    );

    refreshProfiles();
    vscode.window.showInformationMessage(
        `Profile created successfully (${selectedMethod.authMethodId})`,
    );

    // For user-based auth, discover environments and let user pick inline
    const isUserBased = selectedMethod.authMethodId === 'deviceCode'
        || selectedMethod.authMethodId === 'interactive';

    if (isUserBased) {
        try {
            const envResult = await vscode.window.withProgress(
                {
                    location: vscode.ProgressLocation.Notification,
                    title: 'Discovering environments...',
                    cancellable: false,
                },
                () => daemonClient.envList(),
            );

            if (envResult.environments.length > 0) {
                const envItems = envResult.environments.map(env => ({
                    label: env.friendlyName,
                    description: env.type ? `[${env.type}]` : undefined,
                    detail: env.apiUrl,
                    apiUrl: env.apiUrl,
                }));

                const selectedEnv = await vscode.window.showQuickPick(envItems, {
                    title: 'Select Default Environment',
                    placeHolder: 'Choose a Dataverse environment for this profile',
                    ignoreFocusOut: true,
                });

                if (selectedEnv) {
                    await daemonClient.envSelect(selectedEnv.apiUrl);
                    refreshProfiles();
                }
            }
        } catch {
            // Environment discovery failed — not critical, user can select later
        }
    }
```

**Step 2: Remove environment URL step for user-based auth in `collectAuthMethodParams`**

At the top of `collectAuthMethodParams`, skip the URL input entirely for user-based flows:

```ts
async function collectAuthMethodParams(authMethodId: string): Promise<AuthParams | null> {
    const params: AuthParams = {};

    const isUserBased = authMethodId === 'deviceCode' || authMethodId === 'interactive';

    // User-based flows discover environments after authentication.
    // SPNs require the URL upfront to scope the token.
    if (!isUserBased) {
        const envUrl = await vscode.window.showInputBox({
            title: 'Create Profile: Environment URL',
            prompt: 'Enter the Dataverse environment URL',
            placeHolder: 'https://org.crm.dynamics.com',
            ignoreFocusOut: true,
            validateInput: (value) => {
                if (!value.trim()) {
                    return 'Environment URL is required for service principal authentication';
                }
                try {
                    new URL(value);
                } catch {
                    return 'Enter a valid URL (e.g., https://org.crm.dynamics.com)';
                }
                return undefined;
            },
        });

        if (envUrl === undefined) {
            return null;
        }
        params.environmentUrl = envUrl.trim();
    }

    switch (authMethodId) {
        case 'deviceCode':
        case 'interactive':
            // No additional params needed
            break;
        // ... rest of switch cases unchanged
    }

    return params;
}
```

**Step 3: Run lint, compile, and tests**

```bash
cd extension && npm run lint && npm run compile && npm run test
```

Expected: All pass.

**Step 4: Commit**

```bash
git add extension/src/commands/profileCommands.ts
git commit -m "feat(extension): inline environment picker for user-based auth

Replace raw URL text input with post-authentication environment
discovery for deviceCode and interactive flows. After the profile is
created and authenticated, discover available environments and show
a QuickPick inline. SPN flows keep the URL text input since the
token must be scoped to a specific resource."
```

---

## Task 4: Tree View Welcome & Error States

**Files:**
- Modify: `extension/package.json`
- Modify: `extension/src/extension.ts`
- Modify: `extension/src/views/profileTreeView.ts`
- Modify: `extension/src/views/solutionsTreeView.ts`

**Step 1: Add `viewsWelcome` to `package.json`**

Add a `viewsWelcome` array inside the `contributes` object (after the `configuration` block):

```json
"viewsWelcome": [
  {
    "view": "ppds.profiles",
    "contents": "Could not connect to the PPDS daemon.\n[Retry](command:ppds.refreshProfiles)\n[Show Logs](command:ppds.showLogs)",
    "when": "ppds.daemonState == 'error'"
  },
  {
    "view": "ppds.profiles",
    "contents": "No authentication profiles found.\n[Create Profile](command:ppds.createProfile)",
    "when": "ppds.daemonState == 'ready' && ppds.profileCount == 0"
  },
  {
    "view": "ppds.solutions",
    "contents": "Select a profile and environment to browse solutions.\n[Select Environment](command:ppds.selectEnvironment)",
    "when": "ppds.daemonState != 'ready'"
  }
]
```

**Step 2: Set context keys in `profileTreeView.ts`**

Update `getChildren` in `ProfileTreeDataProvider` to set context keys:

```ts
async getChildren(element?: ProfileTreeItem): Promise<ProfileTreeItem[]> {
    if (element) {
        return [];
    }

    try {
        const result = await this.daemonClient.authList();
        void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'ready');
        void vscode.commands.executeCommand('setContext', 'ppds.profileCount', result.profiles.length);
        if (result.profiles.length === 0) {
            return [];
        }
        return result.profiles.map(p => new ProfileTreeItem(p));
    } catch (err) {
        void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'error');
        void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);
        return [];
    }
}
```

Accept a `log` parameter in the constructor and log the error:

```ts
constructor(
    private readonly daemonClient: DaemonClient,
    private readonly log: vscode.LogOutputChannel,
) {}
```

In the catch block, log the error:
```ts
} catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    this.log.error(`Failed to list profiles: ${msg}`);
    void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'error');
    void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);
    return [];
}
```

**Step 3: Set context keys in `solutionsTreeView.ts`**

Same pattern — accept `log` in constructor, log errors in `getSolutions` catch block:

```ts
constructor(
    private readonly daemon: DaemonClient,
    private readonly log: vscode.LogOutputChannel,
) {}
```

In `getSolutions`:
```ts
} catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    this.log.error(`Failed to list solutions: ${msg}`);
    return [];
}
```

**Step 4: Update `extension.ts` — pass log channel to tree providers**

```ts
const profileTreeProvider = new ProfileTreeDataProvider(client, logChannel);
```

```ts
const solutionsTreeProvider = new SolutionsTreeDataProvider(client, logChannel);
```

**Step 5: Initialize context keys in `extension.ts`**

Add at the start of `activate()`, after creating the log channel:

```ts
void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'starting');
void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);
```

**Step 6: Update tests**

Update `profileTreeView.test.ts` — the `ProfileTreeDataProvider` constructor now takes a second `log` argument. Pass a mock log channel.

Update `smokeTest.test.ts` if needed — `ProfileTreeDataProvider` and `SolutionsTreeDataProvider` constructors changed.

**Step 7: Run lint, compile, and tests**

```bash
cd extension && npm run lint && npm run compile && npm run test
```

Expected: All pass.

**Step 8: Commit**

```bash
git add extension/package.json extension/src/extension.ts \
       extension/src/views/profileTreeView.ts \
       extension/src/views/solutionsTreeView.ts \
       extension/src/__tests__/views/profileTreeView.test.ts \
       extension/src/__tests__/integration/smokeTest.test.ts
git commit -m "feat(extension): add tree view welcome and error states

Add viewsWelcome contributions for Profiles and Solutions views with
actionable messages when daemon is not running, no profiles exist,
or no environment is selected. Set context keys (ppds.daemonState,
ppds.profileCount) from tree providers. Log errors instead of
silently swallowing them."
```

---

## Task 5: Invalidate Tokens Context Menu

**Files:**
- Modify: `extension/package.json`
- Modify: `extension/src/commands/profileCommands.ts`

**Step 1: Register the command in `profileCommands.ts`**

Add after the rename command registration (around line 163):

```ts
// ── Invalidate Profile Tokens ──────────────────────────────────────
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.invalidateProfile', async (item: unknown) => {
        try {
            const profileItem = item as { profile?: { index: number; name: string | null } } | undefined;
            if (!profileItem?.profile) {
                vscode.window.showWarningMessage('No profile selected.');
                return;
            }

            const { name, index } = profileItem.profile;
            const displayName = name ?? `Profile ${index}`;
            const profileName = name ?? index.toString();

            const confirm = await vscode.window.showWarningMessage(
                `Invalidate cached tokens for "${displayName}"? You will need to re-authenticate.`,
                { modal: true },
                'Invalidate',
            );

            if (confirm !== 'Invalidate') {
                return;
            }

            await daemonClient.profilesInvalidate(profileName);
            refreshProfiles();
            vscode.window.showInformationMessage(`Tokens invalidated for "${displayName}".`);
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Failed to invalidate tokens: ${message}`);
        }
    }),
);
```

**Step 2: Add command and menu entries to `package.json`**

Add to the `commands` array:
```json
{
    "command": "ppds.invalidateProfile",
    "title": "PPDS: Invalidate Tokens",
    "icon": "$(debug-disconnect)"
}
```

Add to the `view/item/context` menus array (after the deleteProfile entry):
```json
{
    "command": "ppds.invalidateProfile",
    "when": "view == ppds.profiles && viewItem == profile",
    "group": "profile@4"
}
```

**Step 3: Run lint, compile, and tests**

```bash
cd extension && npm run lint && npm run compile && npm run test
```

Expected: All pass.

**Step 4: Commit**

```bash
git add extension/package.json extension/src/commands/profileCommands.ts
git commit -m "feat(extension): add invalidate tokens context menu action

Add ppds.invalidateProfile command to the profile tree item context
menu. Clears cached tokens for a profile, forcing re-authentication
on next use. Includes confirmation dialog before invalidation."
```

---

## Task 6: Show Logs Command

**Files:**
- Modify: `extension/package.json`
- Modify: `extension/src/extension.ts`

**Step 1: Register the command in `extension.ts`**

Add after the solutions commands block:

```ts
// ── Show Logs ───────────────────────────────────────────────────────
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.showLogs', () => {
        logChannel?.show();
    }),
);
```

**Step 2: Add command entry to `package.json`**

Add to the `commands` array:
```json
{
    "command": "ppds.showLogs",
    "title": "PPDS: Show Logs",
    "icon": "$(output)"
}
```

**Step 3: Add to Tools tree view**

In `extension/src/views/toolsTreeView.ts`, add a "Show Logs" entry to the static tools array:

```ts
private static readonly tools: { label: string; commandId: string; icon: string; alwaysEnabled?: boolean }[] = [
    { label: 'Data Explorer', commandId: 'ppds.dataExplorer', icon: 'database' },
    { label: 'Notebooks', commandId: 'ppds.openNotebooks', icon: 'notebook' },
    { label: 'Solutions', commandId: 'ppds.openSolutions', icon: 'package' },
    { label: 'Show Logs', commandId: 'ppds.showLogs', icon: 'output', alwaysEnabled: true },
];
```

Update `getChildren` to respect `alwaysEnabled`:
```ts
return ToolsTreeDataProvider.tools.map(
    t => new ToolTreeItem(t.label, t.commandId, t.icon, !t.alwaysEnabled && !this.hasActiveProfile),
);
```

**Step 4: Run lint, compile, and tests**

```bash
cd extension && npm run lint && npm run compile && npm run test
```

Expected: All pass.

**Step 5: Commit**

```bash
git add extension/package.json extension/src/extension.ts \
       extension/src/views/toolsTreeView.ts
git commit -m "feat(extension): add Show Logs command and tools tree entry

Register ppds.showLogs command to open the PPDS LogOutputChannel.
Available from command palette and always-enabled in the Tools tree
view for easy discoverability during debugging."
```

---

## Verification

After all tasks:

1. `cd extension && npm run lint` — 0 errors
2. `cd extension && npm run compile` — esbuild succeeds
3. `cd extension && npm run test` — all tests pass
4. `npm run local` — install and manually verify:
   - Click away from an input dialog → it persists (Escape still cancels)
   - Create a deviceCode profile → environment picker shown inline after auth
   - No profiles → welcome message with "Create Profile" link
   - Daemon not running → error message with "Retry" and "Show Logs" links
   - Right-click profile → "Invalidate Tokens" menu item works
   - Tools tree → "Show Logs" opens the output channel
   - Output channel shows timestamped, leveled log messages
5. Push and verify CI passes
