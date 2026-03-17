---
name: start
description: Bootstrap a feature worktree with workflow state. Use when starting new work, beginning a feature, or "let's work on X."
---

# Start

Bootstrap a feature worktree with branch, workflow state, and a Windows Terminal split pane.

## When to Use

- "Let's work on X"
- "Start feature X"
- "I want to implement..."
- Beginning any new feature or task
- SessionStart hook on main suggests this skill

## Process

### 1. Parse Feature Name

Check `$ARGUMENTS` for a feature name (e.g., `/start panel-metadata`).

If no name provided, ask the user: "What feature are you starting?"

### 2. Derive Names

- Strip `feature/` prefix if user included it (avoid `feature/feature/...`)
- Kebab-case the name
- Branch: `feature/<name>`
- Worktree: `.worktrees/<name>`

### 3. Check for Existing Worktree

```bash
git worktree list
```

If a worktree already exists for this branch:
- Report: "Worktree already exists at `.worktrees/<name>` on branch `feature/<name>`."
- Offer to open a split pane to it instead of creating a new one.
- Do NOT create a duplicate.

### 4. Check for Existing Branch

```bash
git branch --list "feature/<name>"
```

- If branch exists (but no worktree): `git worktree add .worktrees/<name> feature/<name>` (no `-b`)
- If branch does not exist: `git worktree add .worktrees/<name> -b feature/<name>`

### 5. Initialize Workflow State

Write `.claude/workflow-state.json` in the worktree:

```json
{
  "branch": "feature/<name>",
  "started": "<ISO 8601 timestamp>"
}
```

### 6. Open Windows Terminal Tab

```bash
wt -w 0 new-tab --title "<name>" -- pwsh -NoExit -Command "Set-Location '<absolute-worktree-path>'"
```

- `-w 0` adds the tab to the current window (not a new window)
- `--title` shows the worktree name on the tab
- `Set-Location` runs after the PowerShell profile loads (the profile may override `-d`)
- Each worktree gets its own full-width tab

If `wt` is not found, warn and continue:
```
⚠ Windows Terminal (wt) not found. Worktree created at .worktrees/<name> — navigate manually.
```

### 7. Print Workflow Guidance

```
✓ Worktree ready: .worktrees/<name>
✓ Branch: feature/<name>
✓ Workflow state initialized

Next steps (new feature):
  1. /design (if no spec exists)
  2. /spec-audit (if spec exists)
  3. /implement <plan-path>

Next steps (bug fix):
  1. Fix the issue
  2. /gates
  3. /pr
```

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| Name already has `feature/` prefix | Strip it — `feature/feature/x` is wrong |
| Worktree path already exists | Report existing worktree, offer to open pane to it |
| Branch exists but no worktree | Use `git worktree add` without `-b` |
| `wt` command not available | Warn, skip terminal, continue |
| On a feature branch (not main) | Still works — creates a new worktree for parallel work |
