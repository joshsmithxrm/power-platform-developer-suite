# Session Retrospective

Analyze a specific Claude Code session to extract learnings and improve workflows.

## Usage

`/retrospective [project-path]`

Examples:
- `/retrospective` - Analyze current project's most recent session
- `/retrospective C:\VS\.claude-worktrees\sdk\optimistic-almeida` - Specific worktree
- `/retrospective sdk` - Shorthand for ppds/sdk

## Philosophy

**Iterate on sessions, not ecosystems.** Analyzing one session deeply is more actionable than synthesizing 30 days of cross-repo data. Do retrospectives after significant sessions, extract learnings, implement improvements immediately.

## Critical Behaviors

### Be Direct
- Give honest feedback on what went wrong
- Don't soften criticism - the goal is improvement
- "The AI went off-track here because X was missing from CLAUDE.md" is appropriate

### This Is a Discussion
- Present findings, discuss implications together
- User provides context you don't have
- Track insights that emerge
- Decide together what to change

---

## Phase 1: Locate Session Logs

### Session Log Location

Claude Code stores session logs at:
```
C:\Users\[username]\.claude\projects\[encoded-path]\
```

**Path encoding:** `C:\path\to\project` → `C--path-to-project`

### Find the Session

```powershell
# List recent sessions for a project
$projectPath = "C--VS--claude-worktrees-sdk-optimistic-almeida"
Get-ChildItem "C:\Users\$env:USERNAME\.claude\projects\$projectPath" -Filter "*.jsonl" |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, Length, LastWriteTime -First 5
```

### Session File Structure

| File | Purpose |
|------|---------|
| `[UUID].jsonl` | Main conversation log (largest file) |
| `agent-*.jsonl` | Subagent task logs |
| `[UUID]/tool-results/` | Cached tool outputs |

---

## Phase 2: Analyze Session

Read the main session log and analyze for:

### 2.1 Friction Detection

Search for signals of problems:

| Signal | What to Look For |
|--------|------------------|
| User corrections | "no", "wrong", "not what I meant", "stop" |
| Repeated instructions | Same thing said twice |
| User frustration | Short responses after long outputs |
| Missing context | User provides info mid-task that should have been asked |
| Apologies | Claude apologizing indicates something went wrong |

**Extract specific examples** - not just counts.

### 2.2 Workflow Analysis

- **Phases:** When did plan mode start/end? What were the transitions?
- **Commits:** How often? After logical phases or random?
- **Tool usage:** Effective? Parallelized? Right tools for the job?
- **Questions:** Did Claude ask the right questions upfront?

### 2.3 Positive Patterns

What went RIGHT that should be reinforced:
- Smooth approvals ("lgtm", "proceed", "yes")
- Good question-asking before implementation
- Effective use of agents/exploration
- Clean commit cadence

### 2.4 Session Metrics

| Metric | Value |
|--------|-------|
| User messages | [count] |
| Tool uses | [count] |
| Files modified | [count] |
| Commits made | [count] |
| Friction incidents | [count] |
| User corrections ("no/wrong") | [count] |

---

## Phase 3: Cross-Reference Artifacts

### Git History

```bash
git log --oneline --since="[session-date]"
git diff --stat [first-commit]..[last-commit]
```

- Match conversation flow to commit timing
- Evaluate commit granularity
- Assess commit message quality

### Documentation Created

- ADRs created?
- CHANGELOG updated?
- CLAUDE.md updated?
- Other docs?

### PR Created

```bash
gh pr view [PR#]
```

- Description quality
- Issue links
- Any review feedback?

---

## Phase 4: Identify Improvements

**This is the critical phase.** For each finding, determine the actionable fix.

### 4.1 CLAUDE.md Updates

| Repo | Update Needed | Why |
|------|---------------|-----|
| [repo] | Add rule: "..." | Prevented mistake X |
| [repo] | Add to NEVER: "..." | AI did Y which was wrong |

Questions to ask:
- Did the AI make a mistake that a CLAUDE.md rule would prevent?
- Is there tribal knowledge that should be documented?
- Are there patterns that should be in ALWAYS/NEVER?

### 4.2 Slash Commands

| Action | Command | Purpose |
|--------|---------|---------|
| Create | `/[name]` | [what it does] |
| Update | `/[name]` | [what to add/change] |

Questions to ask:
- Was there a repeated manual process that should be a command?
- Did an existing command miss a step (like `/pre-pr` missing push)?
- Would a command have prevented an error?

### 4.3 Workspace Documentation

| Document | Update |
|----------|--------|
| `AGENTIC_WORKFLOW.md` | Add section on X |
| `[other].md` | Update Y |

Questions to ask:
- Did we discover a new pattern that should be documented?
- Is there process guidance that was improvised but should be formalized?
- Are there templates or checklists that would help?

### 4.4 Repo-Level Documentation

| Repo | Document | Update |
|------|----------|--------|
| [repo] | `docs/[file].md` | [change] |

---

## Phase 5: Create Retrospective Report

Store in `docs/retrospectives/YYYY-MM-DD_BRIEF_DESCRIPTION.md`:

```markdown
# Session Retrospective: [Title]

**Date:** YYYY-MM-DD
**Project:** [path or repo]
**Branch:** [branch-name]
**Duration:** [if known]
**Outcome:** [PR #, files changed, key deliverables]

---

## Summary

[1-3 sentences on what was accomplished]

---

## What Went Well

| Pattern | Why It Worked |
|---------|---------------|
| [pattern] | [explanation] |

---

## Friction Points

### [Issue Title]
- **Issue:** What happened
- **Impact:** How it affected the session
- **Root Cause:** Why it happened
- **Fix:** What we're changing to prevent recurrence

---

## Improvements Identified

### CLAUDE.md Updates
- [ ] [repo]: [update]

### Slash Commands
- [ ] [action]: [command] - [purpose]

### Workspace Documentation
- [ ] [document]: [update]

### Repo Documentation
- [ ] [repo]/[document]: [update]

---

## Metrics

| Metric | Value |
|--------|-------|
| User messages | |
| Files modified | |
| Commits | |
| Friction incidents | |
| User corrections | |

---

## Action Items

- [ ] Implement CLAUDE.md updates
- [ ] Implement slash command changes
- [ ] Update documentation
- [ ] Commit and push changes
```

---

## Phase 6: Implement Improvements

After discussing findings with user, implement the approved changes:

1. **CLAUDE.md updates** - Add rules to appropriate repos
2. **Slash commands** - Create or update commands
3. **Documentation** - Update workspace and repo docs
4. **Commit** - Group related changes, clear commit messages

---

## When to Run

- After significant sessions (10+ files, architectural changes)
- When friction was encountered
- When new patterns emerged
- Periodically for high-activity projects

**Don't wait for monthly reviews.** Iterate after each meaningful session.

---

## Related

- `/handoff` - Generate context for next session
- `/create-issue` - Create issue for follow-up work
- `docs/AGENTIC_WORKFLOW.md` - Process documentation
