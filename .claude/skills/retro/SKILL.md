---
name: retro
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured analysis of recent work sessions to identify patterns, trace root causes, and recommend improvements.

## Quality Bar (READ BEFORE STARTING)

A retrospective is NOT adequate if it only analyzes commit messages. Commit messages are the AI's self-reported summary — exactly the thing that needs verification. A proper retrospective reads diffs, reads transcripts, and quotes the user directly.

If your subagents return analysis based only on commit subjects and bodies, **reject their output and re-dispatch with explicit diff-reading instructions.**

## When to Use

- End of a feature branch before PR
- After a multi-day sprint
- When the user asks to review recent work quality
- After a particularly rough session (thrashing, rework)

## Process

### 1. Gather Raw Data

**Determine the scope** from $ARGUMENTS:

- `/retro latest` or `/retro` (no args) → find the latest session (most recent 30+ minute gap)
- `/retro 6h` or `/retro 2d` → explicit time window (hours or days)
- `/retro abc123..def456` → explicit commit range
- `/retro "2 days"` → explicit since-style window

**Default behavior (no args):** Find the latest session, NOT "2 days ago". This prevents accidentally pulling in multiple sessions when the user wants to review just the most recent one.

```bash
# Step 1: Get recent commits to find session boundaries
git log --since="2 days ago" --format="%H %ai" --no-merges

# Step 2: Identify the most recent 30+ minute gap
# Everything after that gap = the latest session

# Step 3: Fetch full details for ONLY the scoped commits
git log {start}..{end} --format="COMMIT:%H%nDATE:%ai%nSUBJECT:%s%nBODY:%b%n---" --no-merges
```

**Commit count guard:** If the scope contains more than 25 commits, warn the user and suggest narrowing. High-volume retros produce shallow analysis.

Identify session boundaries: gaps of 30+ minutes between commits = new session.

### 2. Discover Transcript Paths

Before dispatching session agents, find the conversation transcripts:

