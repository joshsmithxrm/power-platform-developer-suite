---
name: webview-cdp
description: Interact with VS Code extension webview panels via Chrome DevTools Protocol. Use when implementing or verifying webview UI — take screenshots to see your work, click elements, type text, send keyboard shortcuts, inspect DOM state. Triggers include any task involving VS Code extension webview panels, Data Explorer, plugin traces UI, or any panel built with WebviewPanelBase.
allowed-tools: Bash(node *webview-cdp*), Bash(cd * && node *webview-cdp*)
---

# VS Code Webview CDP Tool

Interact with extension webview panels running inside VS Code. Take screenshots to see what you've built, click buttons, type text, test keyboard shortcuts, right-click context menus, and inspect DOM state.

## When to Use

- After implementing or modifying any webview panel UI
- Before claiming UI work is complete — always screenshot to verify
- When debugging visual issues — screenshot to see the current state
- When testing interactions — clicks, keyboard shortcuts, context menus, dropdowns

## Setup

The tool is at `extension/tools/webview-cdp.mjs`. No additional installation needed.

## Two Modes: `attach` vs `launch`

### `attach` — Use with VS Code MCP (preferred for code review / testing sessions)

Connects CDP to the user's running VS Code instance. Use this when VS Code MCP tools (`mcp__vscode__*`) are available in your session — both tools operate on the same VS Code instance, so you can:
- Use VS Code MCP to execute commands (open panels, run tasks, read diagnostics)
- Use CDP to see and interact with webview content (screenshots, clicks, DOM inspection)

```bash
# Attach to the user's VS Code (auto-discovers the CDP port)
node extension/tools/webview-cdp.mjs attach

# Or specify the port if auto-discovery fails
node extension/tools/webview-cdp.mjs attach 9223

# Now use VS Code MCP to open a panel, then CDP to inspect it
# mcp__vscode__execute_command("ppds.openDataExplorer")
node extension/tools/webview-cdp.mjs screenshot data-explorer.png

# When done — detaches without closing VS Code
node extension/tools/webview-cdp.mjs close
```

**The user must start VS Code with CDP enabled:**
```
code --remote-debugging-port=9223
```

**NEVER use `close` to kill an attached instance** — `close` on an attached session only cleans up the session file, it does NOT kill VS Code. This is intentional — the attached instance is the user's editor.

### `launch` — Standalone testing (when VS Code MCP is not available)

Launches an isolated VS Code instance with the extension loaded. Use this for standalone CDP-only testing when VS Code MCP tools are not in your session.

```bash
# Launch an isolated VS Code instance
node extension/tools/webview-cdp.mjs launch 9223

# Test and verify
node extension/tools/webview-cdp.mjs screenshot current-state.png
node extension/tools/webview-cdp.mjs click "#my-button"
node extension/tools/webview-cdp.mjs screenshot after-click.png

# When done — kills the launched instance
node extension/tools/webview-cdp.mjs close
```

**Note:** The launched instance uses an isolated profile (`--user-data-dir`) and does NOT have the user's extensions installed. VS Code MCP tools will NOT work against it. If you need both CDP and VS Code MCP, use `attach` instead.

### How to Choose

| Scenario | Use | Why |
|----------|-----|-----|
| Code review / testing session with VS Code MCP available | `attach` | Both tools work on the same instance |
| Quick visual check during implementation | `attach` | Faster, no launch overhead |
| Standalone testing without VS Code MCP | `launch` | Self-contained, no user setup needed |
| CI or automated testing | `launch` | Reproducible isolated environment |

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `attach [port]` | `attach` or `attach 9223` | Connect to running VS Code (auto-discovers port) |
| `launch [port] [workspace]` | `launch 9223` | Start isolated VS Code with extension |
| `close` | `close` | Detach (if attached) or kill (if launched) |
| `connect [port]` | `connect` | Test connectivity, list webview targets |
| `screenshot <file>` | `screenshot result.png` | Capture webview as PNG |
| `eval "<js>"` | `eval "document.title"` | Run JS in webview, print result |
| `click "<selector>" [--right]` | `click "#btn"` | Left or right click element |
| `type "<selector>" "<text>"` | `type "#input" "hello"` | Type text into element |
| `select "<selector>" "<value>"` | `select "#dropdown" "opt1"` | Select dropdown option |
| `key "<combo>"` | `key "ctrl+enter"` | Send keyboard shortcut |
| `mouse <event> <x> <y>` | `mouse mousedown 150 200` | Raw mouse event at coordinates |

All commands except `launch`, `attach`, and `close` accept `--target N` to select a specific webview when multiple panels are open.

## Common Patterns

### Verify a button click
```bash
node extension/tools/webview-cdp.mjs click "#execute-btn"
node extension/tools/webview-cdp.mjs screenshot after-execute.png
```

### Test a keyboard shortcut
```bash
node extension/tools/webview-cdp.mjs key "ctrl+enter"
node extension/tools/webview-cdp.mjs screenshot after-shortcut.png
```

### Test a context menu
```bash
node extension/tools/webview-cdp.mjs click "td[data-row='1']" --right
node extension/tools/webview-cdp.mjs screenshot context-menu.png
node extension/tools/webview-cdp.mjs click ".context-menu [data-action='copy']"
```

### Test drag selection
```bash
node extension/tools/webview-cdp.mjs eval "JSON.stringify(document.querySelector('td[data-row=\"2\"]').getBoundingClientRect())"
node extension/tools/webview-cdp.mjs mouse mousedown 150 200
node extension/tools/webview-cdp.mjs mouse mousemove 300 250
node extension/tools/webview-cdp.mjs mouse mouseup 300 250
node extension/tools/webview-cdp.mjs screenshot after-drag.png
```

### Check DOM state
```bash
node extension/tools/webview-cdp.mjs eval "document.querySelector('.cell-selected') !== null"
node extension/tools/webview-cdp.mjs eval "document.querySelector('#status-text').textContent"
```

## Gap Protocol

If you encounter a webview interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need and why the current commands don't cover it
3. **Propose an enhancement** — a new command, flag, or behavior that would close the gap
4. **Ask the user** — whether to implement the enhancement now or defer it

This ensures the tool evolves based on real needs.

## Important

- **Always screenshot after changes** — don't assume your code works, verify visually
- **Use `attach` when VS Code MCP is available** — both tools work on the same instance
- **Use `launch` only for standalone testing** — it creates an isolated instance without the user's extensions
- **NEVER close an attached instance** — `close` on attached sessions only cleans up the session file
- **Do NOT use agent-browser for VS Code webviews** — it cannot reach webview iframe targets
- **Do NOT use Playwright MCP for webview testing** — same limitation
- **Do NOT skip visual verification** — the whole point of this tool is seeing your work
- **Avoid eval expressions that read secrets** — auth tokens, cookies, etc. will appear in logs
