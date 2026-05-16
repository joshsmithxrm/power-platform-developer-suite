# PR — Reference

## §1 - Canonical Entry Point Rationale

`/pr` is the ONLY sanctioned path for automated PR creation. Direct `gh pr create` bypasses:
- Draft-open (defeating #834's ready-flip gate)
- `pr_monitor.py` spawn (no polling, no Gemini wait, no triage)
- State tracking (`.workflow/state.json`)
- Prerequisite enforcement (`pr-gate.py` hook)

Human-initiated `gh pr create` from a non-worktree terminal is fine — this rule applies to agents only. Humans in a worktree can set `PPDS_PR_GATE_HUMAN=1` to override the hook.

## §2 - Self-Review Subagent

Dispatch the `code-reviewer` agent at `.claude/agents/code-reviewer.md` against `git diff origin/main...HEAD`. Include:
- The Constitution: `specs/CONSTITUTION.md`
- Acceptance criteria: for each issue in `python scripts/workflow-state.py get issues`, fetch AC text via `gh issue view <N>`

Returns findings as DEFECT / CONCERN / NIT. Present findings and ask which to address:
```
Pre-PR self-review: DEFECTs: N  CONCERNs: N  NITs: N
Reply: "F-1, F-3", "all", "defects", or "skip"
```

DEFECTs must be fixed before opening. If user elects to fix, return to implement loop and re-enter `/pr`. Self-review runs again on the updated diff.

**Bypass:** pass `--no-self-review` for trivially small diffs (rename, single-line fix, docs-only).

## §3 - PR Body Template

```
## Summary
<1-3 bullet points>

Closes #NNN

## Test Plan
<bulleted checklist>

## Verification
- [x] /gates passed
- [x] /verify completed (surfaces: ...)
- [x] /qa completed (surfaces: ...)
- [x] /review completed (findings: N)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

Omit `Closes` lines if no linked issues. Title ≤70 chars, conventional commit format.

## §4 - Monitor Details

The monitor handles: CI polling, Gemini review wait (overload detection + retry), CodeQL check wait, triage dispatch, threaded replies, reconciliation, draft→ready conversion, retro, and notification. It runs as a detached background process surviving session exit.

**Why the monitor exists:** Gemini review timing is unpredictable (2–10+ minutes). Inline polling with a fixed timeout creates a gap where late-arriving comments go untriaged.

> **Retro-enforced (PR #868):** Agent skipped the monitor, manually replied to 3 of 9 comments via `gh api`, missed all CodeQL comments. Manual comment triage is never an acceptable substitute.

Launch command: `python scripts/pr_monitor.py --worktree "$(pwd)" --pr {pr-number}`

- **Windows:** `subprocess.Popen(..., creationflags=subprocess.CREATE_BREAKAWAY_FROM_JOB | subprocess.CREATE_NEW_PROCESS_GROUP)`
- **Unix:** `subprocess.Popen(..., start_new_session=True)`

Fallback (monitor can't launch): wait inline, triage yourself, convert to ready. Record reason:
```bash
python scripts/workflow-state.py set pr.monitor_launched "fallback: <reason>"
```

## §5 - Post-Merge Cleanup

After PR merges, cleanup is user-initiated:
1. User sees merged notification (or runs `/status`, observes `pr.state == MERGED`).
2. User runs `/cleanup` manually — presents prunable worktrees/branches for confirmation.

The monitor writes final status to `.workflow/pr-monitor.log` on terminal state (MERGED/CLOSED). It does NOT auto-invoke `/cleanup`.

Future: opt-in `--cleanup-on-merge` flag with merge poller + single confirmation in final notification. Track via separate issue + spec.

## §6 - Error Recovery

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present to user, do NOT auto-resolve |
| Self-review subagent fails | Log; ask user: proceed without self-review or abort |
| PR creation fails | Check `gh auth status`; suggest `gh auth login` |
| Push rejected | Fetch and retry rebase |
| Monitor fails to launch | Inline fallback (§4); record fallback reason |

## §7 - Supervisor Inbox Protocol (Step 0)

At skill entry (Step 0), before rebasing, poll for supervisor directives:
```bash
python scripts/supervisor_msg.py read --consume
```

| Kind | Action |
|------|--------|
| `abort` | Stop immediately; surface abort message to operator; do not create PR |
| `revise` | Apply feedback; stop; let orchestrator re-dispatch with corrections |
| `approve` | Proceed (no change to flow) |
| `note` | Log message; continue |

Empty inbox → proceed normally.
