# Verify - REFERENCE

Mechanics for `.claude/skills/verify/SKILL.md`: the safety pre-check, the
report template, and one section per surface. Every command below runs from
the repo root in the Bash tool (Git Bash on Windows), so paths use forward
slashes.

---

## Safety pre-check

Applies to every flow that reaches live Dataverse (CLI, TUI, extension, MCP).
The `.claude/hooks/shakedown-safety.py` PreToolUse hook inspects each Bash
tool and PowerShell tool command that invokes `ppds` or `ppds-mcp-server`
by program name (a path ending in `ppds` counts) or through `dotnet`
(`dotnet run --project src/PPDS.Cli`, `dotnet .../ppds.dll`,
`dotnet tool exec PPDS.Cli`), also inside `bash -c`, `pwsh -Command` and
`cmd /c`. Full model: `docs/SAFE-SHAKEDOWN.md`.

- **`--` before application arguments.** Write
  `dotnet run --project src/PPDS.Cli -- data list`. Without `--` the hook
  checks several readings of the arguments and may block a read.
- **Both readings, every mention.** Each command is read with PowerShell
  rules and with POSIX rules whatever the tool, and every mention of
  `ppds`, `dotnet` or `dnx` the readings cannot place in a word counts as
  an invocation with an unknown verb.
- **Caps.** Past any cap a command is treated as an unreadable invocation,
  mention or not, and both gates refuse it: 200,000 characters, nesting 8
  levels deep, 4,000,000 characters of reading work, 3.5 seconds (a
  watchdog refuses at 4.5), 500 words naming a guarded program per reading,
  400 arguments per invocation, and in the original matcher 500 `ppds` or
  `dotnet` words with 400 tokens / 4,096 characters after each. The full
  list is in `docs/SAFE-SHAKEDOWN.md`.
- **Only recognised shapes pass.** What the hook cannot place is refused:
  a response file, an argument only known at run time, an option bundle
  that may hold `-e` / `-p`, a target it cannot resolve. With an allowlist,
  every `--environment` / `--profile` value (any delimiter) and every
  `PPDS_` variable that can move the target is checked, not only the
  active env.
- **Mentions count.** A commit message, pull request body or search pattern
  that mentions `ppds` is treated as an invocation when a gate applies; pass
  long text through a file (`git commit -F`, `gh pr create --body-file`).

- **Env allowlist (opt-in).** Off until a list names an env. The hook reads,
  first match wins: `$PPDS_SAFE_ENVS` (comma-separated, read from the
  environment Claude Code was started in), then `safety.shakedown_safe_envs`
  in the tracked `.claude/settings.json` (checked in as `[]`), then the
  legacy `.claude/state/safe-envs.json` (`safe_envs`). Once on, `ppds *` is allowed
  only when the active env is in the list, and an unidentifiable active env
  blocks; `env select` / `auth select` to a listed target and the read forms
  of `env` pass with an unlisted one. A misconfigured source (unparseable settings, wrong type, a value
  naming no env) blocks every `ppds` command until it is fixed or removed.
- **Write-block.** Armed by the sentinel `.claude/state/shakedown-active.json`
  (what `/shakedown` Phase 0 writes; under 24h old) or by `PPDS_SHAKEDOWN=1`
  in the environment Claude Code was started in. An inline
  `PPDS_SHAKEDOWN=1 ppds ...` in the Bash tool does not reach the hook.
  Passes only command paths the hook's table classifies as read-only, and
  the conditional ones in their recognised shape (`plugins deploy --dry-run`,
  `query sql` with inline SQL holding no write word, `version` alone or with
  `--check`, `ppds-mcp-server --read-only`); everything else, `ppds` with no
  arguments (the TUI) and any command the table does not know included, is
  refused. A help flag passes only directly after the command path.

Before running anything below:

1. `ppds env who` (allowed whatever the active env) and confirm the env is a dev/test env you
   mean to touch, and in the allowlist if one is configured.
2. When invoked from `/shakedown`, confirm `.claude/state/shakedown-active.json`
   exists so writes fail closed.
3. Invoke the CLI as `ppds`, `src/PPDS.Cli/bin/Debug/net10.0/ppds` or
   `dotnet run --project src/PPDS.Cli -- ...`, not through
   `scripts/ppds-dev.ps1`: the hook does not see that script (nor the CLI or
   TUI that a script such as `tui-verify.mjs` starts).

