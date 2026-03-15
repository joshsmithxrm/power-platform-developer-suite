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

## Workflow

```bash
# 1. Start a VS Code instance with the extension loaded
node extension/tools/webview-cdp.mjs launch 9223

# 2. After compiling (npm run compile), verify your work
node extension/tools/webview-cdp.mjs screenshot current-state.png
# IMPORTANT: Actually look at the screenshot to verify the UI

# 3. Interact and verify
node extension/tools/webview-cdp.mjs click "#my-button"
node extension/tools/webview-cdp.mjs screenshot after-click.png

# 4. When done
node extension/tools/webview-cdp.mjs close
```

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [port] [workspace]` | `launch 9223` | Start VS Code with extension |
| `close` | `close` | Kill VS Code instance |
| `connect [port]` | `connect 9223` | Test connectivity, list webview targets |
| `screenshot <file>` | `screenshot result.png` | Capture webview as PNG |
| `eval "<js>"` | `eval "document.title"` | Run JS in webview, print result |
| `click "<selector>" [--right]` | `click "#btn"` | Left or right click element |
| `type "<selector>" "<text>"` | `type "#input" "hello"` | Type text into element |
| `select "<selector>" "<value>"` | `select "#dropdown" "opt1"` | Select dropdown option |
| `key "<combo>"` | `key "ctrl+enter"` | Send keyboard shortcut |
| `mouse <event> <x> <y>` | `mouse mousedown 150 200` | Raw mouse event at coordinates |

All commands except `launch` and `close` accept `--target N` to select a specific webview when multiple panels are open.

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
- **Do NOT use agent-browser for VS Code webviews** — it cannot reach webview iframe targets
- **Do NOT use Playwright MCP for webview testing** — same limitation
- **Do NOT skip visual verification** — the whole point of this tool is seeing your work
- **Avoid eval expressions that read secrets** — auth tokens, cookies, etc. will appear in logs
