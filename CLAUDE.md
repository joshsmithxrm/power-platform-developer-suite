# PPDS - Power Platform Developer Suite

SDK, CLI, TUI, VS Code Extension, and MCP server for Power Platform development.

## NEVER

- Regenerate `PPDS.Plugins.snk` - breaks strong naming for all assemblies
- Create new `ServiceClient` per request - 42,000x slower than pool; use `IDataverseConnectionPool`
- Hold single pooled client for multiple queries - defeats pool parallelism
- Write CLI status messages to stdout - use `Console.Error.WriteLine`; stdout is for data
- Throw raw exceptions from Application Services - wrap in `PpdsException` with ErrorCode

## ALWAYS

- Use connection pool for multi-request scenarios
- Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) - 5x faster than `ExecuteMultiple`
- Use Application Services for all persistent state - single code path for CLI/TUI/RPC
- Accept `IProgressReporter` for operations >1 second - all UIs need feedback
- Include ErrorCode in `PpdsException` - enables programmatic handling

## Tech Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 4.6.2, 8.0, 9.0, 10.0 | Plugins: 4.6.2; libraries/CLI: 8.0+ |
| Terminal.Gui | 1.19+ | TUI framework |

## Key Files

- `src/PPDS.Cli/Services/` - Application Services
- `src/PPDS.Dataverse/Generated/` - Early-bound entities (DO NOT edit)
- `docs/specs/` - Feature specifications

## Commands

| Command | Purpose |
|---------|---------|
| `ppds --help` | Full CLI reference |
| `ppds serve` | RPC server for IDE integration |
| `/ship` | Validate, commit, PR, handle CI |
| `/debug` | Interactive feedback loop for CLI/TUI/MCP |

## Testing

- Unit (default): `--filter Category!=Integration`
- Integration (live): `--filter Category=Integration`
- TUI: `--filter Category=TuiUnit`

See `docs/specs/` for testing strategy.

## Architecture

TUI-first multi-interface platform. All business logic in Application Services, never in UI code.
