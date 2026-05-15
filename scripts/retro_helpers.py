"""Transcript extraction helpers for retrospective analysis."""

import json
import os
import subprocess
import sys

_SCRIPTS_DIR = os.path.dirname(os.path.abspath(__file__))
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from claude_dispatch import derive_slug
from _shakedown_allowlist import SHAKEDOWN_ALLOWLIST, is_allowlisted


def extract_transcript_signals(jsonl_path):
    """Extract structured signals from a JSONL transcript file."""
    signals = {
        "user_corrections": [],
        "tool_failures": [],
        "repeated_commands": [],
    }
    command_counts = {}

    try:
        with open(jsonl_path, "r", encoding="utf-8", errors="replace") as f:
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
                    and isinstance(event.get("message", {}).get("content"), list)
                    and any(b.get("type") == "tool_result" for b in event["message"]["content"] if isinstance(b, dict))
                ):
                    if event_type == "assistant":
                        content = event.get("message", {}).get("content", [])
                    else:
                        content = event.get("content", "")

                    # Normalize: wrap string content into a list for uniform processing
                    if isinstance(content, str):
                        result_texts = [content] if content else []
                    elif isinstance(content, list):
                        result_texts = []
                        for block in content:
                            if isinstance(block, dict) and block.get("type") == "tool_result":
                                rt = block.get("content", "")
                                if isinstance(rt, str):
                                    result_texts.append(rt)
                    else:
                        result_texts = []

                    for result_text in result_texts:
                        if "Exit code:" in result_text and "Exit code: 0" not in result_text:
                            signals["tool_failures"].append(
                                {"tool": "Bash", "error": result_text[:200]}
                            )
                        if "file not found" in result_text.lower() or "no such file" in result_text.lower():
                            signals["tool_failures"].append(
                                {"tool": "Read", "error": result_text[:200]}
                            )
                        if "old_string not found" in result_text.lower():
                            signals["tool_failures"].append(
                                {"tool": "Edit", "error": result_text[:200]}
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
        with open(state_path, "r", encoding="utf-8") as f:
            state = json.load(f)
        signals["stop_hook_count"] = state.get("stop_hook_count", 0)
        signals["stop_hook_blocked"] = state.get("stop_hook_blocked", False)
        signals["stop_hook_last"] = state.get("stop_hook_last")
    except (json.JSONDecodeError, OSError):
        pass
    return signals


def _encode_project_dir(path):
    """Encode a filesystem path into the Claude Code project directory name.

    Delegates to ``claude_dispatch.derive_slug`` so retro and the dispatcher
    stay in lockstep — Claude Code replaces every non-[A-Za-z0-9-] character
    with ``-``, not just ``:\\/.`` (the older retro rule missed underscores
    and dropped transcripts on Windows usernames like ``josh_``).
    """
    return derive_slug(os.path.abspath(path))


def discover_transcripts(worktree_path, since=None):
    """Find transcript files (JSONL, logs) for *this* worktree only.

    Args:
        worktree_path: filesystem path of the worktree.
        since: optional epoch-seconds floor; transcripts with ``st_mtime``
            older than this value are skipped. Lets retro narrow noise from
            unrelated sessions stored under the same project slug.
    """
    transcripts = []
    # Pipeline stage logs
    stages_dir = os.path.join(worktree_path, ".workflow", "stages")
    if os.path.isdir(stages_dir):
        for f in os.listdir(stages_dir):
            if f.endswith(".jsonl"):
                p = os.path.join(stages_dir, f)
                if since is not None:
                    try:
                        if os.stat(p).st_mtime < since:
                            continue
                    except OSError:
                        continue
                transcripts.append(p)
    # Interactive session transcripts (Claude Code stores these)
    claude_dir = os.path.expanduser("~/.claude/projects")
    if os.path.isdir(claude_dir):
        encoded = _encode_project_dir(worktree_path)
        project_dir = os.path.join(claude_dir, encoded)
        if os.path.isdir(project_dir):
            for root, _dirs, files in os.walk(project_dir):
                for f in files:
                    if not f.endswith(".jsonl"):
                        continue
                    p = os.path.join(root, f)
                    if since is not None:
                        try:
                            if os.stat(p).st_mtime < since:
                                continue
                        except OSError:
                            continue
                    transcripts.append(p)
    return transcripts


def _git_log(args, cwd=None):
    """Run ``git log`` and return stdout text, empty string on failure."""
    try:
        out = subprocess.run(
            ["git", "log", *args],
            capture_output=True,
            text=True,
            cwd=cwd,
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired):
        return ""
    if out.returncode != 0:
        return ""
    return out.stdout


def _commits_since_verify(cwd=None):
    """Return commit SHAs (oldest -> newest) authored after the latest
    ``/verify`` marker.

    Heuristic: the marker is a commit whose subject matches ``/verify`` or
    ``verify:``/``verify(``/``verify passed``. If no such commit is found,
    return ``[]`` (the detector then has nothing to flag).
    """
    log = _git_log(["--reverse", "--format=%H%x09%s", "-n", "200"], cwd=cwd)
    if not log:
        return []
    rows = [line.split("\t", 1) for line in log.splitlines() if "\t" in line]
    verify_idx = None
    markers = ("/verify", "verify:", "verify(", "verify passed", "verify ok")
    for i, (_sha, subject) in enumerate(rows):
        s = subject.lower()
        if any(m in s for m in markers):
            verify_idx = i
    if verify_idx is None:
        return []
    return [sha for sha, _s in rows[verify_idx + 1:]]


def _commit_subject(sha, cwd=None):
    return _git_log(["-1", "--format=%s", sha], cwd=cwd).strip()


def _commit_files(sha, cwd=None):
    out = _git_log(["-1", "--name-only", "--format=", sha], cwd=cwd)
    return [line.strip().replace("\\", "/") for line in out.splitlines() if line.strip()]


_FIX_PREFIX_RE = ("fix", "bug", "hotfix", "patch", "revert")
_SUBPROCESS_HINTS = ("subprocess", "claude_dispatch", "spawn", "popen", "claude --bg", "claude -p")


def _looks_like_fix(subject):
    s = subject.lower().lstrip()
    return any(s.startswith(p) for p in _FIX_PREFIX_RE)


def _spawns_subprocess(path, cwd=None):
    """Heuristic: file imports subprocess or references claude_dispatch."""
    try:
        full = os.path.join(cwd or ".", path)
        with open(full, "r", encoding="utf-8", errors="replace") as f:
            head = f.read(8192).lower()
    except OSError:
        return False
    return any(h in head for h in _SUBPROCESS_HINTS)


def detect_allowlist_drift(cwd=None, post_verify_commits=None):
    """Return drift proposals for files touched by post-/verify fix commits.

    A proposal is generated for any subprocess-spawning file NOT in the
    shakedown allowlist that received >=1 fix commit after ``/verify``.
    Each proposal carries the file path, the fix-commit count, and the
    suggested allowlist line so a human can rubber-stamp the addition.

    Args:
        cwd: repository root; defaults to current working directory.
        post_verify_commits: pre-computed list of commit SHAs to inspect
            (testing seam). When ``None``, derived from ``git log``.

    Returns:
        list[dict]: ``[{"file": ..., "fix_count": N, "proposal": str,
        "commits": [sha, ...]}]``.
    """
    if post_verify_commits is None:
        post_verify_commits = _commits_since_verify(cwd=cwd)
    if not post_verify_commits:
        return []

    file_to_commits = {}
    for sha in post_verify_commits:
        subject = _commit_subject(sha, cwd=cwd)
        if not _looks_like_fix(subject):
            continue
        for path in _commit_files(sha, cwd=cwd):
            if is_allowlisted(path):
                continue
            if not _spawns_subprocess(path, cwd=cwd):
                continue
            file_to_commits.setdefault(path, []).append(sha)

    proposals = []
    for path, shas in sorted(file_to_commits.items()):
        count = len(shas)
        proposals.append({
            "file": path,
            "fix_count": count,
            "commits": shas,
            "proposal": (
                f"Add {path} to shakedown allowlist "
                f"({count} post-/verify fix{'es' if count != 1 else ''} this PR)"
            ),
        })
    return proposals


def write_drift_proposals(findings_path, proposals):
    """Merge *proposals* into ``.workflow/retro-findings.json``.

    Schema (additive): root dict gains ``allowlist_drift`` key holding the
    list of proposals. Other keys are preserved. Creates the file (and
    parent dir) if missing. Returns the proposals list (echoed for caller
    convenience). When *proposals* is empty, no file is created.
    """
    if not proposals:
        return proposals
    os.makedirs(os.path.dirname(os.path.abspath(findings_path)) or ".", exist_ok=True)
    existing = {}
    try:
        with open(findings_path, "r", encoding="utf-8") as f:
            existing = json.load(f) or {}
    except (json.JSONDecodeError, OSError):
        existing = {}
    if not isinstance(existing, dict):
        existing = {}
    existing["allowlist_drift"] = proposals
    with open(findings_path, "w", encoding="utf-8") as f:
        json.dump(existing, f, indent=2, sort_keys=True)
        f.write("\n")
    return proposals
