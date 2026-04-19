---
name: retro
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured analysis of recent work sessions. Collects evidence and presents it to the user — the USER provides judgment, not the AI.

## Phase Registration (MUST run first)

```bash
python scripts/workflow-state.py set phase retro
```

This enables the stop-hook bypass for the retro phase (no workflow enforcement during retrospectives).

## Quality Bar (READ BEFORE STARTING)

- Commit messages are the AI's self-reported summary — exactly the thing that needs verification.
- Do NOT grade sessions (no letter grades, no "successful" labels). Present evidence, let the user decide.
- Do NOT delegate transcript reading to agents. Agents reported "zero user corrections" when the user was furious. Read transcripts yourself.
- Do NOT use grep for JSONL transcripts — lines are too long on Windows, results get "[Omitted]". Use the python extraction approach.

## Mode Detection

Detect automatically based on context AND the optional argument mode:

**Pipeline mode** (CWD is a worktree AND `.workflow/pipeline.log` exists AND running via `claude -p`):
→ Jump to Pipeline Retro section below.

**Interactive mode** (user is present in conversation):
→ Follow the full Interactive Retro process below.

### Argument Modes

When invoked interactively, the user can scope the analysis depth via argument:

| Mode | Trigger | Depth | Time | Use when |
|------|---------|-------|------|----------|
| `/retro pr` | After a PR merges | Light | 5–10 min | Single PR retrospective — commit-history sweep, surface user corrections in transcript, file findings if any |
| `/retro incident` | After something breaks (test, deploy, agent crash) | Medium | 20–40 min | Single incident investigation — timeline, contributing-factors, draft-fix where safe |
| `/retro release` | After a release ships (or a multi-PR window closes) | Heavy | Hours | Cross-session pattern analysis — like the v1-prelaunch retro: parallel subagents, full transcript audit, governance / hygiene findings |

If no argument is supplied, default to `pr` mode for the most recent PR.

The three modes use the same skill (this file) — they differ only in scope
breadth and time budget. The Interactive Retro process below applies to all
three; sections marked `(release-mode only)` are skipped for lighter modes.

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

**Search main repo transcripts by PR number or branch name** (grep -l for file names only — safe because it matches filenames, not line content):

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
- **issue-only**: Specific code bug, missing logic, or broken behavior with a concrete
  fix. Must be reproducible (would happen on every run). Litmus test: "Can someone open
  this issue, make a code change, and close it?"
- **observation**: Run metrics (timing, ratios, counts), single-run anomalies (gaps,
  timeouts), patterns without concrete fixes ("most-thrashed file"), behavioral
  recommendations ("consider adding a checklist"). Stays in retro report — NOT filed
  as a GitHub issue.

### 10. Persistent Store Update

After writing `.workflow/retro-findings.json`, update the persistent retro store:

1. **Read** `.retros/summary.json` (from repo root, not worktree `.workflow/`)
   - If file doesn't exist: create with seed schema (schema_version: 1, total_retros: 0, empty findings_by_category, zeroed metrics)
   - If file has invalid JSON: log warning, create fresh
   - If `schema_version` differs from expected (1): log warning, create fresh

2. **Append** each finding to `findings_by_category[category]` with `{date, branch, finding_id}`
   - Append only — never mutate or delete existing entries during this step
   - New categories are added as new keys

3. **Increment** `total_retros`

4. **Update** `metrics` (rolling averages computed from all entries in store):
   - `avg_fix_ratio`: average feat/fix ratio across all retros
   - `pipeline_success_rate`: fraction of retros with severity "clean"
   - `avg_convergence_rounds`: average convergence rounds (from pipeline retros)

5. **Trim** entries that fall outside both retention windows:
   - Keep entries within last 20 retros (by `total_retros` count)
   - Keep entries within last 6 months (by `date` field)
   - Remove only entries that exceed BOTH thresholds (older than 6 months AND beyond 20 retros)

6. **Update** `last_updated` to today's date

7. **Write** `.retros/summary.json`

---

## Pipeline Retro

When running as a pipeline stage (headless, no user present):

1. Read `.workflow/pipeline.log` — extract stage timing, failures, retries
2. Read worktree transcripts — count sessions, extract basic metrics
3. Read git log — commit count, feat/fix ratio
4. Write `.workflow/retro-findings.json` with mechanical metrics ONLY
5. **Update persistent store:** Follow the same Persistent Store Update process (section 10) to append findings to `.retros/summary.json`
6. No grading, no judgment, no root cause analysis — just data
7. **Crash detection:** If this retro runs for less than 5 minutes and produces zero findings, log a warning in retro-findings.json

### Issue-Worthiness Classification

Before classifying a finding as `issue-only`, apply the litmus test: "Can someone open
this issue, make a code change, and close it?" If the answer is "this is just how that
run went" or "there's no specific code to change," classify as `observation` instead.

**Examples of `observation` (do NOT file as issue):**
- "High fix ratio (11 fix / 2 feat)" — run metric
- "109-minute gap between stages" — single-run timing
- "Most-thrashed file (6 touches)" — pattern without a fix
- "Converge timed out at 900s" — single-run anomaly
- "38% of compute spent on converge" — percentage metric
- "Consider adding a checklist for X" — behavioral recommendation

**Examples of `issue-only` (DO file as issue):**
- "Agent frontmatter uses 'allowedTools' instead of 'tools'" — code bug, concrete fix
- "Pipeline resumes while previous stage still running" — reproducible logic bug
- "MSYS converts /path to C:/path in issue titles" — specific, fixable
- "Duplicate PR stage runs when PR already exists" — missing guard, concrete fix

The pipeline orchestrator reads this file for auto-heal decisions.

## Transcript Signal Extraction

Before manual transcript reading, run the automated signal extractor:

```bash
python -c "
import sys; sys.path.insert(0, 'scripts')
from retro_helpers import extract_transcript_signals, extract_enforcement_signals, discover_transcripts
transcripts = discover_transcripts('.')
for t in transcripts:
    signals = extract_transcript_signals(t)
    if signals['user_corrections'] or signals['tool_failures'] or signals['repeated_commands']:
        print(f'=== {t} ===')
        print(f'  Corrections: {len(signals[\"user_corrections\"])}')
        print(f'  Tool failures: {len(signals[\"tool_failures\"])}')
        print(f'  Repeated commands: {len(signals[\"repeated_commands\"])}')
state_signals = extract_enforcement_signals('.workflow/state.json')
if state_signals['stop_hook_count'] > 0:
    print(f'Stop hook blocked {state_signals[\"stop_hook_count\"]} times')
"
```

Use these signals as starting points for investigation. They highlight sessions that need deeper review.

## Interactive Isolation

In interactive mode (not pipeline, not pr-monitor), dispatch retro as a subagent for isolation:

```
Agent tool:
  subagent_type: general-purpose
  prompt: |
    Run a retrospective analysis. You receive ONLY:
    - Transcript files (listed below)
    - Constitution (specs/CONSTITUTION.md)
    - Workflow state (.workflow/state.json)
    
    Do NOT access conversation history. Analyze the transcripts independently.
    
    Transcripts: {list of discovered transcript paths}
    
    Follow the retro analysis process and output structured findings.
  run_in_background: false
```

The subagent returns findings; the parent session writes them to state and handles filing.
