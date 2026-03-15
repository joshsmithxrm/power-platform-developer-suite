# VS Code Webview CDP Tool

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-14
**Code:** [extension/tools/](../extension/tools/) | None

---

## Overview

A CLI tool that connects to a running VS Code instance via Chrome DevTools Protocol (CDP) and provides direct access to extension webview panel content. Enables AI agents (Claude Code) to see, interact with, and verify webview UI during development — closing the feedback loop between implementation and visual verification.

### Goals

- **Visual feedback**: AI agents can take screenshots of webview panels to see what they've built
- **DOM interaction**: Click elements, type text, send keyboard shortcuts, right-click context menus, interact with dropdowns and checkboxes
- **State inspection**: Run arbitrary JavaScript in the webview context to read DOM state, CSS classes, element attributes
- **Self-managing lifecycle**: Launch and close VS Code instances without manual intervention
- **Generic and stable**: No knowledge of any specific webview's DOM structure — works with any VS Code extension webview without tool updates when the DOM changes

### Non-Goals

- Debugging (breakpoints, step-through) — use VS Code's built-in debugger
- Testing VS Code native UI (command palette, settings, tree views) — use existing VS Code MCP
- Automated regression testing in CI — use Vitest unit tests and Playwright E2E tests
- Recording/playback of interaction sequences
- Accessibility tree snapshots (use `eval` with custom JS if needed)

---

## Architecture

```
Claude Code
    │
    │  Bash: node webview-cdp.mjs <command> [args]
    ▼
┌──────────────────────┐
│   webview-cdp CLI     │  Single-file Node.js script (~300 lines)
│                       │  Reads extension/tools/.webview-cdp-session.json
└───────┬──────────────┘
        │
        │  1. GET http://localhost:<port>/json/list
        │  2. Find iframe target (type: "iframe", URL: vscode-webview://)
        │  3. Open WebSocket to target's webSocketDebuggerUrl
        │  4. Send Runtime.enable, discover execution contexts
        │  5. Find active-frame context (match by URL pattern)
        │  6. Execute command via Runtime.evaluate / Input.dispatch*
        │  7. Close WebSocket, exit
        ▼
┌──────────────────────┐
│  VS Code (Electron)   │  Launched with --extensionDevelopmentPath
│  --remote-debugging-  │  and --remote-debugging-port=<port>
│   port=<port>         │
│                       │
│  ┌─────────────────┐  │
│  │ Extension Host   │  │
│  │  └─ QueryPanel   │  │
│  │     └─ webview   │──│── iframe CDP target
│  │        └─ active  │  │   (webSocketDebuggerUrl)
│  │           -frame  │  │
│  └─────────────────┘  │
└──────────────────────┘
```

Each CLI invocation is stateless: connect, execute, disconnect. The VS Code process persists independently between invocations. A session file tracks the PID and port to avoid passing them on every command.

### Components

| Component | Responsibility |
|-----------|----------------|
| `webview-cdp.mjs` | CLI entry point, argument parsing, command dispatch |
| Target discovery | Fetch `/json/list`, filter for iframe targets with `vscode-webview://` URLs |
| Context discovery | Listen for `Runtime.executionContextCreated`, find active-frame context by URL pattern |
| Session file | `.webview-cdp-session.json` — persists PID and port between invocations |
| Skill file | `.agents/skills/webview-cdp/SKILL.md` — teaches AI agents when/how to use the tool |

### Dependencies

- **Runtime:** Node.js (already required by the extension build toolchain)
- **npm:** `ws` (WebSocket client)
- **VS Code:** Must support `--remote-debugging-port` and `--extensionDevelopmentPath` flags (stable Electron feature)
- Depends on: [architecture.md](./architecture.md) (extension webview panel architecture)

---

## Specification

### Core Requirements

