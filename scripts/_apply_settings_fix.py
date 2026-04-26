"""C-2: Strip $CLAUDE_PROJECT_DIR from every hook command in settings.json (AC-51)."""
from __future__ import annotations

import json
import re
from pathlib import Path

p = Path(".claude/settings.json")
data = json.loads(p.read_text(encoding="utf-8"))

PAT = re.compile(r'python\s+"\$CLAUDE_PROJECT_DIR/(\.claude/hooks/[^"]+)"')

count = 0
for matcher in data.get("hooks", {}).values():
    if not isinstance(matcher, list):
        continue
    for entry in matcher:
        for hook in entry.get("hooks", []):
            cmd = hook.get("command", "")
            if not cmd:
                continue
            new = PAT.sub(r'python ".claude/hooks/\1" --project-dir "$CLAUDE_PROJECT_DIR"', cmd)
            # AC-51 forbids $CLAUDE_PROJECT_DIR in any command — even as an arg.
            # Use plain relative path; hooks already resolve project dir from
            # _pathfix.get_project_dir() which uses git toplevel.
            new = PAT.sub(r'python ".claude/hooks/\1"', cmd)
            if new != cmd:
                hook["command"] = new
                count += 1

p.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
print(f"Replaced {count} hook command(s)")

remaining = sum(
    1 for h in (
        hook for matcher in data.get("hooks", {}).values()
        if isinstance(matcher, list)
        for entry in matcher
        for hook in entry.get("hooks", [])
    )
    if "$CLAUDE_PROJECT_DIR" in (h.get("command") or "")
)
print(f"Remaining $CLAUDE_PROJECT_DIR commands: {remaining}")
