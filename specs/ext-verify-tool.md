# Extension Verify Tool

**Status:** Draft
**Last Updated:** 2026-03-15
**Code:** [src/PPDS.Extension/tools/](../src/PPDS.Extension/tools/) | None
**Surfaces:** Extension

---

## Overview

A CLI tool that launches and controls VS Code via Playwright's Electron integration, providing AI agents with full visual and interactive access to extension webview panels. Enables screenshots, DOM interaction, command palette execution, keyboard shortcuts, console log capture, and output channel reading — closing the feedback loop between implementation and visual verification.

### Version History

- **v1.0** — Raw CDP over WebSocket. Proved the concept but could not execute VS Code commands, read console logs, or reliably trigger keyboard shortcuts in VS Code's native UI.
- **v2.0** (this version) — Playwright Electron engine. Replaces raw CDP with `@playwright/test`'s `_electron` module. Fixes all v1 gaps: command palette, keyboard shortcuts, console capture, webview frame traversal, output channel logs.

### Goals

- **Visual feedback**: AI agents can take screenshots of webview panels and full VS Code window
- **DOM interaction**: Click elements, type text, send keyboard shortcuts, right-click context menus, interact with dropdowns and checkboxes — in both webview content and VS Code's native UI
- **Command execution**: Open panels, run VS Code commands, interact with the command palette
- **Telemetry**: Capture console output, page errors, and extension output channel logs
- **Self-managing lifecycle**: Launch and close VS Code instances without manual intervention
- **Generic and stable**: No knowledge of any specific webview's DOM structure

### Non-Goals

- Debugging (breakpoints, step-through) — use VS Code's built-in debugger
- Attaching to a user's running VS Code instance (deferred — `attach` may return in a future version)
- Automated regression testing in CI — use Vitest unit tests and Playwright E2E tests
- Recording/playback of interaction sequences

---

## Architecture

```
Claude Code
    │
    │  Bash: node webview-cdp.mjs <command> [args]
    ▼
┌──────────────────────────┐
│   webview-cdp CLI         │  Short-lived process per command
│   (caller)                │  Reads .webview-cdp-session.json
└───────┬──────────────────┘
        │
        ├── launch:  forks daemon → daemon starts VS Code + HTTP server
        │            daemon writes session file with daemonPort + PID
        │
        ├── other commands:  POST http://localhost:<daemonPort>/execute
        │   daemon receives request → executes via Electron Page
        │   → uses Playwright Electron APIs (keyboard, frames, etc.)
        │   → returns result as JSON
        │
        └── close:  POST /shutdown to daemon
                    daemon calls electronApp.close() → VS Code shuts down
                    daemon deletes session file and exits

┌─────────────────────────┐     ┌──────────────────────────┐
│  webview-cdp daemon      │────▶│  VS Code (Electron)       │
│  (long-lived background) │     │                           │
│                          │     │  ┌─────────────────────┐  │
│  HTTP server on random   │     │  │ Main Page            │  │
│    port for CLI commands │     │  │  ├─ Sidebar          │  │
│  Holds ElectronApp alive │     │  │  ├─ Command Palette  │  │
│  Captures console logs   │     │  │  ├─ Notifications    │  │
│  Executes ALL Playwright │     │  │  └─ Webview Panel    │  │
│    commands via Electron │     │  │     └─ outer iframe  │  │
│    Page (not CDP)        │     │  │        └─ inner frame│  │
└─────────────────────────┘     │  └─────────────────────┘  │
                                └──────────────────────────┘
```

### Connection Model

**Why a daemon:** Playwright's `_electron.launch()` creates VS Code as a child process. When the parent Node.js process exits, the child is killed. Empirically verified: the wsEndpoint becomes invalid immediately on parent exit. A long-lived daemon process is required to keep VS Code alive across multiple CLI invocations.