1. The tool MUST connect to a VS Code instance's CDP endpoint and discover webview iframe targets
2. The tool MUST find the correct execution context within the nested iframe structure (wrapper → active-frame)
3. The tool MUST NOT contain any knowledge of specific webview DOM structures — it is a generic pipe
4. The tool MUST manage VS Code lifecycle (launch with extension loaded, clean shutdown)
5. The tool MUST handle the case where multiple webview panels are open (target selection)
6. Screenshots MUST capture the webview content as rendered in VS Code, not a separate browser

### Command Interface

| Command | Signature | Purpose |
|---------|-----------|---------|
| `launch` | `launch [port] [workspace]` | Start VS Code with extension and remote debugging |
| `close` | `close` | Kill the managed VS Code instance |
| `connect` | `connect [port]` | Test connectivity, list available webview targets |
| `screenshot` | `screenshot <file> [--target N]` | Capture webview panel as PNG |
| `eval` | `eval "<js>" [--target N]` | Run JavaScript in webview context, print result |
| `click` | `click "<selector>" [--right] [--target N]` | Left or right click an element |
| `type` | `type "<selector>" "<text>" [--target N]` | Clear element and type text |
| `select` | `select "<selector>" "<value>" [--target N]` | Select dropdown option |
| `key` | `key "<combo>" [--target N]` | Send keyboard shortcut (e.g., `ctrl+c`, `ctrl+enter`, `Escape`) |
| `mouse` | `mouse <event> <x> <y> [--target N]` | Dispatch mousedown/mousemove/mouseup at coordinates |

### Primary Flows

**Launch flow:**

1. **Check port**: Verify port is not already in use — error if occupied
2. **Resolve paths**: Find `extension/` directory relative to workspace
3. **Spawn VS Code**: `code --extensionDevelopmentPath=<ext> --remote-debugging-port=<port> <workspace>`
4. **Wait for CDP**: Poll `http://localhost:<port>/json/list` every 500ms, up to 15 seconds
5. **Write session**: Save `{ pid, port }` to `.webview-cdp-session.json`
6. **Print confirmation**: Output port and PID to stdout

**Command execution flow (all commands except launch/close):**

1. **Read session**: Load port from `.webview-cdp-session.json` (or accept `--port` override)
2. **Discover targets**: `GET http://localhost:<port>/json/list`, filter for `type: "iframe"` with `vscode-webview://` URL
3. **Select target**: Use `--target N` index, or default to first iframe target found
4. **Connect WebSocket**: Open connection to target's `webSocketDebuggerUrl`
5. **Discover context**: Send `Runtime.enable`, collect `executionContextCreated` events, find active-frame context by matching URL containing `/active-frame/` or by probing with `document.body !== null`
6. **Execute**: Run the command-specific CDP calls
7. **Output result**: Print to stdout (screenshot path, eval result, or nothing on success)
8. **Disconnect**: Close WebSocket, exit with code 0

**Close flow:**

1. **Read session**: Load PID from `extension/tools/.webview-cdp-session.json`
2. **Verify process**: Check that the PID belongs to a VS Code / Electron process before killing (prevents killing an unrelated process if the PID was reused after a crash)
3. **Kill process**: Terminate by PID (or skip if process is already dead)
4. **Clean up**: Delete session file
5. **Print confirmation**: Output to stdout

### Validation Rules

| Input | Rule | Error |
|-------|------|-------|
| `port` | Integer between 1024 and 65535 | "Invalid port: must be 1024-65535" |
| `screenshot <file>` | File path must be writable (parent directory exists) | "Cannot write to: <path>" |
| `eval "<js>"` | Expression must be non-empty string | "Empty expression" |
| `click "<selector>"` | Selector must be non-empty string | "Empty selector" |
| `type "<selector>" "<text>"` | Selector and text must both be provided | "Usage: type \<selector\> \<text\>" |
| `select "<selector>" "<value>"` | Value matches option's `value` attribute or visible text content (tried in that order) | "Option not found: \<value\>" |
| `mouse <event>` | Event must be one of: `mousedown`, `mousemove`, `mouseup` | "Invalid mouse event. Must be: mousedown, mousemove, mouseup" |
| `mouse <x> <y>` | Coordinates must be non-negative numbers | "Invalid coordinates" |
| `key "<combo>"` | Combo must be non-empty. Modifiers must be: `ctrl`, `shift`, `alt`, `meta`. Unrecognized key names (e.g., `ctrl+xyz`) are forwarded to CDP as-is — validation only rejects malformed syntax and unknown modifiers | "Invalid key combo: unknown modifier 'foo'" |
| `--target N` | N must be a non-negative integer within range of discovered targets | "Target index N out of range (found M webviews)" |

