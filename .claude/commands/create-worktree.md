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
3. Configures trust (creates `.claude/settings.local.json`)
4. Opens a new Claude session with bypass permissions enabled

## Process

### 1. Validate Name

- Name must be alphanumeric with hyphens (e.g., `design-auth`, `quick-fix`)
- No spaces or special characters

### 2. Create Worktree

```bash
# Get parent directory of current repo
PARENT_DIR=$(dirname "$(pwd)")
REPO_NAME=$(basename "$(pwd)")
WORKTREE_PATH="${PARENT_DIR}/${REPO_NAME}-{name}"

# Create worktree
git worktree add "${WORKTREE_PATH}" -b {name}
```

### 3. Configure Trust

Create `settings.local.json` to enable autonomous startup without permission prompts:

```bash
# Create .claude directory and settings.local.json
mkdir -p "${WORKTREE_PATH}/.claude"
cat > "${WORKTREE_PATH}/.claude/settings.local.json" << 'EOF'
{
  "permissions": {
    "defaultMode": "bypassPermissions"
  }
}
EOF
```

### 4. Open Claude Session

```bash
# Windows Terminal (Windows)
wt -w 0 nt -d "${WORKTREE_PATH}" --title "{name}" pwsh -NoExit -Command "claude --permission-mode bypassPermissions"

# Or for macOS/Linux (future)
# open -a "Terminal" "${WORKTREE_PATH}"
```

## Output

```
Create Worktree
===============
[✓] Name validated: design-auth
[✓] Worktree created: ../ppds-design-auth
[✓] Branch created: design-auth
[✓] Trust configured: .claude/settings.local.json
[✓] Claude session opened (bypass mode)

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
| Trust configuration | Same | Same |

## Security

This skill creates `.claude/settings.local.json` with `defaultMode: bypassPermissions`.

**Why this is safe:**
- Project-level `.claude/settings.json` deny/allow rules are still enforced
- Dangerous commands (`git push --force`, `git clean`, etc.) remain blocked
- Human PR review is required before any merge
- The file is git-ignored and scoped to that worktree only

**Security layers (same as orchestration):**
| Layer | Mechanism |
|-------|-----------|
| 1 | `settings.local.json` - pre-accepts bypass mode only |
| 2 | `settings.json` - blocks dangerous operations |
| 3 | Human PR review - final gate |

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
