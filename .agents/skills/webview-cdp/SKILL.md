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

## Core Workflow

```bash
# 1. Launch an isolated VS Code instance with the extension loaded
node extension/tools/webview-cdp.mjs launch 9223

# 2. Open a panel by clicking VS Code's native UI (--page targets VS Code itself, not webviews)
node extension/tools/webview-cdp.mjs click --page "[aria-label='Data Explorer']"
# Wait for the webview to load
sleep 3

# 3. Take a screenshot to see what you built
node extension/tools/webview-cdp.mjs screenshot current-state.png
# IMPORTANT: Actually look at the screenshot to verify the UI

# 4. Interact with webview content (no --page = targets the webview)
node extension/tools/webview-cdp.mjs click "#execute-btn"
node extension/tools/webview-cdp.mjs screenshot after-click.png

# 5. When done
node extension/tools/webview-cdp.mjs close
```

## Two Targets: `--page` vs default (webview)

Every interaction command (`click`, `eval`, `type`, `key`, `screenshot`, `mouse`, `select`) works on **two targets**:

| Flag | Target | Use for |
|------|--------|---------|
| *(none)* | Webview iframe content | Buttons, inputs, tables inside your extension's webview panels |
| `--page` | VS Code's main UI | Sidebar items, tabs, menus, command palette, native VS Code elements |

**You need `--page` to navigate VS Code's UI** (open panels, click sidebar items, switch tabs). Once a webview panel is open, drop `--page` to interact with its content.

```bash
# Click something in VS Code's sidebar (--page)
node extension/tools/webview-cdp.mjs click --page "[aria-label='Data Explorer']"

# Click a button inside the webview panel (no --page)
node extension/tools/webview-cdp.mjs click "#execute-btn"

# Screenshot VS Code's full window (--page)
node extension/tools/webview-cdp.mjs screenshot --page full-window.png

# Screenshot just the webview content (no --page)
node extension/tools/webview-cdp.mjs screenshot webview-only.png
```

## Connection Modes

### `launch` — Self-contained (primary mode)

Launches an isolated VS Code instance with the extension loaded. Fully automated, no user involvement.

```bash
node extension/tools/webview-cdp.mjs launch 9223
# ... work ...
node extension/tools/webview-cdp.mjs close
```

### `attach` — Connect to user's VS Code (when CDP is already enabled)

Connects to the user's running VS Code. Use when the user has started VS Code with `--remote-debugging-port`. Useful for debugging issues in the user's actual environment.

```bash
node extension/tools/webview-cdp.mjs attach        # auto-discovers port
node extension/tools/webview-cdp.mjs attach 9223   # specific port
# ... work ...
node extension/tools/webview-cdp.mjs close         # detaches only, does NOT kill VS Code
```

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [port] [workspace]` | `launch 9223` | Start isolated VS Code with extension |
| `attach [port]` | `attach` | Connect to running VS Code |
| `close` | `close` | Kill (launched) or detach (attached) |
| `connect [port]` | `connect` | Test connectivity, list webview targets |
| `screenshot <file>` | `screenshot result.png` | Capture as PNG |
| `eval "<js>"` | `eval "document.title"` | Run JS, print result |
| `click "<selector>" [--right]` | `click "#btn"` | Left or right click |
| `type "<selector>" "<text>"` | `type "#input" "hello"` | Type text into element |
| `select "<selector>" "<value>"` | `select "#dropdown" "opt1"` | Select dropdown option |
| `key "<combo>"` | `key "ctrl+enter"` | Send keyboard shortcut |
| `mouse <event> <x> <y>` | `mouse mousedown 150 200` | Raw mouse event |

**Flags available on all interaction commands:**
- `--page` — target VS Code's main UI instead of webview content
- `--target N` — select specific webview when multiple are open

## Common Patterns

### Open a webview panel
```bash
# Use --page to interact with VS Code's sidebar/UI
node extension/tools/webview-cdp.mjs click --page "[aria-label='Data Explorer']"
sleep 2
node extension/tools/webview-cdp.mjs screenshot panel-opened.png
```

### Verify a button click
```bash
node extension/tools/webview-cdp.mjs click "#execute-btn"
node extension/tools/webview-cdp.mjs screenshot after-execute.png
```

### Test a keyboard shortcut (inside webview)
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

### Discover available elements on the page
```bash
# Find clickable elements in VS Code's sidebar
node extension/tools/webview-cdp.mjs eval --page "Array.from(document.querySelectorAll('[aria-label]')).map(e => e.getAttribute('aria-label')).slice(0, 20).join('\\n')"
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
- **Use `--page` to navigate VS Code UI** — open panels, click sidebar items, switch tabs
- **Drop `--page` for webview content** — buttons, inputs, tables inside extension panels
- **NEVER close an attached instance** — `close` on attached sessions only cleans up the session file
- **Do NOT use agent-browser for VS Code webviews** — it cannot reach webview iframe targets
- **Do NOT use Playwright MCP for webview testing** — same limitation
- **Do NOT skip visual verification** — the whole point of this tool is seeing your work
- **Avoid eval expressions that read secrets** — auth tokens, cookies, etc. will appear in logs
