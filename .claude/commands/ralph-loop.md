# Ralph Loop

Start an autonomous iteration loop. Claude will work on the task repeatedly until completion or max iterations.

## Usage

```
/ralph-loop "<task description>" [--max-iterations N] [--completion-promise "SIGNAL"]
```

## Arguments

- `<task description>` - The task for Claude to work on (required)
- `--max-iterations N` - Maximum iterations before stopping (default: 20)
- `--completion-promise "SIGNAL"` - Exact string that signals completion (default: "DONE")

## Examples

```
/ralph-loop "Fix all TypeScript errors in src/" --max-iterations 10
/ralph-loop "Implement the login form. Output COMPLETE when done." --completion-promise "COMPLETE"
/ralph-loop "Get all tests passing" --max-iterations 30 --completion-promise "ALL TESTS PASS"
```

## How It Works

1. This command writes state to `~/.ppds/ralph/{session}.json`
2. You start working on the task
3. When you try to exit, the Stop hook intercepts
4. If completion signal not found, you continue with the same task
5. Repeat until done or max iterations reached

## Process

### 1. Parse Arguments

Extract from the user's command:
- Task description (required)
- Max iterations (default 20)
- Completion promise (default "DONE")

### 2. Initialize State

Write the ralph loop state file:

```bash
# Get session ID
SESSION_ID="${CLAUDE_SESSION_ID:-default}"

# Write state file (Python creates directory if needed)
python -c "
import json
import os
from pathlib import Path
from datetime import datetime

session_id = os.environ.get('CLAUDE_SESSION_ID', 'default')
state = {
    'session_id': session_id,
    'prompt': '''$ARGUMENTS''',
    'max_iterations': 20,
    'current_iteration': 0,
    'completion_promise': 'DONE',
    'started_at': datetime.now().isoformat(),
    'active': True
}
state_file = Path.home() / '.ppds' / 'ralph' / f'{session_id}.json'
state_file.parent.mkdir(parents=True, exist_ok=True)
state_file.write_text(json.dumps(state, indent=2))
print(f'Ralph loop started: {state_file}')
"
```

### 3. Output Task

After initializing, output the task clearly:

```
Ralph Loop Started
==================
Task: [task description]
Max iterations: [N]
Completion signal: [SIGNAL]

Starting iteration 1...

---

[task description]
```

### 4. Begin Work

Now work on the task. When you complete it, include the completion promise in your output.

Example completion:
```
All TypeScript errors have been fixed. The build now passes.

DONE
```

## Cancellation

To cancel an active loop, use `/cancel-ralph`.

## Tips

1. **Clear success criteria** - Include specific, verifiable conditions in your task
2. **Use completion promises** - Always specify what string signals "done"
3. **Set reasonable limits** - 20 iterations is usually enough; increase for complex tasks
4. **Include verification** - Ask Claude to run tests/builds to verify completion

## Example Task Template

```
/ralph-loop "
Implement [feature].

Requirements:
- [ ] Requirement 1
- [ ] Requirement 2
- [ ] All tests pass

Verification:
- Run: dotnet test
- Check: No errors

When all requirements met and verification passes, output: FEATURE_COMPLETE
" --max-iterations 25 --completion-promise "FEATURE_COMPLETE"
```
