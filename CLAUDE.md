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
- `.plans/` - Implementation plans (ephemeral, gitignored)

## Testing

- .NET unit: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- .NET integration: `dotnet test PPDS.sln --filter "Category=Integration" -v q`
- .NET TUI: `dotnet test --filter "Category=TuiUnit"`
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
- TUI snapshots: `npm run tui:test`

### Test Conventions

| Area | Test Type | Trait / Framework | Notes |
|------|-----------|-------------------|-------|
| Application Services | Unit (mocked deps) | `Unit` | Mock IDataverseConnectionPool, IProgressReporter |
| Dataverse SDK logic | FakeXrmEasy | `Unit` | Use FakeXrmEasyTestsBase for SDK behavior |
| Query engine | Unit (pure functions) | `Unit` | Deterministic transforms |
| Import orchestration | Unit + FakeXrmEasy | `Unit` | Mock pool, bulk executor |
| CLI commands | Unit (mock services) | `Unit` | Commands are thin wrappers — test services |
| TUI extracted logic | Unit | `TuiUnit` | Business logic, not Terminal.Gui rendering |
| Extension panels | Vitest | N/A | Message contracts + handler behavior |
| MCP tools | Unit (mock services) | `Unit` | Param validation + basic execution |
| Live Dataverse | Integration | `Integration` | Needs test-dataverse environment |

- **Coverage bar:** 80% on new code (patch), enforced by Codecov
- **AC mapping:** every spec AC must have a corresponding test (Constitution I6)
- **File placement:** `tests/{Project}.Tests/{mirror source path}/{ClassName}Tests.cs`

## Specs

- Constitution: `specs/CONSTITUTION.md` — read before any work (includes Spec Laws SL1–SL5)
- Template: `specs/SPEC-TEMPLATE.md`

## Extension Versioning

Odd/even minor convention: odd minor = pre-release, even minor = stable. See `.plans/2026-03-03-vscode-extension-prerelease-design.md`.

## Architecture

TUI-first multi-interface platform. All business logic in Application Services, never in UI code.

## Workflow (REQUIRED SEQUENCE)

### New feature or non-trivial change
1. /design (brainstorm → spec → plan → review)
2. /implement (or /implement <plan-path>)
3. /gates — STOP on failure, fix before proceeding
4. /verify for EVERY affected surface — you MUST use the product:
   - Extension changed → /ext-verify (screenshots required)
   - TUI changed → /tui-verify (PTY interaction required)
   - MCP changed → /mcp-verify (tool invocation required)
   - CLI changed → /cli-verify (run the command)
5. /qa for at least one affected surface (blind verification)
6. /review → /converge until 0 critical, 0 important
7. /pr (rebase, create PR, monitor CI + reviews)

### Bug fix or small change
1. /gates before committing
2. If UI/output changed → /verify for affected surface
3. /pr when ready

### Docs change
1. Edit docs and commit
2. /pr when ready

### Enforcement
Steps 3-6 are enforced by hooks. The PR gate hook will block `gh pr create`
if these steps are incomplete. Run `/status` to check current workflow state.

### STOP conditions
- DO NOT skip steps 3-6 because "tests pass." Tests are necessary, not sufficient.
- DO NOT declare work complete without visual verification of affected surfaces.

### Autonomy scope
"Don't ask, just do it" applies to: committing after tasks, running gates,
running verification, running QA, running review, triaging external review
comments (fix valid ones, dismiss invalid ones with rationale).
"Don't ask, just do it" does NOT apply to: skipping any workflow step,
filing/closing issues, creating PRs without passing gates.

After external review: respond to EACH comment individually on the PR with
the action taken (fixed in <commit>, or dismissed with rationale). Include
a summary of all comments and actions in the PR status report.

## Git Hooks

Pre-commit hook (`scripts/hooks/`) runs:
- **C# staged:** `dotnet build` + `dotnet test` (unit only)
- **TS staged:** typecheck + eslint
Auto-configured by `npm install`. Manual: `git config core.hooksPath scripts/hooks`.

## Backlog

- Rules and label taxonomy: `docs/BACKLOG.md`
- Use `/backlog` skill for issue triage and management

## Gotchas

- VS Code `LogOutputChannel` writes to `exthost/<extId>/Name.log`, NOT `N-Name.log`
- Agent research summaries may be wrong — read code yourself before stating facts
