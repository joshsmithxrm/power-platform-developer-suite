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
- `docs/plans/` - Implementation plans

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

## Workflow (REQUIRED SEQUENCE)

### New feature or non-trivial change
1. /spec (or verify spec exists with numbered ACs)
2. /spec-audit (verify spec matches codebase reality)
3. Write implementation plan → user approves
4. /implement <plan-path>
5. /gates — STOP on failure, fix before proceeding
6. /verify for EVERY affected surface — you MUST use the product:
   - Extension changed → /ext-verify (screenshots required)
   - TUI changed → /tui-verify (PTY interaction required)
   - MCP changed → /mcp-verify (tool invocation required)
   - CLI changed → /cli-verify (run the command)
7. /qa for at least one affected surface (blind verification)
8. /review → /converge until 0 critical, 0 important
9. /pr (rebase, create PR, monitor CI + reviews)

### Bug fix or small change
1. /gates before committing
2. If UI/output changed → /verify for affected surface
3. /pr when ready

### Commits and enforcement
Commit after each GitHub issue fixed or plan task completed — don't ask, just do it. One commit per fix.
Steps 5-8 enforced by hooks. PR gate blocks `gh pr create` if incomplete. Run /status to check.

### STOP conditions
- DO NOT skip steps 5-8 because "tests pass." Tests are necessary, not sufficient.
- DO NOT declare work complete without visual verification of affected surfaces.

### Autonomy scope
"Don't ask, just do it" applies to: committing, gates, verification, QA, review, triaging review comments. Does NOT apply to: skipping workflow steps, filing/closing issues, PRs without passing gates.
After external review: respond to EACH PR comment individually with action taken. Include summary in status report.

## Git Hooks

Pre-commit hook (`scripts/hooks/`) runs `typecheck:all` + `eslint --quiet` on extension TS changes. Auto-configured by `npm install`. Manual: `git config core.hooksPath scripts/hooks`.

## Gotchas

- VS Code `LogOutputChannel` writes to `exthost/<extId>/Name.log`, NOT `N-Name.log`
- Agent research summaries may be wrong — read code yourself before stating facts