### Constraints

- Single file implementation (`extension/tools/webview-cdp.mjs`)
- No dependency on Playwright, Puppeteer, or any browser automation framework — raw CDP over WebSocket only
- No persistent state beyond the session file — each command is a fresh connection
- Session file location: `extension/tools/.webview-cdp-session.json` (gitignored via `extension/.gitignore`)
- All errors to stderr, all results to stdout
- Exit code 0 on success, 1 on error
- Single-user tool — not designed for concurrent access from multiple sessions
- WebSocket connection timeout: 5 seconds per command invocation

### Security Considerations

- **Local development only**: This tool connects exclusively to `localhost`. It is not designed for, and must not be used for, remote connections. The CDP endpoint is unauthenticated — anyone on the local machine can connect.
- **Process spawn (CONSTITUTION S2)**: The `launch` command spawns VS Code via `child_process.spawn('code', [...args])` with `shell: true`. Justification: the `code` command on Windows is a batch file (`code.cmd`), which requires shell execution. All arguments are hardcoded strings or validated port numbers — no user-supplied strings are interpolated into the command.
- **Eval output and secrets (CONSTITUTION S3)**: The `eval` command returns whatever the JavaScript expression produces. If the agent evaluates an expression that reads auth tokens, cookies, or other secrets from the webview DOM, those values will appear in stdout and potentially in conversation logs. This is acceptable for a local development tool — the agent should avoid evaluating expressions that read sensitive data, and the skill file includes this guidance.
- **No innerHTML (CONSTITUTION S1)**: The tool does not render any HTML. All output is plain text or binary PNG files.

---

## Acceptance Criteria

