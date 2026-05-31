"""Transcript extraction helpers for retrospective analysis."""

import json
import os
import re
import subprocess
import sys

_SCRIPTS_DIR = os.path.dirname(os.path.abspath(__file__))
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from claude_dispatch import derive_slug
from _shakedown_allowlist import SHAKEDOWN_ALLOWLIST, is_allowlisted

# Correction patterns: direct commands + question-form (per #1097 fix).
# Question-form corrections often arrive as interrogatives rather than imperatives
# ("why is the PR ready but the monitor hasn't been run?") and were previously missed.
_CORRECTION_PATTERNS = [
    "no,",
    "no ",
    "wrong",
    "try again",
    "that's not",
    "thats not",
    "not what i",
    "why ",
    "shouldn't ",
    "didn't you ",
    "weren't you supposed ",
    "isn't this ",
]

_FRUSTRATION_PATTERNS = [
    "wtf",
    "ugh",
    "dammit",
    "come on",
    "ffs",
    "this is broken",
    "this is wrong",
    "why isn't",
    "why can't",
]


def extract_transcript_signals(jsonl_path):
    """Extract structured signals from a JSONL transcript file."""
    signals = {
        "user_corrections": [],
        "tool_failures": [],
        "repeated_commands": [],
        "frustration_hits": [],
    }
    command_counts = {}
    tool_call_count = 0

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

                # Claude Code transcripts use "user", not "human" (bug #1097 fix 1).
                if event_type == "user":
                    content = event.get("message", {}).get("content", "")

                    if isinstance(content, list):
                        text = " ".join(
                            b.get("text", "")
                            for b in content
                            if isinstance(b, dict) and b.get("type") == "text"
                        )
                    else:
                        text = content if isinstance(content, str) else ""

                    text_lower = text.lower().strip()
                    if any(p in text_lower for p in _CORRECTION_PATTERNS):
                        signals["user_corrections"].append(
                            {"text": text[:200], "pattern": "correction"}
                        )
                    if any(p in text_lower for p in _FRUSTRATION_PATTERNS):
                        signals["frustration_hits"].append(
                            {"text": text[:200], "pattern": "frustration"}
                        )

                    # is_error: true is the primary tool-failure signal (bug #1097 fix 2).
                    # tool_result blocks live inside user events, not tool_result-type events.
                    if isinstance(content, list):
                        for block in content:
                            if not isinstance(block, dict) or block.get("type") != "tool_result":
                                continue
                            result_content = block.get("content", "")
                            if isinstance(result_content, list):
                                result_text = " ".join(
                                    b.get("text", "") for b in result_content
                                    if isinstance(b, dict) and b.get("type") == "text"
                                )
                            elif isinstance(result_content, str):
                                result_text = result_content
                            else:
                                result_text = ""

                            result_lower = result_text.lower()
                            tool_name = "unknown"
                            if "file not found" in result_lower or "no such file" in result_lower:
                                tool_name = "Read"
                            elif "old_string not found" in result_lower:
                                tool_name = "Edit"
                            elif "exit code:" in result_lower:
                                tool_name = "Bash"

                            if block.get("is_error") or tool_name != "unknown":
                                signals["tool_failures"].append(
                                    {"tool": tool_name, "error": result_text[:200]}
                                )

                if event_type == "assistant":
                    msg_content = event.get("message", {}).get("content", [])
                    if isinstance(msg_content, list):
                        for block in msg_content:
                            if not isinstance(block, dict) or block.get("type") != "tool_use":
                                continue
                            tool_call_count += 1
                            if block.get("name") == "Bash":
                                cmd = block.get("input", {}).get("command", "")
                                if cmd:
                                    command_counts[cmd] = (
                                        command_counts.get(cmd, 0) + 1
                                    )
    except OSError:
        pass

    for cmd, count in command_counts.items():
        if count >= 3:
            signals["repeated_commands"].append(
                {
                    "command": cmd[:200],
                    "count": count,
                }
            )

    u = len(signals["user_corrections"])
    t = len(signals["tool_failures"])
    r = len(signals["repeated_commands"])
    f = len(signals["frustration_hits"])
    signals["needs_manual_review"] = bool(u > 0 or t > 2 or r > 3 or f > 0)
    # A non-trivial session (>50 tool calls) with zero signals across all detectors
    # is suspicious — likely indicates the extractor missed something.
    all_zero = u == 0 and t == 0 and r == 0 and f == 0
    signals["signal_extractor_suspect"] = bool(all_zero and tool_call_count > 50)

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


def write_session_flags(findings_path, flags):
    """Merge escalation *flags* into ``.workflow/retro-findings.json``.

    Writes ``needs_manual_review`` and ``signal_extractor_suspect`` (and any
    other keys in *flags*) into the findings JSON. Creates the file (and
    parent dir) if missing. Existing keys are preserved.
    """
    if not flags:
        return flags
    os.makedirs(os.path.dirname(os.path.abspath(findings_path)) or ".", exist_ok=True)
    existing = {}
    try:
        with open(findings_path, "r", encoding="utf-8") as f:
            existing = json.load(f) or {}
    except (json.JSONDecodeError, OSError):
        existing = {}
    if not isinstance(existing, dict):
        existing = {}
    existing.update(flags)
    with open(findings_path, "w", encoding="utf-8") as f:
        json.dump(existing, f, indent=2, sort_keys=True)
        f.write("\n")
    return flags


