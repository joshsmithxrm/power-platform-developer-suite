---
name: webview-cdp
description: Visual verification of VS Code extension webview panels via Playwright Electron — screenshots, clicks, typing, keyboard shortcuts, VS Code commands, console logs. Use after implementing or modifying any UI-affecting change (CSS, layout, HTML templates, message wiring). For non-visual changes (string constants, config, internal refactors), compile + test is sufficient.
allowed-tools: Bash(node *webview-cdp*), Bash(cd * && node *webview-cdp*), Bash(npm run * --prefix src/extension)
---

# VS Code Webview CDP Tool (v2 — Playwright Electron)

Interact with extension webview panels running inside VS Code. Take screenshots to see what you've built, click buttons, type text, test keyboard shortcuts, execute VS Code commands, right-click context menus, read console logs, and inspect DOM state.

## When to Use

- After implementing or modifying any webview panel UI
- When debugging visual issues — screenshot to see the current state
- When testing interactions — clicks, keyboard shortcuts, context menus, dropdowns
- When checking for errors — read console logs and output channel content

## Setup

The tool is at `src/extension/tools/webview-cdp.mjs`. Uses `@playwright/test` and `@vscode/test-electron` (both already dev dependencies). First run downloads VS Code (~30-60 seconds, cached afterward).

## Core Workflow

```bash
# 1. Launch VS Code with the extension (--build compiles extension + daemon)
node src/extension/tools/webview-cdp.mjs launch --build

# 2. Open a panel via command palette
node src/extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"

# 3. Take a screenshot to see what you built
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/current-state.png
# IMPORTANT: Actually look at the screenshot to verify the UI

# 4. Interact with webview content (use --ext to target the PPDS webview)
node src/extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/after-click.png

# 5. Check for errors
node src/extension/tools/webview-cdp.mjs logs

# 6. When done
node src/extension/tools/webview-cdp.mjs close
```

**Always use `--ext "power-platform-developer-suite"`** on `eval`, `click`, `type`, `select`, and `wait` commands. VS Code may have other webviews open (walkthrough, settings, etc.) — without `--ext`, you might interact with the wrong panel.

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [workspace] [--build]` | `launch` or `launch --build` | Start VS Code with extension (--build compiles extension + daemon first) |
| `close` | `close` | Shut down VS Code and daemon |
| `connect` | `connect` | List available webview frames |
| `command "<cmd>"` | `command "PPDS: Data Explorer"` | Execute VS Code command via command palette |
| `wait [timeout] [--ext id]` | `wait 10000` | Wait until webview appears |
| `screenshot <file>` | `screenshot $TEMP/shot.png` | Capture full VS Code window (always full window; `--page` accepted but ignored) |
| `eval "<js>" [--page]` | `eval "document.title"` | Run JS in webview or page context |
| `click "<selector>" [--right] [--page]` | `click "#btn"` | Click element |
| `type "<selector>" "<text>" [--page]` | `type "#input" "hello"` | Type text into element |
| `select "<selector>" "<value>" [--page]` | `select "#dropdown" "opt1"` | Select dropdown option |
| `key "<combo>" [--page]` | `key "ctrl+enter"` | Send keyboard shortcut (works everywhere!) |
| `mouse <event> <x> <y> [--page]` | `mouse mousedown 150 200` | Raw mouse event at coordinates |
| `logs [--channel name]` | `logs --channel "PPDS"` | Read console logs or output channel |
| `text "<selector>" [--page]` | `text "#status"` | Read textContent of element |
| `notebook run` | `notebook run` | Execute focused cell (clicks run button) |
| `notebook run-all` | `notebook run-all` | Execute all cells |

**Flags:**
- `--page` — target VS Code's native UI instead of webview content
- `--target N` — select specific webview by index
- `--ext "<id>"` — select webview by extension ID (more stable than index)

## Two Targets: `--page` vs default

**Note:** For `screenshot`, both modes produce a full window capture — `--page` is accepted but ignored. The distinction below applies to `eval`, `click`, `type`, `select`, `key`, and `mouse`.

| Flag | Target | Use for |
|------|--------|---------|
| *(none)* | Webview iframe content | Buttons, inputs, tables inside your extension's panels |
| `--page` | VS Code's main UI | Sidebar, tabs, menus, command palette, native UI elements |

## Common Patterns

### Open a panel and verify it loaded
```bash
node src/extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/panel.png
```

### Test a keyboard shortcut
```bash
node src/extension/tools/webview-cdp.mjs key "ctrl+enter"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/after-shortcut.png
```

### Open the command palette
```bash
node src/extension/tools/webview-cdp.mjs key "ctrl+shift+p"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/palette.png
node src/extension/tools/webview-cdp.mjs key "Escape"
```

### Test a context menu
```bash
node src/extension/tools/webview-cdp.mjs click "td[data-row='1']" --right
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/context-menu.png
node src/extension/tools/webview-cdp.mjs click ".context-menu [data-action='copy']"
```

### Test drag selection
```bash
node src/extension/tools/webview-cdp.mjs eval "JSON.stringify(document.querySelector('td[data-row=\"2\"]').getBoundingClientRect())"
node src/extension/tools/webview-cdp.mjs mouse mousedown 150 200
node src/extension/tools/webview-cdp.mjs mouse mousemove 300 250
node src/extension/tools/webview-cdp.mjs mouse mouseup 300 250
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/after-drag.png
```

### Check for errors
```bash
# Console output (captured continuously by daemon)
node src/extension/tools/webview-cdp.mjs logs

