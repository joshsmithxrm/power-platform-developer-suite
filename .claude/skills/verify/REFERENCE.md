# Verify - REFERENCE

Rationale and worked examples for `.claude/skills/verify/SKILL.md`. The
procedure stays in SKILL.md; everything that doesn't change between
sessions lives here.

---

## TUI Mode - worked example

The TUI verifier wraps `@microsoft/tui-test` + `node-pty`. Full command
reference: §tui below.

```bash
# Phase A: Build and launch
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0

# Phase B (MANDATORY when src/PPDS.Cli/Tui/ changes): interactive nav
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-verify.json

# Phase C: Cleanup
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

---

## Extension Mode - worked example

Wraps `@playwright/test` + `@vscode/test-electron`. Full reference:
§ext below.

```bash
# Phase A: Launch and screenshot
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/data-explorer.png
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"

# Phase B (MANDATORY when query/data/panel code changes): exercise a query
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 5 name FROM account")'
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-query.png

# Phase C: Cleanup
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

Phase B trigger files: `SqlQueryService`, `RpcMethodHandler`, `QueryPanel`,
`query-panel.ts`, anything under data rendering or panel interactions.

---

## Empirical shakedown gate - rationale

PR #1051 Phase B shipped clean from `/verify` then took seven fix commits
because `tests/conftest.py` auto-stubs `claude_dispatch.spawn`. Unit tests
cannot regress what they cannot exercise. The shakedown gate spawns one
real `claude --bg` session against a throwaway prompt and asserts exit 0,
deliberately bypassing the stub.

**Allowlist source of truth:** `scripts/_shakedown_allowlist.py`. Both the
gate (`scripts/verify_shakedown.py`) and the post-`/verify` drift detector
(`scripts/retro_helpers.py:detect_allowlist_drift`) read it. Adding a new
subprocess-spawning wrapper is a one-line append.

**Cost bound:** the helper defaults to a 5-minute timeout. The throwaway
prompt is `"Reply with the word OK and stop."` so a healthy session
finishes in seconds. The gate only runs when the diff touches the allowlist
- typical PRs skip it entirely with one log line.

**Pool discipline:** uses `claude_dispatch.spawn(mode="interactive", ...)`
- subscription pool, never `-p`. The dispatcher emits the SDK-spend
warning when callers pick `-p`, so accidentally regressing this surfaces
in stderr.

---

## Report template

```
## Verification Results -- [component]

| Check | Status | Details |
|-------|--------|---------|
| Unit tests | PASS | 12/12 passing |
| Daemon connection | PASS | PID 12345, uptime 30s |
| Tree view state | PASS | 2 profiles, 3 environments |
| Data Explorer open | PASS | Panel created |
| SQL query execution | PASS | 5 rows returned |
| Webview rendering | PASS | Query panel layout correct |

### Verdict: PASS -- all checks green
```

---

## Retro store schema (Check 7)

If `.retros/summary.json` exists:
- Parse as JSON
- Required keys: `schema_version`, `last_updated`, `total_retros`,
  `findings_by_category`, `metrics`
- `schema_version == 1`

Missing file passes (the store is optional until first retro).

---

## §tui — TUI Verify Reference

Interact with the PPDS TUI (`ppds interactive`) running in a PTY. Read terminal text to verify what's rendered, send keystrokes to navigate, wait for specific content to appear. Text-based verification — the AI reads terminal rows directly instead of interpreting screenshots.

### Safety pre-check

These verification flows talk to live Dataverse. The `shakedown-safety`
PreToolUse hook gates `ppds *` invocations to keep an agent from writing
to a non-dev env, with two concerns in one gate:

- **Env allowlist (always on).** Blocks `ppds *` unless the active env
  is in the allowlist (`$PPDS_SAFE_ENVS` or `safety.shakedown_safe_envs`
  in `.claude/settings.json`).
