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

- **TUI keyboard shortcuts:** Alt+Letter in dialogs, single letter in table/list views, Ctrl+Letter/F-key for global. Full convention in `specs/tui.md` § Keyboard Shortcut Convention.

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
