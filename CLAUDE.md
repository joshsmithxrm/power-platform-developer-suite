# PPDS

Multi-surface platform (CLI / TUI / VS Code Extension / MCP / NuGet).
All business logic lives in **Application Services** — never in UI code.

Read **`specs/CONSTITUTION.md`** before any work (Spec Laws SL1–SL5 are non-negotiable).
Governance for THIS file: **`docs/CLAUDE-MD-GOVERNANCE.md`** (4-question test, line cap, marker).
Agent topology, decision UX, lane assignment: **`.claude/interaction-patterns.md`** (read before `/retro`, `/backlog dispatch`, or any N-decision triage).

## NEVER <!-- enforcement: T3 not-a-directive -->

- Hold a single pooled `IDataverseConnectionPool` client across multiple parallel queries — defeats pool parallelism. (No analyzer; subtle.) <!-- enforcement: T3 escape-hatch: subtle, no analyzer -->
- Write CLI status messages to `stdout` — use `Console.Error.WriteLine`. `stdout` is reserved for data. <!-- enforcement: T2 hook:stdout-discipline -->
- Throw raw exceptions from Application Services — wrap in `PpdsException` with an `ErrorCode`. <!-- enforcement: T2 hook:exception-wrap-check -->
- Trust an agent research summary without reading the underlying code yourself. <!-- enforcement: T3 -->
- Edit `PublicAPI.Unshipped.txt` during a rebase — accept `--theirs` and let it regenerate; manual edits produce phantom API-drift conflicts. <!-- since: PR#956 rationale --> <!-- enforcement: T3 escape-hatch: rebase-context detection too brittle for hook -->

## ALWAYS <!-- enforcement: T3 not-a-directive -->

- Use Application Services for all persistent state — single code path for CLI / TUI / RPC. <!-- enforcement: T3 -->
- Accept `IProgressReporter` for any operation likely to exceed 1 second. <!-- enforcement: T3 -->
- Complete the shipping pipeline: `/gates` → `/verify` → `/pr`. Never stop after `/gates` or `/verify` — the work is not done until `/pr` creates the pull request. <!-- enforcement: T1 hook:session-stop-workflow -->
- On `scripts/pipeline.py` failure, recover via `python scripts/pipeline.py --resume` (or `--from <stage>`) or sequential `/gates` → `/verify` → `/pr` — never ad-hoc parallel debug. <!-- since: PR#956 rationale --> <!-- enforcement: T3 escape-hatch: cannot detect omission of --resume flag -->
- Hard cap on simultaneous background TaskCreate jobs: ≤3. If a 4th is needed, stop and ask. <!-- since: PR#956 rationale --> <!-- enforcement: T1 hook:taskcreate-cap -->
- For any test/build failure, invoke `/debug` first; do not hypothesize without evidence. <!-- since: PR#956 rationale --> <!-- enforcement: T1 hook:debug-first -->

## Testing

- .NET unit: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- .NET integration: `dotnet test PPDS.sln --filter "Category=Integration" -v q`
- .NET TUI: `dotnet test --filter "Category=TuiUnit"`
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
- TUI snapshots: `npm run tui:test`
