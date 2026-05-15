---
name: cleanup
description: Clean up merged worktrees and branches, prune stale remotes, rebase active worktrees onto main. Use when switching contexts, before starting new work, or to tidy up.
---

# Cleanup

Enumerate everything in one pass, dispatch parallel investigators for divergent items, auto-classify into SAFE / AMBIGUOUS / PROTECTED, bulk-approve SAFE in ONE prompt, discuss only AMBIGUOUS, execute one-at-a-time.

**Design principle:** default action, not default discussion. Read `REFERENCE.md §1` for the framing.

Phases: PARSE → PULL+PRE-SCAN → GATHER → CLASSIFY → BUCKET → APPROVE-SAFE → DISCUSS-AMBIGUOUS → EXECUTE-LOCAL → REBASE+POST-SCAN → REMOTE-SWEEP → SESSION-ARCHIVE → REPORT.

## 1. Parse args

- `/cleanup` — all phases
- `/cleanup --dry-run` — run reads + the bulk-approval prompt; skip destructive commands

## 2. Pull main + pre-scan mid-rebase

```bash
git fetch origin main && git checkout main && git merge --ff-only origin/main
```

`--ff-only` fail → STOP, report. Do not force-reset.

**Pre-scan (AC-7):** for every worktree in `git worktree list --porcelain`, if `<path>/.git/rebase-merge` or `<path>/.git/rebase-apply` exists, run `git -C <path> rebase --abort`. Mid-rebase leftovers break this run if not cleared.

## 3. Gather (single pass — AC-1)

Run in parallel:

```bash
git worktree list --porcelain
git branch --format='%(refname:short) %(committerdate:iso8601) %(upstream:short)'
git branch --merged main --format='%(refname:short)'
git remote prune origin --dry-run                       # squash signal
git ls-remote --heads origin
git tag --list --sort=-version:refname
gh pr list --state all --limit 500 --json number,headRefName,state,mergedAt,closedAt
gh issue list --state open --limit 500 --json number,title,body
```

Then `mcp__ccd_session_mgmt__list_sessions` (AC-4). Build `pr_by_branch`, `tag_set`, session map. In non-dry-run, execute `git remote prune origin` so reads see cleaned state. `gh` unavailable → warn, skip step 10.

## 4. Classify

Apply rule filters first (cheap, no agent). Canonical mapping in `REFERENCE.md §2 "Rule-filter table"`.

**Workflow-artifact filter (AC-3):**

```bash
git log main..<branch> --name-only --pretty=format: | sort -u
```

If every non-empty path matches `^(\.retros/|\.workflow/|\.claude/state/)` → SAFE (drift-merged).

**Release tag-lineage (AC-6):** for `release/*`, derive candidate tag (`release/v1.0.0` → try `Cli-v1.0.0`, `Tui-v1.0.0`, `v1.0.0`):

```bash
git merge-base --is-ancestor <tag> <branch>
```

Exit 0 = shipped = PROTECTED. Non-zero = NOT shipped = AMBIGUOUS. Never auto-SAFE a release branch.

**Dispatch parallel investigators (AC-2)** for every branch with divergent commits not classified by rules. Issue all in a single `Agent` message (Lane A, read-only). Prompt template in `REFERENCE.md §3 "Investigator brief"`. Each returns `{last_author_date, divergent_commits, divergent_loc, paths_touched, ships_elsewhere, summary, recommendation, confidence, rationale}`. Collect before bucketing.

## 5. Bucket

Combine rule outputs + investigator returns into SAFE / AMBIGUOUS / PROTECTED. Full mapping in `REFERENCE.md §4 "Bucketing rules"`.

**Stale-lock recovery before final bucketing:** parse PID from each locked worktree's reason; PID-alive check commands in `REFERENCE.md §5 "Stale-lock detection"`. Dead PID → `git worktree unlock <path>`, reclassify by rules. No PID in reason → PROTECTED. In `--dry-run`, record but do not unlock.

## 6. Approve SAFE bulk (AC-10)

ONE message: counts + SAFE list grouped by reason. Ask: **"Approve SAFE bucket?"** Options: `yes` / `no` / `show <branch>`. The only bulk prompt of the run.

## 7. Discuss AMBIGUOUS

Per item: branch + last-author-date + divergent-LOC + investigator summary + lean + one-sentence ask. Cap at 5; if more, re-dispatch investigators with sharper briefs (interaction-patterns §4).

## 8. Execute LOCAL (one-at-a-time — AC-8)

Permission classifier denies batched destructive ops. Process sequentially: never batch `branch -d`, parallel `rebase`, or `rm -rf` across paths.

For each approved worktree (skip locked, skip main), in order:

1. **Kill zombies** referencing the **full worktree path** (not just dirname). Commands in `REFERENCE.md §6 "Zombie + daemon shutdown"`. Wait 2 s.
2. **Shut down daemons** — find `*-session.json` in the worktree, POST `/shutdown` to `daemonPort`, fall back to killing `daemonPid`, delete the session file.
3. **Remove worktree:** `git worktree remove --force <path>`. NEVER remove main. Permission-denied after zombie kill → log "partially removed", do not retry.
4. **Delete branch:** `git branch -d <branch>` for regular-merged; `git branch -D <branch>` for squash / drift / investigator-DELETE.
5. **Orphan sweep:** list `.worktrees/*/`; for each not registered in step 3 porcelain output, guard against main path, kill zombies, `rm -rf`. Windows junctions: remove the link, do not follow target. Skip in `--dry-run`.
6. **In-flight deregister** (for every removed branch): `python scripts/inflight-deregister.py --branch <branch>`, then once `python scripts/inflight-check.py --area scripts/ >/dev/null 2>&1 || true` to sweep stale registry entries.

## 9. Rebase active + post-scan (AC-7)

For each remaining active worktree: `git -C <path> rebase origin/main` sequentially. Conflict → `rebase --abort`, record, continue.

**Post-scan:** re-check every worktree for `.git/rebase-merge` / `.git/rebase-apply`. Abort any found. Catches cancelled-mid-batch leftovers.

## 10. Remote sweep (AC-5 + AC-6)

Skip if `gh` unavailable. Classify each remote per `REFERENCE.md §7 "Remote-sweep classification"`. Present remote SAFE as a second bulk-approval prompt, then delete one-at-a-time:

```bash
git push origin --delete <branch>
```

Remote AMBIGUOUS → per-item prompts. `release/*` failing tag-lineage are AMBIGUOUS by default.

## 11. Session archive batch (AC-4)

Select sessions where `isRunning: false` AND `cwd` not in `git worktree list`. Present batch: "Archive N stale sessions?" → yes/no. On approval, call `mcp__ccd_session_mgmt__archive_session` once per session. **Main-session only — do not delegate to a subagent** (the MCP tool rejects unsupervised mode). `--dry-run` lists only.

## 12. Report

Use the template in `REFERENCE.md §8 "Final report"`. Prefix title with `[DRY RUN]` when applicable.

## Error handling

Full recovery table in `REFERENCE.md §9 "Error catalogue"`.
