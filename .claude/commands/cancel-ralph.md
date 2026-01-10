# Cancel Ralph

Cancel an active Ralph loop. The current iteration will complete, then the session will exit normally.

## Usage

```
/cancel-ralph
```

## Process

### 1. Find Active Loop

```bash
SESSION_ID="${CLAUDE_SESSION_ID:-default}"
STATE_FILE="$HOME/.ppds/ralph/${SESSION_ID}.json"
```

### 2. Deactivate Loop

```bash
python -c "
import json
import sys
import os
from pathlib import Path
from datetime import datetime

session_id = os.environ.get('CLAUDE_SESSION_ID', 'default')
state_file = Path.home() / '.ppds' / 'ralph' / f'{session_id}.json'

if not state_file.exists():
    print('No active Ralph loop found.')
    sys.exit(0)

state = json.loads(state_file.read_text(encoding='utf-8'))
if not state.get('active'):
    print('Ralph loop already inactive.')
    sys.exit(0)

state['active'] = False
state['cancelled_at'] = datetime.now().isoformat()
state['exit_reason'] = 'user_cancelled'
state_file.write_text(json.dumps(state, indent=2), encoding='utf-8')

print(f'Ralph loop cancelled after {state.get(\"current_iteration\", 0)} iterations.')
"
```

### 3. Confirm

```
Ralph Loop Cancelled
====================
Iterations completed: [N]
The session will exit normally after current work completes.
```

## Notes

- Cancellation is graceful - current iteration completes
- State file is preserved for debugging
- To restart, use `/ralph-loop` again
