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
| tui | `mcp-tui-test` configured in Claude Code |
| extension | `acomagu/vscode-as-mcp-server` installed in VS Code + Playwright MCP for webview |
| mcp | MCP Inspector CLI (`npx @modelcontextprotocol/inspector`) |

## Process

### 1. Detect Component

Based on $ARGUMENTS or recent changes:
- `src/PPDS.Cli/Commands/` → CLI mode
- `src/PPDS.Cli/Tui/` → TUI mode
- `extension/` → Extension mode
- `src/PPDS.Mcp/` → MCP mode
- No clear match → Ask user

### 2. Run Unit Tests First

Always run the relevant unit tests before interactive verification. If tests fail, fix them first — don't waste MCP verification cycles on broken code.

- CLI/TUI: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- Extension: `npm run test --prefix src/extension`
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

Use `mcp-tui-test` MCP tools:

1. **Launch:** Start the TUI app with configured dimensions
2. **Wait:** Wait for initial render (look for expected text)
3. **Interact:** Send keyboard input to navigate menus, trigger actions
4. **Capture:** Read screen output after each interaction
5. **Verify:** Check captured output contains expected elements

```
-> launch_tui_app("ppds tui", rows=40, cols=120)
-> wait_for_text("PPDS", timeout=10000)
-> capture_screen() -> verify menu items visible
-> send_keys("Enter") -> navigate into menu
-> capture_screen() -> verify expected view loaded
```

Also run TUI snapshot tests for visual regression:
```bash
npm test --prefix tests/tui-e2e
```

### 5. Extension Mode

**Phase A: Functional Verification (acomagu MCP + ppds.debug.*)**

Use `execute_vscode_command` to run diagnostic commands:

```
-> execute_vscode_command("ppds.debug.daemonStatus")
   Verify: daemon is "ready", process ID present

-> execute_vscode_command("ppds.debug.extensionState")
   Verify: activation succeeded, no errors

-> execute_vscode_command("ppds.debug.treeViewState")
   Verify: profiles tree populated (if auth configured)

-> execute_vscode_command("ppds.dataExplorer")
   Opens Data Explorer panel

-> execute_vscode_command("ppds.debug.panelState")
   Verify: QueryPanel instance exists
```

Use `code_checker` to read VS Code diagnostics:
```
-> code_checker()
   Verify: no errors from PPDS extension
```

**Phase B: Webview Visual Verification (Playwright MCP)**

Start the webview dev server:
```bash
npm run dev:webview --prefix extension
```

Use Playwright MCP:
```
-> browser_navigate("http://localhost:5173/query-panel.html")
-> browser_snapshot() -> verify query input, execute button, results area
-> browser_fill_form("#sql-input", "SELECT TOP 5 name FROM account")
-> browser_click("#execute-btn")
-> browser_snapshot() -> verify results table rendered
```

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

## Rules

1. **Unit tests first** -- always. Don't waste interactive cycles on broken code.
2. **Structured data over screenshots** -- prefer ppds.debug.* JSON over visual inspection.
3. **Report exact state** -- include actual values, not just pass/fail.
4. **Prerequisites are hard gates** -- if MCP not configured, stop and say so.
5. **Don't fix during verify** -- report problems, don't fix them. That's for /debug or /converge.
