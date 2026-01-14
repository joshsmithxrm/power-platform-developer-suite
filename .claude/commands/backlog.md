# Backlog

View current bugs, ready work, and project health. Use as a dashboard or to start planning sessions.

## Usage

`/backlog [options]`

Examples:
- `/backlog` - Summary view of current repo
- `/backlog --full` - Detailed listing with all issues
- `/backlog --plan` - Start a planning session with backlog context
- `/backlog --all` - Cross-repo ecosystem view

## Arguments

`$ARGUMENTS` - Options (see below)

## Options

| Option | Description |
|--------|-------------|
| `--full` | Show all issues in each category, not just top 5 |
| `--plan` | After showing summary, start planning conversation |
| `--all` | Cross-repo view of all PPDS repositories |
| `--bugs` | Show only bugs |
| `--ready` | Show only ready-for-work items |

## Process

### 1. Fetch Backlog Data

Run the CLI command to get backlog data:

```bash
ppds backlog $ARGUMENTS
```

The CLI handles:
- GitHub API calls (GraphQL for project fields, REST for PRs)
- Caching (5-minute TTL in ~/.ppds/cache/backlog.json)
- Cross-repo aggregation (single project tracks all repos)

Display the CLI output to the user.

### 2. Planning Conversation (--plan only)

If `--plan` was specified, after showing the summary, continue to a planning conversation:

```
Based on the backlog, what would you like to focus on?

Options:
1. Start with critical bugs first
2. Pick up some quick wins (small, ready items)
3. Work on specific issues (provide numbers)
4. Run /triage first to process untriaged items
5. Something else

What's your priority?
```

**STOP for user input.**

Based on their response, chain to the appropriate next command:
- Specific issues: `/plan-work <issue numbers>`
- Triage: `/triage`
- Continue planning conversation

## Integration with Other Commands

| After /backlog | Next Command | Purpose |
|----------------|--------------|---------|
| See bugs | `/plan-work 199 200` | Plan bug fixes |
| See untriaged | `/triage` | Process new issues |
| See ready work | `/plan-work --batch <name>` | Plan a batch |
| Use --plan | Natural conversation | Discuss priorities |

## When to Use

- **Daily standup** - Quick view of project state
- **Sprint planning** - See what's ready to work on
- **Prioritization** - Compare bugs vs features
- **Start of session** - "What should I work on?"

## Related Commands

| Command | Purpose |
|---------|---------|
| `/triage` | Process untriaged issues |
| `/plan-work` | Create worktrees for issues |
| `/design` | Start design conversation |
