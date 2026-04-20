---
name: pr
description: Create PR, wait for Gemini review, triage every comment, and present summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, wait for Gemini review, triage comments, and present a summary to the user.

## Canonical Entry Point

This skill is the ONLY sanctioned path for automated PR creation in this repo. Agents and automations MUST invoke `/pr` — raw `gh pr create`, `hub pull-request`, or API-direct calls are forbidden for agent-spawned PRs. Rationale:

- Raw usage bypasses draft-open (defeating #834's ready-flip gate)
- Raw usage bypasses `pr_monitor.py` spawn (no polling, no Gemini wait, no triage)
- Raw usage bypasses state tracking (`.workflow/state.json` records for `/gates`, `/verify`, etc.)
- Raw usage bypasses hooks that run on `gh pr create` path

Human-initiated PR creation via `gh pr create` from a terminal is fine — this rule applies to automated/agent PR creation only.

## Prerequisites

Before running `/pr`, the following must be complete (enforced by the PR gate hook):
- `/gates` passed against current HEAD
- `/verify` completed for at least one surface
- `/qa` completed for at least one surface
- `/review` completed

Run `/status` to check current workflow state.

## Process

Set the PR phase at entry:

```bash
python scripts/workflow-state.py set phase pr
```

### 1. Rebase on Main and Push

```bash
git fetch origin main
git rebase origin/main
```

If conflicts exist, present them to the user — do NOT auto-resolve.

After successful rebase, verify and push:

```bash
# Verify rebase succeeded — origin/main must be an ancestor of HEAD
git merge-base --is-ancestor origin/main HEAD

# Push rebased branch (force-with-lease is safe — only overwrites our own commits)
git push --force-with-lease origin HEAD
```

If `merge-base` fails, the rebase didn't apply correctly — investigate before proceeding.
If push is rejected, fetch and retry the rebase.

### 2. Check for Linked Issues

Before creating the PR, check if there are GitHub issues to close:

```bash
python scripts/workflow-state.py get issues
```

If the result is a JSON array (e.g., `[602, 596]`), include `Closes #NNN` lines in the PR body for each issue.

If no issues are in workflow state and this is an **interactive session** (not headless `claude -p`), ask the user:
> "Does this PR close any GitHub issues? If so, provide the numbers (comma-separated), or press Enter to skip."

Parse the response as a comma-separated list of integers. Store them:
```bash
python scripts/workflow-state.py append issues <N>
```

### 3. Create PR (Draft)

Opens as draft. Monitor flips to ready via `pr_monitor.py` auto-ready-flip logic (added in #834) once CI green + Gemini reviewed + no unreplied comments.

```bash
gh pr create --draft --title "<title>" --body "$(cat <<'EOF'
## Summary
<1-3 bullet points>

Closes #NNN
Closes #NNN

## Test Plan
<bulleted checklist>

## Verification
- [x] /gates passed
- [x] /verify completed (surfaces: ...)
- [x] /qa completed (surfaces: ...)
- [x] /review completed (findings: N)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Omit the `Closes` lines if there are no linked issues. Keep title under 70 characters. Use conventional commit format.

**Immediately after PR creation, write state** (before any step that could fail or be interrupted):

```bash
python scripts/workflow-state.py set pr.url "{pr-url}"
python scripts/workflow-state.py set pr.created now
```

### 4. Launch Background Monitor (MANDATORY)

The pr-monitor handles the entire post-creation lifecycle: CI polling, Gemini review wait (with overload detection + retry), CodeQL check wait, triage dispatch, threaded replies, reconciliation, draft→ready conversion, retro, and notification. It runs as a detached background process that survives session exit.

**This step is MANDATORY. Do not skip it. Do not attempt inline Gemini polling instead.**

The monitor exists because Gemini review timing is unpredictable (2-10+ minutes). Inline polling with a fixed timeout creates a gap where late-arriving comments go untriaged. The monitor eliminates this gap.

```bash
python scripts/pr_monitor.py --worktree "$(pwd)" --pr {pr-number}
```

Launch as a detached background process:
- Windows: `subprocess.Popen(..., creationflags=subprocess.CREATE_BREAKAWAY_FROM_JOB | subprocess.CREATE_NEW_PROCESS_GROUP)`
- Unix: `subprocess.Popen(..., start_new_session=True)`

After launching, verify the PID file was written:
```bash
cat .workflow/pr-monitor.pid
```

If the monitor fails to launch (e.g., `claude` command not found), fall back to manual triage: wait inline, triage comments yourself, convert to ready. But this is the exception, not the norm.

### 5. Present Summary and Return

The monitor is now handling the lifecycle. Present status and return control to the user:

```
PR created (draft): {url}
Monitor launched (PID {pid}) — handling:
  • CI polling (15 min timeout)
  • Gemini review wait (5 min, with overload retry)
  • CodeQL check wait (5 min)
  • Triage + threaded replies
  • Draft → ready conversion (after triage)
  • Retro + notification

Check progress: /status
Monitor log: .workflow/pr-monitor.log
```

Do NOT wait for the monitor to finish. Do NOT do inline Gemini polling. The monitor handles everything asynchronously.

## Error Handling

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present conflicts to user, do NOT auto-resolve |
| PR creation fails | Check `gh auth status`, suggest `gh auth login` if needed |
| Push rejected | Check if branch is behind, suggest rebase |
| Monitor fails to launch | Fall back to inline triage (wait for comments, triage, convert to ready) |
