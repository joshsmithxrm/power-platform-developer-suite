# Start Work

Begin a work session in a prepared worktree by reading the session prompt.

## Usage

`/start-work`

No arguments - reads context from the current directory.

## Process

### 1. Check for Session Prompt

Look for `.claude/session-prompt.md` in the current directory.

If not found:
```
No session prompt found at .claude/session-prompt.md

Options:
1. For issue-driven work: Use /plan-work to create worktree with session prompt
2. For ad-hoc work: Use /create-worktree to set up worktree, then enter plan mode
3. Or manually create .claude/session-prompt.md with your context

Tip: Plan mode is the recommended way to establish session context.
     Enter plan mode and describe what you're working on.
```

### 2. Show Branch Context

Display current branch and working directory status:

```bash
git branch --show-current
git status --short
```

Output format:
```
Branch: feature/phase1-cli-commands
Status: Clean (or: 3 uncommitted changes)
```

### 3. Display Session Prompt

Read and display the full contents of `.claude/session-prompt.md`:

```bash
cat .claude/session-prompt.md
```

### 4. Enter Plan Mode

After displaying session context, automatically enter plan mode:

```
Entering plan mode to verify and plan implementation...
```

Use the EnterPlanMode tool to begin planning. Plan mode will:
1. Verify current code patterns match issue assumptions
2. Check ADR compliance
3. Create an implementation plan for approval

## Output Format

```
================================================================================
WORK SESSION
================================================================================

Branch: feature/phase1-cli-commands
Status: Clean

--------------------------------------------------------------------------------
SESSION CONTEXT
--------------------------------------------------------------------------------

[Contents of .claude/session-prompt.md]

--------------------------------------------------------------------------------

Entering plan mode to verify and plan implementation...
```

Then use the EnterPlanMode tool.

## When to Use

- Starting a new Claude Code session in a prepared worktree
- Resuming work after a break
- Onboarding to a worktree created by someone else

## Related Commands

| Command | Purpose |
|---------|---------|
| `/plan-work` | Triage issues, create worktrees with session prompts |
| `/create-worktree` | Create worktree for ad-hoc work (no issue required) |
| `/pre-pr` | Validate before creating PR |
| `/handoff` | Generate context summary for next session |
| `/prune` | Clean up merged worktrees |
