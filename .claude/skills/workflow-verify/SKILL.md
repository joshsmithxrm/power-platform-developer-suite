---
name: workflow-verify
description: Testing patterns for workflow infrastructure — hooks, pipeline, settings, skills, agents, state files. Use when verifying .claude/ changes, testing hook behavior, or validating pipeline stages.
---

# Workflow Infrastructure Verification

How to test changes to `.claude/` infrastructure: hooks, skills, agents, settings, pipeline, state.

## Hook Testing

### PreToolUse / PostToolUse hooks

Pipe JSON matching the hook's input schema to the hook script. Check exit code (0=allow, 2=block) and stderr (feedback message).

```bash
# Test a PreToolUse hook (e.g., protect-main-branch)
python -c "
import subprocess, json
input_data = json.dumps({'file_path': 'src/PPDS.Cli/Program.cs'})
result = subprocess.run(
    ['python', '.claude/hooks/protect-main-branch.py'],
    input=input_data, capture_output=True, text=True
)
print(f'exit={result.returncode} stderr={result.stderr[:100]}')
"
```

**Exit codes:** 0 = allow, 2 = block (with stderr message shown to AI)

### SessionStart hooks

SessionStart hooks write to stderr (context injection). Run standalone and check stderr output:

```bash
python .claude/hooks/session-start-workflow.py 2>&1
```

### Stop hooks

Stop hooks return JSON with `decision: "block"` or allow (exit 0). Must check `stop_hook_active` env var to prevent infinite loops.

### Notification hooks

Pipe notification JSON on stdin:

```bash
echo '{"notification_type": "idle_prompt", "message": "test", "title": "test", "cwd": "."}' | python .claude/hooks/notify.py
```

### Compaction re-injection

Run `/compact` in a session to trigger the `compact` SessionStart matcher. Verify workflow state is re-injected by checking the AI's next response references the workflow status.

## Pipeline Testing

### Dry run (all stages)

```bash
mkdir -p .plans && echo "# Test\n## Phase 1\nDo something" > .plans/test-plan.md
python scripts/pipeline.py --plan .plans/test-plan.md --branch test/dry-run --dry-run --no-retro --worktree .
```

### Test specific stage

```bash
python scripts/pipeline.py --plan .plans/test-plan.md --branch test/dry-run --dry-run --no-retro --from implement --worktree .
```

### Resume detection

```bash
python -c "
import sys; sys.path.insert(0, 'scripts')
from pipeline import find_last_completed_stage
# Write a fake pipeline.log
import os; os.makedirs('.workflow', exist_ok=True)
with open('.workflow/pipeline.log', 'w') as f:
    f.write('[implement] DONE\n[gates] DONE\n')
print(find_last_completed_stage('.workflow/pipeline.log'))
# Expected: 'gates'
"
```

## Settings Validation

After editing `.claude/settings.json`:

```bash
python -c "import json; json.load(open('.claude/settings.json')); print('valid JSON')"
```

Check matcher format — matchers are regex patterns tested against tool names or notification types.

## State File Testing

```bash
# Initialize
python scripts/workflow-state.py init my-branch

# Set values
python scripts/workflow-state.py set gates.passed true
python scripts/workflow-state.py set verify.cli now
python scripts/workflow-state.py set review.findings 3

# Read values
python scripts/workflow-state.py get gates.passed
python scripts/workflow-state.py show

# Clear values
python scripts/workflow-state.py set-null gates.passed

# Delete state
python scripts/workflow-state.py delete
```

## Common Pitfalls

- **Windows path escaping in JSON:** Use forward slashes or double-backslash. `$CLAUDE_PROJECT_DIR` uses forward slashes.
- **`$CLAUDE_PROJECT_DIR` in worktrees:** Resolves to the worktree root, not the main repo. Hooks using this var work correctly in both locations.
- **Exit code handling in bash pipes:** `echo ... | python script.py` — the exit code is from `python`, not `echo`. Use `$?` or `subprocess.run` for reliable capture.
- **Hook timeouts:** Default 600s (10 min). Set shorter for fast hooks (5s for path checks, 10s for notifications, 120s for build+test).
