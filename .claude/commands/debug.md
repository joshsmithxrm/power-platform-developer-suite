# Debug

Give Claude an interactive feedback loop for CLI, TUI, Extension, and MCP development.

## Usage

`/debug` - Auto-detect component and run appropriate feedback loop
`/debug cli` - Test CLI commands
`/debug tui` - Run TUI and inspect state
`/debug extension` - Build, install, and test VS Code extension locally
`/debug mcp` - Test MCP tools

## Process

### 1. Detect Component

Based on recent changes or explicit argument:
- `src/PPDS.Cli/Commands/` → CLI mode
- `src/PPDS.Cli/Tui/` → TUI mode
- `src/PPDS.Extension/` → Extension mode
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
npm test --prefix tests/PPDS.Tui.E2eTests
```

If visual issues, update snapshots:
```bash
npm test --prefix tests/PPDS.Tui.E2eTests -- --update-snapshots
```

### 4. Extension Mode

**Build & install for manual testing:**

```bash
cd <root>/src/PPDS.Extension && npm run lint && npm run compile && npm run test && npm run local
```

Then reload VS Code (Ctrl+Shift+P -> Reload Window) and test manually.

**Useful npm scripts** (run from repo root with `ext:` prefix, or from src/PPDS.Extension/ directly):

| Request | Command |
|---------|---------|
| Build + install | `npm run ext:local` |
| Just install existing build | `npm run ext:local:install` |
| Revert to marketplace version | `npm run ext:local:revert` |
| Uninstall local | `npm run ext:local:uninstall` |
| Run unit tests only | `npm run ext:test` |
| Run E2E tests | `npm run ext:test:e2e` |
| Watch mode (hot reload) | `npm run ext:watch` |
| Full release test | `npm run ext:release:test` |

**F5 Launch Configurations** (from VS Code debug panel):

| Configuration | When to use |
|---------------|-------------|
| Run Extension | Default — full build, then launch debug host |
| Run Extension (Watch Mode) | Iterating — hot reloads on file changes |
| Run Extension (No Build) | Quick — skip build, use existing compiled code |
| Run Extension (Open Folder) | Testing with a specific project folder |
| Run Extension Tests | Run VS Code extension integration tests |

**For AI self-verification:** Use `/verify extension` to exercise commands,
inspect state, and verify webview rendering via MCP tools.

### 5. MCP Mode

Test MCP tools via MCP Inspector:

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```

Or use `/verify mcp` for structured verification.

## Iterative Fix Loop

When issues found:
1. Parse error/failure
2. Locate source code
3. Apply minimal fix
4. Re-run to verify
5. Repeat until green
