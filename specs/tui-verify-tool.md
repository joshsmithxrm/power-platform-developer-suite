# TUI Verify Tool

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-16
**Code:** [tests/PPDS.Tui.E2eTests/tools/](../tests/PPDS.Tui.E2eTests/tools/) | None

---

## Overview

A CLI tool that lets AI agents launch the PPDS TUI in a PTY, send keystrokes, read terminal text, and wait for content — closing the feedback loop between TUI implementation and verification. Mirrors the `webview-cdp.mjs` pattern but targets Terminal.Gui applications instead of VS Code webviews.

The primary verification workflow is text-based. The AI reads terminal rows directly and searches for expected strings. PNG rendering is deferred — terminal text is more useful than pixels for AI verification of a TUI.

### Goals

- **Interactive TUI verification**: AI agents can launch, navigate, and inspect the TUI without manual intervention
- **Text-based feedback**: Read specific terminal rows, wait for text to appear, get terminal dimensions
- **Keystroke injection**: Send individual keys, key combos (ctrl+shift+t), and typed text
- **Same lifecycle model as webview-cdp**: Launch → interact → close, with a persistent daemon between commands

### Non-Goals

- PNG pixel rendering of terminal state (deferred — `serialize()` dump available for debugging)
- Automated visual regression testing in CI (use `@microsoft/tui-test` snapshot tests directly)
- Mouse interaction (Terminal.Gui keyboard-first; mouse support deferred)
- Attaching to an already-running TUI process

---

## Architecture

```
Claude Code
    |
    |  Bash: node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs <command> [args]
    v
+----------------------------+
|   tui-verify CLI            |  Short-lived process per command
|   (caller)                  |  Reads .tui-verify-session.json
+--------+-------------------+
         |
         +-- launch:  forks daemon -> daemon spawns PTY + HTTP server
         |            daemon writes session file with port/pid
         |
         +-- other commands:  POST http://localhost:<port>/execute
         |   daemon executes against Terminal instance
         |   -> uses tui-test Terminal API (getBuffer, write, etc.)
         |   -> returns result as JSON
         |
         +-- close:  POST /shutdown to daemon
                     daemon kills PTY, cleans up, exits

+----------------------------+     +----------------------------+
|  tui-verify daemon          |---->|  ppds tui (in PTY)          |
|  (long-lived background)    |     |                             |
|                             |     |  Terminal.Gui application    |
|  HTTP server on random port |     |  120 cols x 30 rows         |
|  Holds tui-test Terminal    |     |  Renders to virtual terminal |
|  Executes commands via API  |     |                             |
+----------------------------+     +----------------------------+
```

### Why a daemon?

Same reason as webview-cdp: the `@microsoft/tui-test` `Terminal` object owns the PTY process. When the Node.js process exits, the PTY is killed. A long-lived daemon keeps the PTY alive across multiple CLI invocations.

### Components

| Component | Responsibility |
|-----------|----------------|
| `tui-verify.mjs` | CLI entry point (caller mode) + daemon mode (`--daemon` flag). Single file. |
| Daemon process | Long-lived process holding the tui-test `Terminal` instance and HTTP server |
| Session file | `.tui-verify-session.json` — persists `daemonPort`, `daemonPid` |
| Key mapper | Translates human-readable key combos ("ctrl+shift+t") to terminal escape sequences |

### Dependencies

- **Runtime:** Node.js (already required by the extension build toolchain)
- **npm:** `@microsoft/tui-test` (already a dev dependency in `tests/PPDS.Tui.E2eTests/`)
- **Transitive:** `@homebridge/node-pty-prebuilt-multiarch` (PTY management, pulled in by tui-test), `@xterm/headless` (terminal emulation, pulled in by tui-test)
- Depends on: [tui-foundation spec](./tui.md) (TUI architecture)
- Mirrors: [webview-cdp spec](./vscode-webview-cdp-tool.md) (architecture pattern)

---

## Specification

### Core Requirements

