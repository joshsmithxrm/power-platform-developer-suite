---
name: pr
description: Create PR, wait for Gemini review, triage every comment, and present summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, wait for Gemini review, triage comments, and present a summary to the user.

## Canonical Entry Point

This skill is the ONLY sanctioned path for automated PR creation in this repo. Agents and automations MUST invoke `/pr` — directly calling `gh pr create`, `hub pull-request`, or the GitHub API outside of this skill is forbidden for agent-spawned PRs. (The skill itself calls `gh pr create` internally in Step 3 — that is the sanctioned invocation.) Rationale:

- Direct invocation bypasses draft-open (defeating #834's ready-flip gate)
- Direct invocation bypasses `pr_monitor.py` spawn (no polling, no Gemini wait, no triage)
- Direct invocation bypasses state tracking (`.workflow/state.json` records for `/gates`, `/verify`, etc.)
- Direct invocation bypasses prerequisite enforcement (the PR gate hook described in Prerequisites below)

Human-initiated PR creation via `gh pr create` from a terminal on a non-worktree checkout is fine — this rule applies to automated/agent PR creation only.

This rule is hook-enforced: `.claude/hooks/pr-gate.py` detects agent context (cwd in `.claude/worktrees/agent-*` or Claude Code agent env vars) and blocks `gh pr create` unless the `/pr` skill has set `pr.invoked_via_skill=true` in workflow state. Humans running from a worktree can set `PPDS_PR_GATE_HUMAN=1` to override.

## Prerequisites

Prerequisite enforcement (`/gates`, `/verify`, `/qa`, `/review`) lives in
`pr-gate.py` at `.claude/hooks/pr-gate.py`, which blocks `gh pr create`
if any step is missing or stale. The hook is the single source of truth
— this skill does not repeat that check. Run `/status` to inspect current
workflow state before invoking `/pr`.

## Process

Set the PR phase and skill-entry marker at entry:

```bash
python scripts/workflow-state.py set phase pr
# Required: tells `.claude/hooks/pr-gate.py` this PR went through the skill.
# Without this marker, an agent-context `gh pr create` is blocked.
python scripts/workflow-state.py set pr.invoked_via_skill true
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

### 3. Pre-PR Self-Review

Before opening the PR, dispatch the `code-reviewer` subagent (defined at
`.claude/agents/code-reviewer.md`) against the diff `origin/main...HEAD` to
catch issues a pre-open self-review would catch — instead of paying for them
as Gemini comment churn post-open.

Rationale: pattern-matching on PRs #825–#830 shows a 4-commit average for
post-open rework. Running an impartial reviewer against the diff before
opening the PR shifts that rework left.

**Optional bypass:** if invoked with `--no-self-review`, skip this step
entirely. Use the flag when self-review would block an urgent fix or when
the diff is trivially small (rename, single-line fix, docs-only).

Dispatch the subagent with:
- The diff: `git diff origin/main...HEAD`
- The Constitution: `specs/CONSTITUTION.md`
- Acceptance criteria for each issue number in `.workflow/state.json`'s
  `issues` array (read via `python scripts/workflow-state.py get issues`).
  Fetch AC text with `gh issue view <N>` if not already in session context.
  If `issues` is empty or absent, skip this input.

The subagent returns findings classified as DEFECT / CONCERN / NIT. Present
them to the user and ask which to address before the PR opens:

```
Pre-PR self-review findings:
  DEFECTs: {N}   ← must fix before opening
  CONCERNs: {N}  ← should fix
  NITs: {N}      ← optional

Reply with finding IDs to address (e.g., "F-1, F-3"), "all", "defects", or
"skip" to open the PR as-is.
```

If the user elects to address findings, return to the implementation loop
(edit → commit → re-run `/gates`/`/verify`/`/qa`/`/review`) and re-enter
`/pr` when ready. The self-review runs again on the updated diff.

If the user elects to skip or only NITs are reported, proceed to step 4.

### 4. Create PR (Draft)

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

### 5. Launch Background Monitor (MANDATORY — state-tracked)

The pr-monitor handles the entire post-creation lifecycle: CI polling, Gemini review wait (with overload detection + retry), CodeQL check wait, triage dispatch, threaded replies, reconciliation, draft→ready conversion, retro, and notification. It runs as a detached background process that survives session exit.

**This step is MANDATORY. Do not skip it. Do not manually triage comments via `gh api` instead.**

> **Retro-enforced (PR #868):** Agent skipped the monitor, manually replied to 3 of 9 review comments via `gh api`, missed all CodeQL comments. User had to force monitor invocation. Manual comment triage is never an acceptable substitute — the monitor handles Gemini, CodeQL, ready-flip, and notification as a unit.

The monitor exists because Gemini review timing is unpredictable (2-10+ minutes). Inline polling with a fixed timeout creates a gap where late-arriving comments go untriaged. The monitor eliminates this gap.

```bash
python scripts/pr_monitor.py --worktree "$(pwd)" --pr {pr-number}
```

Launch as a detached background process:
- Windows: `subprocess.Popen(..., creationflags=subprocess.CREATE_BREAKAWAY_FROM_JOB | subprocess.CREATE_NEW_PROCESS_GROUP)`
- Unix: `subprocess.Popen(..., start_new_session=True)`

After launching, verify the PID file was written AND record in workflow state:
```bash
cat .workflow/pr-monitor.pid
python scripts/workflow-state.py set pr.monitor_launched now
```

If the monitor fails to launch (e.g., `claude` command not found), fall back to manual triage: wait inline, triage comments yourself, convert to ready. But this is the exception, not the norm — and you MUST record the fallback reason:
```bash
python scripts/workflow-state.py set pr.monitor_launched "fallback: <reason>"
```

### 6. Present Summary and Return

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

### 7. Post-Merge Cleanup Surfacing

After the PR merges, the worktree and local branch are no longer needed.
Cleanup itself is user-initiated — `/cleanup` deletes worktrees and local
branches, which is destructive and per interaction-patterns §5 must be
confirmed by the user before execution. This skill does NOT auto-invoke
`/cleanup`.

Current behavior (what the monitor does today): on terminal states
(`MERGED`, `CLOSED`), the monitor writes the final status to
`.workflow/pr-monitor.log` and its notification payload. It does not poll
`mergedAt` on a schedule and does not invoke `/cleanup`.

Expected user flow after merge:

1. User sees the merged notification (or runs `/status` and observes
   `pr.state == MERGED`).
2. User runs `/cleanup` manually. `/cleanup` presents the list of
   prunable worktrees/branches for confirmation before deleting.

Future enhancement (out of scope for this PR): add an opt-in flag on
`/pr` (e.g. `--cleanup-on-merge`) that, combined with a monitor-side
merge poller, surfaces a single confirmation prompt in the final
notification rather than silently deleting state. Track via a separate
issue + spec with numbered ACs before implementing.

## Error Handling

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present conflicts to user, do NOT auto-resolve |
| Self-review subagent fails | Log the failure; ask user whether to proceed without self-review or abort |
| PR creation fails | Check `gh auth status`, suggest `gh auth login` if needed |
| Push rejected | Check if branch is behind, suggest rebase |
| Monitor fails to launch | Fall back to inline triage (wait for comments, triage, convert to ready) |
| Post-merge state surfaced but user forgot to run `/cleanup` | No automatic recovery — `/cleanup` is user-initiated by design; `/status` will keep reporting merged state until user runs it |
