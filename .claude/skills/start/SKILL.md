---
name: start
description: Bootstrap a feature worktree with workflow state. Use when starting new work, beginning a feature, or "let's work on X."
---

# Start

Bootstrap a feature worktree with branch, workflow state, and a Windows Terminal tab.

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

Initialize workflow state in the worktree:

```bash
python scripts/workflow-state.py init "feature/<name>"
```

### 6. Transfer Session Context

Copy the current session to the new worktree's project directory so the conversation
continues with full context:

1. Find the current session JSONL file:
   ```bash
   ls -t ~/.claude/projects/<current-project-path>/*.jsonl | head -1
   ```
   The current project path is derived from `$CLAUDE_PROJECT_DIR` with `/` and `\` replaced by `-` and `:` stripped
   (e.g., `C--VS-ppdsw-ppds` for `C:\VS\ppdsw\ppds`).

2. Determine the target project path encoding for the worktree
   (e.g., `C--VS-ppdsw-ppds--worktrees-<name>`).

3. Create the target directory and copy the session file:
   ```bash
   mkdir -p ~/.claude/projects/<target-project-path>
   cp <source-session-file> ~/.claude/projects/<target-project-path>/
   ```

4. Extract the session ID (filename without `.jsonl` extension).

### 7. Open Windows Terminal Tab

```bash
wt -w 0 new-tab --title "<name>" -d "<absolute-worktree-path>" -- pwsh -NoExit -Command "Set-Location '<absolute-worktree-path>' && claude --resume <session-id>"
```

- `-w 0` adds the tab to the current window (not a new window)
- `--title` shows the worktree name on the tab
- `-d` sets the starting directory (backup for Set-Location)
- `Set-Location` runs after the PowerShell profile loads (the profile may override `-d`)
- `claude --resume <session-id>` continues this conversation with full context
- Each worktree gets its own full-width tab
- Use `&&` not `;` to chain commands (`;` is Windows Terminal's subcommand separator)

If `wt` is not found, warn and continue:
```
⚠ Windows Terminal (wt) not found. Worktree created at .worktrees/<name>.
Resume manually: cd <path> && claude --resume <session-id>
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