1. The tool MUST launch the PPDS TUI in a PTY via `@microsoft/tui-test`'s `spawn()` API
2. The tool MUST persist the PTY across CLI invocations using a daemon process
3. The tool MUST read terminal text content from specific rows via `getBuffer()`
4. The tool MUST inject keystrokes via `write()` and named key methods
5. The tool MUST poll terminal content for expected text with configurable timeout
6. The tool MUST NOT use `shell: true` in any process spawn (CONSTITUTION S2)
7. Terminal size MUST be 120 columns x 30 rows (matches existing tui-test config)
8. All status messages MUST go to stderr; only command results go to stdout

### Command Interface

| Command | Signature | Purpose |
|---------|-----------|---------|
| `launch` | `launch [--build]` | Start ppds tui in a PTY. `--build` runs `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0` first |
| `close` | `close` | Kill PTY process, shut down daemon, clean up session file |
| `screenshot` | `screenshot <file>` | Dump `terminal.serialize()` to file as JSON (text + color metadata for debugging) |
| `key` | `key "<combo>"` | Send keystroke. Supports: named keys (enter, tab, escape, F1-F12), modifiers (ctrl+c, alt+t), raw characters |
| `type` | `type "<text>"` | Write text string into focused input, character by character |
| `text` | `text <row>` | Read and return text content at terminal row (0-based) |
| `wait` | `wait "<text>" [timeout]` | Poll terminal content until text substring appears. Default timeout 10000ms |
| `rows` | `rows` | Return terminal dimensions as `120x30` (cols x rows, standard WxH convention) |

### Primary Flows

**Launch flow (caller process):**

1. **Check for stale session**: If session file exists, ping daemon HTTP server. If ping succeeds, warn "TUI already running" and exit. If fails, kill stale PID, delete session file.
2. **Optional build**: If `--build` flag, run `execFileSync('dotnet', ['build', 'src/PPDS.Cli/PPDS.Cli.csproj', '-f', 'net10.0'])` — uses `execFileSync` (not `execSync`) to avoid shell invocation per CONSTITUTION S2.
3. **Fork daemon**: Spawn `node tui-verify.mjs --daemon` as a detached background process. Redirect daemon stderr to `.tui-verify-daemon.log` via `openSync` file descriptor — provides diagnostics if the daemon fails to start.
4. **Wait for session**: Poll for session file (daemon writes it when PTY is ready), up to 30 seconds. On timeout, read the last 20 lines of `.tui-verify-daemon.log` and include them in the error message.
5. **Print confirmation**: Output "TUI launched (daemon PID X, port Y)" to stderr.

**Launch flow (daemon process — `--daemon` flag):**

1. **Resolve program path**: If `--build` was used, run the built binary directly (`src/PPDS.Cli/bin/Debug/net10.0/ppds.exe tui`). Otherwise, use the built binary path as well (caller is responsible for having built first). The daemon never invokes `dotnet run` — it always launches the compiled executable via tui-test's `spawn({ program: { file, args } })`.
2. **Spawn PTY**: Call tui-test `spawn({ program: { file, args }, rows: 30, columns: 120 })`.
3. **Wait for ready**: Poll `getBuffer()` until non-empty content appears (Terminal.Gui has rendered), up to 30 seconds.
4. **Start HTTP server**: Listen on random available port. Expose `/execute`, `/shutdown`, `/health`.
5. **Write session file**: `{ daemonPort, daemonPid: process.pid }`.
6. **Wait for shutdown**: Listen for `/shutdown` request or `SIGTERM`/`SIGINT`. On any, call `terminal.kill()`, delete session file, exit.

**Command execution flow (all commands except launch/close):**

1. **Read session**: Load `daemonPort` from `.tui-verify-session.json`.
2. **Send request**: POST to `http://localhost:<port>/execute` with action and params as JSON.
3. **Daemon executes**: Runs the action against the Terminal instance.
4. **Output result**: Print result to stdout.

**Text read flow (`text <row>`):**

