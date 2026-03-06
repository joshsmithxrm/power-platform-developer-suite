# VS Code Extension UX Fixes Design

> **Date:** 2026-03-06
> **Status:** Approved
> **Branch:** feature/vscode-extension-mvp

## Problem

The VS Code extension MVP has several UX issues discovered during manual testing:

1. No structured logging — errors are silently swallowed, making debugging impossible
2. Input dialogs dismiss when clicking outside VS Code (e.g., to copy an App ID from Azure Portal)
3. Profile creation asks for a raw environment URL when we can discover environments after authentication
4. Tree views show empty panels with no explanation when the daemon isn't running or data is unavailable
5. Token invalidation is wired in the daemon client but has no UI trigger
6. No quick way to access extension logs

## Design

### 1. Structured Logging via LogOutputChannel

Replace the raw `vscode.OutputChannel` in `daemonClient.ts` with `vscode.LogOutputChannel` (created via `vscode.window.createOutputChannel('PPDS', { log: true })`).

`LogOutputChannel` provides `.info()`, `.warn()`, `.error()`, `.debug()`, `.trace()` with automatic timestamps and level filtering. VS Code writes contents to `~/.vscode/logs/` on disk. No custom logger interface needed.

Create the channel once in `extension.ts` and pass it to:
- `DaemonClient` (replace all `appendLine()` calls with appropriate log levels)
- Tree data providers (log fetch errors instead of silently returning `[]`)
- Command handlers (log errors before showing user-facing messages)

### 2. Input Focus Persistence

Add `ignoreFocusOut: true` to every `showQuickPick` and `showInputBox` call across:
- `profileCommands.ts` — create wizard, rename
- `environmentCommands.ts` — environment selector, manual URL input
- `environmentConfigCommand.ts` — config inputs
- `DataverseNotebookController.ts` — notebook environment picker

Users can still cancel with Escape.

### 3. Profile Creation — Inline Environment Picker

**User-based auth (deviceCode, interactive):**

1. Pick auth method
2. Enter profile name (optional)
3. Call `profilesCreate` — authentication happens (device code/browser flow)
4. On success, call `env/list` to discover environments
5. Show environment QuickPick inline as part of the creation flow
6. Call `env/select` with their choice
7. Done — one cohesive flow

**SPN flows (clientSecret, certificate):**

Keep the existing URL text input (required — can't authenticate without it). Collect SPN-specific params. Create profile. No post-creation environment picker needed.

Remove the auto-launch of `ppds.selectEnvironment` after profile creation (line 406 in profileCommands.ts) — environment selection is now handled inline for user-based auth, and unnecessary for SPNs.

Profile name remains optional for user-based auth (consistent with CLI/TUI). Unnamed profiles display as "Profile {index}" in the tree view.

### 4. Tree View Welcome & Error States

Use VS Code's `viewsWelcome` contribution point in `package.json` for declarative welcome content. Set context keys (`ppds.daemonRunning`, `ppds.profileCount`) via `setContext` based on daemon state.

States to handle:
- **Daemon not running:** "PPDS daemon is not running" + retry link
- **No profiles:** "No profiles found" + create profile link
- **No environment selected:** Solutions/Tools show "Select an environment to get started"
- **Error fetching:** Log to LogOutputChannel, show generic message with link to output channel

Tree providers set context keys when they succeed or fail to fetch data.

### 5. Invalidate Tokens Context Menu

Register `ppds.invalidateProfile` command that calls `daemonClient.profilesInvalidate()`. Add to profile tree item context menu alongside Delete and Rename. Show confirmation before invalidating. Refresh profile tree after success.

The RPC endpoint and daemon client method already exist — this only needs a command registration and a `package.json` menu entry.

### 6. Show Logs Command

Register `ppds.showLogs` command that calls `logChannel.show()`. Add to:
- Command palette as "PPDS: Show Logs"
- Tools tree view for discoverability

## Files Affected

| File | Changes |
|------|---------|
| `extension/src/extension.ts` | Create LogOutputChannel, pass to consumers |
| `extension/src/daemonClient.ts` | Accept LogOutputChannel, replace appendLine with leveled logging |
| `extension/src/commands/profileCommands.ts` | ignoreFocusOut, inline env picker, invalidate command |
| `extension/src/commands/environmentCommands.ts` | ignoreFocusOut |
| `extension/src/commands/environmentConfigCommand.ts` | ignoreFocusOut |
| `extension/src/notebooks/DataverseNotebookController.ts` | ignoreFocusOut |
| `extension/src/views/profileTreeView.ts` | Accept logger, set context keys, log errors |
| `extension/src/views/toolsTreeView.ts` | Set context keys |
| `extension/src/views/solutionsTreeView.ts` | Accept logger, set context keys, log errors |
| `extension/package.json` | viewsWelcome, invalidateProfile command+menu, showLogs command |
| `extension/src/__tests__/daemonClient.test.ts` | Update mocks for LogOutputChannel |
| `extension/src/__tests__/integration/smokeTest.test.ts` | Update mocks |

## Out of Scope

- Managed Identity / GitHub Federated / Azure DevOps Federated auth methods (CI/CD-only, not interactive UI flows)
- Clear all profiles command (destructive nuclear option, CLI-only is fine)
- Update environment on profile (low priority, select env after switching profile)
- Webview panel for profile creation (QuickPick wizard is idiomatic VS Code for infrequent operations)