# Extension output channel logs
node src/extension/tools/webview-cdp.mjs logs --channel "PPDS"
```

### Execute notebook cells
```bash
# Run the focused cell — clicks the run button (reliable, focus-independent)
node src/extension/tools/webview-cdp.mjs notebook run
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/after-run.png

# Run all cells
node src/extension/tools/webview-cdp.mjs notebook run-all
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/all-cells.png
```

**Avoid** using `command "Notebook: Execute Cell"` — opening the command palette steals focus from the cell, causing the command to silently fail. Similarly, `key "ctrl+enter"` may trigger `executeAndInsertBelow` (creates a duplicate cell) depending on VS Code's keybinding context.

### Hide panel for notebook screenshots
```bash
# The output panel takes vertical space — hide it to see cell output clearly
node src/extension/tools/webview-cdp.mjs command "View: Toggle Panel Visibility"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/notebook-clean.png
```

### Check DOM state
```bash
node src/extension/tools/webview-cdp.mjs eval "document.querySelector('.cell-selected') !== null"
node src/extension/tools/webview-cdp.mjs eval "document.querySelector('#status-text').textContent"
```

### Read element text
```bash
# Quick read of an element's text content
node src/extension/tools/webview-cdp.mjs text "#execution-time" --ext "power-platform-developer-suite"
# Returns: "in 298ms via Dataverse"

# Equivalent eval (more verbose, same result):
node src/extension/tools/webview-cdp.mjs eval 'document.querySelector("#execution-time")?.textContent'
```

## Monaco Editor

**`type` does NOT work with Monaco Editor.** Monaco uses a hidden textarea that doesn't respond to Playwright's `fill()` or `keyboard.type()`. Use `eval` instead:

```bash
# Set Monaco editor content
node src/extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 10 * FROM account")'

# Read Monaco editor content
node src/extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].getValue()'
```

`type` works fine on regular `<input>`, `<textarea>`, and `<select>` elements — just not Monaco.

## VS Code Tree Views

**`click --page` on tree view items is fragile.** VS Code tree views use virtualized lists with dynamic DOM — CSS selectors break when items scroll or re-render. Use `command` instead:

```bash
# Good — use command palette to trigger tree actions
node src/extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"

# Fragile — selector may not match after scroll/re-render
node src/extension/tools/webview-cdp.mjs click --page ".pane-body .monaco-list-row"
```

Reserve `click --page` for stable, non-virtualized UI elements (toolbar buttons, tab headers, status bar items).

## Screenshots

**Always save screenshots to a temp directory. NEVER save to the repo working tree.**

```bash
# Good — uses temp directory (use $TEMP on Windows, $TMPDIR on macOS/Linux)
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/my-screenshot.png
node src/extension/tools/webview-cdp.mjs screenshot /tmp/my-screenshot.png

