# Plan Work from GitHub Issues

Triage GitHub issues, group them into branches, create worktrees, and generate session prompts.

## Usage

`/plan-work [options] [issue-numbers...]`

Examples:
- `/plan-work 52` - Single issue
- `/plan-work 52 78 79` - Multiple issues to triage together
- `/plan-work --target "This Week"` - All issues with Target="This Week"
- `/plan-work --batch tui-enhancements` - Predefined batch group
- `/plan-work --epic 210 --limit 5` - First 5 children of epic #210

## Options

| Option | Description |
|--------|-------------|
| `--target <value>` | Select issues by Target field value |
| `--batch <name>` | Select predefined batch group (see table below) |
| `--epic <number>` | Select child issues of an epic |
| `--limit <n>` | Limit number of issues (default: 10) |

## Predefined Batches

| Batch Name | Issues | Description |
|------------|--------|-------------|
| `meta-process` | 238, 233 | Process/scope definition |
| `bugs-critical` | 199, 200, 202 | User-facing bugs |
| `tui-foundation` | 234 | Abstract SQL table pattern |
| `tui-enhancements` | 204, 205, 206, 207, 208 | TUI improvements |
| `phase-3-traces` | 140, 152, 153, 154, 155, 156, 157, 158 | Plugin traces |
| `phase-4-webresources` | 141, 159, 160, 161, 162, 163 | Web resources |
| `plugin-registration` | 70, 66, 67, 68, 63, 65 | Plugin registration |

## Arguments

`$ARGUMENTS` - Space-separated GitHub issue numbers (e.g., `52 78 79`)

## Process

### 1. Fetch Issues

Fetch all specified issues in parallel using `gh issue view`:

```bash
gh issue view <number> --repo <owner>/<repo>
```

Infer repository from git remote:
```bash
git remote get-url origin
```

### 2. Check Existing Work

For each issue, check if work already exists:

**Recent commits mentioning the issue:**
```bash
git log --oneline -20 --grep="#<issue-number>"
```

**Open PRs for the issue:**
```bash
gh pr list --search "<issue-number> in:title,body"
```

**Existing branches:**
```bash
git branch -a | grep -i "<issue-keyword>"
```

### 3. Validate Project Fields

Before proceeding, check that all issues have been triaged:

**Fetch project field values:**
```bash
gh api graphql -f query='
  query {
    node(id: "PVT_kwHOAGk32c4BLj-0") {
      ... on ProjectV2 {
        items(first: 100) {
          nodes {
            content { ... on Issue { number } }
            fieldValues(first: 10) {
              nodes {
                ... on ProjectV2ItemFieldSingleSelectValue { field { ... on ProjectV2SingleSelectField { name } } name }
              }
            }
          }
        }
      }
    }
  }
'
```

**Validation checks:**
- All issues have Size field (for effort estimation)
- All issues have Target field (for prioritization context)
- Warn if mixing different Targets (e.g., "This Week" with "Q1 2026")

```markdown
## Validation Results

| Issue | Size | Target | Status |
|-------|------|--------|--------|
| #204 | M | CLI v1.0.0 | ✓ |
| #205 | S | CLI v1.0.0 | ✓ |
| #206 | - | - | ⚠️ Missing triage |

Warning: Issue #206 is missing Size and Target fields.
Run `/triage 206` first, or proceed without validation? (triage/proceed)
```

### 4. Analyze Dependencies

Parse each issue body for dependency patterns:
- "Blocked by #X" or "Depends on #X" → Check if #X is closed
- "Blocks #X" → Note relationship
- Labels like `blocked`, `waiting`

### 5. Propose Branch Groupings

Based on analysis, propose how to group issues into branches:

**Grouping criteria:**
- Related functionality (same feature area)
- Dependency chains (dependent issues together)
- Scope (small fixes separate from large features)
- One issue may warrant its own branch