Note: All acceptance criteria require a live VS Code instance and are verified via manual integration testing. Pure functions (argument parsing, session file I/O, target filtering, key combo parsing) will have unit tests in `extension/tools/webview-cdp.test.mjs` but are not listed as ACs since they test implementation details, not user-facing behavior.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `launch` starts a VS Code instance with `--extensionDevelopmentPath` and `--remote-debugging-port`, and writes a session file with PID and port | Manual: run `launch`, verify VS Code opens and session file exists | 🔲 |
| AC-02 | `close` kills the VS Code process by PID and deletes the session file | Manual: run `close` after `launch`, verify VS Code closes and session file is removed | 🔲 |
| AC-03 | `connect` discovers at least one webview iframe target when a webview panel is open in VS Code | Manual: open a webview panel, run `connect`, verify iframe target listed | 🔲 |
| AC-04 | `connect` reports "No webview found" when no webview panel is open | Manual: close all webview panels, run `connect`, verify error message | 🔲 |
| AC-05 | `screenshot` produces a valid PNG file showing the rendered webview content | Manual: open a webview panel, run `screenshot`, open PNG and verify it shows the webview | 🔲 |
| AC-06 | `eval` executes JavaScript in the webview context and returns the result as JSON to stdout | Manual: run `eval "1 + 1"`, verify output is `2`. Run `eval "document.title"`, verify a non-empty string is returned | 🔲 |
| AC-07 | `click` triggers a left-click on an element matching the CSS selector, causing the expected UI response | Manual: run `click` on a button element, take screenshot, verify button was activated | 🔲 |
| AC-08 | `click --right` triggers a right-click on an element, causing a contextmenu event | Manual: run `click --right` on an element, take screenshot, verify context menu is visible | 🔲 |
| AC-09 | `type` clears the target element and types the provided text | Manual: run `type` on an input/textarea, then `eval` to verify the element's value matches | 🔲 |
| AC-10 | `key` dispatches keyboard shortcuts that the webview responds to | Manual: run `key "Escape"`, verify any open menu/dialog is dismissed | 🔲 |
| AC-11 | `mouse` dispatches mousedown/mousemove/mouseup events at specified coordinates | Manual: get element coordinates via `eval`, dispatch mouse events, screenshot to verify state change | 🔲 |
| AC-12 | `select` chooses an option from a dropdown control by value or visible text | Manual: run `select` on a dropdown element, `eval` to verify selected value | 🔲 |
| AC-13 | `--target N` flag selects a specific webview when multiple panels are open | Manual: open 2 webview panels, use `--target 0` and `--target 1`, verify different content in screenshots | 🔲 |
| AC-14 | Context discovery finds the correct execution context (active-frame, not wrapper) regardless of context ID numbering | Manual: run `eval "document.body.children.length"`, verify a positive integer is returned (wrapper frame would return a different structure) | 🔲 |
| AC-15 | Tool reconnects cleanly on each invocation — no stale WebSocket state between commands | Manual: run 5 commands in sequence (`connect`, `screenshot`, `eval`, `click`, `screenshot`), all succeed independently | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| VS Code not running | Any command without prior `launch` | stderr: "No session found. Run `webview-cdp launch` first.", exit 1 |
| Port in use | `launch 9223` when 9223 is occupied | stderr: "Port 9223 already in use.", exit 1 |
| No webview open | `screenshot` with no panels open | stderr: "No webview found. Is a webview panel open?", exit 1 |
| Selector not found | `click "#nonexistent"` | stderr: "Element not found: #nonexistent", exit 1 |
| JS eval error | `eval "throw new Error('boom')"` | stderr: "Eval error: boom", exit 1 |
| VS Code crashed | Command after VS Code was killed externally | stderr: "Connection refused. VS Code may have closed.", exit 1 |
| Multiple webviews, no target flag | `screenshot out.png` with 3 panels open | Uses first iframe target (index 0) |
| CDP timeout on launch | VS Code fails to start within 15 seconds | stderr: "Timeout: VS Code did not start within 15 seconds.", exit 1 |
| Unrecognized key name | `key "ctrl+xyz"` | Forwarded to CDP — key name `xyz` is syntactically valid but may not produce a visible effect |
| Invalid modifier | `key "foo+a"` | stderr: "Invalid key combo: unknown modifier 'foo'", exit 1 |
| Stale PID reused | `close` when PID now belongs to a different process | Verify process name before kill — if not VS Code/Electron, delete session file without killing, print warning |
| Concurrent launch | Two `launch` calls simultaneously | Second call fails with "Port already in use" |
| Target index out of range | `--target 5` with only 2 webviews | stderr: "Target index 5 out of range (found 2 webviews)", exit 1 |

---

## Core Types

### Session File

```json
{
  "pid": 12345,
  "port": 9223
}
```

Written by `launch`, read by all other commands, deleted by `close`. Location: `extension/tools/.webview-cdp-session.json`. Gitignored.

### CDP Target

```json
{
  "id": "E0865FCE8D750754FB1C7EA861468825",
  "type": "iframe",
  "title": "vscode-webview://...",
  "url": "vscode-webview://1u8rcrcep.../index.html?...&extensionId=JoshSmithXRM.power-platform-developer-suite",
  "webSocketDebuggerUrl": "ws://localhost:9223/devtools/page/E0865FCE..."
}
```

Discovered by fetching `GET http://localhost:<port>/json/list` and filtering for `type: "iframe"` with URL containing `vscode-webview://`.

