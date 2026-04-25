# PPDS

Multi-surface platform (CLI / TUI / VS Code Extension / MCP / NuGet).
All business logic lives in **Application Services** — never in UI code.

Read **`specs/CONSTITUTION.md`** before any work (Spec Laws SL1–SL5 are non-negotiable).
Governance for THIS file: **`docs/CLAUDE-MD-GOVERNANCE.md`** (4-question test, line cap, marker).
Agent topology, decision UX, lane assignment: **`.claude/interaction-patterns.md`** (read before `/retro`, `/backlog dispatch`, or any N-decision triage).

## NEVER

- Hold a single pooled `IDataverseConnectionPool` client across multiple parallel queries — defeats pool parallelism. (No analyzer; subtle.)
- Write CLI status messages to `stdout` — use `Console.Error.WriteLine`. `stdout` is reserved for data.
- Throw raw exceptions from Application Services — wrap in `PpdsException` with an `ErrorCode`.
- Trust an agent research summary without reading the underlying code yourself.

## ALWAYS

- Use Application Services for all persistent state — single code path for CLI / TUI / RPC.
- Accept `IProgressReporter` for any operation likely to exceed 1 second.
- Complete the shipping pipeline: `/gates` → `/verify` → `/pr`. Never stop after `/gates` or `/verify` — the work is not done until `/pr` creates the pull request.

## Testing

- .NET unit: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- .NET integration: `dotnet test PPDS.sln --filter "Category=Integration" -v q`
- .NET TUI: `dotnet test --filter "Category=TuiUnit"`
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
- TUI snapshots: `npm run tui:test`