**Output format:**
```
Issue Analysis
==============

#52 - Add query command group
    Status: Open, no existing work
    Dependencies: None
    Scope: New feature (medium)

#78 - Add DaemonConnectionPoolManager
    Status: Open, no existing work
    Dependencies: Blocked by #71 (CLOSED ✓)
    Scope: Enhancement (medium)

#79 - Add daemon testing infrastructure
    Status: Partially addressed by commit abc123
    Dependencies: Related to #78
    Scope: Testing (medium)

Proposed Branches
=================

Branch 1: feature/query-commands
  Issues: #52
  Rationale: Standalone feature, no dependencies

Branch 2: feature/daemon-improvements
  Issues: #78, #79
  Rationale: Related scope (daemon), #79 tests #78's work

Do you want to proceed with this plan? (Modify/Confirm)
```

### 6. Confirm with User

**STOP and ask for confirmation before creating anything.**

If user wants modifications:
- Allow changing branch names
- Allow regrouping issues
- Allow excluding issues

### 7. Create Worktrees

For each approved branch, create a worktree as a sibling folder:

```bash
git worktree add -b <branch-name> ../<folder-name> main
```

Naming convention:
- Folder: `<repo>-<short-descriptor>` (e.g., `sdk-query`, `sdk-daemon`)
- Branch: `feature/<descriptor>` or `fix/<descriptor>`

### 8. Update .gitignore

Ensure `.claude/session-prompt.md` is gitignored:

```bash
# Check if pattern exists
grep -q "session-prompt.md" .gitignore

# If not, add it
echo -e "\n# Claude Code session prompts\n.claude/session-prompt.md" >> .gitignore
```

Do this in the main repo AND each worktree.

### 9. Generate Session Prompts

For each worktree, create `.claude/session-prompt.md` containing:

```markdown
# Session Prompt

## Branch Purpose
This branch implements: #<issues>

## Issues
<For each issue: number, title, 2-3 sentence summary>

## Triage Findings
<Any discoveries from analysis:>
- "Issue #X was partially addressed by commit Y"
- "Dependency #Z is now closed, unblocked"
- "Related to #W which is in a different branch"

## Grouping Rationale
<Why these issues are together in this branch>

## Suggested First Steps
1. Explore existing <relevant area> code as reference
2. Create implementation plan
3. <Issue-specific starting point>

## Acceptance Criteria
<Combined from all issues in this branch>
```

### 10. Output Summary

```
Worktrees Created
=================

| Folder | Branch | Issues | Prompt |
|--------|--------|--------|--------|
| ../sdk-query | feature/query-commands | #52 | .claude/session-prompt.md |
| ../sdk-daemon | feature/daemon-improvements | #78, #79 | .claude/session-prompt.md |

To start working:

  cd ../sdk-query && claude
  # Then paste: cat .claude/session-prompt.md

  cd ../sdk-daemon && claude
  # Then paste: cat .claude/session-prompt.md

Note: .claude/session-prompt.md has been added to .gitignore
```

## Behavior Summary

1. Parse issue numbers from arguments
2. Fetch all issues in parallel
3. Check for existing work (commits, PRs, branches)
4. Analyze dependencies between issues
5. Propose branch groupings with rationale
6. **STOP for user confirmation**
7. Create worktrees as sibling folders
8. Add `.claude/session-prompt.md` to .gitignore
9. Generate focused prompts in each worktree
10. Output summary with next steps

## Edge Cases

**Issue not found:**
```
Error: Issue #999 not found in joshsmithxrm/ppds-sdk
Continuing with remaining issues...
```

**Issue already closed:**
```
Warning: Issue #50 is CLOSED
  - Include anyway? (May be for reference or follow-up work)
```

**Existing worktree/branch:**
```
Warning: Branch 'feature/query-commands' already exists
  - Use existing branch?
  - Create with different name?
  - Skip this group?
```

**Single issue:**
- Skip grouping analysis
- Create single worktree
- Still generate prompt

## When to Use

- Starting work on new issues
- Planning a sprint or batch of work
- Triaging related issues to understand scope
- Setting up parallel workstreams

## Related

- **/triage**: Categorize issues and get batch recommendations
- **/create-issue**: Create pre-triaged issues
- **ROADMAP.md**: Field definitions and guidelines