### Execution Context

```json
{
  "id": 2,
  "origin": "",
  "name": "",
  "auxData": {
    "isDefault": true,
    "type": "default",
    "frameId": "ABC123..."
  }
}
```

Discovered via `Runtime.executionContextCreated` events after sending `Runtime.enable`. The correct context is identified by probing or by matching the frame URL containing `/active-frame/`.

### Usage Pattern

A typical agent session:

```bash
# Start of UI work session
node extension/tools/webview-cdp.mjs launch 9223

# Open a webview panel (agent uses VS Code MCP or eval)
# ... agent implements a feature, runs npm run compile ...

# Visual verification loop
node extension/tools/webview-cdp.mjs screenshot before.png
node extension/tools/webview-cdp.mjs click "#some-button"
node extension/tools/webview-cdp.mjs screenshot after-click.png
node extension/tools/webview-cdp.mjs eval "document.querySelector('.status').textContent"
node extension/tools/webview-cdp.mjs key "ctrl+c"
node extension/tools/webview-cdp.mjs click "td[data-row='1']" --right
node extension/tools/webview-cdp.mjs screenshot context-menu.png

# End of session
node extension/tools/webview-cdp.mjs close
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| No session | Session file missing or unreadable | "Run `webview-cdp launch` first" |
| Connection refused | CDP endpoint not responding | "VS Code may have closed. Run `launch` again" |
| No iframe targets | No webview panels open | "Open a webview panel in VS Code" |
| Context not found | Active-frame execution context not discovered | "Webview may still be loading. Try again in a moment" |
| Selector not found | CSS selector matches no elements | "Element not found: <selector>" |
| Eval error | JavaScript threw an exception | Forward the error message |
| Port in use | Another process occupies the port | "Port N already in use. Pick another or run `close`" |
| Timeout | VS Code didn't start or CDP didn't respond in time | "Timeout after N seconds" |

### Recovery Strategies

- **Connection errors**: The tool does not retry automatically. The agent should run `launch` again or wait and retry the command.
- **Stale session**: If session file exists but VS Code is dead, any command will get a connection error. Agent runs `close` (cleans up session file) then `launch`.

---

## Design Decisions

### Why a CLI tool instead of an MCP server?

**Context:** We need Claude Code to interact with VS Code webviews. MCP servers are the standard way to add tools to Claude Code, but they add complexity.

**Decision:** Single-file CLI script invoked via bash.

**Alternatives considered:**
- MCP server: Persistent connection eliminates reconnection overhead, native tool integration. Rejected because: more code (~400 vs ~300 lines), MCP lifecycle management, harder to debug, the reconnection overhead (~500ms) is negligible for our usage pattern (5-10 commands per session, not 100).
- Long-running daemon with CLI wrapper: Best of both worlds but most complex. Process management on Windows, cleanup on exit. Rejected for unnecessary complexity.

**Consequences:**
- Positive: Simple to build, debug, and maintain. Single file. No framework dependencies.
- Negative: ~500ms overhead per command for WebSocket connection/context discovery. Acceptable for interactive development use.

### Why raw CDP instead of Playwright/Puppeteer?

**Context:** Playwright and Puppeteer are the standard tools for browser automation via CDP.

**Decision:** Direct WebSocket communication with the CDP protocol.

**Test results from experiment (2026-03-14):**

| Approach | Result |
|----------|--------|
| Playwright `connectOverCDP` | `frame.childFrames()` returned 0 for `vscode-webview://` iframes — cannot traverse into webview content |
| agent-browser `tab` command | Only lists `page` and `webview` type targets, skips `iframe` type — cannot discover webview targets |
| Raw CDP WebSocket to iframe target | Full DOM access via `Runtime.evaluate` with correct `contextId` — complete read/write access to webview content |