**Launch** forks a daemon process (`node webview-cdp.mjs --daemon`). The daemon:
1. Calls `_electron.launch()` to start VS Code
2. Waits for workbench ready
3. Installs dialog interception hooks
4. Starts console capture (`page.on('console')`, `page.on('pageerror')`) writing to `.webview-cdp-console.log`
5. Starts HTTP server on a random available port for CLI commands
6. Writes session file with `{ daemonPort, daemonPid, userDataDir, logFile }`
7. Listens for `SIGTERM` / `SIGINT` to trigger clean shutdown
8. The foreground `launch` process polls until the session file appears, prints confirmation, and exits

**Subsequent commands** send HTTP requests to the daemon's local server. The daemon executes them using the Electron-mode Playwright `Page` (which has full keyboard, frame traversal, and input capabilities). This avoids CDP reconnection entirely — all Playwright interaction happens in the daemon process where the Electron APIs work correctly. The daemon returns results as JSON over HTTP.

**Close** reads the daemon PID from the session file and sends `SIGTERM`. The daemon catches this, calls `electronApp.close()` (which cleanly shuts down VS Code and its process tree), deletes the session file and console log, then exits.

**Orphan detection:** If `launch` finds an existing session file, it pings the daemon's HTTP server (`GET http://localhost:<daemonPort>/health`). If the ping succeeds, the previous instance is still running — the tool warns and exits. If the ping fails, the session is stale — the tool kills the daemon PID (if still alive), deletes the stale session file, and proceeds with a fresh launch.

### Webview Frame Traversal

VS Code renders extension webviews inside a double-nested iframe structure:

```
Page (VS Code workbench)
  └─ iframe[class*="webview"] (webview container)
       └─ iframe (active-frame — extension's actual DOM)
```

The tool traverses this using the proven pattern from the archived E2E helpers:

