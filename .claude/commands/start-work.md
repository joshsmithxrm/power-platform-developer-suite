# Start Work

Begin a work session by fetching GitHub issues and creating a session prompt.

## Usage

`/start-work <issue-numbers...>`

Examples:
- `/start-work 200 202` - Fetch issues #200 and #202, create session prompt
- `/start-work 276 277 278 279 280` - Fetch multiple related issues

## Arguments

`$ARGUMENTS` - Space-separated issue numbers (required for new sessions)

## Process

### 1. Parse Arguments

If issue numbers provided, go to step 2.

If no arguments:
- Check if `.claude/session-prompt.md` exists
- If exists: read and display it, then enter plan mode
- If not exists: show usage error

```
No session prompt found and no issue numbers provided.

Usage: /start-work <issue-numbers>
Example: /start-work 200 202

Tip: /next-work provides issue numbers in its output.
```

### 2. Fetch Issue Details

For each issue number:

```bash
gh issue view <number> --json number,title,body,labels
```

### 3. Write Session Prompt

Create `.claude/session-prompt.md` with fetched issue context:

```markdown
# Session: <inferred-title-from-issues>

## Issues
- #<num>: <title>
- #<num>: <title>

## Context

<issue body content, cleaned up>

## First Steps
1. Explore the codebase to understand current implementation
2. Enter plan mode to design the approach
```

### 4. Show Branch Context

```bash
git branch --show-current
git status --short
```

Output:
```
Branch: feature/import-bugs
Status: Clean
```

### 5. Display Session Prompt

Output the generated session prompt content.

### 6. Enter Plan Mode

Use the EnterPlanMode tool to begin planning.

## Output Format

```
================================================================================
WORK SESSION
================================================================================

Branch: feature/import-bugs
Status: Clean

--------------------------------------------------------------------------------
SESSION CONTEXT
--------------------------------------------------------------------------------

[Generated session prompt content]

--------------------------------------------------------------------------------

Entering plan mode to verify and plan implementation...
```

## When to Use

- Starting a new Claude session in a worktree after `/next-work`
- Resuming work after a break (no arguments if session-prompt.md exists)
- Setting up a worktree for specific issues

## Related Commands

| Command | Purpose |
|---------|---------|
| `/next-work` | Get recommendations and create worktrees |
| `/create-worktree` | Create worktree for ad-hoc work |
| `/pre-pr` | Validate before creating PR |
| `/handoff` | Generate context summary for next session |
