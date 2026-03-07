# Open in Maker Portal / Open in Dynamics 365 — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add "Open in Maker Portal" and "Open in Dynamics 365" commands that open the browser for any profile's environment, from both the command palette and profile tree context menu.

**Architecture:** Two VS Code commands backed by a shared profile-picking helper. Command palette shows a quick pick pre-selecting the active profile; tree context menu passes the profile directly. The daemon's RPC DTO is patched to include `environmentId` (already in the domain model, just not serialized).

**Tech Stack:** TypeScript (VS Code extension API), C# (daemon RPC handler)

**Design doc:** `docs/plans/2026-03-06-open-in-browser-commands-design.md`

---

### Task 1: Add `environmentId` to the RPC EnvironmentSummary DTO

The domain model `EnvironmentSummary` in `IEnvironmentService.cs:92` already carries `EnvironmentId`. The RPC DTO in `RpcMethodHandler.cs` strips it during serialization. Fix both the DTO class and the mapping.

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:1743-1750` (DTO class)
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:103-107` (mapping)

**Step 1: Add `EnvironmentId` property to the RPC DTO class**

In `RpcMethodHandler.cs`, find the `EnvironmentSummary` class (line ~1743) and add the property:

```csharp
public class EnvironmentSummary
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }
}
```

**Step 2: Include `EnvironmentId` in the mapping**

In the same file, find the `auth/list` handler mapping (line ~103) and add the field:

```csharp
Environment = p.Environment != null ? new EnvironmentSummary
{
    Url = p.Environment.Url,
    DisplayName = p.Environment.DisplayName,
    EnvironmentId = p.Environment.EnvironmentId
} : null,
```

**Step 3: Build to verify**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj`
Expected: Build succeeded

**Step 4: Commit**

```
feat(daemon): expose environmentId in auth/list EnvironmentSummary
```

---

### Task 2: Update TypeScript `EnvironmentSummary` interface

**Files:**
- Modify: `extension/src/types.ts:26-29`

**Step 1: Add `environmentId` to the interface**

```typescript
export interface EnvironmentSummary {
    url: string;
    displayName: string;
    environmentId: string | null;
}
```

**Step 2: Verify no type errors**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors (the new field is nullable so existing code doesn't break)

**Step 3: Commit**

```
feat(extension): add environmentId to EnvironmentSummary type
```

---

### Task 3: Implement browser commands

Create a new command file for the browser commands. These are separate from profile commands because they're a different concern (navigation, not profile management).

**Files:**
- Create: `extension/src/commands/browserCommands.ts`

**Step 1: Write the test**

Create `extension/src/__tests__/commands/browserCommands.test.ts`:

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock vscode module
const mockShowQuickPick = vi.fn();
const mockShowInformationMessage = vi.fn();
const mockShowErrorMessage = vi.fn();
const mockOpenExternal = vi.fn();
const mockUriParse = vi.fn((url: string) => ({ toString: () => url }));

vi.mock('vscode', () => ({
    window: {
        showQuickPick: mockShowQuickPick,
        showInformationMessage: mockShowInformationMessage,
        showErrorMessage: mockShowErrorMessage,
    },
    env: {
        openExternal: mockOpenExternal,
    },
    Uri: {
        parse: mockUriParse,
    },
}));

import { buildMakerUrl, buildDynamicsUrl } from '../../commands/browserCommands.js';

describe('browserCommands', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    describe('buildMakerUrl', () => {
        it('returns deep link when environmentId is present', () => {
            const url = buildMakerUrl('abc-123');
            expect(url).toBe('https://make.powerapps.com/environments/abc-123/solutions');
        });

        it('returns base URL when environmentId is null', () => {
            const url = buildMakerUrl(null);
            expect(url).toBe('https://make.powerapps.com');
        });
    });

    describe('buildDynamicsUrl', () => {
        it('returns the environment URL directly', () => {
            const url = buildDynamicsUrl('https://org.crm.dynamics.com');
            expect(url).toBe('https://org.crm.dynamics.com');
        });

        it('strips trailing slash', () => {
            const url = buildDynamicsUrl('https://org.crm.dynamics.com/');
            expect(url).toBe('https://org.crm.dynamics.com');
        });
    });
});
```

**Step 2: Run test to verify it fails**

Run: `cd extension && npx vitest run src/__tests__/commands/browserCommands.test.ts`
Expected: FAIL — module not found

**Step 3: Implement browserCommands.ts**

Create `extension/src/commands/browserCommands.ts`:

