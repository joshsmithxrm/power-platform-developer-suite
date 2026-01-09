# Autonomous Session

What happens inside each worker session from start to PR.

## Session Flow

```
/start-work <issue-numbers>
    ↓
Create session status file (status: registered → planning)
    ↓
Fetch issue context
    ↓
Enter plan mode
    ↓
Design implementation approach
    ↓
/commit (planning checkpoint)
    ↓
Exit plan mode, begin implementation (status: planning_complete → working)
    ↓
Implement → /commit (implementation checkpoint)
    ↓
/test → fix → repeat until green
    ↓
/commit (tests passing checkpoint)
    ↓
Self-verify
    ↓
/ship (autonomous CI-fix and bot-comment handling)
    ↓
Update session status (status: complete)
```

## Domain Gates

During implementation, Claude pauses for human approval on:

| Gate | Trigger | Example |
|------|---------|---------|
| Auth/Security | Token handling, credential storage, permissions | "Adding token refresh logic" |
| Performance-critical | Bulk operations, connection pooling, parallelism | "Changing DOP calculation" |

When hitting a gate:
1. Update session status to `stuck`
2. Include context and options
3. Wait for orchestrator to relay guidance
4. Continue after approval

## Test-Fix Loop

After implementation, run `/test` (unified command):

```
/test
    ↓
Auto-detect test type:
    - Unit tests (default)
    - TUI tests (if TUI files changed)
    - Integration tests (if --integration flag)
    ↓
Run tests
    ↓
If failures:
    - Analyze output
    - Fix issue
    - Repeat (max 5 attempts)
    ↓
If stuck on same failure 3x:
    - Update status to stuck
    - Wait for guidance
```

## Phase Commits

Use `/commit` at natural checkpoints to create recovery points:

| Phase | Commit Message Pattern | When |
|-------|----------------------|------|
| Planning | `chore(issue-N): planning complete` | After plan file written |
| Implementation | `feat/fix(scope): description` | After implementation, before tests |
| Tests passing | `test(scope): add tests for X` | After tests pass |

**Why commit at phases:**
- Creates recovery checkpoints if session crashes
- Makes code review easier (incremental commits)
- Aligns with session status transitions

## Self-Verification

Before shipping, verify the work:

### 1. CLI Smoke Test
If CLI commands were changed:
```bash
ppds --version
ppds <affected-command> --help
```

### 2. Self Code Review
Check implementation against CLAUDE.md rules:
- No magic strings for generated entities
- Using bulk APIs for multi-record operations
- Application Services for business logic
- IProgressReporter for long operations

### 3. Issue Verification
Re-read the original issue(s) and verify:
- All acceptance criteria met
- No scope creep
- Solution matches the problem

## Shipping

`/ship` handles the full shipping flow autonomously:

```
Commit changes
    ↓
Push to remote
    ↓
Create PR
    ↓
Wait for CI
    ↓
If CI fails:
    - Fetch logs
    - Analyze failure
    - Fix and push
    - Repeat (max 3 attempts)
    ↓
If bot comments appear:
    - Read comments
    - Address feedback
    - Push fixes
    ↓
Update session status: pr_ready
```

## Escalation

Sessions escalate to human (via orchestrator) when:

| Situation | Session Action |
|-----------|----------------|
| Stuck on test failure 3x | Set status: stuck, include error context |
| Domain gate hit | Set status: stuck, include decision needed |
| CI failure 3x | Set status: stuck, include CI logs |
| Unclear requirements | Set status: stuck, include question |

## Session Files

### Session Status (`~/.ppds/sessions/work-{issue}.json`)

Created by `/start-work`, updated throughout session.

### Session Prompt (`.claude/session-prompt.md`)

Local to worktree, contains issue context and plan.

## Related

- [Parallel Work](./parallel-work.md) - How orchestrator manages sessions
- [Human Gates](./human-gates.md) - When to escalate
