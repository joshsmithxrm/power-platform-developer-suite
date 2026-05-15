#!/usr/bin/env python3
"""
Recover a Claude Code session that's invisible to the resume picker.

A workflow helper for the `/recover-session` skill. Mechanics only —
the skill drives the operator dialogue and decision points.

Subcommands (Phases 1–4 ship `identify` + `diagnose` + `restore` +
`prepare` per specs/recover-session.md).

Usage:
    python scripts/recover-session.py identify --query "<phrase>"
    python scripts/recover-session.py diagnose --session <uuid>
            [--ccd-sessions-file <path>]
    python scripts/recover-session.py restore  --session <uuid>
            [--ccd-sessions-file <path>]
    python scripts/recover-session.py prepare  --session <uuid>
            [--ccd-sessions-file <path>]

Output contract (per Constitution I1):
    stdout: JSON only (data)
    stderr: prose status / error messages

Exit codes:
    0 — success (identify returned >=0 candidates; an empty list is success)
    2 — input validation error (e.g., query too short)
    3 — environment error (e.g., ~/.claude/projects not found)

Design notes (see specs/recover-session.md Design Decisions for full
rationale):
    - Rank candidates by transcript line-1 (first user message), not by
      raw keyword hit count. Originating-session bug 2026-05-15: ranking
      by hit count picked the wrong session because UUID fragments in
      the JSONL inflated counts.
    - Skip subagent transcripts: nested .jsonl files under
      <session>/subagents/ are not sessions in their own right.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterator

# Read budget per transcript to avoid OOM on multi-MB JSONLs.
# Most sessions are <5MB; the body scan caps here.
MAX_SCAN_BYTES = 5 * 1024 * 1024


def _projects_dir() -> Path:
    """Return the conventional projects dir; overridable via env for tests."""
    override = os.environ.get("RECOVER_SESSION_PROJECTS_DIR")
    if override:
        return Path(override)
    return Path(os.path.expanduser("~/.claude/projects"))


@dataclass(frozen=True)
class DiagnoseResult:
    """Diagnosis verdict for a session.

    See specs/recover-session.md "Core Types" for field semantics.
    `is_archived` is `None` when the caller didn't supply CCD session
    state — the skill is expected to fetch it via MCP and merge.
    """

    session_id: str
    transcript_exists: bool
    is_archived: bool | None
    worktree_exists: bool
    branch_exists: bool
    entrypoint: str
    recoverable: bool
    next_action: str
    recorded_cwd: str
    branch: str
    repo_root: str


@dataclass(frozen=True)
class IdentifyResult:
    """One candidate session, matched against the operator's query.

    See specs/recover-session.md "Core Types" for field semantics.
    """

    session_id: str
    title: str
    cwd: str
    entrypoint: str
    first_user_message: str
    last_activity_ts: str
    match_score: int


# ---------------------------------------------------------------------------
# Pure helpers (no subprocess, no I/O beyond the explicit path argument)
# ---------------------------------------------------------------------------


def iter_top_level_transcripts(projects_dir: Path) -> Iterator[Path]:
    """Yield top-level session transcripts, skipping subagent transcripts.

    Layout:
        <projects_dir>/<encoded-cwd>/<session-uuid>.jsonl     <- yield
        <projects_dir>/<encoded-cwd>/<session-uuid>/subagents/<x>.jsonl  <- skip

    The "skip subagents" rule lives in code so the caller (and tests)
    can't bypass it accidentally.
    """
    if not projects_dir.exists():
        return
    for project_dir in sorted(projects_dir.iterdir()):
        if not project_dir.is_dir():
            continue
        for entry in sorted(project_dir.iterdir()):
            if entry.is_file() and entry.suffix == ".jsonl":
                yield entry


def read_first_user_message(transcript: Path, max_bytes: int = MAX_SCAN_BYTES) -> str:
    """Return the first user-typed message text in the transcript.

    Walks JSONL line-by-line, returning the first record where:
        type == "user" AND message.content is a non-empty string
        (or a list whose first text element is non-empty).

    Returns "" if no qualifying message is found within max_bytes.

    Note: a session's leading lines are typically `queue-operation`
    entries and `attachment` hook records; the first `user`-typed
    message is what the operator wrote. THAT is the disambiguator.
    """
    bytes_read = 0
    try:
        with transcript.open("r", encoding="utf-8", errors="replace") as f:
            for line in f:
                bytes_read += len(line)
                if bytes_read > max_bytes:
                    return ""
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("type") != "user":
                    continue
                msg = obj.get("message")
                if not isinstance(msg, dict):
                    continue
                content = msg.get("content")
                text = _extract_text(content)
                if text:
                    return text
    except OSError:
        return ""
    return ""


def _extract_text(content) -> str:
    """Pull plain text out of message.content (string or list-of-blocks)."""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: list[str] = []
        for block in content:
            if isinstance(block, dict):
                text = block.get("text")
                if isinstance(text, str):
                    parts.append(text)
            elif isinstance(block, str):
                parts.append(block)
        return "\n".join(parts)
    return ""


def read_session_metadata(transcript: Path) -> dict:
    """Extract cwd, entrypoint, gitBranch, version from the transcript.

    These fields appear on attachment / hook lines emitted by
    SessionStart. The first occurrence wins; later lines repeat them.
    """
    fields = ("cwd", "entrypoint", "gitBranch", "version", "sessionId")
    found: dict = {}
    try:
        with transcript.open("r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                for k in fields:
                    if k not in found and obj.get(k):
                        found[k] = obj[k]
                if all(k in found for k in fields):
                    break
    except OSError:
        pass
    return found


def scan_for_query(transcript: Path, query: str, max_bytes: int = MAX_SCAN_BYTES) -> tuple[bool, bool]:
    """Return (line1_hit, body_hit) for the given query.

    line1_hit  — query (case-insensitive) appears in the first user message.
    body_hit   — query appears anywhere in the scanned bytes (excluding line 1).

    A transcript with the query ONLY in line 1 returns (True, False).
    A transcript with the query in line 1 AND elsewhere returns (True, True).
    A transcript with the query only outside line 1 returns (False, True).
    """
    query_lower = query.lower()
    first_user = read_first_user_message(transcript, max_bytes=max_bytes)
    line1_hit = query_lower in first_user.lower() if first_user else False
    body_hit = False
    bytes_read = 0
    try:
        with transcript.open("r", encoding="utf-8", errors="replace") as f:
            for line in f:
                bytes_read += len(line)
                if bytes_read > max_bytes:
                    break
                # Approximate "outside line 1": skip the literal first user
                # message text we already captured. Cheap and good enough —
                # if the query string appears anywhere else in the file
                # we count it as a body hit.
                if query_lower in line.lower():
                    if first_user and query_lower in first_user.lower():
                        # Skip the (single) occurrence inside the first user
                        # message to avoid double-counting it as a body hit.
                        # We just check: did this line contain the first-user
                        # text? Approximate via substring match.
                        if first_user and first_user[:200].lower() in line.lower():
                            continue
                    body_hit = True
                    break
    except OSError:
        pass
    return line1_hit, body_hit


def score_match(line1_hit: bool, body_hit: bool) -> int:
    """Rank score: line-1 dominates body.

    100 — line-1 hit (regardless of body)
     10 — body-only hit
      0 — no hit (caller should drop)
    """
    if line1_hit:
        return 100
    if body_hit:
        return 10
    return 0


def last_activity_iso(transcript: Path) -> str:
    """File mtime as ISO 8601 — cheap proxy for last activity."""
    try:
        mtime = transcript.stat().st_mtime
    except OSError:
        return ""
    from datetime import datetime, timezone

    return datetime.fromtimestamp(mtime, tz=timezone.utc).isoformat()


def build_identify_result(transcript: Path, query: str) -> IdentifyResult | None:
    """Score one transcript against the query; return None if no match."""
    line1, body = scan_for_query(transcript, query)
    score = score_match(line1, body)
    if score == 0:
        return None
    meta = read_session_metadata(transcript)
    first_msg = read_first_user_message(transcript)
    return IdentifyResult(
        session_id=transcript.stem,
        title="",  # CCD title lookup is the skill's job; helper stays filesystem-only
        cwd=meta.get("cwd", ""),
        entrypoint=meta.get("entrypoint", "unknown"),
        first_user_message=first_msg[:400],
        last_activity_ts=last_activity_iso(transcript),
        match_score=score,
    )


def rank_results(results: list[IdentifyResult]) -> list[IdentifyResult]:
    """Sort by match_score DESC, then last_activity_ts DESC.

    A line-1 match (score 100) always outranks a body-only match
    (score 10), regardless of recency. Within the same score band,
    the most-recently-active session wins.
    """
    return sorted(
        results,
        key=lambda r: (-r.match_score, -_iso_epoch(r.last_activity_ts)),
    )


def _iso_epoch(iso: str) -> float:
    """Parse ISO 8601 to epoch seconds; 0 on failure (oldest)."""
    if not iso:
        return 0.0
    from datetime import datetime

    try:
        return datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()
    except (ValueError, TypeError):
        return 0.0


# ---------------------------------------------------------------------------
# Diagnose helpers — pure-Python wrappers around git/CCD state.
# ---------------------------------------------------------------------------


def find_transcript_by_uuid(session_id: str, projects_dir: Path) -> Path | None:
    """Return the first top-level transcript matching <session_id>.jsonl.

    Walks the same top-level-only set that identify uses, so subagent
    JSONLs cannot masquerade as a session.
    """
    if not session_id or not projects_dir.exists():
        return None
    for transcript in iter_top_level_transcripts(projects_dir):
        if transcript.stem == session_id:
            return transcript
    return None


def resolve_repo_root_from_cwd(recorded_cwd: str) -> str:
    """Walk up from recorded_cwd looking for the main repo root.

    A worktree's `.git` is a *file* pointing at `<main>/.git/worktrees/<n>`.
    A main repo has a `.git` *directory*. Both contain `commondir` info.
    We resolve to the directory that contains a `.git` entry — caller can
    verify it's a repo by attempting `git -C <root> rev-parse --git-dir`.

    Returns "" if no .git found by walking up.
    """
    if not recorded_cwd:
        return ""
    p = Path(recorded_cwd).resolve() if Path(recorded_cwd).exists() else Path(recorded_cwd)
    # Walk up. Stop at filesystem root.
    seen: set[str] = set()
    while str(p) not in seen:
        seen.add(str(p))
        git_entry = p / ".git"
        if git_entry.exists():
            # If recorded_cwd is itself a worktree, the main repo root is
            # the parent referenced in .git/commondir. We prefer to return
            # the *main* repo root so callers can `git -C <root> worktree add`
            # without that command being confused by being inside a worktree.
            return _find_main_repo_root(p)
        parent = p.parent
        if parent == p:
            break
        p = parent
    return ""


def _find_main_repo_root(start: Path) -> str:
    """Given a path whose .git points into a worktree, return the main repo path."""
    git_entry = start / ".git"
    try:
        if git_entry.is_dir():
            return str(start).replace("\\", "/")
        if git_entry.is_file():
            # gitdir: C:/path/to/main/.git/worktrees/<name>
            content = git_entry.read_text(encoding="utf-8", errors="replace").strip()
            if content.startswith("gitdir:"):
                gitdir = Path(content.split(":", 1)[1].strip())
                # Walk up from gitdir to find the directory containing the .git dir
                # gitdir = <main>/.git/worktrees/<name>; parent.parent = <main>/.git;
                # parent.parent.parent = <main>
                main_git = gitdir.parent.parent  # <main>/.git
                if main_git.name == ".git":
                    return str(main_git.parent).replace("\\", "/")
    except OSError:
        pass
    return str(start).replace("\\", "/")


def git_branch_exists(repo_root: str, branch: str) -> bool:
    """True iff `git -C <repo_root> branch --list <branch>` returns non-empty."""
    if not repo_root or not branch:
        return False
    try:
        proc = subprocess.run(
            ["git", "-C", repo_root, "branch", "--list", branch],
            capture_output=True, text=True, timeout=15,
            stdin=subprocess.DEVNULL,
        )
        return bool(proc.stdout.strip())
    except (subprocess.SubprocessError, FileNotFoundError):
        return False


def parse_worktree_porcelain(porcelain: str) -> list[str]:
    """Extract worktree paths from `git worktree list --porcelain` output."""
    paths: list[str] = []
    for line in porcelain.splitlines():
        if line.startswith("worktree "):
            paths.append(line[len("worktree "):].strip().replace("\\", "/"))
    return paths


def worktree_exists_at(repo_root: str, recorded_cwd: str) -> bool:
    """True iff recorded_cwd is registered as a worktree AND the dir exists.

    Both conditions matter: a registered worktree whose directory was
    rm-rf'd looks present to git until pruned, but Claude Desktop can't
    anchor to it. Conversely, a populated directory not registered with
    git is not a worktree.
    """
    if not repo_root or not recorded_cwd:
        return False
    try:
        proc = subprocess.run(
            ["git", "-C", repo_root, "worktree", "list", "--porcelain"],
            capture_output=True, text=True, timeout=15,
            stdin=subprocess.DEVNULL,
        )
    except (subprocess.SubprocessError, FileNotFoundError):
        return False
    registered = {p.lower() for p in parse_worktree_porcelain(proc.stdout)}
    target = recorded_cwd.replace("\\", "/").lower()
    return target in registered and Path(recorded_cwd).exists()


def _norm_path(p: str) -> str:
    """Normalize a path for cross-OS comparison: forward slashes, lowercased on win."""
    if not p:
        return ""
    norm = p.replace("\\", "/").rstrip("/")
    # On Windows, paths are case-insensitive; lowercase for comparison.
    if os.name == "nt":
        norm = norm.lower()
    return norm


def lookup_archived_in_ccd_sessions(
    ccd_sessions: list[dict],
    *,
    cwd: str = "",
    branch: str = "",
    session_id: str = "",
) -> bool | None:
    """Find the CCD session whose cwd or branch matches and return is_archived.

    The CCD `sessionId` (e.g., `local_0cd45537-...`) is a different UUID
    from the transcript filename stem (e.g., `fefdaf81-...`); they do
    not share a substring. The reliable link between a transcript and a
    CCD session is the absolute `cwd` (both record it identically) or,
    as a fallback, the `branch` name.

    Match priority: cwd exact > branch exact > sessionId substring
    (last-resort, kept for installations where the IDs do correlate).

    Returns None if no match.
    """
    if not ccd_sessions:
        return None
    norm_cwd = _norm_path(cwd)
    # Priority 1: cwd match
    if norm_cwd:
        for s in ccd_sessions:
            s_cwd = _norm_path(s.get("cwd", ""))
            if s_cwd and s_cwd == norm_cwd:
                return bool(s.get("isArchived", False))
    # Priority 2: branch match (multiple sessions can share a branch only
    # if you reused a worktree, but the active one is the unarchived one;
    # prefer the most-recent non-archived match)
    if branch:
        archived_candidates = []
        for s in ccd_sessions:
            if s.get("branch") == branch:
                archived_candidates.append(bool(s.get("isArchived", False)))
        if archived_candidates:
            # If any non-archived match exists, the session is not archived.
            return not any(not a for a in archived_candidates)
    # Priority 3: sessionId substring (last resort)
    if session_id:
        for s in ccd_sessions:
            sid = s.get("sessionId", "")
            if sid.endswith(session_id) or session_id.endswith(sid.removeprefix("local_")):
                return bool(s.get("isArchived", False))
    return None


def decide_next_action(
    transcript_exists: bool,
    branch_exists: bool,
    worktree_exists: bool,
    is_archived: bool | None,
) -> str:
    """State machine for the recovery next-step.

    See specs/recover-session.md Specification Flows A-D.
    """
    if not transcript_exists:
        return "unrecoverable-transcript-missing"
    if not branch_exists:
        return "unrecoverable-branch-missing"

    if is_archived is None:
        # Skill must fetch CCD state and re-call, OR present conditional handoff.
        if worktree_exists:
            return "check-archive-then-resume"
        return "restore-then-check-archive-then-resume"

    if is_archived and worktree_exists:
        return "unarchive-and-resume"
    if is_archived and not worktree_exists:
        return "restore-unarchive-resume"
    if not is_archived and not worktree_exists:
        return "restore-and-resume"
    return "resume"


# ---------------------------------------------------------------------------
# CLI entry
# ---------------------------------------------------------------------------


def cmd_identify(query: str, projects_dir: Path | None = None) -> int:
    if len(query) < 4:
        print(
            json.dumps({"error": "QueryTooShort", "min_chars": 4}),
            file=sys.stderr,
        )
        return 2
    pd = projects_dir or _projects_dir()
    if not pd.exists():
        print(
            json.dumps({"error": "ProjectsDirNotFound", "path": str(pd)}),
            file=sys.stderr,
        )
        return 3

    results: list[IdentifyResult] = []
    for transcript in iter_top_level_transcripts(pd):
        r = build_identify_result(transcript, query)
        if r is not None:
            results.append(r)

    ranked = rank_results(results)
    payload = [asdict(r) for r in ranked]
    print(json.dumps(payload, indent=2))
    return 0


def _diagnose_internal(
    session_id: str,
    projects_dir: Path,
    ccd_sessions_file: Path | None,
) -> DiagnoseResult:
    """Shared diagnosis logic — used by cmd_diagnose and cmd_restore."""
    transcript = find_transcript_by_uuid(session_id, projects_dir)
    if transcript is None:
        return DiagnoseResult(
            session_id=session_id, transcript_exists=False, is_archived=None,
            worktree_exists=False, branch_exists=False, entrypoint="unknown",
            recoverable=False, next_action="unrecoverable-transcript-missing",
            recorded_cwd="", branch="", repo_root="",
        )
    meta = read_session_metadata(transcript)
    recorded_cwd = meta.get("cwd", "")
    branch = meta.get("gitBranch", "")
    entrypoint = meta.get("entrypoint", "unknown")
    repo_root = resolve_repo_root_from_cwd(recorded_cwd)
    branch_ok = git_branch_exists(repo_root, branch) if (repo_root and branch) else False
    worktree_ok = worktree_exists_at(repo_root, recorded_cwd) if (repo_root and recorded_cwd) else False
    is_archived: bool | None = None
    if ccd_sessions_file and ccd_sessions_file.exists():
        try:
            sessions = json.loads(ccd_sessions_file.read_text(encoding="utf-8"))
            if isinstance(sessions, list):
                is_archived = lookup_archived_in_ccd_sessions(
                    sessions,
                    cwd=recorded_cwd,
                    branch=branch,
                    session_id=session_id,
                )
        except (OSError, json.JSONDecodeError):
            pass
    return DiagnoseResult(
        session_id=session_id, transcript_exists=True, is_archived=is_archived,
        worktree_exists=worktree_ok, branch_exists=branch_ok, entrypoint=entrypoint,
        recoverable=branch_ok or worktree_ok,
        next_action=decide_next_action(
            transcript_exists=True, branch_exists=branch_ok,
            worktree_exists=worktree_ok, is_archived=is_archived,
        ),
        recorded_cwd=recorded_cwd, branch=branch, repo_root=repo_root,
    )


def cmd_diagnose(
    session_id: str,
    projects_dir: Path | None = None,
    ccd_sessions_file: Path | None = None,
) -> int:
    pd = projects_dir or _projects_dir()
    result = _diagnose_internal(session_id, pd, ccd_sessions_file)
    print(json.dumps(asdict(result), indent=2))
    return 0


# ---------------------------------------------------------------------------
# Prepare helpers (Phase 4)
# ---------------------------------------------------------------------------


def git_ahead_behind(repo_root: str, branch: str, base: str = "origin/main") -> tuple[int, int]:
    """Return (ahead, behind) commit counts of `branch` relative to `base`.

    Uses `git rev-list --left-right --count <branch>...<base>` which
    emits two whitespace-separated integers: left=ahead, right=behind.

    Returns (0, 0) on any error — the caller renders this as "no delta
    available" rather than failing the whole prepare phase.
    """
    if not repo_root or not branch:
        return (0, 0)
    spec = f"{branch}...{base}"
    try:
        proc = subprocess.run(
            ["git", "-C", repo_root, "rev-list", "--left-right", "--count", spec],
            capture_output=True, text=True, timeout=15,
            stdin=subprocess.DEVNULL,
        )
    except (subprocess.SubprocessError, FileNotFoundError):
        return (0, 0)
    if proc.returncode != 0:
        return (0, 0)
    parts = proc.stdout.split()
    if len(parts) != 2:
        return (0, 0)
    try:
        return (int(parts[0]), int(parts[1]))
    except ValueError:
        return (0, 0)


def gh_pr_list_merged_since(since_iso: str, limit: int = 30) -> list[dict]:
    """Return PRs merged on/after `since_iso` (best-effort).

    Uses `gh pr list --state merged --search "merged:>=<date>"`. If
    `gh` is unavailable, returns []. The skill renders a softer
    catch-up in that case.
    """
    if not since_iso:
        return []
    # Trim to date portion for gh's search syntax (it accepts YYYY-MM-DD).
    date_part = since_iso.split("T", 1)[0]
    try:
        proc = subprocess.run(
            ["gh", "pr", "list",
             "--state", "merged",
             "--search", f"merged:>={date_part}",
             "--limit", str(limit),
             "--json", "number,title,mergedAt"],
            capture_output=True, text=True, timeout=20,
            stdin=subprocess.DEVNULL,
        )
    except (subprocess.SubprocessError, FileNotFoundError):
        return []
    if proc.returncode != 0:
        return []
    try:
        data = json.loads(proc.stdout)
        if isinstance(data, list):
            return data
    except json.JSONDecodeError:
        pass
    return []


def scan_transcript_for_issue_refs(transcript: Path, max_bytes: int = MAX_SCAN_BYTES) -> list[int]:
    """Find #NNN issue references in the transcript. Returns sorted unique ints.

    Uses a negative lookbehind `(?<!\\w)#(...)\\b` so the `#` cannot be
    preceded by a word character — guards against UUID-fragment false
    positives like `deadbeef#1234` where the digits aren't an actual
    issue reference.
    """
    import re as _re

    pattern = _re.compile(r"(?<!\w)#(\d{2,6})\b")
    found: set[int] = set()
    bytes_read = 0
    try:
        with transcript.open("r", encoding="utf-8", errors="replace") as f:
            for line in f:
                bytes_read += len(line)
                if bytes_read > max_bytes:
                    break
                for m in pattern.finditer(line):
                    try:
                        found.add(int(m.group(1)))
                    except ValueError:
                        continue
    except OSError:
        return []
    return sorted(found)


def format_catch_up_message(
    diag: DiagnoseResult,
    ahead_behind: tuple[int, int],
    merged_prs: list[dict],
    issue_refs: list[int],
) -> str:
    """Render the operator-facing catch-up text."""
    ahead, behind = ahead_behind
    lines: list[str] = []
    lines.append(
        f"Recovered session `{diag.session_id}` on branch `{diag.branch}`. "
        "Before continuing, reconcile what's changed while the session was dark:"
    )
    lines.append("")
    if behind:
        lines.append(
            f"- Branch is {behind} commit{'s' if behind != 1 else ''} BEHIND `origin/main` "
            f"(and {ahead} ahead). Run `git pull --rebase origin main` to catch up. "
            "If `PublicAPI.Unshipped.txt` conflicts, accept `--theirs` "
            "(see CLAUDE.md NEVER-rule on rebase API drift)."
        )
    else:
        lines.append(f"- Branch is up to date with `origin/main` ({ahead} ahead).")

    if merged_prs:
        lines.append("")
        lines.append(f"- {len(merged_prs)} PR{'s' if len(merged_prs) != 1 else ''} merged into main since "
                     f"this session went quiet ({diag.session_id}):")
        for pr in merged_prs[:10]:
            num = pr.get("number", "?")
            title = pr.get("title", "(no title)")
            lines.append(f"    - #{num} — {title}")
        if len(merged_prs) > 10:
            lines.append(f"    - … and {len(merged_prs) - 10} more")
        lines.append(
            "  Check whether any of these are work this session was about to dispatch."
        )
    else:
        lines.append("")
        lines.append("- No merged-PR delta available (gh unavailable, or none merged).")

    if issue_refs:
        lines.append("")
        refs = ", ".join(f"#{n}" for n in issue_refs[:20])
        lines.append(f"- Issues referenced in this session's transcript: {refs}")
        lines.append("  Run `gh issue view <N>` for each to confirm current state before redispatching.")

    lines.append("")
    lines.append("Once you've reconciled, resume the original task.")
    return "\n".join(lines)


def cmd_prepare(
    session_id: str,
    projects_dir: Path | None = None,
    ccd_sessions_file: Path | None = None,
    gh_fetcher=None,  # injection seam for tests
) -> int:
    pd = projects_dir or _projects_dir()
    diag = _diagnose_internal(session_id, pd, ccd_sessions_file)

    if not diag.transcript_exists:
        print(json.dumps({
            "prepared": False,
            "reason": "TranscriptNotFound",
            "session_id": session_id,
        }))
        return 1

    transcript = find_transcript_by_uuid(session_id, pd)
    # transcript can't be None here because diag.transcript_exists is True.
    assert transcript is not None
    issue_refs = scan_transcript_for_issue_refs(transcript)
    ahead_behind = git_ahead_behind(diag.repo_root, diag.branch)
    since = last_activity_iso(transcript)
    fetcher = gh_fetcher if gh_fetcher is not None else gh_pr_list_merged_since
    merged_prs = fetcher(since)

    catch_up = format_catch_up_message(diag, ahead_behind, merged_prs, issue_refs)
    payload = {
        "prepared": True,
        "session_id": session_id,
        "branch": diag.branch,
        "branch_delta": {"ahead": ahead_behind[0], "behind": ahead_behind[1]},
        "merged_prs": merged_prs,
        "issue_refs": issue_refs,
        "catch_up_message": catch_up,
        "resume_command": f"cd {diag.recorded_cwd} && claude --resume {session_id}",
    }
    print(json.dumps(payload, indent=2))
    return 0


def cmd_restore(
    session_id: str,
    projects_dir: Path | None = None,
    ccd_sessions_file: Path | None = None,
) -> int:
    """Recreate the session's worktree at its recorded cwd.

    Safety guards (per Design Decisions in specs/recover-session.md):
      - Target path must be absolute (rejects NestedWorktreeRefused class)
      - Uses `git -C <repo_root>` so the caller's cwd cannot influence
        the resolution.
      - Refuses cleanly when the worktree already exists or when the
        branch is checked out in a different worktree.
    """
    pd = projects_dir or _projects_dir()
    diag = _diagnose_internal(session_id, pd, ccd_sessions_file)

    if not diag.transcript_exists:
        print(json.dumps({
            "restored": False,
            "reason": "TranscriptNotFound",
            "session_id": session_id,
        }))
        return 1

    if not diag.branch_exists:
        print(json.dumps({
            "restored": False,
            "reason": "BranchNotFound",
            "session_id": session_id,
            "branch": diag.branch,
            "hint": "use `git reflog` to find the branch tip and recreate manually",
        }))
        return 1

    target = diag.recorded_cwd
    if not target:
        print(json.dumps({
            "restored": False,
            "reason": "NoRecordedCwd",
            "session_id": session_id,
        }))
        return 1

    if not os.path.isabs(target):
        print(json.dumps({
            "restored": False,
            "reason": "NestedWorktreeRefused",
            "detail": "recorded cwd is not absolute; refusing to restore via relative path",
            "recorded_cwd": target,
        }))
        return 2

    if diag.worktree_exists:
        print(json.dumps({
            "restored": False,
            "reason": "already-present",
            "path": target,
            "branch": diag.branch,
        }))
        return 0  # idempotent — running twice is success

    # `git -C <repo_root> worktree add <abs-path> <branch>`
    cmd = ["git", "-C", diag.repo_root, "worktree", "add", target, diag.branch]
    try:
        proc = subprocess.run(
            cmd, capture_output=True, text=True, timeout=30,
            stdin=subprocess.DEVNULL,
        )
    except (subprocess.SubprocessError, FileNotFoundError) as e:
        print(json.dumps({"restored": False, "reason": "git-invocation-failed", "detail": str(e)}))
        return 1

    if proc.returncode == 0:
        print(json.dumps({
            "restored": True,
            "path": target,
            "branch": diag.branch,
            "repo_root": diag.repo_root,
        }))
        return 0

    stderr = (proc.stderr or "").strip()
    # Git's two variants of the branch-already-in-use error:
    #   "is already checked out at '<path>'"        (older)
    #   "is already used by worktree at '<path>'"   (newer)
    if "already checked out" in stderr or "already used by worktree" in stderr:
        import re as _re
        m = _re.search(
            r"(?:already checked out at|already used by worktree at) '([^']+)'",
            stderr,
        )
        current = m.group(1) if m else ""
        print(json.dumps({
            "restored": False,
            "reason": "BranchInUseElsewhere",
            "branch": diag.branch,
            "current_worktree": current,
        }))
        return 1

    print(json.dumps({
        "restored": False,
        "reason": "git-failed",
        "stderr": stderr,
    }))
    return 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="recover-session",
        description="Recover a Claude Code session that disappeared from the resume picker.",
    )
    subparsers = parser.add_subparsers(dest="cmd", required=True)

    p_identify = subparsers.add_parser(
        "identify",
        help="Search transcripts for a phrase; return ranked candidates.",
    )
    p_identify.add_argument(
        "--query",
        required=True,
        help="Phrase the operator remembers (>=4 chars).",
    )

    p_diagnose = subparsers.add_parser(
        "diagnose",
        help="Report archive/worktree/branch state for a session UUID.",
    )
    p_diagnose.add_argument(
        "--session",
        required=True,
        help="Session UUID (transcript filename stem).",
    )
    p_diagnose.add_argument(
        "--ccd-sessions-file",
        type=Path,
        default=None,
        help="Path to a JSON file containing mcp__ccd_session_mgmt__list_sessions output. "
             "If omitted, is_archived is reported as null.",
    )

    p_restore = subparsers.add_parser(
        "restore",
        help="Recreate the session's worktree at its recorded cwd.",
    )
    p_restore.add_argument(
        "--session",
        required=True,
        help="Session UUID (transcript filename stem).",
    )
    p_restore.add_argument(
        "--ccd-sessions-file",
        type=Path,
        default=None,
        help="(Optional) CCD list_sessions JSON; consulted for the diagnose pre-check.",
    )

    p_prepare = subparsers.add_parser(
        "prepare",
        help="Compose the catch-up message + resume command for a session.",
    )
    p_prepare.add_argument("--session", required=True, help="Session UUID.")
    p_prepare.add_argument(
        "--ccd-sessions-file",
        type=Path, default=None,
        help="(Optional) CCD list_sessions JSON; consulted for the diagnose pre-check.",
    )

    args = parser.parse_args(argv)
    if args.cmd == "identify":
        return cmd_identify(args.query)
    if args.cmd == "diagnose":
        return cmd_diagnose(args.session, ccd_sessions_file=args.ccd_sessions_file)
    if args.cmd == "restore":
        return cmd_restore(args.session, ccd_sessions_file=args.ccd_sessions_file)
    if args.cmd == "prepare":
        return cmd_prepare(args.session, ccd_sessions_file=args.ccd_sessions_file)
    parser.error(f"unknown subcommand: {args.cmd}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
