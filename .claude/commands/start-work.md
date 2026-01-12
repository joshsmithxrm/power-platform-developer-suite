# Start Work

Begin a work session by fetching GitHub issues, creating a session prompt, and registering with the orchestrator.

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

Tip: /plan-work provides issue numbers in its output.
```

### 2. Fetch Issue Details

For each issue number:

```bash
gh issue view <number> --json number,title,body,labels
```

### 3. Create Session Status File

Create `~/.ppds/sessions/work-<primary-issue>.json`:

```json
{
  "id": "work-<primary-issue>",
  "status": "working",
  "issues": ["#200", "#202"],
  "branch": "<current-branch>",
  "worktree": "<current-directory>",
  "started": "<ISO-timestamp>",
  "lastUpdate": "<ISO-timestamp>",
  "stuck": null
}
```

This registers the session with the orchestrator for monitoring.

### 4. Write Session Prompt

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

### 5. Show Branch Context

```bash
git branch --show-current
git status --short
```

Output:
```
Branch: feature/import-bugs
Status: Clean
```

### 5b. Check Base Branch Freshness

**Important:** Worktrees may be created from a stale local `main`. Check against `origin/main`:

```bash
# Fetch latest from origin
git fetch origin

# Check how many commits we're behind origin/main
BEHIND_COUNT=$(git rev-list --count HEAD..origin/main)

if [ "$BEHIND_COUNT" -gt 0 ]; then
  echo "WARNING: Branch is $BEHIND_COUNT commits behind origin/main."
  echo "Consider rebasing now to avoid conflicts later:"
  echo "  git rebase origin/main"
fi
```

This early warning helps catch stale branches before significant work is done.

### 6. Display Session Prompt

Output the generated session prompt content.

### 7. Enter Plan Mode

Use the EnterPlanMode tool to begin planning.

**Required Plan Structure** (see [autonomous-session.md](../workflows/autonomous-session.md)):

Your plan MUST include these sections:

1. **My Understanding** - Restate the issue in your own words
2. **Patterns I'll Follow** - Cite specific ADRs and pattern files:
   - `docs/patterns/bulk-operations.cs` - For multi-record operations
   - `docs/patterns/service-pattern.cs` - For Application Services
   - `docs/patterns/tui-panel-pattern.cs` - For TUI development
   - `docs/patterns/cli-command-pattern.cs` - For CLI commands
   - `docs/patterns/connection-pool-pattern.cs` - For Dataverse connections
3. **Approach** - Implementation steps
4. **What I'm NOT Doing** - Explicit scope boundaries
5. **Questions Before Proceeding** - If any

**Example citation:**
```
Following the service layer pattern (docs/patterns/service-pattern.cs):
- Accepting IProgressReporter for long operations
- Throwing PpdsException with ErrorCode
- Registering in AddCliApplicationServices()
```

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

## Session Status Updates

During work, update the session status file when:
- Hitting a domain gate (Auth/Security, Performance) → set `status: "stuck"`
- Encountering blockers → set `status: "stuck"` with context
- Completing work → handled by `/ship`

Example stuck status:
```json
{
  "status": "stuck",
  "stuck": {
    "reason": "Auth/Security decision needed",
    "context": "Token refresh approach unclear",
    "options": ["sliding", "fixed"],
    "since": "<ISO-timestamp>"
  }
}
```

## When to Use

- Starting a new Claude session in a worktree after `/plan-work`
- Resuming work after a break (no arguments if session-prompt.md exists)
- Setting up a worktree for specific issues

## Related Commands

| Command | Purpose |
|---------|---------|
| `/plan-work` | Orchestrator: analyze, plan, spawn sessions |
| `/test` | Run tests with auto-detection |
| `/ship` | Complete work: validate + commit + PR |

## Reference

- [Parallel Work Workflow](.claude/workflows/parallel-work.md)
- [Autonomous Session](.claude/workflows/autonomous-session.md)
