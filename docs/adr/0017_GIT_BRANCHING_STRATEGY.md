# ADR-0017: Git Branching Strategy

**Status:** Accepted
**Date:** 2026-01-07
**Authors:** Josh, Claude

## Context

The power-platform-developer-suite repository uses git worktrees for parallel development. Without documented conventions:
- Branch names are inconsistent
- Worktree locations vary
- Unclear when to create worktrees vs branches
- PR workflow not standardized

## Decision

### Branch Naming

| Prefix | Purpose | Example |
|--------|---------|---------|
| `feature/` | New functionality | `feature/plugin-traces` |
| `fix/` | Bug fixes | `fix/bypass-plugins` |
| `docs/` | Documentation only | `docs/file-format-policy` |
| `chore/` | Maintenance, refactoring | `chore/pre-pr-cleanup` |
| `release/` | Release preparation | `release/v1.0.0` |

### Worktree Strategy

Use worktrees for parallel, isolated work. Each worktree is a separate directory with its own branch.

**Location:** `{base}/ppds-{branch-suffix}` (sibling to main `ppds` folder)

```
{base}/
├── ppds/                       # Main repo (main branch)
├── ppds-plugin-traces/         # feature/plugin-traces
├── ppds-tui-enhancements/      # feature/tui-enhancements
└── ppds-file-format-adr/       # docs/file-format-policy
```

**When to use worktrees:**
- Work spans multiple sessions
- Need to context-switch between features
- Large feature requiring isolation

**When to use simple branches:**
- Quick fixes (< 1 hour)
- Single-session work
- No need to switch away

### Creating Worktrees

**For issue-driven work:** Use `/plan-work`
```
/plan-work 123 456
```
- Fetches issues from GitHub
- Creates worktrees with session prompts
- Session prompt includes verification checklist and research hints
- Then use `/start-work` in the worktree to begin

**For ad-hoc work:** Use `/create-worktree`
```
/create-worktree "add authentication caching"
/create-worktree "fix null pointer in bulk ops" --fix
/create-worktree "update docs" --issue  # also creates GitHub issue
```
- No GitHub issue required
- Infers branch type from description
- Then enter plan mode in the worktree to establish context

### Worktree Lifecycle

```bash
# Option 1: Issue-driven (recommended for tracked work)
/plan-work 123          # In ppds/ orchestrator
cd ../ppds-feature-x
/start-work             # Displays session context
# Enter plan mode to verify and plan

# Option 2: Ad-hoc (for untracked work)
/create-worktree "description"  # In ppds/ orchestrator
cd ../ppds-xxx
# Enter plan mode to establish context

# Work, commit, push
git push -u origin feature/{name}
gh pr create

# After PR merges, clean up
/prune                  # Or: git worktree remove ../ppds-{name}
```

### Plan Mode as Session Context

Plan mode is the recommended way to establish session context in any worktree:

**Issue-driven worktrees:**
- Session prompt created by `/plan-work` contains verification checklist
- Verification ensures issue approach is still valid for current codebase
- Plan mode researches, verifies, then creates implementation plan

**Ad-hoc worktrees:**
- No session prompt - user describes work in plan mode
- Plan mode researches codebase and creates implementation plan
- Plan file becomes the session context

**Why plan mode?**
- Issues can be stale - created based on older codebase state
- Plan mode verifies approach against current patterns and ADRs
- User approval before implementation prevents wasted effort

### Main Branch Protection

- `main` is protected; direct commits blocked
- All changes require PR with passing CI
- Squash merge preferred for clean history

### PR Conventions

1. **Title:** `type: description` (e.g., `feat: add plugin traces CLI`)
2. **Body:** Include "Closes #123" for linked issues
3. **CI:** All checks must pass
4. **Review:** Required for non-docs changes

## Consequences

### Positive
- Consistent branch naming aids navigation
- Worktrees enable true parallel development
- Clear lifecycle prevents orphaned worktrees
- Protected main ensures stability

### Negative
- More disk space for multiple worktrees
- Must remember to clean up after merge

### Neutral
- Existing workflows unchanged
- Optional adoption of worktree pattern