**Alternatives considered:**
- Playwright: Standard, well-maintained, rich API. Rejected because it cannot traverse VS Code's `vscode-webview://` protocol iframe boundary.
- agent-browser: Purpose-built for Electron. Rejected because it excludes `iframe` type targets from its target listing.
- Windows UI Automation API: Can see into webviews via accessibility tree. Rejected because it cannot read CSS classes, data attributes, or DOM state — only accessibility properties.

**Consequences:**
- Positive: Actually works. Full DOM access inside VS Code webviews.
- Negative: More low-level code. No high-level abstractions for common patterns. We handle CDP protocol messages directly.

### Why the tool manages VS Code lifecycle?

**Context:** Multiple VS Code instances can run simultaneously (user's editor, Extension Development Host, our tool's instance). Connecting to the wrong one would be confusing and error-prone.

**Decision:** The tool launches its own VS Code instance with `--extensionDevelopmentPath` and `--remote-debugging-port`, tracks the PID, and kills it on close.

**Alternatives considered:**
- Connect to user's existing VS Code: Ambiguous when multiple instances exist. Requires user to manually launch with debug port.
- Auto-discover by scanning ports: Slow, unreliable, might connect to wrong instance.

**Consequences:**
- Positive: Zero ambiguity about which instance. Clean lifecycle management. User's editor is unaffected.
- Negative: Cannot attach to an existing debug session. No breakpoints. For visual testing this is acceptable — debugging is a separate workflow.

### Why no DOM structure knowledge?

**Context:** We could build targeted snapshot functions that know about the Data Explorer's specific elements (toolbar buttons, results table, selection state).

**Decision:** The tool is completely generic. No knowledge of any specific webview DOM. The AI agent brings context about the DOM because it just wrote the code.

**Alternatives considered:**
- PPDS-specific helpers (e.g., `clickCell(row, col)`, `getSelectedRange()`): More ergonomic for Data Explorer testing. Rejected because it creates a maintenance burden — two places to update every time the DOM changes (the webview and the tool).

**Consequences:**
- Positive: Tool never needs updating when webview DOM changes. Works with any VS Code extension webview, not just ours.
- Negative: Commands are more verbose — agent must construct selectors and JS expressions manually. Acceptable because the agent has full context from having just written the code.

---

## Extension Points

### Adding a New Command

1. **Add argument parsing**: New case in the command dispatch switch
2. **Implement handler**: Function that receives the WebSocket connection + execution context ID and performs CDP calls
3. **Update skill file**: Add command to the reference table in `.agents/skills/webview-cdp/SKILL.md`

### Gap Protocol (Skill-Driven Evolution)

The skill file instructs AI agents: if an interaction cannot be accomplished with existing commands, the agent MUST:

1. Stop and describe the gap (what interaction is needed, why current commands don't cover it)
2. Propose a concrete enhancement (new command, new flag, or new behavior)
3. Ask the user whether to implement the enhancement now or work around it

This ensures the tool evolves based on real usage needs rather than upfront speculation.

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| Port | number | No | 9223 | CDP remote debugging port |
| Workspace | string | No | cwd | VS Code workspace to open |
| Target | number | No | 0 | Webview target index when multiple panels are open |

All configuration is via CLI arguments. No config files beyond the session file.

---

## Related Specs

- [vscode-data-explorer-selection-copy.md](./vscode-data-explorer-selection-copy.md) - Selection and copy features that this tool can verify (CSS classes, keyboard shortcuts, context menus)
- [vscode-data-explorer-monaco-editor.md](./vscode-data-explorer-monaco-editor.md) - Monaco editor integration testable via this tool

---

## Roadmap

- **Dev server integration**: If the tool proves valuable, consider also supporting connection to the Vite dev server (`localhost:5173`) for faster iteration without VS Code overhead
- **CI smoke tests**: If the tool stabilizes, explore running headless VS Code in CI with screenshot comparison for visual regression detection
- **Upstream contribution**: If VS Code's webview architecture stabilizes, consider contributing iframe target support to agent-browser or the official Playwright MCP
