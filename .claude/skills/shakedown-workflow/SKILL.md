---
name: shakedown-workflow
description: Behavioral integration test for the entire workflow. Creates throwaway worktrees, runs pipelines with PPDS_SHAKEDOWN=1, collects results, runs retro, produces report, cleans up.
---

# Shakedown Workflow

End-to-end behavioral verification of the development workflow. Creates throwaway worktrees from the current branch, runs the pipeline in each, and verifies the entire flow works.

## When to Use

- Before shipping major workflow changes
- After modifying hooks, pipeline, or skills
- When you want to verify the full loop: start → implement → gates → verify → qa → review → converge → pr

## Process

### Step 1: Select Test Paths

$ARGUMENTS may contain `--paths feature,bug,resume` to select which scenarios to run.

Available scenarios:
- **feature**: Full feature development path (design → implement → ship)
- **bug**: Bug fix path (direct implement → ship)
- **resume**: Partial-state resume test

If no paths specified, run `feature` and `bug` by default.

### Step 2: Create Throwaway Worktrees

For each selected scenario:

```bash
# Create worktree from CURRENT branch (not main) — inherits modified .claude/, scripts/, specs/
git worktree add .worktrees/shakedown-<scenario> --detach HEAD
```

### Step 3: Run Pipelines

For each worktree, run the pipeline with PPDS_SHAKEDOWN=1:

```bash
cd .worktrees/shakedown-<scenario>
PPDS_SHAKEDOWN=1 python scripts/pipeline.py --worktree . --spec specs/<synthetic-spec>.md --dry-run
```

Use `--dry-run` mode — we're testing the orchestration logic, not running real AI sessions.

PPDS_SHAKEDOWN=1 ensures:
- No `gh pr create` (PR stage logs PR_SKIPPED_SHAKEDOWN)
- No `gh issue create` or `gh issue comment` (retro filing suppressed)
- No desktop notifications (notify.py exits 0)
- Stop hook exits 0 immediately

### Step 4: Collect Results

Read `.workflow/pipeline-result.json` from each worktree. Aggregate:
- Which scenarios passed/failed
- Stage durations
- Any errors or timeouts

### Step 5: Run Retro

Run retro across all shakedown session transcripts:

```bash
PPDS_SHAKEDOWN=1 python -c "
import sys; sys.path.insert(0, 'scripts')
from retro_helpers import discover_transcripts, extract_transcript_signals
for wt in ['.worktrees/shakedown-feature', '.worktrees/shakedown-bug']:
    for t in discover_transcripts(wt):
        signals = extract_transcript_signals(t)
        print(f'{t}: corrections={len(signals[\"user_corrections\"])}, failures={len(signals[\"tool_failures\"])}')
"
```

### Step 6: Produce Report

Write `.workflow/shakedown-report.json`:

```json
{
  "timestamp": "<ISO>",
  "scenarios": {
    "feature": {"status": "pass|fail", "duration": "Ns", "error": null},
    "bug": {"status": "pass|fail", "duration": "Ns", "error": null}
  },
  "findings": [],
  "retro_summary": "..."
}
```

### Step 7: Cleanup

Remove throwaway worktrees:

```bash
git worktree remove .worktrees/shakedown-feature --force
git worktree remove .worktrees/shakedown-bug --force
```

## Rules

1. Always use `PPDS_SHAKEDOWN=1` — never file real issues or create real PRs from shakedown
2. Worktrees branch from current branch, not main — this tests the current code
3. `--dry-run` mode tests orchestration without AI sessions
4. Clean up ALL worktrees even if some scenarios fail
5. Report findings, don't try to fix them — shakedown is read-only
