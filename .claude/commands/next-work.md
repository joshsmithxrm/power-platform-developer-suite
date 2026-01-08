# Next Work

Analyze project state and recommend next parallel workstreams based on milestone, dependencies, and current progress.

## Usage

`/next-work [--milestone <name>] [--count <n>]`

Examples:
- `/next-work` - Recommend from v1.0 milestone (default)
- `/next-work --milestone v1.1` - Recommend from v1.1
- `/next-work --count 2` - Recommend 2 workstreams instead of 3

## Arguments

| Argument | Default | Description |
|----------|---------|-------------|
| `--milestone` | `v1.0` | Which milestone to pull work from |
| `--count` | `3` | Number of parallel workstreams to recommend |

## Process

### 1. Check Current State

**List active worktrees:**
```bash
git worktree list
```

**List open PRs:**
```bash
gh pr list --state open --json number,title,headRefName
```

**Infer in-progress work:**
- Worktree branches that aren't `main`
- Match branch names to issues (e.g., `feature/plugin-traces` → #140)
- Note: Some worktrees may be stale - check last commit date

**Output:**
```markdown
## Currently In Progress

| Worktree | Branch | Issues | Last Activity |
|----------|--------|--------|---------------|
| ppds-logging-observability | feature/logging-observability | #266-280 | 2 hours ago |
| ppds-tui-mvp | feature/tui-sql-mvp | #234 | 30 min ago |

Open PRs: 2 (#283, #285)
```

### 2. Query Available Work

**Fetch milestone issues:**
```bash
gh issue list --milestone "<milestone>" --state open --json number,title,labels,body --limit 100
```

**Parse dependencies from issue bodies:**
Look for patterns:
- "Blocked by #X" or "Depends on #X"
- "After #X"
- "Requires #X"

Check if blocking issues are closed (batch query to avoid N+1 API calls):
```bash
# Collect all unique blocking issue numbers, then check in one GraphQL call
gh api graphql -f query='
  query {
    repository(owner: "{owner}", name: "{repo}") {
      issues(first: 100, states: [OPEN, CLOSED], filterBy: {}) {
        nodes { number state }
      }
    }
  }
' --jq '.data.repository.issues.nodes | map({(.number|tostring): .state}) | add'

# Returns: {"52": "CLOSED", "78": "OPEN", ...}
# Filter locally to check which blocking issues are resolved
```

**Categorize issues:**
```markdown
## Available Work (v1.0)

### Ready (not blocked, not in progress)
- #254: Extract IDataMigrationService [area:services]
- #257: Extract ISolutionService [area:services]
- #140: Add plugintraces command [area:cli]
- #281: Build MCP Server [area:mcp]
...

### Blocked
- #290: VS Code extension scope (blocked by: TUI MVP not complete)

### Already In Progress
- #266-280: Logging & Observability (ppds-logging-observability)
- #234: TUI SQL table abstraction (ppds-tui-mvp)
```

### 3. Group by Logical Area

Group ready issues by label or inferred area:

```markdown
## Logical Groupings

### Services (9 issues)
#254, #257, #259, #260, #261, #262, #263, #264, #265
- Pattern: ADR-0015 service extraction
- Enables: TUI panels, MCP tools
- Effort: Medium each, highly parallelizable within group

### MCP Server (1 issue)
#281
- New project: PPDS.Mcp
- Enables: Claude Code integration
- Effort: Large, but well-defined phases

### Plugin Traces (2 issues)
#140, #247
- New command group: ppds plugintraces
- Enables: Debugging workflow
- Effort: Medium

### Web Resources (6 issues)
#141, #159, #160, #161, #162, #163
- New command group: ppds webresources
- Enables: Extension parity
- Effort: Medium-Large

### SQL DML (1 issue)
#235
- Parser enhancement: UPDATE/DELETE/INSERT
- Enables: Full SQL support
- Effort: Large (parser changes)

### Bug Fixes (2 issues)
#200, #202
- Quick wins, should be prioritized
- Effort: Small-Medium
```

### 4. Apply Ranking Heuristics

**Priority order:**
1. Issues with `v1-blocker` label
2. Issues with `priority:critical` or `P0-Critical`
3. Bug fixes (quick wins, user-facing impact)
4. Enabler work (services that unblock other work)
5. Issues with `priority:high` or `P1-High`
6. New features by size (smaller first for momentum)

**Parallelism considerations:**
- Don't recommend two groups that modify the same files
- Prefer independent workstreams
- Balance quick wins with substantial progress

### 5. Generate Recommendations

```markdown
## Recommended Next Workstreams

Based on v1.0 milestone, current progress, and dependencies:

### 1. Service Extractions (HIGH PRIORITY)
**Issues:** #254, #257, #259, #260, #261, #262, #263, #264, #265
**Why:** Enables TUI panels AND MCP tools. Unblocks two other workstreams.
**Effort:** 9 issues, but mechanical pattern (ADR-0015). Can batch.
**Branch:** `feature/service-extractions`

### 2. MCP Server (HIGH VALUE)
**Issues:** #281
**Why:** Claude Code integration - high visibility, well-defined scope.
**Effort:** 1 large issue with clear phases. Can start infrastructure now.
**Branch:** `feature/mcp-server`
**Note:** Some tools need services from #1, but can build infra in parallel.

### 3. Plugin Traces (QUICK WIN)
**Issues:** #140, #247
**Why:** Complete new feature, small scope, useful for debugging.
**Effort:** 2 issues, straightforward implementation.
**Branch:** `feature/plugin-traces`

---

**Alternative considerations:**
- Bug fixes (#200, #202) could be a quick parallel session
- Web Resources (#141, #159-163) if you want extension parity sooner
- SQL DML (#235) if you want CLI feature-complete sooner

---

Create these 3 worktrees? (yes / modify / skip)
```

### 6. Handle User Response

**If "yes" or confirmed:**
Create worktrees and session prompts using `/plan-work` logic:

```bash
# For each recommended workstream:
git worktree add -b <branch> ../<folder> main
mkdir -p ../<folder>/.claude
# Generate session-prompt.md with issues, context, first steps
```

**If "modify":**
Ask which recommendations to change:
- Remove a workstream
- Add a different grouping
- Change issue grouping within a workstream

**If "skip":**
Exit without creating anything. User can run `/plan-work` manually with specific issues.

### 7. Output Summary

```markdown
## Worktrees Created

| Folder | Branch | Issues |
|--------|--------|--------|
| ppds-service-extractions | feature/service-extractions | #254, #257, #259-265 |
| ppds-mcp-server | feature/mcp-server | #281 |
| ppds-plugin-traces | feature/plugin-traces | #140, #247 |

Session prompts created in each `.claude/session-prompt.md`

To start:
  cd ../ppds-service-extractions && claude
  cd ../ppds-mcp-server && claude
  cd ../ppds-plugin-traces && claude

Then run /start-work in each session.
```

## Behavior Summary

1. Check active worktrees and PRs to understand current state
2. Fetch milestone issues and parse dependencies
3. Categorize: in-progress, blocked, ready
4. Group ready issues by logical area
5. Rank and recommend top N workstreams
6. **STOP for user confirmation**
7. Create worktrees and session prompts
8. Output summary with next steps

## Edge Cases

**No issues in milestone:**
```
No open issues found in milestone "v1.0".
Check milestone name or use --milestone to specify a different one.
```

**All issues blocked or in progress:**
```
All v1.0 issues are either in progress or blocked:
- In progress: 15 issues across 3 worktrees
- Blocked: 5 issues (waiting on in-progress work)

Consider:
1. Help complete in-progress work
2. Check if blocked issues can be unblocked
3. Pull from v1.1 milestone instead
```

**Stale worktrees detected:**
```
Warning: Worktree 'ppds-old-feature' has no commits in 14 days.
Consider pruning with: git worktree remove ../ppds-old-feature

Excluding from "in progress" calculation.
```

**Missing project fields:**
```
Note: Some issues missing Priority/Size fields in GitHub Project.
Recommendations based on labels and dependencies only.
Run /triage to add project fields for better recommendations.
```

## Customization

**Grouping preferences:**
The command uses labels to group issues. Common groupings:
- `area:*` labels → group by area
- `phase:*` labels → group by implementation phase
- `epic:*` labels → group by epic

**Override grouping:**
If automatic grouping is wrong, user can say "modify" and manually adjust.

## Related Commands

| Command | When to Use |
|---------|-------------|
| `/next-work` | "What should I work on?" - Get recommendations |
| `/plan-work 52 78` | "I know what I want" - Create worktrees for specific issues |
| `/start-work` | "I'm in a worktree" - Read the session prompt |
| `/triage` | "Issues need categorization" - Add labels/milestones |
| `/handoff` | "I'm done for now" - Generate context for next session |

## Example Session

```
You: /next-work

Claude:
## Currently In Progress
- ppds-logging-observability: L&O (#266-280)
- ppds-tui-mvp: TUI SQL (#234)

## Recommended Next Workstreams

1. **Service Extractions** - #254, #257, #259-265 (enables TUI + MCP)
2. **MCP Server** - #281 (Claude Code integration)
3. **Plugin Traces** - #140, #247 (quick win, debugging)

Create these 3 worktrees? (yes / modify / skip)

You: yes

Claude:
## Worktrees Created
| Folder | Branch | Issues |
|--------|--------|--------|
| ppds-service-extractions | feature/service-extractions | #254, #257, #259-265 |
| ppds-mcp-server | feature/mcp-server | #281 |
| ppds-plugin-traces | feature/plugin-traces | #140, #247 |

To start: cd ../ppds-service-extractions && claude
```
