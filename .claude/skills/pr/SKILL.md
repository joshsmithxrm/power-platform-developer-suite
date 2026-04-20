---
name: pr
description: Create PR, wait for Gemini review, triage every comment, and present summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, wait for Gemini review, triage comments, and present a summary to the user.

## Prerequisites

Prerequisite enforcement (`/gates`, `/verify`, `/qa`, `/review`) lives in
`pr-gate.py` at `.claude/hooks/pr-gate.py`, which blocks `gh pr create`
if any step is missing or stale. The hook is the single source of truth
— this skill does not repeat that check. Run `/status` to inspect current
workflow state before invoking `/pr`.

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
- Any spec ACs linked from `.workflow/state.json` (`issues` list)

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

### 5. Launch Background Monitor (MANDATORY)

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

### 7. Post-Merge Auto-Cleanup

After the PR merges, the worktree and local branch are no longer needed.
The monitor is responsible for detecting merge completion and invoking
`/cleanup`; this section documents the contract so both sides agree.

Detection: poll `gh pr view <number> --json mergedAt,state` — the PR is
merged when `mergedAt` is non-null and `state == "MERGED"`. Until then,
do nothing (the cleanup must not run for closed-without-merge PRs).

Action on merge detected: invoke the `/cleanup` skill
(`.claude/skills/cleanup/SKILL.md`). Cleanup will prune this worktree,
delete the local branch, and rebase remaining active worktrees onto main.

If `/cleanup` cannot be invoked from the background monitor context, the
fallback is to surface the merged state in the final notification and
instruct the user to run `/cleanup` manually — do not leave merged
worktrees around silently.

## Error Handling

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present conflicts to user, do NOT auto-resolve |
| Self-review subagent fails | Log the failure; ask user whether to proceed without self-review or abort |
| PR creation fails | Check `gh auth status`, suggest `gh auth login` if needed |
| Push rejected | Check if branch is behind, suggest rebase |
| Monitor fails to launch | Fall back to inline triage (wait for comments, triage, convert to ready) |
| Post-merge cleanup fails | Surface failure in notification; instruct user to run `/cleanup` manually |