- **Write-block during shakedown (active when `PPDS_SHAKEDOWN=1`).**
  Blocks write verbs (`create`, `update`, `delete`, `plugins deploy`
  without `--dry-run`, `solutions import`, etc.). The shakedown skill
  exports `PPDS_SHAKEDOWN=1` in its Phase 0.

Before running any of the patterns below:

1. Confirm `ppds env who` shows a safe env (in the allowlist).
2. If invoked from `/shakedown`, verify `PPDS_SHAKEDOWN=1` is set so write
   commands fail closed.
3. Read `docs/SAFE-SHAKEDOWN.md` for the full model and bypass procedure.

### When to Use

- After implementing or modifying any TUI screen, widget, or keyboard handler
- When debugging TUI rendering — read specific rows to see what's there
- When testing keyboard navigation — send keystrokes and verify focus moved
- When verifying status bar content after an operation

### Setup

The tool is at `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`. Uses `@microsoft/tui-test` (already a dev dependency). Requires the TUI to be built first. The TUI subcommand is `ppds interactive` (the tool handles this internally).

### First Steps After Launch

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

### Commands

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

### Key Combos

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

### Screen Layout (120x30 terminal)

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

### Common Patterns

#### Open SQL Query screen
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+t"   # Tools menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"   # SQL Query (first item)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
```

#### Check query status label
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 24  # inner status (Ready, Mode, error, etc.)
```

#### Check profile and environment
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28  # Profile: ... Environment: ...
```

#### Navigate Query menu items
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+q"   # open Query menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "down"    # navigate items
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"   # select
```

Query menu items (top to bottom): Execute, Show FetchXML, Show Execution Plan, History, (separator), Filter Results, (separator), TDS Read Replica.

#### Type and execute a query
```bash
# Focus is usually on the query editor after opening SQL Query
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT TOP 10 name FROM account"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "F5"     # execute
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "rows" 30000  # wait for results
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 24      # check status (e.g., "Returned 10 rows in 298ms via Dataverse")
```

#### Debug rendering issues
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-state.json
# Read the file to see full terminal state with colors
```

#### Read multiple rows to understand layout
```bash
for i in 0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29; do
  echo "Row $i: $(node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text $i)"
done
```

### Screenshots

The `screenshot` command dumps the terminal's `serialize()` output as JSON — text with color metadata. Not a PNG image. Use `$TEMP` for the output path:

```bash
# Good
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/debug.json

# BAD — creates file in repo
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot debug.json
```

### Verification Before Completion (TUI)

**Interactive verification required** — any change affecting TUI rendering:
- Screen layout, widget placement, borders
- Keyboard handlers, focus order
- Status bar content, menu items
- Dialog boxes, error messages

**Compile + test sufficient** — no visual impact:
- Application Service logic (business rules)
- Dataverse queries, data transformation
- CLI command behavior (not TUI)

### Error Recovery (TUI)

#### `launch` fails or times out
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

#### `wait` times out
1. Check if TUI is alive: `text 0` — if error, TUI crashed. `close` then `launch --build`
2. Check if you're on the right screen: read several rows with `text`
3. Check the daemon log: `cat tests/PPDS.Tui.E2eTests/tools/.tui-verify-daemon.log`
4. Increase timeout: `wait "text" 30000`

#### TUI crashes mid-session
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close   # clean up
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
```

### Gap Protocol (TUI)

If you encounter a TUI interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need
3. **Propose an enhancement** — a new command or flag
4. **Ask the user** — implement now or defer

---

## §ext — Extension Webview Verify Reference

Interact with extension webview panels running inside VS Code. Take screenshots to see what you've built, click buttons, type text, test keyboard shortcuts, execute VS Code commands, right-click context menus, read console logs, and inspect DOM state.

### Safety pre-check

These verification flows talk to live Dataverse. The `shakedown-safety`
PreToolUse hook gates `ppds *` invocations to keep an agent from writing
to a non-dev env, with two concerns in one gate:

- **Env allowlist (always on).** Blocks `ppds *` unless the active env
  is in the allowlist (`$PPDS_SAFE_ENVS` or `safety.shakedown_safe_envs`
  in `.claude/settings.json`).
