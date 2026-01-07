# Session Handoff

Generate a context summary for handoff between Claude Code sessions.

## Usage

`/handoff`

## Output

Creates a handoff summary with:

### 1. Current State
- Which repo(s) you're working in
- Branch name and status (`git status`)
- Recent commits (`git log --oneline -5`)
- Uncommitted changes summary

### 2. Work Completed This Session
- Features/fixes implemented
- Issues created or updated
- Files created or significantly modified
- Decisions made

### 3. Work In Progress
- Current task (if incomplete)
- Blockers or open questions
- Decisions deferred to user

### 4. Next Steps
- Immediate next actions
- Which repo to work in next
- Pending items from todo list
- Suggested priority order

### 5. Key Context
- Important decisions made
- Cross-repo implications
- Related issues/PRs created

## Format

Output as markdown that can be:
1. Copied to a new session's first message
2. Saved to `docs/work/handoff-[date].md` (if requested)

## Example Output

```markdown
## Session Handoff - 2025-01-15

### Repos Touched
- sdk/ (feature/plugin-registration-cli)
- extension/ (main)

### Completed
- Created CLI commands issue (sdk#70)
- Created extension migration issue (extension#95)
- Updated workspace settings for issue permissions

### In Progress
- Documentation cleanup discussion ongoing

### Blockers
- None

### Next Steps
1. Decide on extension .claude cleanup
2. Start SDK CLI commands implementation
3. Update extension to use CLI backend

### Key Decisions
- Using process invocation (not daemon) for plugin registration
- Extension becomes UI shell, SDK has logic
```

## Notes

- Run before `/clear` or ending a session
- Captures context that would otherwise be lost
- Especially useful for cross-repo work where state spans multiple folders
