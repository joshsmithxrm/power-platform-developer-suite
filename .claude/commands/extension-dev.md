# Extension Dev

Build, test, and debug the VS Code extension locally from any worktree or branch.

## Quick Reference

| Task | Command |
|------|---------|
| Dev build + install | `npm run local` |
| Production test build | `npm run test-release` |
| Revert to marketplace | `npm run marketplace` |
| Uninstall local | `npm run uninstall-local` |
| Unit tests | `npm run test` |
| Lint + compile + test | `npm run lint && npm run compile && npm run test` |
| E2E tests | `npm run test:e2e` |
| Watch mode | `npm run watch` |

All commands run from `extension/` relative to repo root or worktree root.

## Locate Extension Directory

The extension may be in the main repo or a worktree. Resolve the right path:

```bash
# From repo root:     ./extension/
# From worktree:      .worktrees/<name>/extension/
# Current worktree:   check `git worktree list` for the active one
```

Always `cd` into the `extension/` directory before running npm scripts.

## Build & Install Locally

```bash
cd <root>/extension && npm run local
```

What this does:
1. Increments dev version counter (e.g., `0.5.0-dev.3`)
2. Patches package.json temporarily
3. Builds production bundle (esbuild)
4. Packages VSIX (`vsce package --pre-release`)
5. Restores package.json
6. Installs VSIX (`code --install-extension --force`)

After install: reload VS Code window (Ctrl+Shift+P → "Reload Window").

## Revert to Marketplace

```bash
cd <root>/extension && npm run marketplace
```

Uninstalls local dev version and reinstalls the marketplace release.

## Automated Verification

Run before every local install:

```bash
cd <root>/extension && npm run lint && npm run compile && npm run test
```

All 3 must pass (0 lint errors, esbuild succeeds, all vitest tests pass).

## Manual Test Checklist

After installing locally, verify these in VS Code:

- Activity bar: PPDS icon → sidebar opens with Profiles/Tools/Solutions
- Create profile: deviceCode flow → environment picker shown inline after auth
- Input persistence: alt-tab away from input dialog → returns intact (Escape cancels)
- Tree welcome: no profiles → "Create Profile" link. Daemon down → "Retry" + "Show Logs"
- Select environment: QuickPick → status bar updates
- Invalidate tokens: right-click profile → "Invalidate Tokens" with confirmation
- Show Logs: Tools tree → "Show Logs" opens timestamped output channel
- Notebooks: create .ppdsnb → SQL query → execute → results rendered
- IntelliSense: SQL/FetchXML completions in notebook cells
- Data Explorer: command palette → "PPDS: Data Explorer"
- Solutions: browse tree, toggle managed filter
- Export: CSV/JSON export from notebook cell results

## F5 Debugging

Open repo in VS Code → F5. Launches extension from source. In this mode:
- Bundled CLI binary NOT available (only in packaged VSIX)
- Falls back to `ppds` from PATH
- Requires `ppds` CLI installed and in PATH

## Iterative Fix Loop

When issues found during manual testing:
1. Identify the issue (which command/view/feature)
2. Check logs: "PPDS: Show Logs" in command palette
3. Fix source code
4. `npm run lint && npm run compile && npm run test`
5. `npm run local` to reinstall
6. Reload VS Code and re-verify
7. Repeat until green

## Platform-Specific VSIX Packaging

For testing platform-specific builds (bundled CLI binary):

```bash
npm run package:win32-x64    # Windows
npm run package:linux-x64    # Linux
npm run package:darwin-x64   # macOS Intel
npm run package:darwin-arm64  # macOS Apple Silicon
```

Requires .NET 8 SDK for `dotnet publish`.