1. **Get buffer**: `terminal.getBuffer()` returns `string[][]` (2D array of cells).
2. **Extract row**: `buffer[row]` gives array of single-character strings.
3. **Join and trim**: `buffer[row].join('').trimEnd()`.
4. **Return**: Print row content to stdout.

**Wait flow (`wait "<text>" [timeout]`):**

1. **Start timer**: Record `Date.now()`.
2. **Poll loop**: Every 250ms, scan all rows of `getBuffer()` for the search text as a substring.
3. **Found**: Print "Found: <text>" to stdout, exit 0.
4. **Timeout**: Print "Timeout: '<text>' not found within Xms" to stderr, exit 1.

**Key flow (`key "<combo>"`):**

1. **Parse combo**: Split on `+`, identify modifiers (ctrl, alt, shift) and the base key.
2. **Map to terminal input**: Use tui-test's named methods where available (`keyUp`, `keyDown`, `keyEscape`, `keyCtrlC`, `keyCtrlD`). For other combos, map to raw ANSI escape sequences via `terminal.write()`. For `enter`, use `terminal.submit()`.
3. **Execute**: Send the input to the PTY.
4. **Brief settle**: Wait 100ms for Terminal.Gui to process the input.

**Close flow:**

1. **Read session**: Load `daemonPort` and `daemonPid`.
2. **Request shutdown**: POST to `/shutdown`. Daemon calls `terminal.kill()`, deletes session file, exits.
3. **Wait for cleanup**: Poll until session file disappears (up to 10 seconds).
4. **Force kill if needed**: If session file persists, force-kill daemon PID, delete manually.
5. **Print confirmation**: "TUI closed" to stderr.

### Keystroke Mapping

Terminal.Gui uses ANSI/VT escape sequences. The key mapper must handle:

| Input | Method | Terminal Sequence |
|-------|--------|-------------------|
| `enter` | `terminal.submit()` | `\r` |
| `tab` | `terminal.write('\t')` | `\t` |
| `escape` | `terminal.keyEscape()` | `\x1b` |
| `up` / `down` / `left` / `right` | `terminal.keyUp()` etc. | Arrow escape sequences |
| `backspace` | `terminal.keyBackspace()` | `\x7f` |
| `delete` | `terminal.keyDelete()` | `\x1b[3~` |
| `ctrl+c` | `terminal.keyCtrlC()` | `\x03` |
| `ctrl+d` | `terminal.keyCtrlD()` | `\x04` |
| `ctrl+<letter>` | `terminal.write(String.fromCharCode(code - 64))` | Control character |
| `alt+<letter>` | `terminal.write('\x1b' + letter)` | ESC + letter |
| `F1`-`F12` | `terminal.write(fnSequence)` | Standard VT function key sequences |
| `ctrl+shift+<letter>` | Compose modifier escape | Platform-dependent |

### Constraints

- Single file implementation (`tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`)
- Uses `@microsoft/tui-test` (already a dev dependency in parent package.json)
- Session file: `tests/PPDS.Tui.E2eTests/tools/.tui-verify-session.json` (gitignored)
- Daemon log file: `tests/PPDS.Tui.E2eTests/tools/.tui-verify-daemon.log` (gitignored) — daemon stderr redirected here for diagnostics. On launch timeout, the caller prints the last 20 lines of this log before reporting failure. Deleted by `close`.
- All errors to stderr, all results to stdout
- Exit code 0 on success, 1 on error
- Terminal dimensions: 120 columns x 30 rows (fixed, matching tui-test config)
- Windows primary platform — PTY handling via `node-pty` (cross-platform, bundled with tui-test)
- No `shell: true` anywhere (CONSTITUTION S2)
- Daemon process runs detached — survives if calling terminal is closed

### Security Considerations

- **Local development only**: Spawns a local TUI process in a PTY.
- **Process spawn (CONSTITUTION S2)**: `launch` forks a daemon via `child_process.spawn()` with `shell: false`. The daemon delegates to tui-test's `spawn()` which uses `node-pty` (no shell).
- **No secrets in output (CONSTITUTION S3)**: Terminal buffer content may include connection strings if the TUI displays them. The tool passes through whatever the TUI renders — agents should avoid dumping full terminal state in logs.
- **No innerHTML (CONSTITUTION S1)**: Not applicable — tool outputs plain text only.