---

## Report template

```
## Verification Results -- [component]

| Check | Status | Details |
|-------|--------|---------|
| Unit tests | PASS | 12/12 passing |
| Data Explorer open | PASS | Panel created, env "Dev" shown |
| SQL query execution | PASS | 5 rows returned in 298ms |
| Webview rendering | PASS | Screenshot reviewed: toolbar, grid, status bar |

### Verdict: PASS -- all checks green
```

---

## §cli — CLI Mode

### Build and run

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0 -v q
src/PPDS.Cli/bin/Debug/net10.0/ppds <command> <args>
```

The apphost is `ppds.exe` on Windows; Git Bash resolves it from the path
above without the extension, and the same path works on macOS/Linux. To test
the packaged global tool instead, run `pwsh scripts/Install-LocalCli.ps1`
(packs and reinstalls `PPDS.Cli`), then call `ppds`.

### What to check

- **Exit code** 0 on success. Non-zero codes are defined in
  `src/PPDS.Cli/Infrastructure/Errors/ExitCodes.cs`: 1 partial success,
  2 failure, 3 invalid arguments, 4 connection, 5 auth, 6 not found,
  7 mapping required, 8 validation, 9 forbidden, 10 precondition failed,
  11 confirmation required (stdin redirected; `--force` bypasses),
  12 incomplete.
- **stdout is data only, stderr is status** (Constitution I1). Pipe stdout
  to `jq` or `grep` to prove it is clean; `--output-format json` where the
  command offers it.
- **Errors** go to stderr with a non-zero exit: an unknown command
  (`ppds nonexistent`), missing arguments (`ppds query sql`), and no active
  profile.
- **Help** `ppds <command> --help` shows the new options.

Commonly used: `ppds auth list`, `ppds auth create`, `ppds auth who`,
`ppds env list`, `ppds env who`, `ppds env select <name>`,
`ppds query sql "<sql>"`, `ppds data export`, `ppds serve` (daemon, long
running), `ppds interactive` (TUI).

---

## §tui — TUI Mode

Tool: `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` (`@microsoft/tui-test` +
`node-pty`). It runs `ppds interactive` in a 120x30 PTY behind a background
daemon; you read rows as text instead of interpreting screenshots.

- It launches `src/PPDS.Cli/bin/Debug/net10.0/ppds.exe`, so it currently
  works only on Windows. `launch --build` runs
  `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0` first.
- `launch` with a live session prints "TUI is already running" and does
  nothing, so after a rebuild `close` first or you test the old binary.
- The daemon exits after 30 minutes without a request.
- Session and log files live next to the tool
  (`.tui-verify-session.json`, `.tui-verify-daemon.log`); `close` deletes
  the log, so read it before closing.

### Worked example

The TUI starts with no profile connected; pick a profile and environment
before most screens work.

```bash
# Phase A: build, launch, confirm it rendered
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0

# Connect: Alt+P profile picker, arrow to the profile, Enter
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+p"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"
# Row 28 shows "Environment: None" if no env came from the profile; then Alt+E
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+e"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# Phase B (mandatory when src/PPDS.Cli/Tui/ changes): drive the screen
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+t"    # Tools menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"    # SQL Query (first item)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2         # [ 1: SQL Query - <env name> ] [+]
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "ctrl+a"   # select the pre-filled query
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT TOP 10 name FROM account"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "F5"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "rows" 30000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 24        # "Returned 10 rows in 298ms via Dataverse"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs render "$TEMP/tui-sql.png"

# Phase C: cleanup
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

The SQL editor opens pre-filled with
`SELECT TOP 100 accountid, name, createdon FROM account`, and `type` inserts
at the cursor; read rows 4-9 to confirm the editor holds what you expect
before pressing F5.

### Commands

