---
name: retro
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured analysis of recent work sessions. Collects evidence and presents it to the user — the USER provides judgment, not the AI.

## Quality Bar (READ BEFORE STARTING)

- Commit messages are the AI's self-reported summary — exactly the thing that needs verification.
- Do NOT grade sessions (no letter grades, no "successful" labels). Present evidence, let the user decide.
- Do NOT delegate transcript reading to agents. Agents reported "zero user corrections" when the user was furious. Read transcripts yourself.
- Do NOT use grep for JSONL transcripts — lines are too long on Windows, results get "[Omitted]". Use the python extraction approach.

## Mode Detection

Detect automatically based on context:

**Pipeline mode** (CWD is a worktree AND `.workflow/pipeline.log` exists AND running via `claude -p`):
→ Jump to Pipeline Retro section below.

**Interactive mode** (user is present in conversation):
→ Follow the full Interactive Retro process below.

---

## Interactive Retro

### 1. Previous Retro Review

Before analyzing anything new, check what happened last time:

1. Look for `.workflow/retro-findings.json` in the current worktree or recent worktrees
2. If found, check which findings are still open vs resolved:
   - For `issue-only` findings: check if the GitHub issues are still open
   - For `auto-fix`/`draft-fix` findings: check if the referenced files were changed
3. Report: "Last retro found N issues. M resolved, K still open: [list]"

If no previous retro found, skip this step.

### 2. Gather Raw Data

**Determine the scope** from $ARGUMENTS:

- `/retro latest` or `/retro` (no args) → find the latest session (most recent 30+ minute gap)
- `/retro 6h` or `/retro 2d` → explicit time window
- `/retro abc123..def456` → explicit commit range
- `/retro the last 2 PRs` → find by PR number

```bash
git log --since="2 days ago" --format="%H %ai" --no-merges
```

**Commit count guard:** If scope contains more than 25 commits, warn and suggest narrowing.

### 3. Find ALL Relevant Transcripts

This is where the old retro failed. The interactive session (main repo) is where the real user interaction happens. The worktree transcripts are headless pipeline sessions with zero user input.

**Search main repo transcripts by PR number or branch name:**

```bash
# Find main repo transcript directory
ls -d ~/.claude/projects/*ppdsw-ppds/

# Search for PR numbers or branch names in those transcripts
grep -l "#NNN\|branch-name" ~/.claude/projects/*ppdsw-ppds/*.jsonl
```

**Also find worktree transcripts** for headless session analysis:

```bash
ls -d ~/.claude/projects/*ppdsw-ppds*worktrees-<name>*
ls -lt <project-dir>/*.jsonl
```

### 4. Extract User Messages via Python

Do NOT use grep on JSONL — lines are too long. Use this python approach:

```bash
python -c "
import json
path = r'<transcript-path>'
with open(path, 'r', encoding='utf-8') as f:
    for i, line in enumerate(f, 1):
        try:
            obj = json.loads(line)
            if obj.get('type') == 'user' and 'message' in obj:
                msg = obj['message']
                content = msg.get('content', '')
                if isinstance(content, str) and len(content) < 2000:
                    print(f'LINE {i}: {content[:500]}')
                    print('---')
                elif isinstance(content, list):
                    for item in content:
                        if isinstance(item, dict) and item.get('type') == 'text' and len(item.get('text','')) < 2000:
                            print(f'LINE {i}: {item[\"text\"][:500]}')
                            print('---')
        except Exception:
            pass
"
```

Run this for EACH relevant transcript. Read the output YOURSELF — do not delegate to agents.

### 5. Severity Heuristic

Count from the extracted user messages:
- User interrupts (`[Request interrupted by user]`)
- Profanity or frustration indicators
- Session crashes (transcripts with < 10 lines or very short duration)
- Wrong-branch incidents (git branch in transcript metadata shows `main` during implementation)

If any count > 0, this is a **rough session**. Analyze at full depth.
If all counts are 0, this is a **clean session**. Lighter analysis is fine.

### 6. Analyze

For each session in scope:

**Mechanical metrics** (from git):
- Commit count, feat/fix ratio
- Thrashing (3+ commits on same fix)
- Feat-then-fix chains (feat followed by fix commits)
- Pipeline stage timing (from pipeline.log if available)
- Sessions-to-success (how many crashed sessions before one succeeded)

**User voice** (from transcripts):
- Direct quotes of corrections, frustrations, redirections
- Timeline of what the user asked for vs what happened
- Points where the AI deviated from instructions

**Contributing factors** (replace single 5-Whys):
For each incident, list ALL contributing factors:
```
Incident: AI worked on main instead of worktree
Contributing factors:
  - No hook blocked file writes on main (tooling)
  - AI ignored worktree convention despite CLAUDE.md (behavior)
  - Session launched from main folder, not worktree (setup)
  - /design skill didn't explicitly say "never edit on main" (skill gap)
```

### 7. Present to User

Present evidence organized as:
- **Timeline:** What happened, in order
- **User quotes:** Direct quotes showing corrections/frustrations
- **Metrics:** Commits, feat/fix ratio, sessions-to-success, stage timing
- **Contributing factors:** Per incident
- **What worked:** Genuine positives

Do NOT rate or grade. Do NOT say "overall this was successful." Present the facts and let the user react.

### 8. Skills/Tools Audit (optional — only if user requests or scope warrants)

If the scope includes process problems (skills not followed, hooks not working):
1. Verify file paths in skills exist
2. Check for stale references to removed features
3. Flag overlapping or conflicting skill triggers
4. Identify missing skills for repeated manual behaviors

### 9. Structured Output

After discussing with the user, write `.workflow/retro-findings.json`:

```json
{
  "session_date": "YYYY-MM-DD",
  "pr": "#NNN",
  "stats": {
    "total_commits": 0,
    "feat_fix_ratio": "N/M",
    "thrashing_incidents": 0,
    "sessions_to_success": 0,
    "user_interrupts": 0,
    "severity": "rough|clean"
  },
  "findings": [
    {
      "id": "R-01",
      "tier": "auto-fix|draft-fix|issue-only",
      "description": "What is wrong",
      "files": ["path/to/affected/file"],
      "fix_description": "What to do about it",
      "contributing_factors": [
        {"factor": "description", "type": "tooling|behavior|skill|setup"}
      ]
    }
  ]
}
```

Tier definitions:
- **auto-fix**: Stale references, typos, hardcoded values. Safe to auto-implement.
- **draft-fix**: Skill wording, checklist gaps. Auto-PR but flag for review.
- **issue-only**: Architectural changes, new skills. Create GitHub issue, needs /design.

---

## Pipeline Retro

When running as a pipeline stage (headless, no user present):

1. Read `.workflow/pipeline.log` — extract stage timing, failures, retries
2. Read worktree transcripts — count sessions, extract basic metrics
3. Read git log — commit count, feat/fix ratio
4. Write `.workflow/retro-findings.json` with mechanical metrics ONLY
5. No grading, no judgment, no root cause analysis — just data
6. **Crash detection:** If this retro runs for less than 5 minutes and produces zero findings, log a warning in retro-findings.json

The pipeline orchestrator reads this file for auto-heal decisions.
