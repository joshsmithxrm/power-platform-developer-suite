---
name: retrospective
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured analysis of recent work sessions to identify patterns, assign blame candidly, and recommend improvements.

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

```bash
# Get commits for the time period (default: 2 days)
git log --since="2 days ago" --format="COMMIT:%H%nDATE:%ai%nSUBJECT:%s%nBODY:%b%n---" --no-merges
```

Identify session boundaries: gaps of 30+ minutes between commits = new session.

### 2. Dispatch Agents

Launch ALL of these simultaneously — do NOT proceed until all are dispatched:

- [ ] **One agent per session** for deep dives (step 3). Do NOT batch multiple sessions into one agent — each session gets its own agent with a focused scope.
- [ ] **One agent for skills/tools audit** (step 4)
- [ ] **One agent for memory cross-reference** (step 5)

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
> 4. **Search for conversation transcripts** matching this date range:
>    ```bash
>    find ~/.claude -name "*.jsonl" -newer {start_date_file} 2>/dev/null | head -10
>    ```
>    If transcripts exist, search for user corrections, frustrations,
>    and repeated instructions. Quote the user directly.
>
> 5. **Return structured output:**
>    - Session summary (2-3 sentences)
>    - Feat-then-fix chains with commit hashes and diff evidence
>    - Thrashing incidents with diff evidence
>    - Direct user quotes (if transcripts found)
>    - Blame assignment per incident (AI / Process / Tooling / User)
>    - What went well

#### Blame Categories

| Category | When to assign |
|----------|---------------|
| **AI** | Generated code that was broken on arrival, didn't read docs before implementing, shotgun debugging, shipped without testing |
| **Process** | No verification gate, no review before merge, working 14+ hours, premature documentation |
| **Tooling** | Platform behavior that is genuinely underdocumented or surprising |
| **User** | Flawed requirements, continuing to push during fatigue, not enforcing breaks |

### 4. Audit Skills and Tools (parallel agent)

Dispatch one agent with this scope:

- Read all `.claude/skills/` and `.claude/commands/` files
- Check each for staleness (references to removed tools, wrong paths, outdated patterns)
- Verify descriptions trigger correctly (not too broad, not too narrow)
- Check for conflicts between skills
- Identify repeated manual behaviors that should be skills

### 5. Cross-Reference Memory (parallel agent)

Dispatch one agent with this scope:

- Read all memory files
- Flag any memory entries that contradict the current codebase
- Flag architecture decisions that were reversed but not updated
- Flag ephemeral task tracking masquerading as memory

### 6. Synthesize

After ALL agents return, compile findings into:

**Aggregate Stats:**
- Total commits, feat/fix ratio, thrashing incidents count
- Blame distribution (AI/Process/Tooling/User percentages)

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
- Distinguish between "superpowers already covers this" vs "needs a project-specific fix"

## Output

Present findings to the user for discussion — do NOT save to `docs/plans/` or create an action plan. Wait for the user's input before proposing changes.
