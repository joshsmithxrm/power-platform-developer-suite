# Plan Work from GitHub Issues

Triage GitHub issues, group them into branches, create worktrees, and generate session prompts.

## Usage

`/plan-work <issue-numbers...>`

Examples:
- `/plan-work 52` - Single issue
- `/plan-work 52 78 79` - Multiple issues to triage together

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

### 3. Analyze Dependencies

Parse each issue body for dependency patterns:
- "Blocked by #X" or "Depends on #X" → Check if #X is closed
- "Blocks #X" → Note relationship
- Labels like `blocked`, `waiting`

### 4. Propose Branch Groupings

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

### 5. Confirm with User

**STOP and ask for confirmation before creating anything.**

If user wants modifications:
- Allow changing branch names
- Allow regrouping issues
- Allow excluding issues

### 6. Create Worktrees

For each approved branch, create a worktree as a sibling folder:

```bash
git worktree add -b <branch-name> ../<folder-name> main
```

Naming convention:
- Folder: `<repo>-<short-descriptor>` (e.g., `sdk-query`, `sdk-daemon`)
- Branch: `feature/<descriptor>` or `fix/<descriptor>`

### 7. Update .gitignore

Ensure `.claude/session-prompt.md` is gitignored:

```bash
# Check if pattern exists
grep -q "session-prompt.md" .gitignore

# If not, add it
echo -e "\n# Claude Code session prompts\n.claude/session-prompt.md" >> .gitignore
```

Do this in the main repo AND each worktree.

### 8. Generate Session Prompts

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

### 9. Output Summary

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
