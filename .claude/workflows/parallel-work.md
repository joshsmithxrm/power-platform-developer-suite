# Parallel Work Orchestration

How multiple Claude sessions work together on parallel workstreams.

## Overview

The orchestrator pattern enables working on multiple issues simultaneously:

```
/orchestrate
    ↓
List issues and discuss priorities
    ↓
Spawn workers for selected issues
    ↓
Workers enter planning phase
    ↓
Review worker plans at planning_complete
    ↓
Workers implement autonomously
    ↓
Each session ships via /ship
    ↓
Human reviews PRs
```

## Session Coordination (V1 - File-Based)

Sessions coordinate via status files in `~/.ppds/sessions/`:

```json
{
  "id": "work-123",
  "status": "working",
  "issue": "#123",
  "branch": "fix/auth-bug",
  "worktree": "<repo-parent>/ppds-auth-bug",
  "started": "2025-01-08T10:30:00Z",
  "lastUpdate": "2025-01-08T11:15:00Z",
  "stuck": null
}
```

### Status Values

| Status | Meaning | Human Action |
|--------|---------|--------------|
| `registered` | Worktree created, worker starting | None |
| `planning` | Worker exploring codebase | None |
| `planning_complete` | Plan ready for review | Review plan |
| `working` | Session is making progress | None |
| `stuck` | Session needs input | Provide guidance |
| `paused` | Human paused the session | Resume when ready |
| `complete` | PR created, work done | Review PR |
| `cancelled` | Session cancelled | None |

### Stuck Sessions

When stuck, the session includes context:

```json
{
  "status": "stuck",
  "stuck": {
    "reason": "Auth/Security decision needed",
    "context": "Token refresh approach unclear - use sliding expiration or fixed?",
    "options": ["sliding", "fixed", "ask user preference"],
    "since": "2025-01-08T11:00:00Z"
  }
}
```

## Orchestrator Session

The orchestrator runs in its own terminal and monitors all sessions:

```
Monitor ~/.ppds/sessions/*.json and report:

🟢 Working: 3 sessions
   - #123 fix/auth-bug (15 min active)
   - #456 feat/import-retry (8 min active)
   - #789 chore/cleanup (22 min active)

🟡 Stuck: 1 session
   - #234 feat/token-refresh: "Auth/Security decision needed"
     Context: Token refresh approach unclear
     Options: sliding, fixed, ask user preference

✅ PR Ready: 2 sessions
   - #100 https://github.com/.../pull/42
   - #101 https://github.com/.../pull/43
```

When stuck sessions exist, the orchestrator asks for input and relays guidance.

## Session Lifecycle

### Starting a Session

`/start-work` creates the session status file:

```bash
~/.ppds/sessions/work-{issue-number}.json
```

### During Work

Session updates status on significant events:
- Entering/exiting plan mode
- Hitting a domain gate (Auth/Security, Performance)
- Encountering errors that need guidance

### Completing Work

`/ship` updates status to `pr_ready` with PR URL.

### Cleanup

After PR merge, `/prune` removes the session file along with the worktree.

## V2 Vision: ppds serve Integration

Future enhancement using the RPC server:

```
Worker sessions POST status → ppds serve
Orchestrator polls → ppds serve
VS Code extension subscribes → real-time dashboard
Desktop notifications when PRs ready or stuck
```

## Related

- [Autonomous Session](./autonomous-session.md) - What happens in each worker session
- [Human Gates](./human-gates.md) - When sessions escalate to human
