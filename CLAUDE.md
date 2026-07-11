# PPDS

Multi-surface platform (CLI / TUI / VS Code Extension / MCP / NuGet).
All business logic lives in **Application Services** — never in UI code.

Read **`specs/CONSTITUTION.md`** before any work (Spec Laws SL1–SL5 are non-negotiable).
Governance for THIS file: **`docs/CLAUDE-MD-GOVERNANCE.md`** (4-question test, line cap, marker).
The `.claude/` tooling is optional; CI is the authoritative gate (see CONTRIBUTING.md "AI-Assisted Development").

## NEVER <!-- enforcement: T3 not-a-directive -->

- Hold a single pooled `IDataverseConnectionPool` client across multiple parallel queries — defeats pool parallelism. (No analyzer; subtle.) <!-- enforcement: T3 escape-hatch: subtle, no analyzer -->
- Write CLI status messages to `stdout` — use `Console.Error.WriteLine`. `stdout` is reserved for data. <!-- enforcement: T2 hook:stdout-discipline -->
- Throw raw exceptions from Application Services — wrap in `PpdsException` with an `ErrorCode`. <!-- enforcement: T2 hook:exception-wrap-check -->
- Trust an agent research summary without reading the underlying code yourself. <!-- enforcement: T3 -->
- Edit `PublicAPI.Unshipped.txt` during a rebase — accept `--theirs` and let it regenerate; manual edits produce phantom API-drift conflicts. <!-- since: PR#956 rationale --> <!-- enforcement: T3 escape-hatch: rebase-context detection too brittle for hook -->
- Trust a `release/X` branch as the source of truth for what shipped as version X — verify with `git merge-base --is-ancestor <release-tag> <branch>`. Tags are authoritative; the branch tip may sit on a separate lineage. <!-- since: 2026-05-15 cleanup retro --> <!-- enforcement: T3 escape-hatch: only fires when reasoning about release history -->
- Use PowerShell cmdlets (`Test-Path`, `Get-ChildItem`, etc.) via the Bash tool, or embed Windows backslash paths in inline `python -c` literals — see `.claude/WORKFLOW.md` "Bash Tool Portability". <!-- since: #1130 #1131 --> <!-- enforcement: T3 escape-hatch: runtime emission; tests/test_skill_bash_portability.py covers checked-in skill files -->

## ALWAYS <!-- enforcement: T3 not-a-directive -->

- Use Application Services for all persistent state — single code path for CLI / TUI / RPC. <!-- enforcement: T3 -->
- Accept `IProgressReporter` for any operation likely to exceed 1 second. <!-- enforcement: T3 -->
- Ship through `/gates` → `/verify` → `/pr` before calling work done — recommended flow; CI enforces the same gates on every PR. <!-- enforcement: T3 advisory; CI is the contract -->
- For any test/build failure, use `/debug` before proposing fixes; do not hypothesize without evidence. <!-- since: PR#956 rationale --> <!-- enforcement: T3 advisory -->

## Testing

- .NET unit: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- .NET integration: `dotnet test PPDS.sln --filter "Category=Integration" -v q`
- .NET TUI: `dotnet test --filter "Category=TuiUnit"`
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
- TUI snapshots: `npm run tui:test`
