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

## Commands

| Command | Purpose |
|---------|---------|
| `ppds --help` | Full CLI reference |
| `ppds serve` | RPC server for IDE integration |
| `/implement` | Execute implementation plan with spec-aware subagents |
| `/spec` | Create or update a specification |
| `/spec-audit` | Audit specs against code reality |
| `/debug` | Interactive feedback loop for CLI/TUI/MCP |
| `/automated-quality-gates` | Mechanical pass/fail build/test/lint checks |
| `/impartial-code-review` | Bias-free code review against specs |
| `/review-fix-converge` | Gates, review, fix loop with convergence tracking |

## Spec Workflow

- Constitution: `specs/CONSTITUTION.md` — non-negotiable principles
- Template: `specs/SPEC-TEMPLATE.md` — required structure for all specs
- New spec: `/spec {name}` — guided creation with cross-referencing
- Audit: `/spec-audit` — compare all specs against code
- Implement: `/implement` — loads constitution + relevant specs into subagent context
- Review: `/review-fix-converge` — gates, impartial review, fix until converged

## Testing

- Unit (default): `--filter Category!=Integration`
- Integration (live): `--filter Category=Integration`
- TUI: `--filter Category=TuiUnit`

## Extension Versioning

Odd/even minor convention: odd minor = pre-release, even minor = stable. See `docs/plans/2026-03-03-vscode-extension-prerelease-design.md`.

## Architecture

TUI-first multi-interface platform. All business logic in Application Services, never in UI code.
