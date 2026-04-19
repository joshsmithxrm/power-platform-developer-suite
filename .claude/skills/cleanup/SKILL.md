---
name: cleanup
description: Clean up merged worktrees and branches, prune stale remotes, rebase active worktrees onto main. Use when switching contexts, before starting new work, or to tidy up.
---

# Cleanup

Remove merged worktrees and branches, prune stale remote tracking refs, and rebase remaining active worktrees onto main.

## When to Use

- Before starting new feature work
- After a PR is merged
- "Clean up my branches"
- "Tidy up worktrees"
- Periodic housekeeping

## Process

### 1. Parse Arguments

Check `$ARGUMENTS` for flags:

- `/cleanup` (no args) — execute all phases
- `/cleanup --dry-run` — preview what would be cleaned without acting

If `--dry-run` is set, skip all destructive commands but still run all read-only commands to produce the preview report.

### 2. Pull Latest Main

```bash
git fetch origin main
git checkout main
git merge --ff-only origin/main
```

If `merge --ff-only` fails, main has local commits not on origin. STOP and report — do not force-reset main.

### 3. Prune Stale Remotes and Classify Branches

Prune first so we know which remote branches were deleted (indicating merged PRs):

```bash
git remote prune origin --dry-run   # capture what will be pruned (lines like "* [would prune] origin/feature/foo")
```

If `--dry-run` mode is **not** active, execute the actual prune:

```bash
git remote prune origin             # actually prune
```

If `--dry-run` mode **is** active, skip the actual prune — use the `--dry-run` output for classification only.

Save the list of pruned refs (extract the ref name after `[would prune]`, e.g., `origin/feature/foo`) for squash-merge detection below.

Now identify merged branches:

```bash
# Machine-parseable worktree list
git worktree list --porcelain

# Branches merged into main (excluding main itself)
git branch --merged main | grep -v '^\*\?\s*main$'
```

For each branch in the merged list, check for divergent commits:

```bash
git log main..<branch> --oneline
```

If this produces **no output**, the branch has zero commits beyond main — it was created for future work or was fast-forward merged. Remove it from the merged list and classify it as **"not started"** (to be skipped).

**Squash-merge detection (freshly pruned only):** For each local branch (whether or not it has a worktree) that is NOT in the merged list **and NOT classified as "not started"**, check if `origin/<branch>` appears in the pruned refs list from the prune command above. If so, classify as **squash-merged**.

**Do NOT infer squash-merge from a missing remote tracking ref alone.** A missing remote could mean the branch was pruned in a prior session, OR that the branch was never pushed (in-flight local work) — these are indistinguishable after the fact. Deleting in-flight work is catastrophic; leaving a stale branch is harmless. If a branch has no remote tracking ref and wasn't freshly pruned this run, classify as **active** and leave it alone.

Squash-merged detection only fires for branches whose remote was pruned in *this* run's `git remote prune`. Branches whose remotes were pruned in a previous session without being cleaned up will stay around indefinitely — that's the safe trade-off. Treat squash-merged the same as merged for worktree removal, but track separately for branch deletion (step 5).

Build five lists:
- **Merged:** branches in the `--merged` list (after filtering) — with or without worktrees
- **Squash-merged:** branches detected via the pruned-remote heuristic — with or without worktrees
- **Active:** branches NOT merged, NOT squash-merged, NOT "not started"
- **Not started:** branches removed from the merged list by the divergence check — report as skipped
- **Locked worktrees:** worktrees with `locked` attribute in porcelain output — skip regardless of merge status

### 4. Remove Merged Worktrees

Process worktrees **one at a time, sequentially** — do NOT run removals in parallel. A single failure in a parallel batch cancels all sibling calls.

For each merged or squash-merged worktree (not locked, not the main worktree):

**Before removal, shut down any running daemons:**

1. Search for `*-session.json` files in the worktree (e.g., `tests/PPDS.Tui.E2eTests/tools/.tui-verify-session.json`)
2. For each session file found:
   - Read `daemonPort` and `daemonPid` from the JSON
   - Send `POST http://localhost:{port}/shutdown` (timeout 5s)
   - If the HTTP request fails, kill the PID directly: `taskkill /PID {pid} /F` (Windows) or `kill {pid}` (Unix)
   - Delete the session file
3. Proceed with worktree removal after daemons are shut down

```bash
git worktree remove --force .worktrees/<name>
```

`--force` handles worktrees with uncommitted changes. Report each removal including whether force was needed.

**Safety rules:**
- NEVER remove the main worktree (the repo root)
- Skip locked worktrees — report them as skipped with reason
- Report any removal failures and continue with the next worktree

### 4b. Sweep Orphan Directories

