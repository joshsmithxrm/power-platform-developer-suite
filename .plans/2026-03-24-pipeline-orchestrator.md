# Pipeline Orchestrator

**Date:** 2026-03-24
**Origin:** Retrospective findings from PRs #652–#657. AI consistently skips verification, uses wrong review tools, stops mid-workflow, and claims "gates pass" from compile-time checks only. Root cause: the AI has discretion over pipeline progression where it shouldn't.
**Scope:** Deterministic pipeline script, hook fixes, skill updates, /start retirement.

---

## Problem Statement

The current workflow relies on the AI choosing to invoke /gates → /verify → /qa → /review → /pr in sequence. Evidence from 6 retro'd sessions shows:
- AI skipped real verification in 3/6 sessions
- AI used wrong review tool in 3/6 sessions
- AI stopped mid-workflow in 3/6 sessions
- User had to intervene in every single session to push the AI through the pipeline

The stop hook blocks but cannot force the AI to invoke a specific command. In long sessions with near-full context windows, the AI degrades and ignores the block reason.

## Solution

Replace AI-discretion workflow progression with a deterministic Python script that calls `claude -p` in sequence. Each pipeline step gets a fresh Claude session. The script — not the AI — decides what runs next.

---

## Task Breakdown

### Task 1: Create `scripts/pipeline.py`

The orchestrator script. Runs outside Claude Code, calls `claude -p` sequentially.

**File:** `scripts/pipeline.py`

**Arguments:**
```
python scripts/pipeline.py <plan-path> [options]

Options:
  --from <step>       Resume from a specific step (implement|gates|verify|review|converge|pr|retro)
  --name <name>       Override worktree/branch name (default: derived from plan filename)
  --no-retro          Skip the post-PR retro step
  --max-converge <n>  Max converge rounds (default: 3)
  --worktree <path>   Use existing worktree instead of creating one
```

**Pipeline stages:**

```python
STAGES = [
    "worktree",    # Create worktree + init workflow state
    "implement",   # claude -p "/implement <plan>"
    "gates",       # claude -p "/gates"
    "verify",      # claude -p "/verify"
    "review",      # claude -p "/review"
    "converge",    # Loop: converge → gates → verify → review (max N rounds)
    "pr",          # claude -p "/pr"
    "retro",       # claude -p "/retro" (non-blocking, always last)
]
```

**Key behaviors:**

1. **Worktree creation:**
   - Derive name from plan filename: `2026-03-24-pipeline-orchestrator.md` → `pipeline-orchestrator`
   - Branch: `feature/<name>`
   - Path: `.worktrees/<name>`
   - Check for existing worktree/branch (same logic as current /start)
   - Run `python scripts/workflow-state.py init "feature/<name>"`
   - Set plan path in state: `python scripts/workflow-state.py set plan "<plan-path>"`