```js
const outerIframe = await page.waitForSelector('iframe[class*="webview"]');
const outerFrame = await outerIframe.contentFrame();
const innerIframe = await outerFrame.waitForSelector('iframe');
const innerFrame = await innerIframe.contentFrame();
// innerFrame is the extension's DOM — use frame.click(), frame.evaluate(), etc.
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `webview-cdp.mjs` | CLI entry point (caller mode) + daemon mode (`--daemon` flag). Single file handles both roles. |
| Daemon process | Long-lived background process holding `ElectronApplication` alive, capturing console logs continuously |
| Launch engine | `_electron.launch()` + `@vscode/test-electron` for VS Code binary management |
| Session file | `.webview-cdp-session.json` — persists `wsEndpoint`, `daemonPid`, `userDataDir`, `logFile` |
| Frame resolver | `contentFrame()` chain to reach webview inner frames |
| Command palette helper | Open palette, type command, select result — adapted from archived `CommandPaletteHelper.ts` pattern |
| Console capture | `page.on('console')` + `page.on('pageerror')` running in daemon, writing JSONL to `.webview-cdp-console.log` |
| Skill file | `.agents/skills/webview-cdp/SKILL.md` — teaches AI agents when/how to use the tool. **Must be fully rewritten for v2** — the v1 skill file actively contradicts v2 capabilities (claims keyboard shortcuts don't work, documents removed `attach` command, missing `command`/`wait`/`logs`). Must also include guidance to save screenshots to temp directories. |

### Dependencies

- **Runtime:** Node.js (already required by the extension build toolchain)
- **npm:** `@playwright/test` (already a dev dependency), `@vscode/test-electron` (already a dev dependency)
- **Removed:** `ws` (no longer needed — Playwright handles all WebSocket communication)
- Depends on: [architecture.md](./architecture.md) (extension webview panel architecture)

---

## Specification

### Core Requirements

1. The tool MUST launch VS Code via Playwright's `_electron.launch()` and manage the full lifecycle
2. The tool MUST traverse the double-nested iframe structure to reach webview content
3. The tool MUST execute VS Code commands via the command palette
4. The tool MUST capture console output and page errors during the session
5. The tool MUST provide access to extension output channel logs
6. The tool MUST NOT contain any knowledge of specific webview DOM structures — it is a generic pipe
7. Screenshots MUST capture the webview content as rendered in VS Code
8. Keyboard shortcuts MUST trigger VS Code's native keybinding system

### Command Interface

| Command | Signature | Purpose |
|---------|-----------|---------|
| `launch` | `launch [workspace]` | Start VS Code with extension via Playwright Electron |
| `close` | `close` | Shut down VS Code cleanly via Playwright |
| `connect` | `connect` | List available webview frames |
| `command` | `command "<vs-code-command>"` | Execute a VS Code command via command palette |
| `wait` | `wait [timeout] [--ext "<id>"]` | Wait until a webview frame appears (optionally matching extension ID) |
| `screenshot` | `screenshot <file> [--page] [--target N]` | Capture webview content (default) or full window (`--page`) as PNG |
| `eval` | `eval "<js>" [--page] [--target N]` | Run JavaScript in webview or page context |
| `click` | `click "<selector>" [--right] [--page] [--target N]` | Click element in webview or VS Code UI |
| `type` | `type "<selector>" "<text>" [--page] [--target N]` | Type text into an input element |
| `select` | `select "<selector>" "<value>" [--page] [--target N]` | Select dropdown option |
| `key` | `key "<combo>" [--page]` | Send keyboard shortcut (works everywhere now) |
| `mouse` | `mouse <event> <x> <y> [--page] [--target N]` | Dispatch mouse event at coordinates |
| `logs` | `logs [--channel "<name>"] [--level "<level>"]` | Read captured console logs or output channel content |

### Primary Flows

**Launch flow (caller process):**

1. **Check for stale session**: If session file exists, try pinging daemon HTTP server. If ping succeeds, warn "VS Code already running" and exit. If fails, kill stale daemon PID, delete session file.
2. **Fork daemon**: Spawn `node webview-cdp.mjs --daemon [workspace]` as a detached background process with stdio piped to `/dev/null`.
3. **Wait for session**: Poll for session file to appear (daemon writes it when VS Code is ready), up to 60 seconds.
4. **Print confirmation**: Read session file, output "VS Code launched on port X (daemon PID Y)" to stdout, exit.

**Launch flow (daemon process — `--daemon` flag):**

1. **Download VS Code** (if not cached): `@vscode/test-electron` handles this automatically
2. **Resolve extension path**: Find `src/PPDS.Extension/` relative to tool location
3. **Launch via Playwright**: `_electron.launch({ executablePath, args: ['--extensionDevelopmentPath=...', '--user-data-dir=...', '--log=trace'] })`
4. **Get main window**: `electronApp.firstWindow()`
5. **Wait for workbench**: `page.waitForSelector('[id="workbench.parts.editor"]', { timeout: 60000 })`
6. **Intercept native dialogs**: `electronApp.evaluate()` to hook `dialog.showMessageBox`, `dialog.showOpenDialog`, `dialog.showSaveDialog` — auto-dismiss to prevent blocking. If hooks fail, log a warning and continue.
7. **Start console capture**: `page.on('console', ...)` and `page.on('pageerror', ...)` — write JSONL to `.webview-cdp-console.log` continuously
8. **Start HTTP server**: Listen on a random available port. Expose `/execute` (command dispatch), `/shutdown` (clean exit), `/health` (liveness check).
9. **Write session file**: `{ daemonPort, daemonPid: process.pid, userDataDir, logFile }`
10. **Wait for shutdown**: Listen for `SIGTERM` / `SIGINT` / `/shutdown` HTTP request. On any, call `electronApp.close()`, delete session file, delete console log, stop HTTP server, exit.

**Command execution flow (all commands except launch/close):**

1. **Read session**: Load `daemonPort` from `.webview-cdp-session.json`
2. **Send request**: POST to `http://localhost:<daemonPort>/execute` with the command, args, and flags as JSON
3. **Daemon executes**: The daemon resolves the target (page or webview frame via `contentFrame()` chain), executes using Playwright's Electron-mode APIs, and returns the result as JSON
4. **Output result**: CLI prints the result to stdout
5. **No CDP reconnection**: All Playwright interaction happens in the daemon process where the Electron `Page` has full input capabilities (keyboard shortcuts, frame traversal, etc.)