1. Determine which branch(es) the commits came from (from git log output in step 1)
2. Compute the encoded project directory: take the worktree absolute path, replace `\` and `/` with `-`, strip `:`
   Example: `C:\VS\ppdsw\ppds\.worktrees\v1-bugs` → `C--VS-ppdsw-ppds--worktrees-v1-bugs`
3. Handle encoding inconsistency: search BOTH patterns using Bash:
   ```bash
   ls -d ~/.claude/projects/*ppdsw-ppds*worktrees-<name>*
   ```
   Also check the main repo path: `ls -d ~/.claude/projects/*ppdsw-ppds/`
4. Find .jsonl files matching the date range:
   ```bash
   ls -lt <project-dir>/*.jsonl
   ```
   Filter to files modified within the session's date range.
5. Pass the **absolute file path(s)** to each session agent in step 3.

If no transcript paths are found, note it and continue — transcript search becomes optional for those sessions.

### 2.5. Dispatch Agents

Launch ALL of these simultaneously — do NOT proceed until all are dispatched:

- [ ] **One agent per session** for deep dives (step 3). Do NOT batch multiple sessions into one agent — each session gets its own agent with a focused scope.
- [ ] **One agent for skills/tools audit** (step 4)
- [ ] **One agent for memory cross-reference** (step 5) — only if memory is enabled (see step 5)

### 3. Deep Dive Each Session (one agent per session)

Use the prompt template below for EACH session. Do NOT batch sessions — each gets its own agent.

#### Subagent Prompt Template

> Analyze the work session from {start_hash} to {end_hash} ({date_range}, {N} commits).
>
> Commits in this session:
> {paste the commit subjects for this session}
>
> You MUST do all of the following:
>
> 1. **Read the full diff** for this session:
>    ```bash
>    git diff {start_hash}~1 {end_hash} --stat
>    ```
>    Then read file-level diffs for the most-changed files.
>
> 2. **For any feat→fix chain** (a feat commit followed by fix commits
>    for the same thing), run:
>    ```bash
>    git diff {feat_hash} {fix_hash} -- <relevant-files>
>    ```
>    Explain what was wrong in the original feat commit.
>
> 3. **For thrashing** (3+ commits attempting the same fix), read each
>    successive diff and explain what changed between attempts and why
>    the earlier attempts failed.
>
> 4. **Read conversation transcripts** for user corrections.
>
>    Transcripts for this session are at:
>    {orchestrator inserts absolute paths here, or "No transcripts found — skip this step"}
>
>    Search for correction patterns using Grep (NOT bash grep) on the provided files:
>    - Corrections: `"role":"user".*(that's not what|I never said|I didn't ask|not what I meant|who said|where did you get)`
>    - Frustration: `"role":"user".*(fuck|shit|damn|wtf|what the hell|frustrated|half.ass)`
>    - Redirections: `"role":"user".*(you missed|why didn't you|I already told you|I said|read it again|look at the)`
>
>    Lines are long (one JSON record per line). Use the Read tool with
>    offset/limit to read specific user messages. Extract direct quotes.
>
> 5. **Return structured output:**
>    - Session summary (2-3 sentences)
>    - Feat-then-fix chains with commit hashes and diff evidence
>    - Thrashing incidents with diff evidence
>    - Direct user quotes (if transcripts found)
>    - Root cause chain per incident (5-Whys)
>    - What went well

#### Root Cause Analysis (5-Whys)

For each incident, trace the root cause chain instead of assigning blame labels:

```
Incident: AI skipped real verification
  → /verify language is ambiguous about what "verify" means
    → Skill was written assuming AI would interpret "use the product" literally
      → No artifact requirement to prove verification happened
        → Root cause: /verify needs screenshot/output evidence gate
```

The final "why" becomes the action item directly. Capture the full chain in `retro-findings.json` as `root_cause_chain`.

Do NOT use blame categories (AI/Process/Tooling/User). They describe symptoms, not causes.

### 4. Audit Skills and Tools (parallel agent)

Dispatch one agent with this concrete checklist:

1. **Path verification:** For every file path mentioned in any skill/command, run Glob to verify it exists
2. **Command/skill references:** For every `/command` or skill name referenced, verify it exists in `.claude/commands/` or `.claude/skills/`
3. **Tool references:** For every tool name referenced (Bash, Edit, Glob, etc.), verify it matches the current tool catalog
4. **Trigger accuracy:** For each skill's `description` frontmatter, assess: would this trigger on the intended scenario? Would it false-positive on unrelated scenarios?
5. **Conflicts:** Check for overlapping scopes or contradictory instructions between skills
6. **Convention compliance:** Verify worktree paths, commit message formats, and other conventions match CLAUDE.md
7. **Missing skills:** Identify repeated manual behaviors that should be codified as skills

### 5. Cross-Reference Memory (conditional — check before dispatching)

Before dispatching: check if auto-memory is enabled (read CLAUDE.md for "Auto-memory is OFF").
If auto-memory is OFF and no memory files exist under `~/.claude/memory/`, **skip this step entirely**.
Do not dispatch an agent that will just report "nothing found."

If memory IS enabled, dispatch one agent with this scope:

- Read all memory files
- Flag any memory entries that contradict the current codebase
- Flag architecture decisions that were reversed but not updated
- Flag ephemeral task tracking masquerading as memory

### 6. Synthesize

After ALL agents return, compile findings into:

**Aggregate Stats:**
- Total commits, feat/fix ratio, thrashing incidents count
- Root cause distribution (which root causes recur across sessions)

**Per-Session Analysis:**
- Time range, scope, what went well, what went wrong
- Direct user quotes where available
- Feat-then-fix chains with specific commit hashes and diff evidence

**Skills/Tools Audit:**
- Stale/broken items (P0)
- Missing content (P1)
- Improvements (P2+)

**Recommendations:**
- Specific, actionable items ranked by priority
- Distinguish between "existing skill covers this" vs "needs a new skill or skill update"

## Output

Present findings to the user for discussion — do NOT save to `docs/plans/` or create an action plan. Wait for the user's input before proposing changes.

## Structured Output

In addition to the conversational analysis, write `.workflow/retro-findings.json`:

```json
{
  "session_date": "YYYY-MM-DD",
  "pr": "#NNN",
  "stats": {
    "total_commits": 0,
    "feat_fix_ratio": "N/M",
    "thrashing_incidents": 0
  },
  "findings": [
    {
      "id": "R-01",
      "tier": "auto-fix|draft-fix|issue-only",
      "description": "What is wrong",
      "files": ["path/to/affected/file"],
      "fix_description": "What to do about it",
      "root_cause_chain": ["surface problem", "why 1", "why 2", "why 3", "root cause"]
    }
  ]
}
```

Tier definitions:
- **auto-fix**: Stale references, typos, hardcoded values, known hook bugs. Safe to auto-implement.
- **draft-fix**: Spec template additions, skill wording improvements, checklist gaps. Auto-PR but flag for review.
- **issue-only**: Architectural changes, new skills, process redesigns. Create GitHub issue, needs /design session.

The pipeline orchestrator reads this file to drive auto-heal behavior after the retro completes.
