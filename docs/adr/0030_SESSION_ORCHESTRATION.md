# ADR-0030: Session Orchestration

## Status
Accepted (Revised January 2026)

## Context
The `/plan-work` orchestration system was designed in workflow documentation but never implemented. The goal is parallel Claude Code sessions working on multiple issues simultaneously, with human oversight for stuck sessions.

Key requirements:
1. Spawn N worker sessions for GitHub issues
2. Each session works autonomously on an issue
3. Sessions coordinate via status files
4. Human receives prompts when sessions get stuck
5. Sessions complete when PRs are created

## Decision
Implement session orchestration using **Claude-as-orchestrator** with terminal workers:

1. **Claude orchestrator session** - human interacts via natural language
2. **Terminal workers** - complex tasks via Windows Terminal tabs with direct Claude spawn
3. **File-based coordination** - `~/.ppds/sessions/work-{issue}.json`
4. **C# service layer** - `ISessionService` for all session operations

### Why Claude-as-Orchestrator?
The original PowerShell orchestrator required terminal interaction. Claude-as-orchestrator enables:
- Natural language commands ("add issue 123", "status", "guide 456: use Option A")
- Integrated monitoring within the same conversation
- Stateless recovery on restart

### Architecture
```
User (natural language)
  |
  v
Orchestrator Claude Session
  |-- Parses: "add 123", "status?", "guide 456: use Option A"
  |-- Uses: ppds session CLI commands
  |
  v
ISessionService (C# service layer)
  |-- Reads/writes: ~/.ppds/sessions/work-{issue}.json
  |-- Manages: git worktrees
  |-- Spawns: Windows Terminal tabs
  |
  v
WindowsTerminalWorkerSpawner
  |-- Creates: Windows Terminal tab per worker
  |-- Launches: Claude with session prompt
  |-- Worker updates: work-{issue}.json via ppds session update
```

### Session CLI Commands

All commands require `PPDS_INTERNAL=1` environment variable (automatically set for workers).

| Command | Purpose |
|---------|---------|
| `ppds session spawn <issue>` | Create worktree, fetch issue, spawn worker |
| `ppds session list` | List all active sessions with status |
| `ppds session get <id>` | Detailed session info with git diff |
| `ppds session update --id <id> --status <status>` | Worker reports status |
| `ppds session pause <id>` | Pause worker without cancelling |
| `ppds session resume <id>` | Resume paused worker |
| `ppds session cancel <id>` | Cancel and cleanup worktree |
| `ppds session cancel-all` | Cancel all active sessions |
| `ppds session forward <id> <message>` | Send guidance to stuck worker |

### Session File Schema

**work-{issue}.json** - Worker status
```json
{
  "id": "123",
  "issueNumber": 123,
  "issueTitle": "Add export button",
  "status": "working",
  "branch": "issue-123",
  "worktreePath": "C:/VS/ppds-issue-123",
  "startedAt": "2025-01-08T10:30:00Z",
  "lastHeartbeat": "2025-01-08T11:15:00Z",
  "stuckReason": null,
  "forwardedMessage": null,
  "pullRequestUrl": null,
  "worktreeStatus": {
    "filesChanged": 3,
    "insertions": 42,
    "deletions": 5,
    "lastCommitMessage": "feat: add export button",
    "changedFiles": ["src/Commands/ExportCommand.cs", "..."]
  }
}
```

### Session Status Values

| Status | Meaning | Icon |
|--------|---------|------|
| `Registered` | Worktree created, worker starting | `[ ]` |
| `Planning` | Worker exploring codebase, creating plan | `[~]` |
| `PlanningComplete` | Plan ready for review | `[P]` |
| `Working` | Actively implementing | `[*]` |
| `Stuck` | Needs human guidance | `[!]` |
| `Paused` | Human requested pause | `[||]` |
| `Complete` | PR created, work done | `[+]` |
| `Cancelled` | Session cancelled | `[x]` |

### Planning Phase

Workers enter planning mode to explore the codebase before implementing:

```
Registered → Planning → PlanningComplete → Working → Complete
                                    ↓
                                  Stuck (if domain gate hit)
```

The orchestrator watches for `PlanningComplete` status and prompts for human review.

### Worker Spawning

The `WindowsTerminalWorkerSpawner` creates workers via:
1. Creates launcher script at `.claude/start-worker.ps1`
2. Launches Windows Terminal: `wt -w 0 nt -d "<worktree>" --title "Issue #N" pwsh -NoExit -File "<launcher>"`
3. Launcher sets `PPDS_INTERNAL=1` and invokes Claude with permission mode

### Domain Gates
Sessions MUST escalate (set stuck status) when touching:
- **Auth/Security** - Token handling, credentials, permissions
- **Performance-critical** - Bulk operations, connection pooling, parallelism
- **Breaking changes** - Public API modifications
- **Data migration** - Schema changes

### Staleness Detection
Workers must send heartbeats (status updates). Orchestrator flags workers as stale after **90 seconds** without a heartbeat.

### Cancellation
Set session status to `Cancelled`. Worker checks status at iteration start.
Use `--keep-worktree` flag to preserve worktree for debugging.

## Consequences

### Positive
- Parallel development on multiple issues
- Human oversight without constant monitoring
- Natural language interaction (no terminal prompts)
- Clear escalation paths for sensitive areas
- File-based coordination is simple and debuggable
- Stateless orchestrator - survives restarts
- Full C# service layer with DI support

### Negative
- Workers require Windows Terminal (Windows-only for now)
- Session commands gated behind `PPDS_INTERNAL=1`

### Neutral
- Session files in `~/.ppds/sessions/` consistent with ADR-0024
- Planning phase adds human review opportunity without blocking

## Files

| File | Purpose |
|------|---------|
| `.claude/commands/orchestrate.md` | `/orchestrate` slash command |
| `scripts/worker-task-template.md` | Worker prompt template |
| `src/PPDS.Cli/Services/Session/ISessionService.cs` | Service interface |
| `src/PPDS.Cli/Services/Session/SessionService.cs` | Service implementation |
| `src/PPDS.Cli/Services/Session/SessionState.cs` | State models |
| `src/PPDS.Cli/Services/Session/WindowsTerminalWorkerSpawner.cs` | Worker spawner |
| `src/PPDS.Cli/Commands/Session/*.cs` | CLI commands |

## Resilience

### Restart Recovery
Orchestrator is stateless - reads session files on each status check:
1. Workers continue independently (terminals)
2. Orchestrator reads `work-*.json` for current status
3. User says "status" → orchestrator resumes

## Future Enhancements
- **ppds serve integration**: RPC server for VS Code extension
- **VS Code dashboard**: Visual session monitoring
- **Cross-platform**: Bash script for macOS/Linux spawning

## References
- [Parallel Work Workflow](../../.claude/workflows/parallel-work.md)
- [Autonomous Session Workflow](../../.claude/workflows/autonomous-session.md)
- ADR-0024: Shared Local State Architecture
- ADR-0025: UI-Agnostic Progress Reporting
