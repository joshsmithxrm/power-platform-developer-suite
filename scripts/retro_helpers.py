"""Transcript extraction helpers for retrospective analysis."""

import json
import os


def extract_transcript_signals(jsonl_path):
    """Extract structured signals from a JSONL transcript file."""
    signals = {
        "user_corrections": [],
        "tool_failures": [],
        "repeated_commands": [],
    }
    command_counts = {}

    try:
        with open(jsonl_path, "r", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue

                event_type = event.get("type")

                # User messages — correction patterns
                if event_type == "human":
                    content = event.get("message", {}).get("content", "")
                    if isinstance(content, list):
                        content = " ".join(
                            b.get("text", "")
                            for b in content
                            if b.get("type") == "text"
                        )
                    content_lower = content.lower().strip()
                    correction_patterns = [
                        "no,",
                        "no ",
                        "wrong",
                        "try again",
                        "that's not",
                        "thats not",
                        "not what i",
                    ]
                    if any(p in content_lower for p in correction_patterns):
                        signals["user_corrections"].append(
                            {
                                "text": content[:200],
                                "pattern": "correction",
                            }
                        )

                # Tool results — failure patterns
                if event_type == "tool_result" or (
                    event_type == "assistant"
                    and "tool_result" in str(event)
                ):
                    content = event.get("content", "")
                    if isinstance(content, list):
                        for block in content:
                            if block.get("type") == "tool_result":
                                result_text = block.get("content", "")
                                if isinstance(result_text, str):
                                    if (
                                        "Exit code:" in result_text
                                        and "Exit code: 0" not in result_text
                                    ):
                                        signals["tool_failures"].append(
                                            {
                                                "tool": "Bash",
                                                "error": result_text[:200],
                                            }
                                        )
                                    if (
                                        "file not found"
                                        in result_text.lower()
                                        or "no such file"
                                        in result_text.lower()
                                    ):
                                        signals["tool_failures"].append(
                                            {
                                                "tool": "Read",
                                                "error": result_text[:200],
                                            }
                                        )
                                    if (
                                        "old_string not found"
                                        in result_text.lower()
                                    ):
                                        signals["tool_failures"].append(
                                            {
                                                "tool": "Edit",
                                                "error": result_text[:200],
                                            }
                                        )

                # Track commands for repetition detection
                if event_type == "assistant":
                    msg_content = event.get("message", {}).get("content", [])
                    if isinstance(msg_content, list):
                        for block in msg_content:
                            if (
                                block.get("type") == "tool_use"
                                and block.get("name") == "Bash"
                            ):
                                cmd = block.get("input", {}).get("command", "")
                                if cmd:
                                    command_counts[cmd] = (
                                        command_counts.get(cmd, 0) + 1
                                    )
    except OSError:
        pass

    # Find repeated commands (3+)
    for cmd, count in command_counts.items():
        if count >= 3:
            signals["repeated_commands"].append(
                {
                    "command": cmd[:200],
                    "count": count,
                }
            )

    return signals


def extract_enforcement_signals(state_path):
    """Extract stop hook enforcement signals from workflow state."""
    signals = {
        "stop_hook_count": 0,
        "stop_hook_blocked": False,
        "stop_hook_last": None,
    }
    try:
        with open(state_path, "r") as f:
            state = json.load(f)
        signals["stop_hook_count"] = state.get("stop_hook_count", 0)
        signals["stop_hook_blocked"] = state.get("stop_hook_blocked", False)
        signals["stop_hook_last"] = state.get("stop_hook_last")
    except (json.JSONDecodeError, OSError):
        pass
    return signals


def _encode_project_dir(path):
    """Encode a filesystem path into the Claude Code project directory name.

    Claude Code stores per-project data under ``~/.claude/projects/<encoded>``,
    where ``<encoded>`` is the absolute path with ``:``, ``/``, ``\\``, and ``.``
    replaced by ``-``.
    """
    import re

    path = os.path.abspath(path)
    return re.sub(r"[:\\/.]", "-", path)


def discover_transcripts(worktree_path):
    """Find all transcript files (JSONL, logs) for *this* worktree only."""
    transcripts = []
    # Pipeline stage logs
    stages_dir = os.path.join(worktree_path, ".workflow", "stages")
    if os.path.isdir(stages_dir):
        for f in os.listdir(stages_dir):
            if f.endswith(".jsonl"):
                transcripts.append(os.path.join(stages_dir, f))
    # Interactive session transcripts (Claude Code stores these)
    claude_dir = os.path.expanduser("~/.claude/projects")
    if os.path.isdir(claude_dir):
        encoded = _encode_project_dir(worktree_path)
        for entry in os.listdir(claude_dir):
            if entry != encoded:
                continue
            project_dir = os.path.join(claude_dir, entry)
            if not os.path.isdir(project_dir):
                continue
            for root, _dirs, files in os.walk(project_dir):
                for f in files:
                    if f.endswith(".jsonl"):
                        transcripts.append(os.path.join(root, f))
    return transcripts
