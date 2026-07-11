---
name: pr
description: Create PR, wait for the configured reviewer (Gemini or none), triage every comment, and present summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, wait for the configured external review, triage comments, and present a summary to the user.

This skill is the ONLY sanctioned path for automated PR creation. <!-- enforcement: T1 hook:pr-gate --> See REFERENCE.md §1.

## Process

```bash
python scripts/workflow-state.py set phase pr
python scripts/workflow-state.py set pr.invoked_via_skill true
```

### Step 0: Check Supervisor Inbox

```bash
python scripts/supervisor_msg.py read --consume
```

Handle message kinds per REFERENCE.md §7. `abort`/`revise` → stop before creating PR.

### Step 1: Rebase and Push

```bash
git fetch origin main && git rebase origin/main
```

Conflicts → present to user, do NOT auto-resolve. Then:

```bash
git merge-base --is-ancestor origin/main HEAD
git push --force-with-lease origin HEAD
```

### Step 2: Linked Issues

```bash
python scripts/workflow-state.py get issues
```

Include `Closes #NNN` per issue. If empty (interactive): ask user for issue numbers.

### Step 3: Pre-PR Self-Review

Dispatch `code-reviewer` agent against `git diff origin/main...HEAD`. See REFERENCE.md §2 for inputs and finding triage. DEFECTs must be fixed before opening. Skip with `--no-self-review`.

### Step 4: Create PR (Draft)

Write body to temp file (see REFERENCE.md §3 for template), then:

```bash
gh pr create --draft --title "<title>" --body-file "$PR_BODY"
```

Immediately after creation:
```bash
python scripts/workflow-state.py set pr.url "{pr-url}"
python scripts/workflow-state.py set pr.created now
```

### Step 5: Launch Background Monitor (MANDATORY) <!-- enforcement: T1 hook:session-stop-workflow -->

```bash
REVIEWER=$(python scripts/pr_monitor.py --print-reviewer)
python scripts/workflow-state.py set pr.reviewer "$REVIEWER"
python scripts/pr_monitor.py --worktree "$(pwd)" --pr {pr-number} --reviewer "$REVIEWER"
```

Reviewer mode resolves `--reviewer` flag > `PPDS_PR_REVIEWER` env > repo default; `none` disables the external-review wait and triage. Launch as detached background process (see REFERENCE.md §4). Then:
```bash
cat .workflow/pr-monitor.pid
python scripts/workflow-state.py set pr.monitor_launched now
```

On failure: inline fallback — record reason per REFERENCE.md §4. When `$REVIEWER` is `none`, skip the "wait + triage yourself" fallback steps (there is no external reviewer); still record `pr.monitor_launched "fallback: <reason>"` and `pr.reviewer`. <!-- enforcement: T2 hook:pr-monitor-fallback-record -->

### Step 6: Completion Gate (MANDATORY) <!-- since: PR#956 rationale --> <!-- enforcement: T1 hook:session-stop-workflow -->

1. **Monitor**: confirm `.workflow/pr-monitor.pid` exists and process running (`kill -0`). If missing AND no fallback recorded → fail: `"⚠ Monitor PID file missing"`.
2. **Review received** — *skip this check when `$REVIEWER` is `none`* (note "Reviewer: none" in the summary instead). Otherwise poll `gh pr view {N} --json reviews,comments` every 30s for 5 min. If absent → fail. Bypass with `--skip-gemini-check`.

### Step 7: Present Summary

Gemini mode:
```
PR created (draft): {url}
Monitor launched (PID {pid}) — handling CI, Gemini, CodeQL, triage, ready-flip, retro
Gemini review: ✅ verified

Check: /status   Log: .workflow/pr-monitor.log
```

When `$REVIEWER` is `none`, replace the "Gemini review" line with:
```
Reviewer: none — monitor gates on CI + unreplied bot comments only
```

### Step 8: Post-Merge Cleanup

Cleanup is user-initiated via `/cleanup`. See REFERENCE.md §5.