### Validation Rules

| Input | Rule | Error |
|-------|------|-------|
| `screenshot <file>` | File path must be provided | "Usage: screenshot \<file\>" |
| `text <row>` | Row must be integer 0-29 | "Row must be 0-29 (terminal has 30 rows)" |
| `key "<combo>"` | Combo must be non-empty, modifiers must be ctrl/alt/shift | "Invalid key combo" |
| `type "<text>"` | Text must be non-empty | "Usage: type \<text\>" |
| `wait "<text>"` | Search text must be non-empty | "Usage: wait \<text\> [timeout]" |
| `wait [timeout]` | Timeout must be positive integer, default 10000 | "Invalid timeout" |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `launch --build` compiles `src/PPDS.Cli/PPDS.Cli.csproj -f net10.0` and starts the TUI in a PTY | Manual: run `launch --build`, verify TUI process starts and session file written | 🔲 |
| AC-02 | `launch` without `--build` starts TUI using existing build | Manual: run `launch` after prior build, verify TUI starts | 🔲 |
| AC-03 | `close` kills the PTY process and deletes the session file | Manual: run `close`, verify no orphaned processes and session file removed | 🔲 |
| AC-04 | `text <row>` returns the text content of the specified terminal row | Manual: run `text 0`, verify output matches the TUI's top row (title bar) | 🔲 |
| AC-05 | `text <row>` with out-of-range row returns an error | Manual: run `text 50`, verify error "Row must be 0-29" | 🔲 |
| AC-06 | `key "tab"` sends a Tab keystroke to the TUI and moves focus | Manual: run `key "tab"`, then `text` on the focused row, verify focus moved | 🔲 |
| AC-07 | `key "enter"` sends Enter to the TUI | Manual: navigate to a menu item, run `key "enter"`, verify action triggered | 🔲 |
| AC-08 | `key "ctrl+q"` sends Ctrl+Q to the TUI | Manual: run `key "ctrl+q"`, verify TUI responds (quit dialog or exit) | 🔲 |
| AC-09 | `key "escape"` sends Escape to the TUI | Manual: open a dialog, run `key "escape"`, verify dialog closed | 🔲 |
| AC-10 | `wait "<text>"` finds text that is already on screen and returns immediately | Manual: run `wait "PPDS"` after launch, verify instant return | 🔲 |
| AC-11 | `wait "<text>" [timeout]` times out when text never appears | Manual: run `wait "NONEXISTENT" 2000`, verify timeout error after ~2 seconds | 🔲 |
| AC-12 | `type "<text>"` writes characters into the focused input field | Manual: focus a text field, run `type "SELECT"`, verify text appears | 🔲 |
| AC-13 | `screenshot <file>` writes a JSON file containing `serialize()` output | Manual: run `screenshot $TEMP/debug.json`, verify file contains `view` and `shifts` keys | 🔲 |
| AC-14 | `rows` returns `120x30` | Manual: run `rows`, verify output is `120x30` | 🔲 |
| AC-15 | Stale session detection: `launch` when a prior daemon died cleans up and starts fresh | Manual: kill daemon PID manually, run `launch`, verify clean restart | 🔲 |
| AC-16 | No orphaned processes after `close` | Manual: run `close`, check task manager for stale `dotnet` or `ppds` processes | 🔲 |
| AC-17 | Tool works on Windows (primary dev platform) | Manual: all above criteria pass on Windows 11 | 🔲 |
| AC-18 | `/verify tui` command updated to use tui-verify.mjs for interactive verification | Manual: run `/verify tui`, verify it uses tui-verify commands | 🔲 |
| AC-19 | `@tui-verify` skill created with command reference and common patterns | Manual: invoke skill, verify content covers all commands and Terminal.Gui patterns | 🔲 |
| AC-20 | `/qa` command updated with TUI Mode blind verifier section | Manual: run `/qa tui`, verify blind verifier dispatched with TUI-specific protocol | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No session | Any command without prior `launch` | stderr: "No session found. Run `tui-verify launch` first.", exit 1 |
| TUI crashed | Command after TUI process died | stderr: "TUI process exited. Run `launch` again.", exit 1 |
| Row out of range | `text 35` | stderr: "Row must be 0-29 (terminal has 30 rows)", exit 1 |
| Negative row | `text -1` | stderr: "Row must be 0-29 (terminal has 30 rows)", exit 1 |
| Empty key combo | `key ""` | stderr: "Invalid key combo", exit 1 |
| Unknown modifier | `key "super+t"` | stderr: "Invalid key combo: unknown modifier 'super'", exit 1 |
| Wait with zero timeout | `wait "text" 0` | stderr: "Invalid timeout", exit 1 |
| Double launch | `launch` when already running | stderr: "TUI already running. Run `close` first.", exit 0 |
| Close without launch | `close` with no session | stderr: "No session found. Nothing to close.", exit 0 (idempotent) |
| TUI slow to render | `launch` when Terminal.Gui takes >5s to draw | Daemon polls getBuffer() up to 30s before timing out |

