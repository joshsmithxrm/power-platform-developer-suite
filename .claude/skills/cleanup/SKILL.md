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

### 3. Identify Merged Branches

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

Build four lists:
- **Merged worktrees:** worktrees whose branch appears in the merged list (after filtering)
- **Active worktrees:** worktrees whose branch does NOT appear in the merged list
- **Not started:** branches/worktrees removed from the merged list by the divergence check — report as skipped
- **Locked worktrees:** worktrees with `locked` attribute in porcelain output — skip regardless of merge status

### 4. Remove Merged Worktrees

For each merged worktree (not locked, not the main worktree):

```bash
git worktree remove --force .worktrees/<name>
```

`--force` handles worktrees with uncommitted changes. Report each removal including whether force was needed.

**Safety rules:**
- NEVER remove the main worktree (the repo root)
- Skip locked worktrees — report them as skipped with reason
- Report any removal failures and continue with the next worktree

### 5. Delete Merged Local Branches

After worktrees are removed, delete their local branches:

```bash
git branch -d <branch-name>
```

Use `-d` (not `-D`) as a safety check — git will refuse if the branch is not actually merged. If `-d` fails, log the error and continue.

### 6. Prune Stale Remote Tracking Branches

```bash
git remote prune origin
```

Capture output to report which remote refs were pruned.

### 7. Rebase Active Worktrees

For each remaining (non-locked) active worktree:

```bash
git -C <worktree-path> rebase origin/main
```

If rebase produces conflicts:
1. Run `git -C <worktree-path> rebase --abort` immediately
2. Record the worktree as "rebase conflict" in the report
3. Continue to the next worktree

Do NOT leave any worktree in a mid-rebase state.

### 8. Final Report

Present a summary:

```
## Cleanup Report

### Removed (merged)
| Worktree | Branch | Forced? |
|----------|--------|---------|
| .worktrees/foo | feature/foo | No |
| .worktrees/bar | feature/bar | Yes (uncommitted changes) |

### Deleted Branches (no worktree)
- feature/old-thing
- fix/stale-fix

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
- Removed: N worktrees, N branches
- Pruned: N remote refs
- Rebased: N OK, N conflicts
- Skipped: N locked, N not started
```

If `--dry-run` was specified, prefix the report title with `[DRY RUN]` and note that no changes were made.

## Error Handling

| Error | Recovery |
|-------|----------|
| `merge --ff-only` fails on main | STOP — report that main has diverged, do not proceed |
| Worktree removal fails | Log error, continue with next worktree |
| `branch -d` fails | Log error — branch may not actually be merged, skip it |
| Rebase conflict | `git rebase --abort`, record in report, continue |
| Locked worktree | Skip with note in report |
| Not on main branch | `git checkout main` before starting |
