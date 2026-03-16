---
name: tui-verify
description: Interactive TUI verification via PTY — launch, navigate, read text, send keystrokes. Use after implementing or modifying any TUI-affecting change (screens, widgets, keyboard handlers, status bar). For non-visual changes (service logic, data access), compile + test is sufficient.
allowed-tools: Bash(node *tui-verify*), Bash(cd * && node *tui-verify*)
---

# TUI Verify Tool

Interact with the PPDS TUI running in a PTY. Read terminal text to verify what's rendered, send keystrokes to navigate, wait for specific content to appear. Text-based verification — the AI reads terminal rows directly instead of interpreting screenshots.

## When to Use

- After implementing or modifying any TUI screen, widget, or keyboard handler
- When debugging TUI rendering — read specific rows to see what's there
- When testing keyboard navigation — send keystrokes and verify focus moved
- When verifying status bar content after an operation

## Setup

The tool is at `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`. Uses `@microsoft/tui-test` (already a dev dependency). Requires the TUI to be built first.

## Core Workflow

```bash
# 1. Build and launch TUI in PTY (120x30 terminal)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# 2. Wait for it to render
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# 3. Read specific rows to verify content
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0   # title bar
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29  # status bar

# 4. Navigate with keystrokes
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# 5. Verify expected content appeared
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000

# 6. When done
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [--build]` | `launch --build` | Start TUI in PTY. `--build` compiles first |
| `close` | `close` | Kill PTY, clean up (idempotent) |
| `text <row>` | `text 0` | Read terminal row content (0-based, 0-29) |
| `key "<combo>"` | `key "ctrl+t"` | Send keystroke |
| `type "<text>"` | `type "SELECT"` | Type text character by character |
| `wait "<text>" [ms]` | `wait "PPDS" 5000` | Poll until text appears (default 10s) |
| `screenshot <file>` | `screenshot $TEMP/debug.json` | Dump serialize() as JSON (text + colors) |
| `rows` | `rows` | Show terminal dimensions (120x30) |

## Key Combos

| Combo | What it does in Terminal.Gui |
|-------|------------------------------|
| `tab` | Cycle focus to next widget |
| `shift+tab` | Cycle focus to previous widget |
| `enter` | Activate focused button/menu item |
| `escape` | Close dialog/cancel operation |
| `alt+<letter>` | Menu bar shortcut (e.g., `alt+f` for File) |
| `ctrl+q` | Quit (standard Terminal.Gui quit) |
| `up`/`down` | Navigate lists, menus, tree views |
| `F1`-`F12` | Function key shortcuts |
| `ctrl+t` | Common PPDS shortcut for TDS toggle |

## Common Patterns

### Verify a screen loaded
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0  # check title
```

### Navigate to a specific screen
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+s"  # Solutions menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "Solutions" 5000
```

### Check status bar after operation
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29  # bottom row
```

### Type into a text field
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"  # focus the input
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT TOP 10 * FROM account"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"
```

### Debug rendering issues
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-state.json
# Read the file to see full terminal state with colors
```

### Read multiple rows
```bash
for i in 0 1 2 3 4 5; do
  node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text $i
done
```

## Screenshots

The `screenshot` command dumps the terminal's `serialize()` output as JSON — text with color metadata. Not a PNG image. Use `$TEMP` for the output path:

```bash
# Good
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/debug.json

# BAD — creates file in repo
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot debug.json
```

## Verification Before Completion

**Interactive verification required** — any change affecting TUI rendering:
- Screen layout, widget placement, borders
- Keyboard handlers, focus order
- Status bar content, menu items
- Dialog boxes, error messages

**Compile + test sufficient** — no visual impact:
- Application Service logic (business rules)
- Dataverse queries, data transformation
- CLI command behavior (not TUI)

## Error Recovery

### `launch` fails
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close  # clean up stale session
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
```

### `wait` times out
1. Check if TUI is alive: `text 0` — if error, TUI crashed. `close` then `launch --build`
2. Check if you're on the right screen: read several rows with `text`
3. Increase timeout: `wait "text" 30000`

### TUI crashes mid-session
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close   # clean up
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
```

## Gap Protocol

If you encounter a TUI interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need
3. **Propose an enhancement** — a new command or flag
4. **Ask the user** — implement now or defer
