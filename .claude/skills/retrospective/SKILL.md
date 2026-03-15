---
name: retrospective
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured analysis of recent work sessions to identify patterns, assign blame candidly, and recommend improvements.

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

### 2. Deep Dive Each Session (PARALLEL — one agent per session)

For each session, the agent MUST:

**a. Read the actual diffs** for thrashing incidents (2+ fix commits for the same feature):
```bash
git diff <commit1> <commit2> -- <relevant-files>
```

**b. Identify feat-then-fix chains** — a feature commit directly followed by fix commits for the same thing. Count them. This is the primary quality signal.

**c. Identify thrashing** — 3+ commits attempting the same fix. Read diffs to understand what changed between attempts.

**d. Read conversation transcripts** if available (check `.claude/` session logs) to extract direct user corrections and frustrations.

**e. Assign blame per incident:**

| Category | When to assign |
|----------|---------------|
| **AI** | Generated code that was broken on arrival, didn't read docs before implementing, shotgun debugging, shipped without testing |
| **Process** | No verification gate, no review before merge, working 14+ hours, premature documentation |
| **Tooling** | Platform behavior that is genuinely underdocumented or surprising |
| **User** | Flawed requirements, continuing to push during fatigue, not enforcing breaks |

### 3. Audit Skills and Tools (PARALLEL with step 2)

- Read all `.claude/skills/` and `.claude/commands/` files
- Check each for staleness (references to removed tools, wrong paths, outdated patterns)
- Verify descriptions trigger correctly (not too broad, not too narrow)
- Check for conflicts between skills
- Identify repeated manual behaviors that should be skills

### 4. Cross-Reference Memory

- Read all memory files
- Flag any memory entries that contradict the current codebase
- Flag architecture decisions that were reversed but not updated
- Flag ephemeral task tracking masquerading as memory

### 5. Synthesize

Present findings as:

**Aggregate Stats:**
- Total commits, feat/fix ratio, thrashing incidents count
- Blame distribution (AI/Process/Tooling/User percentages)

**Per-Session Analysis:**
- Time range, scope, what went well, what went wrong
- Direct user quotes where available
- Feat-then-fix chains with specific commit hashes

**Skills/Tools Audit:**
- Stale/broken items (P0)
- Missing content (P1)
- Improvements (P2+)

**Recommendations:**
- Specific, actionable items ranked by priority
- Distinguish between "superpowers already covers this" vs "needs a project-specific fix"

## Quality Bar

A retrospective is NOT adequate if it only analyzes commit messages. Commit messages are the AI's self-reported summary — exactly the thing that needs verification. A proper retrospective reads diffs, reads transcripts, and quotes the user directly.

## Output

Save analysis as a discussion document — do NOT save to `docs/plans/`. Present findings to the user for discussion before creating any action plan.
