---
name: tui-verify
description: Interactive TUI verification via PTY — launch, navigate, read text, send keystrokes. Use after implementing or modifying any TUI-affecting change (screens, widgets, keyboard handlers, status bar). For non-visual changes (service logic, data access), compile + test is sufficient.
allowed-tools: Bash(node *tui-verify*), Bash(cd * && node *tui-verify*)
---

# TUI Verify Tool

Interact with the PPDS TUI (`ppds interactive`) running in a PTY. Read terminal text to verify what's rendered, send keystrokes to navigate, wait for specific content to appear. Text-based verification — the AI reads terminal rows directly instead of interpreting screenshots.

## When to Use

- After implementing or modifying any TUI screen, widget, or keyboard handler
- When debugging TUI rendering — read specific rows to see what's there
- When testing keyboard navigation — send keystrokes and verify focus moved
- When verifying status bar content after an operation

## Setup

The tool is at `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`. Uses `@microsoft/tui-test` (already a dev dependency). Requires the TUI to be built first. The TUI subcommand is `ppds interactive` (the tool handles this internally).

## First Steps After Launch

The TUI starts with no profile connected. You must select a profile and environment before most features work.

```bash
# 1. Build and launch TUI in PTY (120x30 terminal)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# 2. Wait for splash screen to render
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# 3. Select profile (Alt+P opens profile picker)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+p"
# Arrow to the desired profile, then Enter to select
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# 4. Select environment (Alt+E opens environment picker)
#    If environment auto-populated from profile, skip this step.
#    Check row 28 to see current state:
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28
#    If it shows "Environment: None", open the picker:
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+e"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# 5. Navigate to SQL Query screen
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+t"   # Tools menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"   # SQL Query (first item)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000

# 6. Verify connection — check the tab title includes environment name
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2
# Expected: │[ 1: SQL Query - <env name> ] [+]
```

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [--build]` | `launch --build` | Start TUI in PTY. `--build` compiles first |
| `close` | `close` | Kill PTY, clean up (idempotent) |
| `text <row>` | `text 0` | Read terminal row content (0-based, 0-29) |
| `key "<combo>"` | `key "alt+t"` | Send keystroke |
| `type "<text>"` | `type "SELECT"` | Type text character by character |
| `wait "<text>" [ms]` | `wait "PPDS" 5000` | Poll until text appears (default 10s) |
| `screenshot <file>` | `screenshot $TEMP/debug.json` | Dump serialize() as JSON (text + colors) |
| `rows` | `rows` | Show terminal dimensions (120x30) |

## Key Combos

| Combo | What it does in PPDS TUI |
|-------|--------------------------|
| `tab` | Cycle focus to next widget |
| `shift+tab` | Cycle focus to previous widget |
| `enter` | Activate focused button/menu item |
| `escape` | Close dialog/cancel operation |
| `alt+f` | Open File menu |
| `alt+q` | Open Query menu (on SQL Query screen) |
| `alt+t` | Open Tools menu |
| `alt+h` | Open Help menu |
| `alt+p` | Open profile selector dialog |
| `alt+e` | Open environment selector dialog |
| `ctrl+q` | Quit TUI (exits immediately, no confirmation) |
| `up`/`down` | Navigate lists, menus, tree views |
| `F5` | Execute query (on SQL Query screen) |
| `F1`-`F12` | Function key shortcuts |

**Known issue:** `ctrl+shift+t` is bound to both "new query tab" and "TDS Read Replica toggle" — the new-tab binding wins. Use the Query menu instead: `alt+q` → navigate to "TDS Read Replica" → `enter`. See [#580](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/580).

## Screen Layout (120x30 terminal)

Understanding the row layout helps you read the right rows:

| Rows | Content |
|------|---------|
| 0 | Title bar: "PPDS - Power Platform Developer Suite" |
| 1 | Menu bar: File, Query/Tools, Help |
| 2 | Tab bar (on SQL Query screen) |
| 3 | Screen title (e.g., "SQL Query - Env Name") |
| 4-9 | Query editor area |
| 10 | Splitter |
| 11-23 | Results table |
| 24 | Inner status label (e.g., "Ready", "Mode: TDS Read Replica...") |
| 25 | Inner frame border |
| 26 | (empty) |
| 27 | Error/notification bar |
| 28 | Profile/Environment status: "Profile: ... Environment: ..." |
| 29 | Bottom border |

**Row 24** is the query status label. **Row 28** is the profile/environment bar. **Row 29** is just the border.

## Common Patterns

### Open SQL Query screen
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+t"   # Tools menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"   # SQL Query (first item)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
```

### Check query status label
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 24  # inner status (Ready, Mode, error, etc.)
```

### Check profile and environment
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28  # Profile: ... Environment: ...
```

### Navigate Query menu items
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+q"   # open Query menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "down"    # navigate items
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"   # select
```

Query menu items (top to bottom): Execute, Show FetchXML, Show Execution Plan, History, (separator), Filter Results, (separator), TDS Read Replica.

### Type and execute a query
```bash
# Focus is usually on the query editor after opening SQL Query
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT TOP 10 name FROM account"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "F5"     # execute
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "rows" 30000  # wait for results
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 24      # check status (e.g., "Returned 10 rows in 298ms via Dataverse")
```

### Debug rendering issues
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-state.json
# Read the file to see full terminal state with colors
```

### Read multiple rows to understand layout
```bash
for i in 0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29; do
  echo "Row $i: $(node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text $i)"
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

### `launch` fails or times out
```bash
# Check the daemon log for diagnostics — this is the most important step
cat tests/PPDS.Tui.E2eTests/tools/.tui-verify-daemon.log

# Common causes:
# - "Binary not found" → run with --build
# - "PTY exited: code=1" → wrong subcommand or missing dependency
# - No log file at all → node-pty compilation issue

# Clean up and retry
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
```

### `wait` times out
1. Check if TUI is alive: `text 0` — if error, TUI crashed. `close` then `launch --build`
2. Check if you're on the right screen: read several rows with `text`
3. Check the daemon log: `cat tests/PPDS.Tui.E2eTests/tools/.tui-verify-daemon.log`
4. Increase timeout: `wait "text" 30000`

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
