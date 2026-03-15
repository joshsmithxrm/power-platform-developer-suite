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
- `specs/` - Feature specifications
- `specs/CONSTITUTION.md` - Non-negotiable principles (read before any work)
- `docs/plans/` - Implementation plans (save all plans here, NOT docs/superpowers/plans/)

## Testing

- .NET unit: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- .NET integration: `dotnet test PPDS.sln --filter "Category=Integration" -v q`
- .NET TUI: `dotnet test --filter "Category=TuiUnit"`
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
- TUI snapshots: `npm run tui:test`

## Specs

- Constitution: `specs/CONSTITUTION.md` — read before any work
- Template: `specs/SPEC-TEMPLATE.md`
- Index: `specs/README.md`

## Extension Versioning

Odd/even minor convention: odd minor = pre-release, even minor = stable. See `docs/plans/2026-03-03-vscode-extension-prerelease-design.md`.

## Architecture

TUI-first multi-interface platform. All business logic in Application Services, never in UI code.

## Workflow

- Spec: /spec → /spec-audit
- Implement: /implement → dispatches subagents, runs /gates and /verify at phase gates
- Review: /review → /converge
- Skills: @webview-panels (panel dev), @webview-cdp (visual verification)
- Execution: commit after every task, verify with /gates before proceeding — don't ask, just do it

## Gotchas

- VS Code `LogOutputChannel` writes to `exthost/<extId>/Name.log`, NOT `N-Name.log`
- Agent research summaries may be wrong — read code yourself before stating codebase behavior as fact