- **Write-block during shakedown (active when `PPDS_SHAKEDOWN=1`).**
  Blocks write verbs (`create`, `update`, `delete`, `plugins deploy`
  without `--dry-run`, `solutions import`, etc.). The shakedown skill
  exports `PPDS_SHAKEDOWN=1` in its Phase 0.

Before running any of the patterns below:

1. Confirm `ppds env who` shows a safe env (in the allowlist).
2. If invoked from `/shakedown`, verify `PPDS_SHAKEDOWN=1` is set so write
   commands fail closed.
3. Read `docs/SAFE-SHAKEDOWN.md` for the full model and bypass procedure.

### When to Use (Extension)

- After implementing or modifying any webview panel UI
- When debugging visual issues — screenshot to see the current state
- When testing interactions — clicks, keyboard shortcuts, context menus, dropdowns
- When checking for errors — read console logs and output channel content

### Setup (Extension)

The tool is at `src/PPDS.Extension/tools/webview-cdp.mjs`. Uses `@playwright/test` and `@vscode/test-electron` (both already dev dependencies). First run downloads VS Code (~30-60 seconds, cached afterward).

### Core Workflow

```bash
# 1. Launch VS Code with the extension (--build compiles extension + daemon)
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build

# 2. Open a panel via command palette
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"

# 3. Take a screenshot to see what you built
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/current-state.png
# IMPORTANT: Actually look at the screenshot to verify the UI

# 4. Interact with webview content (use --ext to target the PPDS webview)
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-click.png

# 5. Check for errors
node src/PPDS.Extension/tools/webview-cdp.mjs logs

# 6. When done
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

**Always use `--ext "power-platform-developer-suite"`** on `eval`, `click`, `type`, `select`, and `wait` commands. VS Code may have other webviews open (walkthrough, settings, etc.) — without `--ext`, you might interact with the wrong panel.

### Commands (Extension)

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

### Two Targets: `--page` vs default

| Flag | Target | Use for |
|------|--------|---------|
| *(none)* | Webview iframe content | Buttons, inputs, tables inside your extension's panels |
| `--page` | VS Code's main UI | Sidebar, tabs, menus, command palette, native UI elements |

### Common Patterns (Extension)

#### Open a panel and verify it loaded
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/panel.png
```

#### Test a keyboard shortcut
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs key "ctrl+enter"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-shortcut.png
```

#### Open the command palette
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs key "ctrl+shift+p"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/palette.png
node src/PPDS.Extension/tools/webview-cdp.mjs key "Escape"
```

#### Test a context menu
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs click "td[data-row='1']" --right
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/context-menu.png
node src/PPDS.Extension/tools/webview-cdp.mjs click ".context-menu [data-action='copy']"
```

#### Monaco Editor
**`type` does NOT work with Monaco Editor.** Use `eval` instead:

```bash
# Set Monaco editor content
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 10 * FROM account")'

# Read Monaco editor content
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].getValue()'
```

#### Check for errors
```bash
# Console output (captured continuously by daemon)
node src/PPDS.Extension/tools/webview-cdp.mjs logs

# Extension output channel logs
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"
```

#### Execute notebook cells
```bash
# Run the focused cell — clicks the run button (reliable, focus-independent)
node src/PPDS.Extension/tools/webview-cdp.mjs notebook run
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-run.png

# Run all cells
node src/PPDS.Extension/tools/webview-cdp.mjs notebook run-all
```

**Avoid** using `command "Notebook: Execute Cell"` — opening the command palette steals focus from the cell. Similarly, `key "ctrl+enter"` may trigger `executeAndInsertBelow` depending on VS Code's keybinding context.

### Screenshots (Extension)

**Always save screenshots to a temp directory. NEVER save to the repo working tree.** <!-- enforcement: T2 hook:screenshot-temp-dir -->

```bash
# Good — uses temp directory (use $TEMP on Windows, $TMPDIR on macOS/Linux)
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/my-screenshot.png

