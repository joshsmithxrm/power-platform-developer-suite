#!/usr/bin/env python3
"""
Ralph Wiggum Stop Hook - Windows-native autonomous iteration.

This hook intercepts session exits and continues iteration loops
until completion or max iterations reached.

Usage:
    1. Run /ralph-loop "your task" --max-iterations 20
    2. Claude works on the task
    3. When Claude tries to exit, this hook checks:
       - Is there an active ralph loop for this session?
       - Has the completion promise been output?
       - Have we hit max iterations?
    4. If not complete, blocks exit and feeds prompt back
    5. Repeat until done

State is stored in ~/.ppds/ralph/{session_id}.json
"""
import json
import sys
import os
from pathlib import Path
from datetime import datetime
from typing import Optional


def get_state_dir() -> Path:
    """Get the ralph state directory (~/.ppds/ralph/)."""
    return Path.home() / ".ppds" / "ralph"


def get_state_file(session_id: str) -> Path:
    """Get the state file for a specific session."""
    return get_state_dir() / f"{session_id}.json"


def load_state(session_id: str) -> Optional[dict]:
    """Load ralph loop state for a session. Returns None if no active loop."""
    state_file = get_state_file(session_id)
    if not state_file.exists():
        return None
    try:
        return json.loads(state_file.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return None


def save_state(session_id: str, state: dict) -> None:
    """Save ralph loop state for a session."""
    state_dir = get_state_dir()
    state_dir.mkdir(parents=True, exist_ok=True)
    get_state_file(session_id).write_text(
        json.dumps(state, indent=2), encoding="utf-8"
    )


def check_completion(transcript_path: Optional[str], promise: str) -> bool:
    """
    Check if completion promise appears in recent transcript output.

    Args:
        transcript_path: Path to the session transcript JSONL file
        promise: The exact string to look for (e.g., "DONE")

    Returns:
        True if promise found in recent assistant messages
    """
    if not transcript_path or not promise:
        return False

    try:
        transcript_file = Path(transcript_path)
        if not transcript_file.exists():
            return False

        # Transcript is JSONL format (one JSON object per line)
        lines = transcript_file.read_text(encoding="utf-8").strip().split("\n")

        # Parse last 50 lines and look for assistant messages
        recent_lines = lines[-50:] if len(lines) > 50 else lines
        assistant_contents = []

        for line in recent_lines:
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
                # Check for assistant role in message
                if entry.get("role") == "assistant":
                    content = entry.get("content", "")
                    if isinstance(content, list):
                        content = " ".join(
                            c.get("text", "") for c in content
                            if isinstance(c, dict) and c.get("type") == "text"
                        )
                    assistant_contents.append(str(content))
            except json.JSONDecodeError:
                continue

        # Check last 5 assistant messages for the promise
        for content in assistant_contents[-5:]:
            if promise in content:
                return True

        return False
    except OSError:
        return False


def main():
    """Main hook entry point."""
    # Read hook input from stdin
    try:
        raw_input = sys.stdin.read()
        hook_input = json.loads(raw_input) if raw_input.strip() else {}
    except json.JSONDecodeError:
        hook_input = {}

    # Note: stop_hook_active indicates Claude is continuing from a previous
    # stop hook. This is expected in Ralph loops - we rely on max_iterations
    # to prevent runaway loops, not this flag.

    # Get session ID - fall back to environment variable or "default"
    session_id = hook_input.get("session_id")
    if not session_id:
        session_id = os.environ.get("CLAUDE_SESSION_ID", "default")

    # Load state for this session
    state = load_state(session_id)

    # No active loop? Allow exit silently
    if not state or not state.get("active"):
        sys.exit(0)

    # Check max iterations
    current = state.get("current_iteration", 0)
    max_iter = state.get("max_iterations", 20)

    if current >= max_iter:
        state["active"] = False
        state["completed_at"] = datetime.now().isoformat()
        state["exit_reason"] = "max_iterations_reached"
        save_state(session_id, state)
        # Output informational message but allow exit
        print(json.dumps({
            "reason": f"Ralph loop complete: max iterations ({max_iter}) reached"
        }))
        sys.exit(0)

    # Check completion promise
    transcript_path = hook_input.get("transcript_path")
    promise = state.get("completion_promise", "")

    if promise and check_completion(transcript_path, promise):
        state["active"] = False
        state["completed_at"] = datetime.now().isoformat()
        state["exit_reason"] = "completion_promise_found"
        save_state(session_id, state)
        print(json.dumps({
            "reason": f"Ralph loop complete: found '{promise}'"
        }))
        sys.exit(0)

    # Continue the loop - increment iteration and block exit
    state["current_iteration"] = current + 1
    state["last_iteration_at"] = datetime.now().isoformat()
    save_state(session_id, state)

    prompt = state.get("prompt", "Continue working on the task.")

    result = {
        "decision": "block",
        "reason": f"[Ralph {state['current_iteration']}/{max_iter}] {prompt}"
    }

    print(json.dumps(result))
    sys.exit(0)


if __name__ == "__main__":
    main()
