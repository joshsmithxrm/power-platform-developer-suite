# Autonomous Work Session - Issue #{ISSUE_NUMBER}

## Context

**Issue**: #{ISSUE_NUMBER} - {ISSUE_TITLE}
**Branch**: {BRANCH_NAME}
**Worktree**: {WORKTREE_PATH}
**Session ID**: {ISSUE_NUMBER}
**Related**: {RELATED_ISSUES}

### Issue Description
{ISSUE_BODY}

---

## Status Reporting

Report your status using `ppds session update`. The orchestrator monitors these updates.

### Status Commands

**Report working (send as heartbeat each iteration):**
```bash
ppds session update --id {ISSUE_NUMBER} --status working
```

**Report stuck (domain gate or repeated failure):**
```bash
ppds session update --id {ISSUE_NUMBER} --status stuck --reason "DESCRIBE_BLOCKER: context and options"
```

**Report complete (PR created):**
```bash
ppds session update --id {ISSUE_NUMBER} --status complete --pr "https://github.com/.../pull/N"
```

### Checking for Guidance

If stuck, poll for human guidance:
```bash
ppds session get {ISSUE_NUMBER} --json
```

Look for `forwardedMessage` field. If present, apply guidance and resume:
```bash
ppds session update --id {ISSUE_NUMBER} --status working
```

---

## Your Workflow

### At Start of Each Iteration
1. Report heartbeat:
   ```bash
   ppds session update --id {ISSUE_NUMBER} --status working
   ```
2. Check for forwarded messages if previously stuck

### Phase 1: Plan
1. Read the issue thoroughly
2. Explore codebase to understand affected areas
3. Design your implementation approach
4. Write plan to `.claude/session-prompt.md` in this worktree

### Phase 2: Implement
1. Follow PPDS patterns (see CLAUDE.md)
2. Use early-bound entities with `EntityLogicalName` and `Fields.*`
3. Use Application Services for business logic
4. Accept `IProgressReporter` for long operations

### Phase 3: Test
Run tests using `/test` command (auto-detects test type):
- Unit tests: default
- TUI tests: if TUI files changed
- Integration: only if explicitly requested

**Test-Fix Loop Rules:**
- Max 5 attempts on same failure before escalating
- If stuck 3x on SAME error, report stuck status

### Phase 4: Ship
Run `/ship` command which handles:
- Commit with proper message
- Push to remote
- Create PR
- Monitor CI (max 3 fix attempts)
- Handle bot comments

---

## Domain Gates (MUST ESCALATE)

Stop and report stuck when touching:

| Gate | Examples |
|------|----------|
| **Auth/Security** | Token handling, credentials, permissions, encryption |
| **Performance-critical** | Bulk operations, connection pooling, parallelism (DOP) |
| **Breaking changes** | Public API modifications, removing/renaming exports |
| **Data migration** | Schema changes, data transformation logic |

Report stuck with clear reason:
```bash
ppds session update --id {ISSUE_NUMBER} --status stuck --reason "Auth/Security: Token refresh approach. Options: sliding (15min) or fixed (1hr) expiration"
```

---

## Completion Criteria

ALL must be true:
- [ ] Implementation complete per issue requirements
- [ ] All unit tests passing
- [ ] `/ship` completed successfully
- [ ] PR created with CI green (or 3 CI-fix attempts made)

---

## Exit Conditions

### Success: Output `<promise>PR_READY</promise>`
When:
- PR is created AND CI is green
- OR PR is created AND you've made 3 CI fix attempts

Before outputting, report complete:
```bash
ppds session update --id {ISSUE_NUMBER} --status complete --pr "PR_URL_HERE"
```

### Stuck: Output `<promise>STUCK_NEEDS_HUMAN</promise>`
When:
- Domain gate hit (Auth/Security, Performance, etc.)
- Same test failure 5x
- Same CI failure 3x
- Requirements unclear, cannot proceed

Before outputting, report stuck:
```bash
ppds session update --id {ISSUE_NUMBER} --status stuck --reason "DESCRIBE_BLOCKER"
```

### Blocked: Output `<promise>BLOCKED_EXTERNAL</promise>`
When:
- Blocked by another PR that must merge first
- Need infrastructure/access you don't have
- External dependency issue

Report stuck with external reason:
```bash
ppds session update --id {ISSUE_NUMBER} --status stuck --reason "BLOCKED: Waiting for PR #X to merge"
```

---

## Important Rules

1. **Never guess** - If unclear, report stuck and ask
2. **No scope creep** - Only implement what the issue asks
3. **Test everything** - If you change it, test it
4. **Document gates** - Always explain WHY you're stuck
5. **Heartbeat regularly** - Send working status each iteration

---

## Self-Verification Checklist (Before Shipping)

- [ ] Re-read original issue - does solution match?
- [ ] CLAUDE.md rules followed?
- [ ] No magic strings for entities?
- [ ] Bulk APIs used for multi-record ops?
- [ ] CLI smoke test if CLI changed: `ppds --version`