After removing merged worktrees, check for orphan directories — directories in `.worktrees/` that are not registered git worktrees (e.g., left behind when `git worktree remove` deregistered but failed to delete the directory).

1. List all directories in `.worktrees/`:
   ```bash
   ls -d .worktrees/*/ 2>/dev/null
   ```

2. Compare against registered worktree paths from `git worktree list --porcelain` (already parsed in step 3). Extract the `worktree` lines to get the full path of each registered worktree.

3. For each directory in `.worktrees/` that does NOT appear in the registered worktree list:
   - **Guard:** Compare the directory's resolved absolute path against the main worktree path (the first `worktree` entry in `git worktree list --porcelain`). If they match, skip it — never remove the main worktree.
   - If `--dry-run`: add to orphan report, do NOT delete
   - Otherwise: `rm -rf .worktrees/<name>`
   - On Windows, if the orphan is a junction/symlink, remove the link itself — do not follow into the target directory

4. If `rm -rf` fails (e.g., permission denied), log as failed in the report and continue with the next orphan.

### 5. Delete Local Branches

After worktrees are removed, delete their local branches:

**Regular-merged branches** (detected by `--merged`):

```bash
git branch -d <branch-name>
```

Use `-d` as a safety check — git will refuse if the branch is not actually merged.

**Squash-merged branches** (detected by pruned-remote heuristic):

```bash
git branch -D <branch-name>
```

Use `-D` because git cannot see squash-merge ancestry, so `-d` will always refuse. The pruned-remote heuristic provides a strong signal that the branch is safe to delete.

If either command fails, log the error and continue.

### 6. Rebase Active Worktrees

Process worktrees **one at a time, sequentially** — do NOT run rebases in parallel. A single conflict in a parallel batch cancels all sibling calls.

For each remaining (non-locked) active worktree:

```bash
git -C <worktree-path> rebase origin/main
```

If rebase produces conflicts:
1. Run `git -C <worktree-path> rebase --abort` immediately
2. Record the worktree as "rebase conflict" in the report
3. Continue to the next worktree

Do NOT leave any worktree in a mid-rebase state.

### 7. Final Report

Present a summary:

```
## Cleanup Report

### Removed (merged)
| Worktree | Branch | Merge Type | Forced? |
|----------|--------|------------|---------|
| .worktrees/foo | feature/foo | regular | No |
| .worktrees/bar | feature/bar | squash (used -D) | Yes (uncommitted changes) |

### Deleted Branches (no worktree)
- feature/old-thing (regular → -d)
- fix/stale-fix (squash → -D)

### Orphans Removed
| Directory | Status |
|-----------|--------|
| .worktrees/old-thing | Removed |
| .worktrees/stale-dir | Failed (permission denied) |

### Pruned Remote Refs
- origin/feature/old-thing
- origin/fix/stale-fix

### Rebased (active)
| Worktree | Branch | Result |
|----------|--------|--------|
| .worktrees/baz | feature/baz | OK |
| .worktrees/qux | feature/qux | Conflict — aborted |

### Skipped
| Worktree | Branch | Reason |
|----------|--------|--------|
| .worktrees/wip | feature/wip | Locked |
| .worktrees/prep | feature/prep | No divergent commits (not started) |

### Summary
- Removed: N worktrees (N regular, N squash-merged), N branches
- Orphans: N removed, N failed
- Pruned: N remote refs
- Rebased: N OK, N conflicts
- Skipped: N locked, N not started
```

If `--dry-run` was specified, prefix the report title with `[DRY RUN]` and note that no changes were made.

## Error Handling

| Error | Recovery |
|-------|----------|
| `merge --ff-only` fails on main | STOP — report that main has diverged, do not proceed |
| `git remote prune origin` fails | Log warning, continue — classification will still work via `--merged` |
| Worktree removal fails (Permission denied) | Log as "partially removed — directory locked by another process", continue with next worktree. Do NOT retry with `rm -rf`. |
| Worktree removal fails (other) | Log error, continue with next worktree |
| `branch -d` fails (regular-merged) | Log error — branch may not actually be merged, skip it |
| `branch -D` fails (squash-merged) | Log error — unexpected, report for manual investigation |
| Rebase conflict | `git rebase --abort`, record in report, continue |
| Locked worktree | Skip with note in report |
| Not on main branch | `git checkout main` before starting |
| Branch has no remote tracking ref and is not in `--merged` | Classify as active — no signal to determine merge status |
| `rm -rf` fails on orphan directory (permission denied) | Log as failed in report, continue with next orphan |
| `.worktrees/` directory doesn't exist | Skip orphan sweep — nothing to check |
| Orphan is a symlink/junction (Windows) | Remove the link itself, do not follow into the target |
