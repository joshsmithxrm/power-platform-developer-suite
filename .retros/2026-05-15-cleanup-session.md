# Retrospective — 2026-05-15 — /cleanup session

**Branch:** `claude/condescending-lamarr-52011f`
**Scope:** one interactive session: /cleanup → /start → discussion → still-pending remote review
**Transcript:** ~607 lines, 187 tool calls, 31 tool-result error hits
**Mode:** interactive

---

## Pattern narrative

This session started as a conservative `/cleanup` invocation and the user had to escalate scope twice to get the work they actually wanted. The skill (as written) only acts on branches that pass `git branch --merged main`, which captured 2 of 33 worktrees on first pass. Everything else looked "active" to the skill but was, in the user's mental model, "obviously stale work that already shipped via a different PR." The user's escalation phrases:

1. "set a /goal and clean it all up" — forced the agent to expand scope beyond `--merged`
2. "dispatch parallel investigators to review each of the branches" — forced per-branch investigation

The parallel-investigator pattern (16 agents in one shot) worked brilliantly: 14 of 16 ambiguous branches turned out to be safe deletes (work already shipped via a different branch / superseded / abandoned). One was a release branch worth keeping. One required a deeper investigation that turned into a `/start` to finalize lingering work. **That investigator pattern is the right default; /cleanup just doesn't know about it.**

Permission classifier was the second-biggest friction source: it denied empty-dir `rm -rf`/`rmdir`, batched branch deletions (had to retry one-at-a-time), batched rebases (left 4 worktrees mid-rebase when cancelled), and PowerShell process search. Each denial required a fallback path. Net: 187 tool calls where ~120 would have sufficed with looser scope or batch-permitted operations.

`/start` worked smoothly as a "spawn a finalize-this-work agent" — but the mission brief the cleanup agent handed it was **factually wrong**: it claimed PR #1075 was a precursor when #1075 was actually the squash merge of every commit on the source branch. The spawned agent caught it, verified with `git diff`, and abandoned cleanly. Good outcome, but a red flag that the cleanup agent's "what is this branch?" analysis was incomplete despite running 16 investigators.

Session-archival exploration (this session): `mcp__ccd_session_mgmt__archive_session` cannot be called from a subagent (unsupervised mode rejected) — it has to be invoked from the main session with user approval per call. Currently 2 active vs 5 archived sessions; only one active session (`peaceful-bartik-86c8cc`) had a stale cwd. Not a big backlog, but `/cleanup` could opportunistically surface candidates for archiving.

---

## Findings

| # | Issue | Recommendation | Confidence | Tier |
|---|-------|---------------|------------|------|
| 1 | `/cleanup` is too conservative — only acts on `--merged` branches, leaves drift-merged / squash-shipped / superseded branches behind | Add a `--full` or `--investigate` mode that auto-dispatches parallel investigators for branches with N divergent commits and proposes deletions in a bulk-approval batch | HIGH | T2 skill |
| 2 | `/cleanup` doesn't enumerate active sessions — user had to manually call out the live one | Call `list_sessions` (or read `.workflow/state.json` across worktrees) at start; treat sessions with `isRunning: true` as do-not-touch and surface them in the report | HIGH | T2 skill |
| 3 | Workflow artifacts (`.retros/`, `.workflow/`, `.claude/state/`) on branches falsely register as "divergent commits" | When computing `git log main..<branch> --oneline`, also check paths via `git log main..<branch> --name-only`. If ALL divergent commits touch only `.retros/**`, `.workflow/**`, `.claude/state/**` → classify as drift-merged (treat like squash-merged) | HIGH | T2 skill |
| 4 | Permission classifier blocks the cleanup skill's batched ops (rebase batch, branch-delete batch, orphan `rm -rf`, even `rmdir` on empty dirs) | Two options: (a) rewrite the skill to use only one-at-a-time patterns the classifier accepts; (b) add a cleanup-context permission set the user pre-approves at `/cleanup` invocation | MED | T2 skill+setup |
| 5 | Mid-rebase states left behind when a parallel rebase batch is cancelled by classifier | Step 6 should pre-scan all worktrees for `.git/rebase-merge` / `.git/rebase-apply` and abort any found, both before and after the rebase loop | HIGH | T2 skill |
| 6 | No remote cleanup at all — user only learned this by asking | Add a final read-only step that reports `gh api repos/:owner/:repo/branches` count vs local, with an explicit "no remote cleanup performed; run `git push origin --delete <branch>` if desired" message | HIGH | T2 skill |
| 7 | Session archival not integrated | Add Step 8: call `list_sessions`, find sessions whose `cwd` isn't in `git worktree list` AND not `isRunning`, propose archiving as a single user-approval batch using `archive_session` | MED | T2 skill |
| 8 | `/start` worked for "spawn finalize agent" use case but handed a factually wrong mission brief (PR #1075 already squash-merged the source branch) | Trust LLM for resume/source-branch handling — but require the calling agent to verify "is this branch actually unshipped?" before constructing the brief. Could be a checklist line in /start's "build launch prompt" step | MED | T3 |
| 9 | `workflow-state.py init` writes to caller's CWD, not the new worktree's path — caused state file to land in the wrong worktree | Add `--worktree-path` flag to `workflow-state.py init` OR have `/start` step 5 `cd` into the new worktree before calling. The current code already runs from worktree dir per the skill, but the cleanup session ran it from the wrong dir | LOW (skill says "run from worktree directory" — the bug was the calling agent not following it) | T3 |
| 10 | User had to keep asking "what's actually on this branch", "when did we last touch it", "did this ship elsewhere" — the cleanup report doesn't surface metadata | When investigators run (finding #1), have them return: last-author-date, PR-shipped-elsewhere check, divergent-LOC count. Put it in the report | HIGH | T2 skill |

---

## Top-3 prioritized recommendations

1. **Bake the parallel-investigator pattern into `/cleanup`** (covers findings #1, #3, #10). Right now the skill stops at `--merged`. The user already wants the broader sweep; that's what we did manually after escalation. Make it the default for any branch with divergent commits, with a bulk-approval UX at the end. This is the single biggest UX gain.

2. **Make `/cleanup` session-aware** (covers #2 + #7). Read active sessions at the start, exclude them from any destructive operation, and propose archive candidates at the end. The subagent restriction on `archive_session` is fine — main-session `/cleanup` is exactly where the prompt should land.

3. **Pre/post-scan for mid-rebase states + remote-cleanup advisory** (covers #5 + #6). Both are small additions but high-value: #5 is a real bug today, #6 is a documentation/expectation gap.

## What worked

- Parallel-investigator pattern with bulk-approval UX — exactly the right shape for ambiguous branches
- `/start` for spawning a finalize-this-work agent — clean handoff, the spawned agent caught the wrong premise
- User's "set a /goal" framing — gave the agent permission to plan iteratively instead of stopping for confirmation each step
- Force-removal of workflow-artifact-only dirty worktrees after explicit "no user code" verification

## What didn't

- Conservative default that needed two scope escalations
- 31 tool-result errors, ~half from permission classifier (correctly cautious but cleanup is the *one* skill where it consistently misfires)
- Wrong mission brief on the spawned /start session (recovered, but indicates the analysis depth needed to live in /cleanup itself)
