# Create Worktree

Create a git worktree and open a new Claude session for ad-hoc work.

## Usage

`/create-worktree <name>` - Create worktree and open Claude session

## Examples

```
/create-worktree design-auth
/create-worktree doc-update
/create-worktree quick-fix
```

## What This Does

1. Creates a new git worktree at `../ppds-{name}`
2. Creates a new branch `{name}` from current HEAD
3. Opens a new Claude session in that directory

## Process

### 1. Validate Name

- Name must be alphanumeric with hyphens (e.g., `design-auth`, `quick-fix`)
- No spaces or special characters

### 2. Create Worktree

```bash
# Get parent directory of current repo
PARENT_DIR=$(dirname "$(pwd)")
REPO_NAME=$(basename "$(pwd)")

# Create worktree
git worktree add "${PARENT_DIR}/${REPO_NAME}-{name}" -b {name}
```

### 3. Open Claude Session

```bash
# Windows Terminal (Windows)
wt -w 0 nt -d "${PARENT_DIR}/${REPO_NAME}-{name}" --title "{name}" pwsh -NoExit -Command "claude"

# Or for macOS/Linux (future)
# open -a "Terminal" "${PARENT_DIR}/${REPO_NAME}-{name}"
```

## Output

```
Create Worktree
===============
[✓] Name validated: design-auth
[✓] Worktree created: ../ppds-design-auth
[✓] Branch created: design-auth
[✓] Claude session opened

You can now work in the new session.
To clean up later: git worktree remove ../ppds-design-auth
```

## Differences from Orchestration

| Feature | `/create-worktree` | `/orchestrate` spawn |
|---------|-------------------|---------------------|
| Session tracking | None | ~/.ppds/sessions/*.json |
| Status updates | None | Heartbeats, stuck reporting |
| Branch naming | User-provided name | issue-{number} |
| GitHub integration | None | Fetches issue context |
| Human oversight | None | Plan review, guidance relay |

## When to Use

- **Design sessions** - Long exploratory work without issue tracking
- **Documentation updates** - Quick doc changes that don't need issues
- **Experiments** - Try something without committing to an issue
- **Parallel design** - Multiple design threads without orchestration overhead

## When NOT to Use

- **Issue implementation** - Use `/orchestrate` for tracked work
- **Anything needing review** - Orchestration provides plan review
- **Team coordination** - Session status helps coordinate

## Cleanup

After work is complete:

```bash
# If merged
git worktree remove ../ppds-{name}
git branch -d {name}

# If abandoned
git worktree remove --force ../ppds-{name}
git branch -D {name}
```

Or use `/prune` to clean up all stale worktrees.