**Command palette flow (`command` action):**

1. **Connect** to page (same as above)
2. **Press** `Control+Shift+P` via `page.keyboard.press()`
3. **Wait** for `.quick-input-widget` to become visible (timeout 5 seconds)
4. **Type** the command name via `page.keyboard.type(text, { delay: 50 })`
5. **Wait** (up to 5 seconds) for either a result item (`.quick-input-list .quick-input-list-entry`) or the "No matching commands" indicator. If no results, report "Command not found: \<command\>" and press Escape to close palette.
6. **Press** Enter to execute the first result
7. **Wait** brief delay (500ms) for command to take effect
8. **Check** for error notifications (`.notifications-toasts .notification-toast.error`)

**Close flow:**

1. **Read session**: Load `daemonPort` and `daemonPid` from session file
2. **Request shutdown**: POST to `http://localhost:<daemonPort>/shutdown`. The daemon receives this, calls `electronApp.close()`, deletes session file and console log, then exits.
3. **Wait for cleanup**: Poll until the session file disappears (up to 10 seconds).
4. **Force kill if needed**: If session file still exists after timeout, force-kill the daemon PID and clean up manually.
5. **Print confirmation**: Output to stdout

**Logs flow:**

1. **Read session**: Load `logFile` and `userDataDir` paths from session file
2. **If `--channel`**: Read extension logs from `userDataDir/logs/` directory, filter by channel name
3. **If no flag**: Read the companion console log file (`.webview-cdp-console.log`). The daemon writes to this file continuously via `page.on('console')`, so it always contains the full session's console output up to the current moment.
4. **If `--level`**: During `launch`, VS Code was started with `--log=<level>` (default: `trace`). The `logs` command reports what level is active. To change it, the agent would need to relaunch.
5. **Print**: Output log content to stdout

### Validation Rules

| Input | Rule | Error |
|-------|------|-------|
| `screenshot <file>` | File path must be writable (parent directory exists) | "Cannot write to: <path>" |
| `eval "<js>"` | Expression must be non-empty string. Note: bash may expand `${}` and backticks in double-quoted expressions. Use single quotes for the outer shell quoting when expressions contain template literals: `eval 'document.querySelector("td").textContent'` | "Empty expression" |
| `click "<selector>"` | Selector must be non-empty string | "Empty selector" |
| `type "<selector>" "<text>"` | Both selector and text must be provided | "Usage: type \<selector\> \<text\>" |
| `select "<selector>" "<value>"` | Value matches option's `value` attribute or visible text | "Option not found: \<value\>" |
| `mouse <event>` | Event must be one of: `mousedown`, `mousemove`, `mouseup` | "Invalid mouse event" |
| `mouse <x> <y>` | Coordinates must be non-negative numbers | "Invalid coordinates" |
| `key "<combo>"` | Non-empty. Modifiers must be: `ctrl`, `shift`, `alt`, `meta` | "Invalid key combo" |
| `command "<cmd>"` | Non-empty string | "Empty command" |
| `wait [timeout]` | Timeout must be positive integer (milliseconds), default 30000. `--ext` filters by extension ID in webview URL. | "Invalid timeout" |
| `--target N` | Non-negative integer within range of discovered webview frames. Alternative: `--ext "<extensionId>"` selects webview by extension ID from the URL (more stable than index). | "Target index N out of range" / "No webview found for extension: \<id\>" |

### Constraints

