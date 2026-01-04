# Prune Local Branches

Clean up local branches and worktrees that no longer have remote tracking branches.

## Usage

`/prune`

## Steps

### 1. Fetch and Prune Remote References

```bash
git fetch --prune
```

### 2. Identify Branches to Clean

List all branches with their tracking status:

```bash
git branch -vv
```

Branches marked with `: gone]` have had their remote deleted (typically after PR merge).

### 3. List Current Worktrees

```bash
git worktree list
```

### 4. Remove Worktrees for Gone Branches

For each worktree whose branch shows `: gone]`:

```bash
git worktree remove "<path>" --force
```

If removal fails due to permissions, run `git worktree prune` to clean metadata.

### 5. Delete Gone Branches

Delete all branches whose remote tracking branch is gone:

```bash
git branch -D <branch-name>
```

### 6. Verify Final State

```bash
git branch -vv
git worktree list
```

## Output

Report a summary table:

```
Pruned Branches
===============
| Deleted Branch | Last Commit |
|----------------|-------------|
| feature/foo    | abc1234     |
| fix/bar        | def5678     |

Remaining Branches (have remotes):
- main
- feature/active-work

Note: If any worktree directories couldn't be deleted due to permissions,
list them so user can manually delete.
```

## Behavior

1. Fetch with prune to update remote tracking info
2. Identify branches marked as "gone"
3. Remove worktrees first (branches with worktrees can't be deleted)
4. Delete the branches
5. Report what was cleaned up and what remains
6. Note any directories that need manual cleanup

## When to Use

- After merging multiple PRs
- Periodic cleanup of stale branches
- Before starting new work to reduce clutter