---

## Core Types

### Session File

```json
{
  "daemonPort": 52345,
  "daemonPid": 12345
}
```

Written by daemon during launch, read by all commands, deleted during close.

### Serialize Output (screenshot)

The tui-test `terminal.serialize()` returns `{ view: string, shifts: Map<string, CellShift> }` where `CellShift` has properties: `bgColorMode`, `bgColor`, `fgColorMode`, `fgColor`, `blink`, `bold`, `dim`, `inverse`, `invisible`, `italic`, `overline`, `strike`, `underline`.

Since `Map` doesn't JSON.stringify natively, the `screenshot` command converts `shifts` to a plain object before writing:

```json
{
  "view": "┌─ PPDS ─────────────────────────────┐\n│ SQL Query    Solutions    Plugins   │\n...",
  "shifts": {
    "0,5": { "bold": 1, "fgColor": 4, "fgColorMode": 1 },
    "1,2": { "fgColor": 12, "fgColorMode": 1 }
  }
}
```

The `view` field contains the rendered terminal text with line breaks. The `shifts` map keys are cell coordinates, values are the styling attributes for those cells. Useful for debugging rendering issues.

> **Source:** Verified from `@microsoft/tui-test@0.0.1-rc.5` type definitions at `lib/terminal/term.d.ts:184-187`.

### Usage Pattern

```bash
# Build and launch
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# Read the title bar
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0

# Navigate with Tab, verify focus moved
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 1

# Wait for a specific screen
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000

# Type into a focused input
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT TOP 10 * FROM account"

# Execute with Enter
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# Dump terminal state for debugging
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-debug.json

# Check dimensions
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs rows

# Shut down
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

---

## API/Contracts

The daemon exposes three HTTP endpoints on `127.0.0.1:<randomPort>`.

### `GET /health`

Returns `200 OK` with body `ok` if the daemon and PTY are alive.

### `POST /shutdown`

Returns `200 OK` with body `ok`, then asynchronously kills the PTY, deletes the session file, and exits the daemon process.

### `POST /execute`

Executes an action against the terminal.

**Request:**

```json
{
  "action": "text",
  "row": 0
}
```

| Action | Params | Response |
|--------|--------|----------|
| `text` | `{ "row": number }` | `{ "success": true, "text": "row content" }` |
| `key` | `{ "combo": "ctrl+t" }` | `{ "success": true }` |
| `type` | `{ "text": "SELECT" }` | `{ "success": true }` |
| `wait` | `{ "text": "SQL Query", "timeout": 10000 }` | `{ "success": true }` or `{ "success": false, "error": "Timeout..." }` |
| `screenshot` | `{ "file": "/tmp/debug.json" }` | `{ "success": true, "path": "/tmp/debug.json" }` |
| `rows` | `{}` | `{ "success": true, "dimensions": "120x30" }` |

**Error response:**

```json
{
  "success": false,
  "error": "Row must be 0-29 (terminal has 30 rows)"
}
```

HTTP status is always 200. Errors are reported via the `success` field (same pattern as webview-cdp).

### Test Examples

```bash
# Verify launch and basic text read
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0
# Expected: top row contains "PPDS" title text