- Single file implementation (`src/PPDS.Extension/tools/webview-cdp.mjs`) — handles both caller and daemon modes via `--daemon` flag
- Uses `@playwright/test` and `@vscode/test-electron` (both already dev dependencies)
- `ws` dependency removed — Playwright handles all communication
- Session file: `src/PPDS.Extension/tools/.webview-cdp-session.json` (gitignored)
- Console log file: `src/PPDS.Extension/tools/.webview-cdp-console.log` (gitignored)
- User data dir: `src/PPDS.Extension/tools/.webview-cdp-profile/` (gitignored)
- All errors to stderr, all results to stdout
- Exit code 0 on success, 1 on error
- Single-user tool — not designed for concurrent access
- `@vscode/test-electron` downloads a specific VS Code build (cached between runs)
- Daemon process runs detached — survives if the calling terminal is closed
- Screenshots should be saved to temp directories, never to the repo working tree

### Security Considerations

- **Local development only**: Connects exclusively to locally-launched VS Code instances.
- **Process spawn (CONSTITUTION S2)**: The `launch` command forks a daemon via `child_process.fork()` (no shell needed). The daemon delegates to `@vscode/test-electron` and Playwright's `_electron.launch()`. No `shell: true` anywhere — Playwright handles Electron process management directly.
- **Eval output and secrets (CONSTITUTION S3)**: The `eval` command returns whatever the JavaScript expression produces. The skill file instructs agents to avoid evaluating expressions that read sensitive data.
- **No innerHTML (CONSTITUTION S1)**: The tool does not render any HTML. All output is plain text or binary PNG files.
- **Native dialog interception**: The `launch` command hooks `dialog.showMessageBox`, `dialog.showOpenDialog`, and `dialog.showSaveDialog` to auto-dismiss native OS dialogs. This prevents dialogs from blocking automated interaction. The hooks auto-cancel file dialogs and auto-accept message dialogs. This needs empirical verification during integration testing — if `electronApp.evaluate()` cannot access the `dialog` module, the tool will document native dialogs as a limitation and skip the hooks.

---

## Acceptance Criteria