# BAD — creates untracked files in the repo
node src/extension/tools/webview-cdp.mjs screenshot screenshot.png
```

**Windows note:** `$TMPDIR` does NOT work on Windows (expands to wrong path). Use `$TEMP` instead.

## Bash Quoting for `eval`

Bash expands `${}` and backticks in double-quoted strings. Use single quotes for eval expressions:

```bash
# Good — single quotes prevent bash expansion
node src/extension/tools/webview-cdp.mjs eval 'document.querySelector("td").textContent'

# BAD — bash expands ${} as a variable
node src/extension/tools/webview-cdp.mjs eval "document.querySelector('td[data-row=\"${row}\"]')"
```

## Verification Before Completion

Not every change needs a screenshot. Match verification to the risk profile:

**Screenshot required** — any change affecting rendered UI:
- CSS, layout, styling, theme variables
- Message protocol wiring (postMessage/onMessage handlers)
- HTML templates, component rendering, DOM structure
- Anything that changes what the user sees in a webview panel or notebook output

```bash
# After implementation + tests pass, verify visually
node src/extension/tools/webview-cdp.mjs launch --build
node src/extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/verification.png
# LOOK at the screenshot — don't just take it
```

**Compile + test sufficient** — structural changes with no visual impact:
- String constants, config flags, package.json metadata
- Command registration, URL handlers, enablement conditions
- Internal refactors that don't change rendered output
- Type definitions, interfaces, error codes

Automated tests prove code compiles and logic is correct. Screenshots prove it renders correctly. CSS can silently break (specificity overrides, wrong variable, theme interaction). Message protocol wiring can compile perfectly and fail at runtime (wrong command string, missing switch case). If your change affects what users see, screenshot it before claiming done.

## Error Recovery

### `launch` fails or hangs

```bash
# Check for stale VS Code processes
node src/extension/tools/webview-cdp.mjs close   # try graceful shutdown first

# If still stuck, the daemon may hold the port
node src/extension/tools/webview-cdp.mjs logs
```

Common causes:
- **Stale process from prior session** — always `close` before `launch`
- **Build failure** with `--build` — run `npm run compile --prefix src/extension` separately to see full error output
- **Daemon won't start** — check `logs --channel "PPDS"` for startup errors

### `wait` times out

Check in order:
1. **Was the command correct?** — `command` names are case-sensitive and must match package.json exactly
2. **Did the extension activate?** — `logs --channel "PPDS"` for activation errors
3. **Is the daemon running?** — extension panels depend on the daemon; check daemon output in logs
4. **Increase timeout** — `wait 30000` for slow machines or first-run scenarios

### Screenshot is blank or wrong panel

- **Blank screenshot** — panel may not have finished rendering. Add `wait` before `screenshot`
- **Wrong panel visible** — use `command` to focus the correct panel, then `screenshot`
- **Stale content** — webview may show cached state. Reopen the panel

### CSS-only changes

CSS changes require `--build` because esbuild bundles CSS files:

```bash
node src/extension/tools/webview-cdp.mjs close
node src/extension/tools/webview-cdp.mjs launch --build
node src/extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/extension/tools/webview-cdp.mjs screenshot $TEMP/css-verify.png
```

You cannot hot-reload CSS in VS Code webviews — a full rebuild + relaunch is required.

## Gap Protocol

If you encounter a webview interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need and why the current commands don't cover it
3. **Propose an enhancement** — a new command, flag, or behavior that would close the gap
4. **Ask the user** — whether to implement the enhancement now or defer it

This ensures the tool evolves based on real needs.

## Important

- **Screenshot after UI-affecting changes** — see "Verification Before Completion" for what requires screenshots vs compile+test
- **Use `command` to open panels** — `command "PPDS: Data Explorer"` then `wait`
- **Use `--page` for VS Code native UI** — sidebar, tabs, menus
- **Drop `--page` for webview content** — buttons, inputs, tables inside extension panels
- **Save screenshots to temp dirs** — never create files in the repo working tree
- **Check logs when debugging** — `logs` for console output, `logs --channel "PPDS"` for extension logs
- **Do NOT use agent-browser for VS Code webviews** — it cannot reach webview iframe targets
- **Do NOT use Playwright MCP for webview testing** — it doesn't support Electron