2. **Each `claude -p` call:**
   - Set working directory to the worktree path
   - Capture exit code
   - Log structured output to `.workflow/pipeline.log`
   - If exit code != 0, log failure and stop (unless it's a converge round)

3. **Converge loop:**
   ```python
   for round in range(max_converge):
       run_stage("gates")
       run_stage("verify")
       result = run_stage("review")
       if review_passed():  # Check .workflow/state.json
           break
       if round == max_converge - 1:
           log("FAILED: Could not converge after {max_converge} rounds")
           sys.exit(1)
       run_stage("converge")  # claude -p "/converge"
   ```

4. **Structured logging to `.workflow/pipeline.log`:**
   ```
   2026-03-24T12:01:00Z [pipeline] START plan=.plans/my-plan.md worktree=.worktrees/my-feature
   2026-03-24T12:01:02Z [worktree] CREATED .worktrees/my-feature branch=feature/my-feature
   2026-03-24T12:01:05Z [implement] START
   2026-03-24T12:14:30Z [implement] DONE exit=0 duration=804s
   2026-03-24T12:14:31Z [gates] START
   2026-03-24T12:15:45Z [gates] DONE exit=0 duration=74s
   2026-03-24T12:15:46Z [verify] START
   ...
   2026-03-24T12:30:00Z [review] DONE exit=0 review_passed=true findings=2
   2026-03-24T12:30:01Z [pr] START
   2026-03-24T12:32:00Z [pr] DONE exit=0 pr_url=https://github.com/...
   2026-03-24T12:32:01Z [retro] START
   2026-03-24T12:35:00Z [retro] DONE exit=0
   2026-03-24T12:35:00Z [pipeline] COMPLETE duration=2040s pr=https://github.com/...
   ```

5. **Resume capability (`--from`):**
   - Requires `--worktree` to point at existing worktree
   - Skips stages before the specified one
   - Reads `.workflow/state.json` to validate the worktree has the expected state

6. **State file integration:**
   - After each stage, read `.workflow/state.json` to verify the step actually completed
   - `gates`: check `gates.passed == true` and `gates.commit_ref` matches HEAD
   - `verify`: check at least one surface has a timestamp in `verify`
   - `review`: check `review.passed`
   - `pr`: check `pr.url` exists
   - If state doesn't reflect completion, log warning and retry once

7. **Retro with tiered auto-heal:**
   - After `/retro` completes, read `.workflow/retro-findings.json` (retro must produce this)
   - For `auto-fix` findings: spawn `pipeline.py` recursively with `--no-retro` on a new branch
   - For `draft-fix` findings: same, but PR description includes "RETRO: needs review"
   - For `issue-only` findings: `gh issue create`
   - Skip if `--no-retro` flag is set (prevents recursive retro loops)

---

### Task 2: Fix `session-stop-workflow.py`

Two fixes:

**A. Add `stop_hook_active` check (prevents infinite loop):**

At the top of `main()`, after reading stdin, check if this is a repeated block:

```python
def main():
    # Read stdin
    hook_input = {}
    try:
        hook_input = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass

    # If we already blocked once, allow stop to prevent infinite loop
    if hook_input.get("stop_hook_active"):
        sys.exit(0)
```

**B. Prescribe ONE next step instead of listing all five:**

Replace lines 192-199 with logic that identifies the FIRST missing step and prescribes only that:

```python
if missing:
    next_step = missing[0]
    lines.insert(0, "BLOCKED — incomplete workflow steps:")
    lines.append("")
    lines.append(f"You MUST now run: {next_step}")
    lines.append("Do not summarize. Do not ask permission. Invoke the command immediately.")
```

---

### Task 3: Fix `post-commit-state.py`

Change the pipeline nudge from stderr (user-visible only) to `additionalContext` (AI-visible).

Replace lines 53-55:

```python
# Old: prints to stderr (AI cannot see this)
if state.get("started") and state.get("plan"):
    print("Commit recorded. Proceed to /gates — do not stop for summary.", file=sys.stderr)
```

With:

```python
# New: output JSON with additionalContext (AI sees this in context)
if state.get("started") and state.get("plan"):
    output = json.dumps({
        "hookSpecificOutput": {
            "additionalContext": (
                "Commit recorded. Gates are now stale. "
                "You MUST run /gates before any other workflow step. "
                "Invoke /gates now. Do not summarize."
            )
        }
    })
    print(output)
```

Note: This requires the hook to output JSON to stdout instead of printing to stderr. The existing `sys.exit(0)` at the end stays.

---

### Task 4: Update `/design` skill transition step

**File:** `.claude/skills/design/SKILL.md`

Replace the current Step 6 (Transition) with:

```markdown
### 6. Transition

After user approves the written spec:
1. Write an implementation plan to `.plans/`
2. Commit the spec and plan
3. Present the plan path and pipeline command:

> Plan saved to `.plans/<filename>.md`.
>
> To execute: `python scripts/pipeline.py .plans/<filename>.md`
>
> Or say "run it" and I'll invoke the pipeline from here.

If the user wants to proceed immediately, invoke the pipeline:
```bash
python scripts/pipeline.py .plans/<filename>.md
```
Run this in the background so the user can check status while it runs.
```

---

### Task 5: Update `/status` command

**File:** `.claude/commands/status.md`

Add pipeline log reading:

After the existing workflow state display, add:

```markdown
### Pipeline Log

If `.workflow/pipeline.log` exists, also display:
- Current pipeline stage (last `START` entry without a matching `DONE`)
- Completed stages with durations
- Any failures

Format:
```
PIPELINE STATUS:
  ✓ worktree     (2s)
  ✓ implement    (13m 24s)
  ✓ gates        (1m 14s)
  → verify       (running for 2m 30s)
  ○ review       (pending)
  ○ pr           (pending)
  ○ retro        (pending)
```
```

---

### Task 6: Update `/converge` command

**File:** `.claude/commands/converge.md`

Strip the internal loop. When invoked by the pipeline, `/converge` should:
1. Read review findings from `.workflow/state.json` (review.findings count)
2. Dispatch fix agents for the findings
3. Commit the fixes
4. Exit

The orchestrator handles the retry loop (converge → gates → verify → review).

Remove: Step 2B (gates), Step 2C (review), Step 2D (convergence evaluation), Step 2E loop logic.
Keep: Step 1 (tracking table), fix dispatch, commit.

The convergence tracking table still gets maintained — the orchestrator reads `.workflow/state.json` after each round to check if review passed.

When invoked interactively (not by the pipeline), `/converge` should still work as a standalone loop. Add a check: if `.workflow/pipeline.log` exists and has an active pipeline, run in single-fix mode. Otherwise, run the full loop for interactive use.

---

### Task 7: Kill `/start` skill

**File:** `.claude/skills/start/SKILL.md`

Delete this file. The orchestrator handles worktree creation.

Update `session-start-workflow.py` line 195: change "To start new work: /start <feature-name>" to "To start new work: /design or python scripts/pipeline.py <plan-path>"

---

### Task 8: Update `/retro` skill for structured output

**File:** `.claude/skills/retro/SKILL.md`

Add a requirement that `/retro` produces `.workflow/retro-findings.json` in addition to the conversational output:

```json
{
  "session_date": "2026-03-24",
  "pr": "#657",
  "stats": {
    "total_commits": 46,
    "feat_fix_ratio": "30/16",
    "thrashing_incidents": 1
  },
  "findings": [
    {
      "id": "R-01",
      "tier": "auto-fix",
      "description": "Co-Authored-By says Opus 4.5 in workspace /commit command",
      "files": [".claude/commands/commit.md"],
      "fix_description": "Update to dynamic system prompt format"
    },
    {
      "id": "R-02",
      "tier": "draft-fix",
      "description": "Spec template missing DI registration checklist",
      "files": ["specs/SPEC-TEMPLATE.md"],
      "fix_description": "Add Integration Checklist section"
    },
    {
      "id": "R-03",
      "tier": "issue-only",
      "description": "Need TypeScript codegen from C# DTOs",
      "fix_description": "Design session needed for codegen approach"
    }
  ],
  "blame_distribution": {
    "ai": 82,
    "process": 14,
    "tooling": 0,
    "user": 4
  }
}
```

The tier values (`auto-fix`, `draft-fix`, `issue-only`) are what `pipeline.py` reads to determine auto-heal behavior.

**Additional Task 8 changes (from retro-on-retro findings):**

**A. Replace blame categories with 5-Whys:**

Replace the blame assignment table (AI/Process/Tooling/User percentages) with a 5-Whys requirement per incident. Instead of labeling "AI: shotgun debugging," require the subagent to trace:
```
Shotgun debugging
  → no tests for this component
    → no test requirement in the spec
      → spec template doesn't mandate test plan
        → fix: update SPEC-TEMPLATE.md
```
The final "why" becomes the action item directly. The `retro-findings.json` captures the root cause, not the surface label.

**B. Skip memory cross-reference dispatch:**

Before dispatching the memory agent (step 5), check if memory files exist. If auto-memory is OFF (check CLAUDE.md) and no memory directory/files exist, skip the dispatch entirely. Don't waste a parallel slot.

**C. Give skills audit agent a concrete checklist:**

Replace "check for staleness" with:
1. For every file path mentioned in a skill, verify it exists (Glob)
2. For every command/skill referenced, verify it's in `.claude/commands/` or `.claude/skills/`
3. For every tool name referenced, verify it's in the current tool catalog
4. Check description frontmatter: would this trigger on the intended scenario? Would it false-positive on unrelated scenarios?
5. Check for contradictions between skills (overlapping scopes, conflicting instructions)
6. For every worktree path convention mentioned, verify it matches CLAUDE.md

---

### Task 9: Update `/implement` to work standalone

`/implement` currently includes a "Mandatory Tail" (Step 6) that runs gates → verify → qa → review → pr. When invoked by the pipeline, this tail is redundant — the pipeline handles it.

Add to the top of `/implement`:

```markdown
## Pipeline Mode Detection

If `.workflow/pipeline.log` exists and has an active pipeline entry, this skill is being
invoked by the pipeline orchestrator. In pipeline mode:
- Execute Steps 1-5 only (plan analysis through phase execution)
- Skip Step 6 (Mandatory Tail) — the pipeline handles gates/verify/review/pr
- Exit cleanly after all phases are committed
```

This keeps `/implement` working both standalone (interactive, with tail) and as a pipeline stage (no tail).

---

### Task 10: Fix `/retro` transcript search (5 failure modes)

**File:** `.claude/skills/retro/SKILL.md`

The transcript search has five compounding failure modes that make it inconsistent:

**A. Move transcript discovery to the orchestrator (step 2), not subagents:**

Currently lines 89-106 tell each subagent to "find the project path" via Glob. This fails because Glob can't list directories. Replace with orchestrator-driven discovery:

1. In step 1 (Gather Raw Data), after identifying commits, determine which branch(es) the commits came from
2. Compute the project directory path directly: replace `/` and `\` with `-`, strip `:` from the CWD
3. Handle the encoding inconsistency: search both `*--worktrees-<name>*` AND `*-.worktrees-<name>*` patterns using `Bash: ls -d`
4. Find .jsonl files matching the date range: `Bash: ls -lt <project-dir>/*.jsonl` and filter by modification time
5. Pass the **absolute file path(s)** to each subagent — not "go find it"

**B. Fix the subagent prompt template (lines 89-106):**

Replace the current "find the project path" instructions with:

```markdown
4. **Read conversation transcripts** for user corrections.

   Transcripts for this session are at:
   {orchestrator passes absolute paths here}

   Search for correction patterns using Grep (NOT bash grep):
   - Corrections: `"role":"user".*(that's not what|I never said|I didn't ask|not what I meant|who said|where did you get)`
   - Frustration: `"role":"user".*(fuck|shit|damn|wtf|what the hell|frustrated|half.ass)`
   - Redirections: `"role":"user".*(you missed|why didn't you|I already told you|I said|read it again|look at the)`

   Lines are long (one JSON record per line). Use the Read tool with
   offset/limit to read specific user messages. Extract direct quotes.

   If no transcript paths were provided, skip this step.
```

**C. Add transcript search verification:**

After step 3 (deep dive) subagents return, check each output for "Direct user quotes." If a subagent reports "no transcripts found" but the orchestrator provided valid paths, flag it and re-dispatch with explicit instructions to read the file.

---

## Dependency Order

```
Task 2  (stop hook fix)           — independent, do first
Task 3  (post-commit fix)         — independent, do first
Task 6  (converge simplify)       — independent
Task 7  (kill /start)             — independent
Task 8  (retro structured output + 5-whys + audit checklist) — independent
Task 9  (implement pipeline mode) — independent
Task 10 (retro transcript fix)    — independent
Task 1  (pipeline.py)             — core, do after 2+3 so hooks are fixed
Task 4  (design transition)       — depends on Task 1
Task 5  (status update)           — depends on Task 1
```

Tasks 2, 3, 6, 7, 8, 9, 10 are all independent and can be parallelized.
Task 1 is the core deliverable.
Tasks 4, 5 depend on Task 1 existing.

---

## Verification

- [ ] `python scripts/pipeline.py .plans/some-test-plan.md` creates worktree and runs through all stages
- [ ] `python scripts/pipeline.py --from gates --worktree .worktrees/test` resumes correctly
- [ ] Stop hook allows stop after one block (no infinite loop)
- [ ] Post-commit nudge appears in AI context (not just user stderr)
- [ ] `/status` shows pipeline progress when pipeline.log exists
- [ ] `/converge` works both standalone (interactive loop) and in pipeline mode (single fix pass)
- [ ] `/implement` skips mandatory tail when invoked by pipeline
- [ ] `/design` presents pipeline command at transition
- [ ] `/retro` produces `.workflow/retro-findings.json`
- [ ] Retro auto-heal creates branches for auto-fix findings (with `--no-retro`)
- [ ] Retro transcript search finds user quotes when transcripts exist
- [ ] Retro skills audit catches stale file paths and missing commands
- [ ] Retro uses 5-Whys instead of blame categories