# BAD — creates untracked files in the repo
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot screenshot.png
```

**Windows note:** `$TMPDIR` does NOT work on Windows. Use `$TEMP` instead.

### Verification Before Completion (Extension)

**Screenshot required** — any change affecting rendered UI:
- CSS, layout, styling, theme variables
- Message protocol wiring (postMessage/onMessage handlers)
- HTML templates, component rendering, DOM structure

```bash
# After implementation + tests pass, verify visually
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/verification.png
# LOOK at the screenshot — don't just take it
```

**Compile + test sufficient** — structural changes with no visual impact:
- String constants, config flags, package.json metadata
- Command registration, URL handlers, enablement conditions
- Internal refactors that don't change rendered output

### Error Recovery (Extension)

#### `launch` fails or hangs
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close   # try graceful shutdown first
node src/PPDS.Extension/tools/webview-cdp.mjs logs
```

Common causes:
- **Stale process from prior session** — always `close` before `launch`
- **Build failure** with `--build` — run `npm run compile --prefix src/PPDS.Extension` separately
- **Daemon won't start** — check `logs --channel "PPDS"` for startup errors

#### CSS-only changes
CSS changes require `--build` because esbuild bundles CSS files. You cannot hot-reload CSS in VS Code webviews.

### Log File Locations

VS Code `LogOutputChannel` writes to `exthost/<extId>/Name.log`:

```
<vscode-user>/logs/<window>/exthost/JoshSmithXRM.power-platform-developer-suite/PPDS.log
```

The numeric prefix on adjacent files (`1-CodeLens.log`, etc.) refers to core VS Code services, not extension-defined channels.

### Gap Protocol (Extension)

If you encounter a webview interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need and why the current commands don't cover it
3. **Propose an enhancement** — a new command, flag, or behavior that would close the gap
4. **Ask the user** — whether to implement the enhancement now or defer it

---

## §cli — CLI Verify Reference

Supporting knowledge for `/verify cli` and `/qa cli`. Documents how to build, run, and verify CLI commands.

### Safety pre-check

These verification flows talk to live Dataverse. The `shakedown-safety`
PreToolUse hook gates `ppds *` invocations to keep an agent from writing
to a non-dev env, with two concerns in one gate:

- **Env allowlist (always on).** Blocks `ppds *` unless the active env
  is in the allowlist (`$PPDS_SAFE_ENVS` or `safety.shakedown_safe_envs`
  in `.claude/settings.json`).
- **Write-block during shakedown (active when `PPDS_SHAKEDOWN=1`).**
  Blocks write verbs (`create`, `update`, `delete`, `plugins deploy`
  without `--dry-run`, `solutions import`, etc.). The shakedown skill
  exports `PPDS_SHAKEDOWN=1` in its Phase 0.

Before running any of the patterns below:

1. Confirm `ppds env who` shows a safe env (in the allowlist).
2. If invoked from `/shakedown`, verify `PPDS_SHAKEDOWN=1` is set so write
   commands fail closed.
3. Read `docs/SAFE-SHAKEDOWN.md` for the full model and bypass procedure.