Note: All acceptance criteria require a running VS Code instance and are verified via integration testing. Pure functions (argument parsing, key combo parsing) retain unit tests in `src/PPDS.Extension/tools/webview-cdp.test.mjs`.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `launch` starts VS Code via Playwright Electron with `--extensionDevelopmentPath`, waits for workbench ready, and writes session file with `wsEndpoint` | Manual: run `launch`, verify VS Code opens and session file contains `wsEndpoint` | 🔲 |
| AC-02 | `close` shuts down VS Code cleanly via Playwright and deletes session file | Manual: run `close` after `launch`, verify VS Code closes and no orphaned processes remain | 🔲 |
| AC-03 | `connect` lists available webview frames with extension ID identification | Manual: open a webview panel, run `connect`, verify frames listed | 🔲 |
| AC-04 | `command` executes a VS Code command via command palette | Manual: run `command "ppds.dataExplorer"`, verify Data Explorer panel opens | 🔲 |
| AC-05 | `wait` blocks until a webview frame appears, then returns | Manual: open panel, run `wait`, verify it returns when webview is detected | 🔲 |
| AC-06 | `screenshot` captures full VS Code window as PNG | Manual: run `screenshot`, open PNG and verify it shows VS Code with extension | 🔲 |
| AC-07 | `screenshot` (default, no `--page`) captures webview panel content | Manual: run `screenshot`, verify PNG shows webview content | 🔲 |
| AC-08 | `eval` executes JavaScript in the webview frame context and returns result | Manual: run `eval "1 + 1"`, verify output is `2` | 🔲 |
| AC-09 | `eval --page` executes JavaScript in VS Code's main page context | Manual: run `eval --page "document.title"`, verify VS Code title returned | 🔲 |
| AC-10 | `click` triggers click on element inside webview frame | Manual: click a button in the webview, screenshot to verify | 🔲 |
| AC-11 | `click --page` triggers click on VS Code native UI element | Manual: click a sidebar item, verify panel opens | 🔲 |
| AC-12 | `click --right` triggers right-click / context menu | Manual: right-click an element, screenshot to verify context menu | 🔲 |
| AC-13 | `type` fills text into an input element inside webview | Manual: type into SQL editor, eval to verify value | 🔲 |
| AC-14 | `key` triggers VS Code keyboard shortcuts in native UI | Manual: run `key "ctrl+shift+p"`, screenshot to verify command palette opens | 🔲 |
| AC-15 | `key` triggers keyboard shortcuts inside webview | Manual: run `key "ctrl+enter"` after focusing webview, verify query execution | 🔲 |
| AC-16 | `mouse` dispatches mouse events at coordinates | Manual: dispatch drag sequence, screenshot to verify selection | 🔲 |
| AC-17 | `select` selects dropdown option inside webview | Manual: select from a dropdown, eval to verify | 🔲 |
| AC-18 | `logs` returns captured console output from the session | Manual: trigger something that logs, run `logs`, verify output contains expected messages | 🔲 |
| AC-19 | `logs --channel "PPDS"` reads extension output channel logs | Manual: run `logs --channel "PPDS"`, verify PPDS extension logs returned | 🔲 |
| AC-20 | `--target N` selects specific webview when multiple panels open | Manual: open 2 panels, verify `--target 0` and `--target 1` show different content | 🔲 |
| AC-21 | Tool reconnects cleanly on each invocation via `connectOverCDP` | Manual: run 5 commands in sequence, all succeed | 🔲 |
| AC-22 | No orphaned processes after `close` (including daemon) | Manual: run `close`, verify no VS Code or ppds processes remain from the launched instance | 🔲 |
| AC-23 | Native dialog interception is attempted during launch. If hooks install successfully, native dialogs are auto-dismissed. If hooks fail, a warning is logged and the tool continues without interception (degraded but functional). | Manual: verify launch logs show either "Dialog hooks installed" or "Dialog hooks not available — native dialogs may block" | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No session | Any command without prior `launch` | stderr: "No session found. Run `webview-cdp launch` first.", exit 1 |
| VS Code crashed | Command after VS Code died | stderr: "Connection failed. VS Code may have closed.", exit 1 |
| No webview open | `screenshot` or `eval` without panel open | stderr: "No webview found. Open a panel first.", exit 1 |
| Selector not found | `click "#nonexistent"` | stderr: "Element not found: #nonexistent", exit 1 |
| JS eval error | `eval "throw new Error('boom')"` | stderr: "Eval error: boom", exit 1 |
| Command not found | `command "nonexistent.cmd"` | Command palette shows "no results" — tool reports: "Command not found: nonexistent.cmd", exit 1 |
| Wait timeout | `wait 5000` with no panel | stderr: "Timeout: no webview found within 5 seconds", exit 1 |
| Multiple webviews, no target | `eval "1+1"` with 3 panels | Uses first webview frame (index 0) |
| Target index out of range | `--target 5` with 2 webviews | stderr: "Target index 5 out of range (found 2 webviews)", exit 1 |
| Native dialog appears (no interception) | OS file picker triggered | Tool documents this as a limitation if hooks can't be installed |
| VS Code download on first run | `launch` with no cached VS Code | `@vscode/test-electron` downloads automatically, may take 30-60 seconds on first run |

---

## Core Types

### Session File

```json
{
  "daemonPort": 52345,
  "daemonPid": 12345,
  "userDataDir": "/path/to/src/PPDS.Extension/tools/.webview-cdp-profile",
  "logFile": "/path/to/src/PPDS.Extension/tools/.webview-cdp-console.log"
}
```

Written by the daemon during launch, read by all other commands (to know which port to POST to), deleted by the daemon during close.