```typescript
import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { ProfileInfo } from '../types.js';

const MAKER_BASE_URL = 'https://make.powerapps.com';

export function buildMakerUrl(environmentId: string | null): string {
    if (environmentId) {
        return `${MAKER_BASE_URL}/environments/${environmentId}/solutions`;
    }
    return MAKER_BASE_URL;
}

export function buildDynamicsUrl(environmentUrl: string): string {
    return environmentUrl.replace(/\/+$/, '');
}

/**
 * Shows a quick pick of profiles that have environments, pre-selecting the active one.
 * Returns the selected profile, or undefined if cancelled.
 */
async function pickProfileWithEnvironment(daemonClient: DaemonClient): Promise<ProfileInfo | undefined> {
    const result = await daemonClient.authList();

    const profilesWithEnv = result.profiles.filter(p => p.environment != null);

    if (profilesWithEnv.length === 0) {
        vscode.window.showInformationMessage('No profiles have an environment selected. Use "PPDS: Select Environment" first.');
        return undefined;
    }

    interface ProfileQuickPickItem extends vscode.QuickPickItem {
        profile: ProfileInfo;
    }

    const items: ProfileQuickPickItem[] = profilesWithEnv.map(p => ({
        label: p.name ?? `Profile ${p.index}`,
        description: p.environment!.displayName,
        detail: p.environment!.url,
        picked: p.isActive,
        profile: p,
    }));

    const selected = await vscode.window.showQuickPick(items, {
        title: 'Select Profile',
        placeHolder: 'Choose which environment to open',
        ignoreFocusOut: true,
    });

    return selected?.profile;
}

/**
 * Resolves the target profile: uses the tree item's profile if provided,
 * otherwise shows a quick pick.
 */
async function resolveProfile(
    item: unknown,
    daemonClient: DaemonClient,
): Promise<ProfileInfo | undefined> {
    // If invoked from tree context menu, item has the profile
    const profileItem = item as { profile?: ProfileInfo } | undefined;
    if (profileItem?.profile) {
        return profileItem.profile;
    }

    // Otherwise show picker
    return pickProfileWithEnvironment(daemonClient);
}

/**
 * Registers browser navigation commands and returns the disposables.
 *
 * Commands registered:
 * - ppds.openInMaker    — open Maker Portal for a profile's environment
 * - ppds.openInDynamics — open Dynamics 365 for a profile's environment
 */
export function registerBrowserCommands(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
): void {

    // ── Open in Maker Portal ─────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openInMaker', async (item?: unknown) => {
            try {
                const profile = await resolveProfile(item, daemonClient);
                if (!profile) return;

                if (!profile.environment) {
                    vscode.window.showInformationMessage(
                        `No environment selected for "${profile.name ?? `Profile ${profile.index}`}".`
                    );
                    return;
                }

                const url = buildMakerUrl(profile.environment.environmentId ?? null);

                if (!profile.environment.environmentId) {
                    vscode.window.showInformationMessage(
                        'Environment ID not available — opening Maker Portal home. Select the environment manually.'
                    );
                }

                await vscode.env.openExternal(vscode.Uri.parse(url));
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to open Maker Portal: ${message}`);
            }
        }),
    );

    // ── Open in Dynamics 365 ──────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openInDynamics', async (item?: unknown) => {
            try {
                const profile = await resolveProfile(item, daemonClient);
                if (!profile) return;

                if (!profile.environment) {
                    vscode.window.showInformationMessage(
                        `No environment selected for "${profile.name ?? `Profile ${profile.index}`}".`
                    );
                    return;
                }

                const url = buildDynamicsUrl(profile.environment.url);
                await vscode.env.openExternal(vscode.Uri.parse(url));
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to open Dynamics 365: ${message}`);
            }
        }),
    );
}
```

**Step 4: Run test to verify it passes**

Run: `cd extension && npx vitest run src/__tests__/commands/browserCommands.test.ts`
Expected: PASS

**Step 5: Commit**

```
feat(extension): add Open in Maker Portal and Open in Dynamics commands
```

---

### Task 4: Register commands in package.json and extension.ts

**Files:**
- Modify: `extension/package.json` (commands array, menus)
- Modify: `extension/src/extension.ts`

**Step 1: Add command definitions to package.json `contributes.commands` array**

Add after the `ppds.showLogs` entry (line ~202):

```json
{
  "command": "ppds.openInMaker",
  "title": "PPDS: Open in Maker Portal",
  "icon": "$(link-external)"
},
{
  "command": "ppds.openInDynamics",
  "title": "PPDS: Open in Dynamics 365",
  "icon": "$(link-external)"
}
```

**Step 2: Add context menu entries to `contributes.menus.view/item/context`**

Add after the `ppds.invalidateProfile` entry (line ~252):

```json
{
  "command": "ppds.openInMaker",
  "when": "view == ppds.profiles && viewItem == profile",
  "group": "open@1"
},
{
  "command": "ppds.openInDynamics",
  "when": "view == ppds.profiles && viewItem == profile",
  "group": "open@2"
}
```

**Step 3: Register commands in extension.ts**

Add import at the top of `extension.ts`:

```typescript
import { registerBrowserCommands } from './commands/browserCommands.js';
```

Add registration after the `registerEnvironmentConfigCommand` call (after line ~104):

```typescript
// ── Browser Commands ──────────────────────────────────────────────
registerBrowserCommands(context, client);
```

**Step 4: Build and lint**

Run: `cd extension && npm run compile && npm run lint`
Expected: No errors

**Step 5: Commit**

```
feat(extension): register Open in Maker/Dynamics in package.json and activation
```

---

### Task 5: Run full test suite and verify

**Files:** None (verification only)

**Step 1: Run unit tests**

Run: `cd extension && npm run test`
Expected: All tests pass including new browserCommands tests

**Step 2: Run lint**

Run: `cd extension && npm run lint`
Expected: No errors

**Step 3: Build daemon**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj`
Expected: Build succeeded

**Step 4: Final commit if any fixups needed**

If tests or lint required changes, commit those fixes.