### Build

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
```

### Run Commands

```bash
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```

### Output Conventions (Constitution I1)

- **stdout** is for data only — pipeable, machine-readable
- **stderr** is for status messages, progress, diagnostics
- When verifying: check stdout for correct data format, stderr for appropriate status messages

### Verification Checklist

For each command:

1. **Executes without error** — exit code is 0
2. **Output format is correct** — JSON, table, or expected text
3. **Pipe-friendly** — stdout can be piped to `jq`, `grep`, etc.
4. **Error handling** — bad input produces clear error message on stderr, non-zero exit code
5. **Help text** — `ppds <command> --help` shows usage

### Common Commands

| Command | Purpose | Expected Output |
|---------|---------|----------------|
| `ppds auth list` | List profiles | JSON array of profiles |
| `ppds auth login` | Interactive login | Status on stderr, profile on stdout |
| `ppds query sql "<sql>"` | Execute SQL | Results on stdout |
| `ppds data export` | Export data | File path on stdout |
| `ppds env list` | List environments | JSON array |
| `ppds serve` | Start daemon | Status on stderr (long-running) |
| `ppds interactive` | Launch TUI | Terminal UI (interactive) |

### Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | General error |
| 2 | Invalid arguments |

### Error Verification

Test error paths:
- Invalid command: `ppds nonexistent` → non-zero exit, error message
- Bad arguments: `ppds query sql` (no query) → non-zero exit, usage hint
- No profile: `ppds query sql "SELECT 1"` (without auth) → clear error about missing profile

---

## §mcp — MCP Verify Reference

Supporting knowledge for `/verify mcp` and `/qa mcp`. Documents how to interact with and verify MCP server tools.

### Safety pre-check

These verification flows talk to live Dataverse. The `shakedown-safety`
PreToolUse hook gates `ppds *` invocations to keep an agent from writing
to a non-dev env, with two concerns in one gate:

- **Env allowlist (always on).** Blocks `ppds *` unless the active env
  is in the allowlist (`$PPDS_SAFE_ENVS` or `safety.shakedown_safe_envs`
  in `.claude/settings.json`).
- **Write-block during shakedown (active when `PPDS_SHAKEDOWN=1`).**
  Blocks write verbs (`create`, `update`, `delete`, `plugins deploy`
  without `--dry-run`, `solutions import`, etc.). The shakedown skill
  exports `PPDS_SHAKEDOWN=1` in its Phase 0.

Before running any of the patterns below:

1. Confirm `ppds env who` shows a safe env (in the allowlist).
2. If invoked from `/shakedown`, verify `PPDS_SHAKEDOWN=1` is set so write
   commands fail closed.
3. Read `docs/SAFE-SHAKEDOWN.md` for the full model and bypass procedure.

### Build

```bash
dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -f net10.0
```

### MCP Inspector

The MCP Inspector provides a web UI for testing tools interactively:

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```

This opens a web UI where you can:
- List available tools
- Invoke tools with parameters
- See JSON responses
- Test session options

### Direct Tool Invocation

For programmatic testing, invoke tools via the MCP protocol:

```bash
# List tools
echo '{"jsonrpc":"2.0","method":"tools/list","id":1}' | ppds-mcp-server

# Call a tool
echo '{"jsonrpc":"2.0","method":"tools/call","params":{"name":"ppds_query_sql","arguments":{"sql":"SELECT TOP 1 name FROM account"}},"id":2}' | ppds-mcp-server
```

### Session Options

Test session configuration flags:

| Flag | Purpose | Test |
|------|---------|------|
| `--profile <name>` | Lock to specific profile | Invoke with wrong profile, verify rejection |
| `--environment <url>` | Lock to specific environment | Verify tools use the locked environment |
| `--read-only` | Prevent DML operations | Run INSERT/UPDATE/DELETE, verify rejection |
| `--allowed-env <urls>` | Restrict environment switching | Try switching to disallowed env, verify rejection |

### Response Validation

For each tool invocation, verify:
1. Response is valid JSON
2. `content` array contains expected text/data
3. `isError` is false for success cases, true for error cases
4. Error messages are descriptive (not raw exceptions)

### Common Failure Modes

| Symptom | Likely Cause |
|---------|-------------|
| "No active profile" | Session not initialized, or profile flag wrong |
| Timeout | Dataverse connection issue, check environment URL |
| Empty results | Query is valid but no matching data |
| "Operation not permitted" | `--read-only` flag blocking DML |

---

## §workflow — Workflow Infrastructure Verify Reference

How to test changes to `.claude/` infrastructure: hooks, skills, agents, settings, pipeline, state.

### Hook Testing

#### PreToolUse / PostToolUse hooks

Pipe JSON matching the hook's input schema to the hook script. Check exit code (0=allow, 2=block) and stderr (feedback message).