# Verify keyboard navigation
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2
# Expected: focus indicator moved to next widget

# Verify error handling
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 50
# Expected: stderr "Row must be 0-29 (terminal has 30 rows)", exit 1

# Verify dimensions
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs rows
# Expected: "120x30"

# Cleanup
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| No session | Session file missing | "Run `tui-verify launch` first" |
| TUI process exited | PTY process died | "Run `launch` again" |
| Row out of range | Row index >= 30 or < 0 | Report valid range |
| Invalid key combo | Unrecognized modifier or empty combo | Report valid modifiers |
| Wait timeout | Text not found within timeout | Report what was searched and timeout used |
| Build failed | `dotnet build` returned non-zero | Forward build error output |
| Daemon timeout | PTY didn't produce content within 30s | "TUI failed to start" |

### Recovery Strategies

- **Connection errors**: Run `close` then `launch` again.
- **Orphaned sessions**: `close` detects unreachable daemon, cleans up session file. Then `launch` fresh.
- **TUI crash**: Daemon detects PTY exit via `terminal.onExit`. Next command gets "TUI process exited" error. Run `close` to clean up, then `launch`.

---

## Design Decisions

### Why text-based verification instead of PNG screenshots?

**Context:** The issue originally called for PNG screenshots mirroring webview-cdp's pixel captures.

**Decision:** Use text-based verification as the primary workflow. The `screenshot` command dumps `serialize()` as JSON for debugging, not as a PNG image.

**Rationale:**
- AI agents read text better than they interpret terminal pixel renderings
- `getBuffer()` gives exact cell content — no OCR errors, no font rendering differences
- `wait "<text>"` is more reliable than pixel comparison for detecting TUI state
- Terminal.Gui rendering is deterministic (same input = same text buffer) unlike browser rendering
- PNG rendering would require an additional canvas library dependency for limited benefit

**Alternatives considered:**
- Full PNG rendering via `node-canvas`: Heavy dependency (~50MB), platform-specific native compilation, marginal benefit for AI verification
- xterm.js addon rendering: Requires browser context, overkill for text verification

**Consequences:**
- Positive: Simpler tool, fewer dependencies, more reliable verification
- Negative: Human reviewers can't "see" the TUI from the output. Mitigated by `serialize()` dump which preserves colors and structure.

### Why put the tool in tests/PPDS.Tui.E2eTests/tools/?

**Context:** Three possible locations: alongside webview-cdp in `src/PPDS.Extension/tools/`, at repo root `tools/`, or under the test project.

**Decision:** `tests/PPDS.Tui.E2eTests/tools/` — colocated with the `@microsoft/tui-test` dependency.

**Rationale:**
- `@microsoft/tui-test` is already installed here — no duplicate dependency
- The tool imports from tui-test directly — relative imports are clean
- webview-cdp lives next to its dependency (`@playwright/test` in `src/PPDS.Extension/`) — same pattern

**Alternatives considered:**
- `src/PPDS.Extension/tools/`: Would need tui-test added as a dependency there — duplicates the install
- `tools/` at repo root: Would need its own `package.json` or workspace config to resolve tui-test

### Why the daemon pattern (same as webview-cdp)?

**Context:** tui-test's `Terminal` object owns the PTY via `node-pty`. The PTY is killed when the owning process exits.

**Decision:** Fork a daemon process that holds the Terminal alive. Same architecture as webview-cdp.

**Alternatives considered:**
- Serialize/deserialize PTY state between invocations: Not supported by node-pty
- Single long-running command with stdin for commands: Breaks the "one command per CLI invocation" model that agents use

---

## Integration

