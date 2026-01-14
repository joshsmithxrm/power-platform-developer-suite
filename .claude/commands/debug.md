# Debug

Give Claude an interactive feedback loop for CLI, TUI, Extension, and MCP development.

## Usage

`/debug` - Auto-detect component and run appropriate feedback loop
`/debug cli` - Test CLI commands
`/debug tui` - Run TUI and inspect state
`/debug mcp` - Test MCP tools

## Process

### 1. Detect Component

Based on recent changes or explicit argument:
- `src/PPDS.Cli/Commands/` → CLI mode
- `src/PPDS.Cli/Tui/` → TUI mode
- `src/PPDS.Mcp/` → MCP mode
- No clear match → Ask user

### 2. CLI Mode

Run CLI commands and verify output:

```bash
# Build
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0

# Run command
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```

Verify:
- Command executes without error
- Output format is correct
- Exit code is 0

### 3. TUI Mode

Run TUI tests and optionally launch interactive:

```bash
# Run TUI unit tests
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --no-build

# Run TUI visual snapshots (if Node.js available)
npm test --prefix tests/tui-e2e
```

If visual issues, update snapshots:
```bash
npm test --prefix tests/tui-e2e -- --update-snapshots
```

### 4. MCP Mode

Test MCP tools via MCP client:

```bash
# Build MCP server
dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj

# Test tools (use MCP client if available)
```

## Iterative Fix Loop

When issues found:
1. Parse error/failure
2. Locate source code
3. Apply minimal fix
4. Re-run to verify
5. Repeat until green