def _encode_project_dir(path):
    """Encode a filesystem path into the Claude Code project directory name.

    Delegates to ``claude_dispatch.derive_slug`` so retro and the dispatcher
    stay in lockstep — Claude Code replaces every non-[A-Za-z0-9-] character
    with ``-``, not just ``:\\/.`` (the older retro rule missed underscores
    and dropped transcripts on Windows usernames like ``josh_``).
    """
    return derive_slug(path)


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
    # Desktop app (Windows): AppData/Roaming/Claude/claude-code-sessions/<userId>/<machineId>/local_*.json
    # Each manifest carries cliSessionId pointing to ~/.claude/projects/<slug>/<cliSessionId>.jsonl
    # Use a normalized seen-set so mixed separators (/ vs \) don't cause duplicates.
    seen_norm = {os.path.normcase(os.path.abspath(t)) for t in transcripts}
    normed_worktree = os.path.normcase(os.path.abspath(worktree_path))
    appdata = os.environ.get("APPDATA") or os.path.join(
        os.path.expanduser("~"), "AppData", "Roaming"
    )
    ccd_root = os.path.join(appdata, "Claude", "claude-code-sessions")
    if os.path.isdir(ccd_root):
        for uid in os.listdir(ccd_root):
            uid_path = os.path.join(ccd_root, uid)
            if not os.path.isdir(uid_path):
                continue
            for mid in os.listdir(uid_path):
                mid_path = os.path.join(uid_path, mid)
                if not os.path.isdir(mid_path):
                    continue
                for fname in os.listdir(mid_path):
                    if not (fname.startswith("local_") and fname.endswith(".json")):
                        continue
                    manifest_path = os.path.join(mid_path, fname)
                    try:
                        with open(manifest_path, "r", encoding="utf-8", errors="replace") as mf:
                            manifest = json.load(mf)
                    except (json.JSONDecodeError, OSError):
                        continue
                    if not isinstance(manifest, dict):
                        continue
                    cwd = manifest.get("worktreePath") or manifest.get("cwd") or ""
                    if not cwd:
                        continue
                    if os.path.normcase(os.path.abspath(cwd)) != normed_worktree:
                        continue
                    cli_id = manifest.get("cliSessionId")
                    if not cli_id:
                        continue
                    slug = _encode_project_dir(cwd)
                    p = os.path.join(
                        os.path.expanduser("~"), ".claude", "projects", slug, cli_id + ".jsonl"
                    )
                    if not os.path.isfile(p):
                        continue
                    p_norm = os.path.normcase(os.path.abspath(p))
                    if p_norm in seen_norm:
                        continue
                    if since is not None:
                        try:
                            if os.stat(p).st_mtime < since:
                                continue
                        except OSError:
                            continue
                    transcripts.append(p)
                    seen_norm.add(p_norm)
    return transcripts


def _git_log(args, cwd=None):
    """Run ``git log`` and return stdout text, empty string on failure.

    Two configuration choices matter here for path handling:

    - ``-c core.quotePath=off`` disables git's C-style escaping of paths
      with spaces, unicode, or backslashes. Without it, a path like
      ``scripts/é.py`` arrives as ``"scripts/\\303\\251.py"`` with octal
      escapes — disabling at the source is more correct than trying to
      unescape on the consumer side.
    - ``encoding="utf-8"`` overrides Python's default of decoding via the
      system codepage (cp1252 on Windows), which would otherwise turn
      git's UTF-8 ``é`` (bytes ``0xC3 0xA9``) into the two-char string
      ``Ã©`` and break disk-path matching against the actual NTFS entry.
    """
    try:
        out = subprocess.run(
            ["git", "-c", "core.quotePath=off", "log", *args],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            cwd=cwd,
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired):
        return ""
    if out.returncode != 0:
        return ""
    return out.stdout


# Match ``/verify``, ``verify:``, ``verify(...)``, ``verify passed``,
# ``verify ok`` only when they appear at the START of the commit subject.
# Substring matching would false-positive on subjects like
# ``feat(verify): tighten marker`` (which references /verify but is not one).
_VERIFY_SUBJECT_RE = re.compile(
    r"^\s*(?:/verify\b|verify[:(]|verify\s+(?:passed|ok)\b)",
    re.IGNORECASE,
)


def _commits_since_verify(cwd=None):
    """Return commit SHAs (oldest -> newest) authored after the latest
    ``/verify`` marker.

    The marker is a commit whose subject *starts* with ``/verify`` or
    ``verify:``/``verify(``/``verify passed``/``verify ok``. If no such
    commit is found, return ``[]`` (the detector then has nothing to flag).
    """
    log = _git_log(["--reverse", "--format=%H%x09%s", "-n", "1000"], cwd=cwd)
    if not log:
        return []
    rows = [line.split("\t", 1) for line in log.splitlines() if "\t" in line]
    verify_idx = None
    for i, (_sha, subject) in enumerate(rows):
        if _VERIFY_SUBJECT_RE.match(subject):
            verify_idx = i
    if verify_idx is None:
        return []
    return [sha for sha, _s in rows[verify_idx + 1:]]


def _commit_subject(sha, cwd=None):
    return _git_log(["-1", "--format=%s", sha], cwd=cwd).strip()


def _commit_files(sha, cwd=None):
    # ``_git_log`` runs with ``core.quotePath=off`` so paths arrive raw
    # (no surrounding quotes, no C-style escapes) regardless of git config.
    out = _git_log(["-1", "--name-only", "--format=", sha], cwd=cwd)
    return [
        line.strip().replace("\\", "/")
        for line in out.splitlines()
        if line.strip()
    ]


_FIX_PREFIX_RE = ("fix", "bug", "hotfix", "patch", "revert")
# Lowercase-by-contract: ``_spawns_subprocess`` lowercases the file content
# before matching, so every hint here must be lowercase. Adding an uppercase
# hint would silently never match.
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