### Console Log Entry

```json
{
  "level": "error",
  "message": "Uncaught TypeError: Cannot read property 'foo' of undefined",
  "source": "main",
  "timestamp": "2026-03-14T22:15:30.123Z"
}
```

Captured by `page.on('console')` and `page.on('pageerror')`. Written to `.webview-cdp-console.log` as JSONL (one JSON object per line).

### Usage Pattern

```bash
# Start VS Code with the extension
node src/PPDS.Extension/tools/webview-cdp.mjs launch

# Open a Data Explorer panel via command palette
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"

# Wait for the webview to appear
node src/PPDS.Extension/tools/webview-cdp.mjs wait

# Take a screenshot to see what's there
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot current-state.png

# Interact with the webview
node src/PPDS.Extension/tools/webview-cdp.mjs type "#sql-editor" "SELECT TOP 10 * FROM account"
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot after-execute.png

# Test keyboard shortcut
node src/PPDS.Extension/tools/webview-cdp.mjs key "ctrl+shift+p"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot command-palette.png
node src/PPDS.Extension/tools/webview-cdp.mjs key "Escape"

# Check for errors
node src/PPDS.Extension/tools/webview-cdp.mjs logs
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"

# Done
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| No session | Session file missing | "Run `webview-cdp launch` first" |
| Connection failed | `connectOverCDP` fails | "VS Code may have closed. Run `launch` again" |
| No webview frames | `contentFrame()` chain finds nothing | "Open a panel first" |
| Selector not found | Playwright's `waitForSelector` times out | "Element not found: <selector>" |
| Eval error | JavaScript threw an exception | Forward the error message |
| Command not found | Command palette shows no results | "Command not found: <command>" |
| Wait timeout | Condition not met within timeout | "Timeout" with description |
| VS Code download fail | `@vscode/test-electron` network error | Forward the download error |

### Recovery Strategies

- **Connection errors**: Run `close` (cleans up stale session file) then `launch` again.
- **Orphaned sessions**: If VS Code died but session file remains, `close` will fail to connect and clean up the session file. The agent can then `launch` fresh.

---

## Design Decisions

### Why Playwright Electron instead of raw CDP? (v2 change)

**Context:** v1 used raw CDP over WebSocket. Field testing revealed critical gaps: cannot execute VS Code commands, cannot capture console logs, keyboard shortcuts don't trigger VS Code keybindings, webview context discovery is unreliable.

**Decision:** Replace raw CDP with Playwright's `_electron` module.

**Test results from field testing (2026-03-14):**

| Capability | Raw CDP (v1) | Playwright Electron (v2) |
|------------|-------------|-------------------------|
| Command palette | Cannot trigger | `page.keyboard.press('Ctrl+Shift+P')` works |
| Keyboard shortcuts | Webview only (not VS Code UI) | Works everywhere |
| Console capture | Not implemented | `page.on('console')` built-in |
| Webview frame access | Manual context probing (unreliable) | `contentFrame()` chain (reliable) |
| Click VS Code UI | Coordinates only | CSS selectors work |
| Process cleanup | Manual PID tracking, orphaned daemons | `browser.close()` handles process tree |
| Screenshot | CDP `Page.captureScreenshot` with fallback | `page.screenshot()` — one line |

**Why keyboard shortcuts work in Playwright but not CDP:** VS Code's keybinding system intercepts input at Electron's `before-input-event` level, before CDP's synthetic renderer-level events reach the DOM. Playwright's `page.keyboard.press()` sends input through Electron's real input pipeline (equivalent to `webContents.sendInputEvent()`), which fires before the `before-input-event` handler — so VS Code's keybinding system sees it as real keyboard input.

**Alternatives considered:**
- Keep raw CDP + add companion Playwright script: Two tools, fragmented experience.
- Build custom IPC bridge in the extension: Maintenance burden, extension-specific.
- Use Windows UI Automation API: Can't read CSS classes or DOM state.

**Consequences:**
- Positive: Every v1 gap is fixed. Single tool does everything.
- Negative: Depends on `@vscode/test-electron` which downloads a specific VS Code build (not the user's installed version). First run takes 30-60 seconds for download.

### Why a daemon process?

**Context:** Playwright's `_electron.launch()` creates VS Code as a child process of the Node.js process that called it. When the parent exits, the child is killed immediately. Empirically verified (2026-03-15): `process.exit(0)` after `_electron.launch()` kills the VS Code Electron process, and the wsEndpoint becomes invalid (`ECONNREFUSED`).

**Decision:** The `launch` command forks a long-lived daemon process that holds the `ElectronApplication` alive. The daemon:
- Keeps VS Code running across multiple CLI invocations
- Captures console logs continuously (not just during individual commands)
- Handles clean shutdown when `close` sends `SIGTERM`
- Exposes HTTP server for CLI commands to send requests, executes them using the Electron Page

**Alternatives considered:**
- Stateless CDP reconnect (Option B): Save wsEndpoint, reconnect via `chromium.connectOverCDP()` per command. Rejected for two reasons: (1) the wsEndpoint dies when the launch process exits, and (2) even if kept alive, `connectOverCDP` creates a CDP-mode Page where `page.keyboard.press()` uses `Input.dispatchKeyEvent` — the same mechanism proven not to trigger VS Code keybindings. Only the Electron-mode Page from `_electron.launch()` has working keyboard shortcuts.
- Script-per-session: Accept a script file with multiple commands. Rejected because it breaks the CLI-per-command model agents use.

**Consequences:**
- Positive: VS Code stays alive. Console capture runs continuously. Clean shutdown via signal handling.
- Negative: Background process that could be orphaned if the calling terminal crashes. Mitigated by orphan detection in `launch` (checks for stale session files).

### Why remove `attach`?

**Context:** v1 had an `attach` command to connect CDP to the user's running VS Code. This required the user to restart VS Code with `--remote-debugging-port`.

**Decision:** Remove `attach` from v2. The primary workflow is `launch` (self-contained). If `attach` is needed later, it can be added via `chromium.connectOverCDP()` without Electron-specific APIs.

### Why remove `ws` dependency?

**Context:** v1 used the `ws` npm package for raw WebSocket communication with CDP endpoints.

**Decision:** Remove `ws`. Playwright handles all WebSocket communication internally via `connectOverCDP`.

---

## Extension Points

### Adding a New Command

1. **Add to `VALID_COMMANDS`** array
2. **Add argument parsing** in `parseArgs`
3. **Implement handler** function that receives the Playwright `page` and/or webview `frame`
4. **Wire into dispatch** switch statement
5. **Update skill file** with command documentation

### Gap Protocol (Skill-Driven Evolution)

The skill file instructs AI agents: if an interaction cannot be accomplished with existing commands, the agent MUST:

1. Stop and describe the gap
2. Propose a concrete enhancement
3. Ask the user whether to implement now or defer

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| Workspace | string | No | cwd | VS Code workspace to open |
| Target | number | No | 0 | Webview frame index when multiple panels are open |
| Log level | string | No | trace | VS Code log level (`--log` flag). Set at launch time. |

All configuration is via CLI arguments. No config files beyond the session file.

---

## Related Specs

- [data-explorer.md](./data-explorer.md) - Selection, copy, and Monaco editor features verifiable via this tool

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Renamed from vscode-webview-cdp-tool.md per SL1 |

---

## Roadmap

- **`attach` command**: Reconnect to user's running VS Code via `chromium.connectOverCDP()` for debugging scenarios
- **CI integration**: Run headless VS Code in CI with screenshot comparison for visual regression detection
- **Log level change at runtime**: Currently set at launch. Could add a command to change VS Code's log level without relaunching.