| Command | Purpose |
|---------|---------|
| `launch [--build]` | Start the TUI in a PTY; `--build` compiles first |
| `close` | Kill the PTY and daemon (safe when nothing is running) |
| `text <row>` | Print one row, 0-29 |
| `key "<combo>"` | Send a keystroke (100ms settle) |
| `type "<text>"` | Type text, 30ms per character |
| `wait "<text>" [ms]` | Poll all rows until the text appears (default 10000) |
| `screenshot <file.json>` | Write `serialize()` output (text + colour shifts) as JSON |
| `render <file.png>` | Render the current screen to a PNG (headless Chromium + xterm.js) and print JSON with the path and the serialize payload |
| `rows` | Print the dimensions (`120x30`) |

Write `screenshot` / `render` output under `$TEMP` (set in Git Bash on
Windows; use `${TMPDIR:-/tmp}` on macOS/Linux), never into the repo.

### Keys

The tool accepts modifiers `ctrl`, `alt`, `shift` and the named keys
`enter`, `tab`, `escape`, `up`, `down`, `left`, `right`, `backspace`,
`delete`, `space`, `F1`-`F12`, or a single character.

- Modifiers are dropped on named keys, so `shift+tab` sends a plain Tab.
- `ctrl+<letter>` sends the control character; `shift` is ignored with it,
  so Ctrl+Shift shortcuts cannot be sent. Use the menu or the F-key
  alternative.

| Combo | PPDS TUI action |
|-------|-----------------|
| `tab` | Next widget |
| `enter` / `escape` | Activate / close dialog or cancel a running query |
| `alt+f`, `alt+t`, `alt+h` | File, Tools, Help menus |
| `alt+q` | Query menu (SQL Query screen) |
| `alt+p` / `alt+e` | Profile / environment selector |
| `up` / `down` | Lists, menus, tree views |
| `ctrl+t` / `ctrl+w` | New SQL Query tab / close tab |
| `ctrl+q` | Quit immediately, no confirmation |
| `F1` / `F12` | Keyboard shortcuts / error log |
| `F5` | Execute query |
| `F6` | Toggle focus between editor and results |
| `F7` / `F8` / `F9` | Execution plan / history / FetchXML |
| `F10` / `F11` | Toggle TDS Read Replica / FetchXML input |

Query menu, top to bottom: Execute, Show FetchXML, Show Execution Plan,
History, (separator), Filter Results, (separator), FetchXML Input,
TDS Read Replica.

### Screen layout (120x30, SQL Query screen)

| Rows | Content |
|------|---------|
| 0 | Title bar: "PPDS - Power Platform Developer Suite" |
| 1 | Menu bar: File, Query (SQL Query screen only), Tools, Help |
| 2 | Tab bar |
| 3 | Screen title (e.g. "SQL Query - Env Name") |
| 4-9 | Query editor |
| 10 | Splitter |
| 11-23 | Results table |
| 24 | Query status label ("Ready", "Mode: TDS Read Replica...", errors) |
| 25 | Inner frame border |
| 26 | (empty) |
| 27 | Error/notification bar |
| 28 | "Profile: ... Environment: ..." |
| 29 | Bottom border |

To dump every row:

```bash
for i in $(seq 0 29); do echo "Row $i: $(node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text $i)"; done
```

### When interactive verification is required

Required for anything that changes what is rendered or how keys behave:
layout, widgets, borders, keyboard handlers, focus order, status bar, menu
items, dialogs, error messages. Compile and unit tests suffice for
Application Service logic, Dataverse queries and data transformation.

### Error recovery (TUI)

- **`launch` fails or times out:** read
  `tests/PPDS.Tui.E2eTests/tools/.tui-verify-daemon.log` first.
  "Binary not found" means build (`--build`); "PTY exited: code=1" means a
  wrong subcommand or missing dependency; no log file at all points at a
  `node-pty` install problem. Then `close` and `launch --build`.
- **`wait` times out:** `text 0` (an error means the TUI died: `close`, then
  `launch --build`); read several rows to see which screen you are on; read
  the daemon log; retry with a longer timeout (`wait "text" 30000`).
- **TUI crashed mid-session:** `close`, then `launch --build`.

---

## §ext — Extension Mode

Tool: `src/PPDS.Extension/tools/webview-cdp.mjs` (`@playwright/test` +
`@vscode/test-electron`). It drives a VS Code window through a background
daemon.

- The first run downloads VS Code (30-60s, cached). The extension loads
  from source (`--extensionDevelopmentPath`).