### Skill: `@tui-verify`

Create `.claude/skills/tui-verify/SKILL.md` with:
- **Frontmatter** must include `allowed-tools: Bash(node *tui-verify*), Bash(cd * && node *tui-verify*)` — sandboxes the skill to tui-verify commands only (mirrors webview-cdp's `Bash(node *webview-cdp*)` pattern)
- When to use (after any TUI change that affects rendering or interaction)
- Command reference table (all 8 commands with examples)
- Common patterns (navigate screens, verify status bar, check menu items)
- Terminal.Gui keyboard navigation specifics (Tab cycles focus, Enter activates, Escape closes dialogs, Alt+letter for menu shortcuts)
- Debugging tips (use `screenshot` to dump serialize() output to `$TEMP`, read specific rows with `text`)
- Gap protocol (same as webview-cdp — stop and report if interaction can't be accomplished)

### Command: `/verify tui`

Replace Section 4 ("TUI Mode") in `.claude/commands/verify.md`. The current content (lines 60-68) says "TUI interactive verification via MCP is not yet available. Use snapshot tests." Replace with:

```markdown
### 4. TUI Mode

**Phase A: Build and Launch**

```bash
# Build and launch TUI in PTY
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# Wait for TUI to render
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# Read the title bar to confirm it loaded
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0
```

**Phase B: Interactive Verification (MANDATORY for TUI rendering/interaction changes)**

If changed files touch `src/PPDS.Cli/Tui/`, Phase B is NOT optional.

```bash
# Navigate to the relevant screen
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2

# Verify status bar content
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29

# Wait for expected screen title
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000

# Dump terminal state for debugging if needed
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-verify.json
```

**Phase C: Cleanup**

```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

See @tui-verify skill for full command reference and Terminal.Gui keyboard patterns.
```

### Command: `/qa tui`

Add a "TUI Mode — Verifier Prompt" section to `.claude/commands/qa.md`, parallel to Extension Mode. The blind verifier prompt:

```markdown
#### TUI Mode — Verifier Prompt

```
You are a QA tester. You have NEVER seen the source code for this TUI
application. You don't know how anything is implemented. You only know
what the product SHOULD do.

## Your Tools

You may ONLY use these tools:
- Bash: ONLY for running `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` commands
- Read: ONLY for viewing screenshot JSON files (in $TEMP)

You MUST NOT use Read/Grep/Glob on source code files. You cannot look
at .cs, .ts, .json, or any file under src/. You are blind to the
implementation.

## Verification Protocol

For EACH check below:
1. Perform the action using tui-verify commands
2. Read the relevant terminal row(s): `text <row>`
3. Optionally dump full state: `screenshot $TEMP/qa-tui-{check-number}.json`
4. Compare actual text to expected text
5. Report PASS or FAIL with the actual text content as evidence

If a check FAILS:
- Show exactly what text you SAW vs what was EXPECTED
- Include the row number and full row content
- Do NOT speculate about why it failed

## Setup

```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close  # clean up prior
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000
```

## Checks

{paste generated checklist here}

## Teardown

```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

## Report Format

| # | Check | Status | Evidence |
|---|-------|--------|----------|
| 1 | description | PASS/FAIL | row N: "actual text content" |

### Verdict: PASS (all green) / FAIL (N issues found)
```
```

Also update the mode detection in Step 2: change `src/PPDS.Cli/Tui/` from "TUI mode (snapshot tests — no blind verification yet)" to "TUI mode (tui-verify)".

---

## Related Specs

- [tui.md](./tui.md) — TUI foundation architecture
- [vscode-webview-cdp-tool.md](./vscode-webview-cdp-tool.md) — Reference implementation (same daemon pattern)

---

## Roadmap

- **PNG rendering**: Add optional pixel rendering via canvas library for human-readable screenshots
- **Mouse support**: Terminal.Gui supports mouse — add `click <row> <col>` command
- **Record/replay**: Capture keystroke sequences for repeatable verification scripts
- **CI integration**: Run TUI verification in CI with headless PTY
