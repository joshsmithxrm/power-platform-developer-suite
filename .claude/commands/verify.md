# Verify

AI self-verification of implemented work using MCP tools. Goes beyond unit tests to verify that code actually works in its runtime environment.

## Usage

`/verify` - Auto-detect component from recent changes
`/verify cli` - Verify CLI command behavior
`/verify tui` - Verify TUI rendering and interaction
`/verify extension` - Verify VS Code extension behavior
`/verify mcp` - Verify MCP server tools

## Prerequisites

Each mode requires specific MCP servers. If a prerequisite is missing, tell the user what to install and stop.

| Mode | Required MCPs / Tools |
|------|----------------------|
| cli | None (uses Bash tool directly) |
| tui | `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` (uses `@microsoft/tui-test` + `node-pty`, both dev deps) |
| extension | `src/PPDS.Extension/tools/webview-cdp.mjs` (uses @playwright/test + @vscode/test-electron, both dev deps) |
| mcp | MCP Inspector CLI (`npx @modelcontextprotocol/inspector`) |

## Process

### 1. Detect Component

Based on $ARGUMENTS or recent changes:
- `src/PPDS.Cli/Commands/` → CLI mode
- `src/PPDS.Cli/Tui/` → TUI mode
- `src/PPDS.Extension/` → Extension mode
- `src/PPDS.Mcp/` → MCP mode
- No clear match → Ask user

### 2. Run Unit Tests First

Always run the relevant unit tests before interactive verification. If tests fail, fix them first — don't waste MCP verification cycles on broken code.

- CLI/TUI: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- Extension: `npm run test --prefix src/PPDS.Extension`
- MCP: `dotnet test --filter "FullyQualifiedName~Mcp" -v q`

### 3. CLI Mode

Run the CLI command and verify output:

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```

Verify:
- Command executes without error
- Output format is correct (JSON where expected, table where expected)
- Exit code is 0
- Edge cases: empty input, invalid args, missing auth

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
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28

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

### 5. Extension Mode

**Phase A: Functional Verification (ext-verify)**

Launch VS Code with the extension and verify panels load:

```bash
# Build and launch (compiles extension + daemon)
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build

# Open Data Explorer and wait for webview
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"

# Screenshot to verify panel rendered correctly
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/data-explorer.png
# LOOK at the screenshot — verify layout, no blank areas, controls visible

# Check for runtime errors
node src/PPDS.Extension/tools/webview-cdp.mjs logs
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"
```

**Phase B: Interaction Verification (MANDATORY for query/data-display/panel-interaction changes)**

If changed files touch query execution (`SqlQueryService`, `RpcMethodHandler`, `QueryPanel`, `query-panel.ts`), data rendering, or panel interactions, Phase B is NOT optional — you must execute at least one query and verify the results.

```bash
# Test query execution
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 5 name FROM account")'
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-query.png

# Test Solutions Panel
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Solutions"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/solutions.png
```

**Phase C: Cleanup**

```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

See @ext-verify skill for full command reference and common patterns.

### 6. MCP Mode

Use MCP Inspector CLI to test tools:

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```

For each tool:
1. Call with valid input -> verify success response shape
2. Call with edge case input -> verify error handling
3. Verify response matches expected schema

### 7. Report

Present structured results:

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

## Workflow State

After verification passes for a surface (verdict is PASS), run:

```bash
python scripts/workflow-state.py set verify.{surface} now
```

Surface key matches mode: `ext`, `tui`, `mcp`, `cli`. Example: `/verify extension` → `verify.ext`.

## Rules

1. **Unit tests first** -- always. Don't waste interactive cycles on broken code.
2. **Structured data over screenshots** -- when both are available, prefer ppds.debug.* JSON over visual inspection. For webview panels, use @ext-verify screenshots (see Extension Mode above).
3. **Report exact state** -- include actual values, not just pass/fail.
4. **Prerequisites are hard gates** -- if MCP not configured, stop and say so.
5. **Don't fix during verify** -- report problems, don't fix them. That's for /debug or /converge.