- `launch --build` runs `npm run compile` in `src/PPDS.Extension`, then
  `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -c Debug` for the daemon.
  CSS changes need `--build` too: esbuild bundles the CSS, and webviews do
  not hot-reload it.
- `launch` with a live session prints "already running" and does not
  relaunch, so `close` before a rebuild.
- `launch --vsix <file>` installs a packaged VSIX into a clean extensions
  directory instead (no build).
- `--session <name>` on any command runs separate, concurrent instances;
  `close --all` closes every session.

**Always pass `--ext "power-platform-developer-suite"`** to `eval`, `click`,
`type`, `select`, `text` and `wait`. VS Code may have other webviews open
(walkthrough, settings); without `--ext` you can act on the wrong one.

### Worked example

```bash
# Phase A: launch, open a panel, look at it
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Open Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot "$TEMP/data-explorer.png"
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"

# Phase B (mandatory when query/data/panel code changes): run a query
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 5 name FROM account")' --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot "$TEMP/after-query.png"
node src/PPDS.Extension/tools/webview-cdp.mjs logs

# Phase C: cleanup
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

Look at every screenshot (Read the PNG); taking it is not verifying it.

Phase B trigger files: `SqlQueryService`, `RpcMethodHandler`, `QueryPanel`,
`query-panel.ts`, and anything under data rendering or panel interactions.

### Commands

| Command | Purpose |
|---------|---------|
| `launch [workspace] [--build] [--vsix file]` | Start VS Code with the extension (90s timeout) |
| `close [--all]` | Shut down VS Code and the daemon |
| `connect` | List webview frames: index, extension id, title, `*` if visible |
| `command "<title>"` | Open the command palette, type the title, press Enter on the first match |
| `wait [ms] [--ext id]` | Wait for a webview to appear (default 30000) |
| `screenshot <file.png>` | Capture the whole VS Code window (`--page`/`--ext`/`--target` are ignored) |
| `eval "<js>"` | Evaluate JS in the webview (or the workbench with `--page`); prints the JSON result |
| `text "<selector>"` | Print an element's `textContent` |
| `click "<selector>" [--right]` | Click (right-click with `--right`) |
| `type "<selector>" "<text>"` | Fill an input; falls back to click + Ctrl+A + typing |
| `select "<selector>" "<value>"` | Choose a `<select>` option |
| `key "<combo>"` | Press keys on whatever has focus in the window (`--page`/`--ext` do not apply) |
| `mouse <mousedown\|mousemove\|mouseup> <x> <y>` | Raw mouse event at window coordinates |
| `logs [--channel name]` | Console log of the window, or an output channel's log file |
| `notebook run` / `notebook run-all` | Run the focused cell / all cells |
| `download <publisher.name> <version>` | Fetch a Marketplace VSIX into the current directory, for `launch --vsix` |

Targeting flags for `eval`, `text`, `click`, `type`, `select`:
`--ext <id>` (by extension id; prefer this), `--target <N|active>` (by index
from `connect`, or the single visible webview), `--page` (VS Code's own UI:
sidebar, tabs, menus, palette, instead of webview content).

Key names: `Enter`, `Escape`, `Tab`, `Backspace`, `Delete`, `ArrowUp`,
`ArrowDown`, `ArrowLeft`, `ArrowRight`, `space` (case-insensitive) or any
Playwright key name; modifiers `ctrl`, `shift`, `alt`, `meta`. `up`/`down`
are not mapped; use `arrowup`/`arrowdown`.

### Command titles

The palette shows each command as `PPDS: <title>` (category `PPDS` in
`src/PPDS.Extension/package.json`). Panels: `PPDS: Open Data Explorer`,
`PPDS: Open Notebooks`, `PPDS: New Notebook`, `PPDS: Open Solutions`,
`PPDS: Open Import Jobs`, `PPDS: Open Plugin Traces`,
`PPDS: Open Plugin Registration`, `PPDS: Open Metadata Browser`,
`PPDS: Open Connection References`, `PPDS: Open Environment Variables`,
`PPDS: Open Web Resources`. Other useful ones: `PPDS: Show Logs`,
`PPDS: Restart Daemon`, `PPDS: Select Profile`, and the
`PPDS Debug: Daemon Status` / `Extension State` / `Tree View State` /
`Panel State` diagnostics. Check package.json for anything else; a wrong
title fails with "Command not found" only if nothing matches, otherwise the
first fuzzy match runs. Each panel title is shared with a tree-context
`...ForEnv` command, and the palette shows both, so confirm from the
screenshot which one opened.

### Patterns

```bash
# Monaco: `type` does NOT work on the Monaco editor. Use eval.
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 10 * FROM account")' --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].getValue()' --ext "power-platform-developer-suite"

