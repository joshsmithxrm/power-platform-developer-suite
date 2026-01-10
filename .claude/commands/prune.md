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

If removal fails with exit code 255 "Directory not empty", a NUL file may be blocking deletion. Remove it with:

```bash
rm -rf "<path>"
```

Then run `git worktree prune` to clean metadata.

### 5. Clean Empty/Orphaned Worktree Directories

After removing worktrees, scan the parent directory for leftover empty folders:

```bash
# Get parent directory of current repo
parent_dir="$(dirname "$(pwd)")"

# Find ppds-* directories that might be orphaned worktrees
for dir in "$parent_dir"/ppds-*; do
  [ -d "$dir" ] || continue

  # Skip if .git is a directory (it's a full repo, not a worktree)
  [ -d "$dir/.git" ] && continue

  # Check if directory is completely empty
  if [ -z "$(ls -A "$dir" 2>/dev/null)" ]; then
    rmdir "$dir"
    echo "Removed empty directory: $dir"
  # Or has only .git file remnant / .claude folder (orphaned worktree)
  elif [ -f "$dir/.git" ] || [ "$(ls -A "$dir")" = ".claude" ]; then
    # Interactive: ask user before deleting partial content
    echo "Found orphaned worktree directory with partial content: $dir"
    # Prompt user for confirmation before removing
  fi
done
```

**Detection logic:**
- `.git` is a **file** → it's a worktree (safe to consider for cleanup)
- `.git` is a **directory** → it's a full repo (ppds-alm, ppds-docs, etc.) → NEVER touch

**Cleanup behavior:**
- Completely empty directories → auto-delete silently
- Directories with partial content (.git remnant, .claude only) → prompt user for confirmation

### 6. Delete Gone Branches

Delete all branches whose remote tracking branch is gone:

```bash
git branch -D <branch-name>
```

### 7. Update Main Branch

Pull main to stay up-to-date:

```bash
git pull
```

### 8. Verify Final State

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

Cleaned Directories
===================
- Removed empty directory: ../ppds-issue-123
- Removed empty directory: ../ppds-issue-456

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
4. Clean empty/orphaned worktree directories (auto-delete empty, prompt for partial)
5. Delete the branches
6. Pull main to stay up-to-date
7. Report what was cleaned up and what remains

## When to Use

- After merging multiple PRs
- Periodic cleanup of stale branches
- Before starting new work to reduce clutter