```bash
# Test a PreToolUse hook (e.g., protect-main-branch)
python -c "
import subprocess, json
input_data = json.dumps({'file_path': 'src/PPDS.Cli/Program.cs'})
result = subprocess.run(
    ['python', '.claude/hooks/protect-main-branch.py'],
    input=input_data, capture_output=True, text=True
)
print(f'exit={result.returncode} stderr={result.stderr[:100]}')
"
```

**Exit codes:** 0 = allow, 2 = block (with stderr message shown to AI)

#### SessionStart hooks

SessionStart hooks write to stderr (context injection). Run standalone and check stderr output:

```bash
python .claude/hooks/session-start-workflow.py 2>&1
```

#### Stop hooks

Stop hooks return JSON with `decision: "block"` or allow (exit 0). Must check `stop_hook_active` env var to prevent infinite loops.

#### Notification hooks

Pipe notification JSON on stdin:

```bash
echo '{"notification_type": "idle_prompt", "message": "test", "title": "test", "cwd": "."}' | python .claude/hooks/notify.py
```

#### Compaction re-injection

Run `/compact` in a session to trigger the `compact` SessionStart matcher. Verify workflow state is re-injected by checking the AI's next response references the workflow status.

### Pipeline Testing

#### Dry run (all stages)

```bash
mkdir -p .plans && echo "# Test\n## Phase 1\nDo something" > .plans/test-plan.md
python scripts/pipeline.py --plan .plans/test-plan.md --branch test/dry-run --dry-run --no-retro --worktree .
```

#### Test specific stage

```bash
python scripts/pipeline.py --plan .plans/test-plan.md --branch test/dry-run --dry-run --no-retro --from implement --worktree .
```

#### Resume detection

```bash
python -c "
import sys; sys.path.insert(0, 'scripts')
from pipeline import find_last_completed_stage
# Write a fake pipeline.log
import os; os.makedirs('.workflow', exist_ok=True)
with open('.workflow/pipeline.log', 'w') as f:
    f.write('[implement] DONE\n[gates] DONE\n')
print(find_last_completed_stage('.workflow/pipeline.log'))
# Expected: 'gates'
"
```

### Settings Validation

After editing `.claude/settings.json`:

```bash
python -c "import json; json.load(open('.claude/settings.json')); print('valid JSON')"
```

Check matcher format — matchers are regex patterns tested against tool names or notification types.

### State File Testing

```bash
# Initialize
python scripts/workflow-state.py init my-branch

# Set values
python scripts/workflow-state.py set gates.passed true
python scripts/workflow-state.py set verify.cli now
python scripts/workflow-state.py set review.findings 3

# Read values
python scripts/workflow-state.py get gates.passed
python scripts/workflow-state.py show

# Clear values
python scripts/workflow-state.py set-null gates.passed

# Delete state
python scripts/workflow-state.py delete
```

### Automated Behavioral Tests

Run `python scripts/verify-workflow.py` for automated behavioral scenario tests against workflow hooks and state management.

```bash
# Run all scenarios (writes verify.workflow timestamp on pass)
python scripts/verify-workflow.py

# Run a single scenario
python scripts/verify-workflow.py hook-stop-block

# List available scenarios
python scripts/verify-workflow.py --list
```

Scenarios test: stop hook block/allow, PR gate block/allow, state invalidation, session-start completeness, resume detection. Each scenario sets up state, exercises a hook via subprocess, and asserts the result.

### Common Pitfalls

- **Windows path escaping in JSON:** Use forward slashes or double-backslash. `$CLAUDE_PROJECT_DIR` uses forward slashes.
- **`$CLAUDE_PROJECT_DIR` in worktrees:** Resolves to the worktree root, not the main repo. Hooks using this var work correctly in both locations.
- **Exit code handling in bash pipes:** `echo ... | python script.py` — the exit code is from `python`, not `echo`. Use `$?` or `subprocess.run` for reliable capture.
- **Hook timeouts:** Default 600s (10 min). Set shorter for fast hooks (5s for path checks, 10s for notifications, 120s for build+test).