# Context menu on a result cell, then pick Copy
node src/PPDS.Extension/tools/webview-cdp.mjs click "td[data-row='1']" --right --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot "$TEMP/context-menu.png"
node src/PPDS.Extension/tools/webview-cdp.mjs click ".context-menu [data-action='copy']" --ext "power-platform-developer-suite"

# Keyboard shortcut / command palette
node src/PPDS.Extension/tools/webview-cdp.mjs key "ctrl+enter"
node src/PPDS.Extension/tools/webview-cdp.mjs key "ctrl+shift+p"
node src/PPDS.Extension/tools/webview-cdp.mjs key "Escape"

# Notebook cells
node src/PPDS.Extension/tools/webview-cdp.mjs notebook run
node src/PPDS.Extension/tools/webview-cdp.mjs notebook run-all
```

**Notebooks:** use `notebook run`, which clicks the cell's run button.
Running a cell through `command "Notebook: ..."` opens the palette and
takes focus from the cell, and `key "ctrl+enter"` may trigger
`executeAndInsertBelow` depending on keybinding context. (`notebook run`
falls back to the palette only when it cannot find the button.)

### Logs

- `logs` prints the window's console messages captured since launch
  (`.webview-cdp-console-<session>.log` next to the tool; deleted on
  `close`, so read it first).
- `logs --channel "PPDS"` prints every `PPDS.log` under the session profile
  (`src/PPDS.Extension/tools/.webview-cdp-profile-<session>/logs/`). The
  profile persists across launches, so output can include earlier runs.
- The `PPDS` LogOutputChannel writes to
  `<user-data-dir>/logs/<window>/exthost/JoshSmithXRM.power-platform-developer-suite/PPDS.log`.
  Numbered files such as `1-CodeLens.log` belong to core VS Code services.

### When a screenshot is required

Required for changes to rendered UI: CSS, layout, theme variables, message
wiring (postMessage/onMessage), HTML templates, DOM structure. Compile and
unit tests suffice for string constants, config flags, package.json
metadata, command registration, URL handlers, enablement conditions, and
refactors that do not change output.

### Error recovery (Extension)

- **`launch` fails or hangs:** run `logs` (while the session still exists),
  then `close` and relaunch. Common causes: a session from an earlier run is
  still alive (`close`, or `close --all`); a build failure under `--build`
  (run `npm run ext:compile` on its own to see it); the daemon did not start
  (check `logs --channel "PPDS"`).

---

## Gap protocol (TUI and extension harnesses)

If an interaction cannot be done with `tui-verify` or `webview-cdp`: stop
rather than work around it silently, describe the interaction you need and
why the commands do not cover it, propose a new command or flag, and ask the
user whether to implement it now or defer it.

---

## §mcp — MCP Mode

### Build

```bash
dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -f net10.0 -v q
src/PPDS.Mcp/bin/Debug/net10.0/ppds-mcp-server --version --read-only
```

`--version` prints the package version and exits before hosting
(`src/PPDS.Mcp/Program.cs`). `--read-only` is there because the safety hook
blocks any `ppds-mcp-server` command without it during a shakedown.

### How the server talks

Stdio JSON-RPC: stdout carries only protocol messages and all logging goes
to stderr (`--log-level <Trace|Debug|Information|Warning|Error>` or
`PPDS_MCP_LOG_LEVEL`; default Warning). An MCP client must complete the
`initialize` handshake before `tools/list` or `tools/call`, so piping a
single JSON-RPC line into the server is not a working test. The repo has no
scripted command-line client, and MCP Inspector is not used anywhere in it.

### How to exercise tools

1. **Protocol level, no Dataverse:** `tests/PPDS.Mcp.Tests`.
   `tests/PPDS.Mcp.Tests/Tools/FormsProtocolTests.cs` is the pattern: an
   in-process server and an `McpClientFactory` client over
   `StreamClientTransport`, then `ListToolsAsync` / `CallToolAsync` and
   assertions on `IsError` and the serialized content. Add a test in that
   shape for a new or changed tool and run
   `dotnet test tests/PPDS.Mcp.Tests/PPDS.Mcp.Tests.csproj -v q`.
2. **Live:** register the built binary as an MCP server in an MCP client
   (the `mcpServers` shape in `src/PPDS.Mcp/README.md`, "Quick Start -
   Claude Code"), with `"args": ["--read-only", ...]` unless you are
   deliberately testing writes, start a new client session, and call the
   tools. This needs the user to add the server and restart the client;
   ask rather than editing their MCP configuration yourself.

### Session options

Parsed by `src/PPDS.Mcp/Infrastructure/McpSessionOptions.cs`:

| Flag | Purpose | Test |
|------|---------|------|
| `--profile <name>` | Lock to a profile (default: the active profile at startup) | Tools report that profile |
| `--environment <url>` | Lock to an environment (default: the profile's) | Tools use the locked environment |
| `--read-only` | Reject DML and guarded mutations | INSERT/UPDATE/DELETE via `ppds_query_sql` is rejected |
| `--allowed-env <url>` | Allow `ppds_env_select` to switch to this URL; repeat the flag per URL | Switching to an unlisted URL is rejected; with no flag, switching is disabled |

The profile and environment are captured at startup; a later
`ppds auth select` or `ppds env select` does not redirect a running server.
Restart it.

### Response validation

For each call: the response is valid JSON; the `content` array holds the
expected data; `isError` is false on success and true on error; error text
is descriptive, not a raw exception.

### Common failure messages

| Message | Cause |
|---------|-------|
| "No active profile configured at session startup. Run 'ppds auth create', then restart this MCP server." | No profile when the server started |
| "DML operations (INSERT, UPDATE, DELETE) are disabled. This MCP session was started with --read-only." | `--read-only` blocking DML in `ppds_query_sql` |
| "Cannot modify metadata: this MCP session is read-only." | `--read-only` blocking a metadata tool |
| Timeout | Dataverse connection; check the environment URL |
| Empty results | Valid query, no matching data |

---

## §workflow — Workflow Infrastructure

How to test changes to `.claude/` infrastructure: hooks, skills, settings.
The hooks are `.claude/hooks/snk-protect.py` (Edit|Write) and
`.claude/hooks/shakedown-safety.py` (Bash and PowerShell); `_pathfix.py`
and `_shellparse.py` are shared helpers, not hooks.

### Hook testing

Pipe the tool envelope (`{"tool_name": ..., "tool_input": {...}}`) to the
script and check the exit code (0 allow, 2 block; 1 means the hook crashed)
and stderr (the message shown to the agent).

```bash
# snk-protect should block (exit=2)
python -c "
import subprocess, json
input_data = json.dumps({'tool_name': 'Write', 'tool_input': {'file_path': 'src/PPDS.Plugins/PPDS.Plugins.snk'}})
result = subprocess.run(
    ['python', '.claude/hooks/snk-protect.py'],
    input=input_data, capture_output=True, text=True
)
print(f'exit={result.returncode} stderr={result.stderr[:100]}')
"
```

Behavioural tests: `tests/test_snk_protect.py`,
`tests/hooks/test_shakedown_safety.py`.

### Settings validation

```bash
python -c "import json; json.load(open('.claude/settings.json')); print('valid JSON')"
```

Matchers are regexes tested against tool names (`Edit|Write`, `Bash`).

### Pitfalls

- **Paths in JSON:** use forward slashes or doubled backslashes; hook
  commands use `$CLAUDE_PROJECT_DIR`.
- **Worktrees:** `$CLAUDE_PROJECT_DIR` is the worktree root, so hooks read
  that worktree's `.claude/settings.json` and `.claude/state/`.
- **Exit codes in pipes:** in `echo ... | python script.py` the status is
  Python's; use `$?` right after, or `subprocess.run` as above.
- **Timeouts:** set per hook in `.claude/settings.json` (currently 5s for
  `snk-protect`, 10s for `shakedown-safety`); keep new hooks as short.
