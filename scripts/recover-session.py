#!/usr/bin/env python3
"""
Recover a Claude Code session that's invisible to the resume picker.

A workflow helper for the `/recover-session` skill. Mechanics only —
the skill drives the operator dialogue and decision points.

Subcommands (Phase 1 ships `identify` only; `diagnose`, `restore`, and
`prepare` are added in later phases per
specs/recover-session.md).

Usage:
    python scripts/recover-session.py identify --query "<phrase>"

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
        key=lambda r: (-r.match_score, r.last_activity_ts),
        reverse=False,
    ) if False else sorted(
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

    args = parser.parse_args(argv)
    if args.cmd == "identify":
        return cmd_identify(args.query)
    parser.error(f"unknown subcommand: {args.cmd}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
