"""Unit tests for scripts/recover-session.py — Phase 1 (identify).

Covers AC-01, AC-02 from specs/recover-session.md plus the supporting
edge-case tests called out in .plans/2026-05-15-recover-session.md.
"""
from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
_MOD_PATH = _REPO / "scripts" / "recover-session.py"

_spec = importlib.util.spec_from_file_location("recover_session", str(_MOD_PATH))
_mod = importlib.util.module_from_spec(_spec)
sys.modules["recover_session"] = _mod
assert _spec.loader is not None
_spec.loader.exec_module(_mod)

# Re-export for terseness
cmd_identify = _mod.cmd_identify
score_match = _mod.score_match
scan_for_query = _mod.scan_for_query
read_first_user_message = _mod.read_first_user_message
iter_top_level_transcripts = _mod.iter_top_level_transcripts
build_identify_result = _mod.build_identify_result
rank_results = _mod.rank_results
IdentifyResult = _mod.IdentifyResult


# ---------------------------------------------------------------------------
# Fixtures: synthesize transcript JSONLs that mirror the real CCD layout.
# ---------------------------------------------------------------------------


def _write_transcript(
    projects_dir: Path,
    project_subdir: str,
    session_uuid: str,
    *,
    first_user_message: str = "",
    body_lines: list[str] | None = None,
    cwd: str = "C:/fake/cwd",
    entrypoint: str = "cli",
    git_branch: str = "main",
) -> Path:
    """Create a synthetic JSONL transcript and return its path.

    Mirrors the structure CCD actually produces:
      - line 1+: queue-operation entries (no message content)
      - early lines: SessionStart attachment hooks with cwd/entrypoint/version
      - then: the first user-type message
      - then: assistant + body entries
    """
    proj = projects_dir / project_subdir
    proj.mkdir(parents=True, exist_ok=True)
    transcript = proj / f"{session_uuid}.jsonl"

    lines: list[str] = [
        json.dumps({
            "type": "queue-operation",
            "operation": "enqueue",
            "sessionId": session_uuid,
            "content": "ignored",
        }),
        json.dumps({
            "type": "attachment",
            "sessionId": session_uuid,
            "cwd": cwd,
            "entrypoint": entrypoint,
            "gitBranch": git_branch,
            "version": "2.1.142",
        }),
    ]
    if first_user_message:
        lines.append(json.dumps({
            "type": "user",
            "sessionId": session_uuid,
            "message": {"content": first_user_message},
        }))
    for body in body_lines or []:
        lines.append(json.dumps({
            "type": "assistant",
            "sessionId": session_uuid,
            "message": {"content": body},
        }))
    transcript.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return transcript


def _write_subagent_transcript(
    projects_dir: Path,
    project_subdir: str,
    parent_session_uuid: str,
    agent_id: str,
    *,
    first_user_message: str = "",
) -> Path:
    """Create a subagent transcript at the nested path that CCD uses."""
    nested = projects_dir / project_subdir / parent_session_uuid / "subagents"
    nested.mkdir(parents=True, exist_ok=True)
    sub = nested / f"{agent_id}.jsonl"
    lines = [
        json.dumps({"type": "queue-operation", "sessionId": agent_id}),
    ]
    if first_user_message:
        lines.append(json.dumps({
            "type": "user",
            "sessionId": agent_id,
            "message": {"content": first_user_message},
        }))
    sub.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return sub


# ---------------------------------------------------------------------------
# Pure-helper tests
# ---------------------------------------------------------------------------


def test_score_match_line_one_dominates_body():
    """score_match: line-1 hit (100) always outranks body-only (10)."""
    assert score_match(line1_hit=True, body_hit=False) == 100
    assert score_match(line1_hit=True, body_hit=True) == 100
    assert score_match(line1_hit=False, body_hit=True) == 10
    assert score_match(line1_hit=False, body_hit=False) == 0


def test_read_first_user_message_skips_non_user_entries(tmp_path):
    """First lines are queue-ops + attachments; first_user_message returns the user line."""
    t = _write_transcript(
        tmp_path, "proj_a", "session-aaa",
        first_user_message="we are working on issue #1074",
    )
    assert read_first_user_message(t) == "we are working on issue #1074"


def test_read_first_user_message_handles_list_content(tmp_path):
    """message.content may be a list of blocks with .text fields."""
    proj = tmp_path / "proj_a"
    proj.mkdir(parents=True)
    t = proj / "session-list.jsonl"
    t.write_text(
        json.dumps({"type": "queue-operation"}) + "\n"
        + json.dumps({
            "type": "user",
            "message": {"content": [
                {"type": "text", "text": "block one"},
                {"type": "text", "text": "block two"},
            ]},
        }) + "\n",
        encoding="utf-8",
    )
    msg = read_first_user_message(t)
    assert "block one" in msg
    assert "block two" in msg


def test_read_first_user_message_returns_empty_when_no_user_entry(tmp_path):
    """If the file has only queue-ops/attachments, return ''."""
    proj = tmp_path / "proj_a"
    proj.mkdir(parents=True)
    t = proj / "session-empty.jsonl"
    t.write_text(
        json.dumps({"type": "queue-operation"}) + "\n"
        + json.dumps({"type": "attachment"}) + "\n",
        encoding="utf-8",
    )
    assert read_first_user_message(t) == ""


def test_iter_top_level_transcripts_skips_subagents(tmp_path):
    """Subagent .jsonl files under <session>/subagents/ are not yielded."""
    _write_transcript(tmp_path, "proj_a", "top-session", first_user_message="hi")
    _write_subagent_transcript(
        tmp_path, "proj_a", "top-session", "agent-deadbeef",
        first_user_message="subagent content"
    )
    yielded = list(iter_top_level_transcripts(tmp_path))
    names = sorted(p.name for p in yielded)
    assert names == ["top-session.jsonl"]


def test_scan_for_query_returns_line_one_hit_only(tmp_path):
    """Query in line 1 but not body → (True, False)."""
    t = _write_transcript(
        tmp_path, "proj_a", "session-line1",
        first_user_message="working on issue #1074 today",
        body_lines=["unrelated assistant reply"],
    )
    line1, body = scan_for_query(t, "1074")
    assert line1 is True
    assert body is False


def test_scan_for_query_returns_body_hit_only(tmp_path):
    """Query in body but not line 1 → (False, True)."""
    t = _write_transcript(
        tmp_path, "proj_a", "session-body",
        first_user_message="hello world how are you",
        body_lines=["reference to 1074 mid-conversation"],
    )
    line1, body = scan_for_query(t, "1074")
    assert line1 is False
    assert body is True


def test_scan_for_query_no_match(tmp_path):
    """Query nowhere → (False, False)."""
    t = _write_transcript(
        tmp_path, "proj_a", "session-nomatch",
        first_user_message="hello there",
        body_lines=["irrelevant body"],
    )
    line1, body = scan_for_query(t, "xyzzy-not-present")
    assert line1 is False
    assert body is False


def test_scan_for_query_is_case_insensitive(tmp_path):
    """Matching is case-insensitive for ergonomic operator input."""
    t = _write_transcript(
        tmp_path, "proj_a", "session-case",
        first_user_message="Working on Issue #1074",
    )
    line1, _body = scan_for_query(t, "issue #1074")
    assert line1 is True


# ---------------------------------------------------------------------------
# Ranking and orchestration tests
# ---------------------------------------------------------------------------


def test_identify_ranks_by_line_one(tmp_path, capsys):
    """AC-01: line-1 match ranks above body match.

    Spec: specs/recover-session.md AC-01.
    """
    # A: line-1 has query
    _write_transcript(
        tmp_path, "proj_line1", "aaa-line1-match",
        first_user_message="we are working on #1074 issue",
        body_lines=["unrelated body content"],
    )
    # B: only body has query
    _write_transcript(
        tmp_path, "proj_body", "bbb-body-match",
        first_user_message="hello world unrelated",
        body_lines=["reference to #1074 here in the body"],
    )

    rc = cmd_identify("#1074", projects_dir=tmp_path)
    assert rc == 0
    captured = capsys.readouterr()
    results = json.loads(captured.out)
    assert len(results) == 2
    assert results[0]["session_id"] == "aaa-line1-match"
    assert results[0]["match_score"] == 100
    assert results[1]["session_id"] == "bbb-body-match"
    assert results[1]["match_score"] == 10


def test_identify_prefers_line_one_over_body(tmp_path, capsys):
    """AC-02: line-1 wins even when body-match candidate is more recent.

    Spec: specs/recover-session.md AC-02.
    """
    # Older line-1 match
    older = _write_transcript(
        tmp_path, "proj_old", "older-line1",
        first_user_message="we are working on #1074",
    )
    # Newer body-only match — should rank LOWER despite being newer
    newer = _write_transcript(
        tmp_path, "proj_new", "newer-body",
        first_user_message="hello",
        body_lines=["#1074 in body only"],
    )

    # Tweak mtimes: newer body match is more recent
    import os
    os.utime(older, (1_700_000_000, 1_700_000_000))   # ~Nov 2023
    os.utime(newer, (1_730_000_000, 1_730_000_000))   # ~Oct 2024

    rc = cmd_identify("#1074", projects_dir=tmp_path)
    assert rc == 0
    results = json.loads(capsys.readouterr().out)
    assert results[0]["session_id"] == "older-line1"  # line-1 wins
    assert results[1]["session_id"] == "newer-body"


def test_identify_returns_empty_for_no_match(tmp_path, capsys):
    """Empty match list is a successful zero-result, not an error."""
    _write_transcript(
        tmp_path, "proj_a", "session-nomatch",
        first_user_message="completely unrelated content",
    )
    rc = cmd_identify("xyzzy-needle", projects_dir=tmp_path)
    assert rc == 0
    results = json.loads(capsys.readouterr().out)
    assert results == []


def test_identify_skips_subagent_transcripts(tmp_path, capsys):
    """Only top-level <session>.jsonl files are candidates."""
    _write_subagent_transcript(
        tmp_path, "proj_a", "parent-uuid", "agent-subagent",
        first_user_message="this contains the needle #1074",
    )
    # No top-level transcript with the match — only the subagent has it
    rc = cmd_identify("#1074", projects_dir=tmp_path)
    assert rc == 0
    results = json.loads(capsys.readouterr().out)
    assert results == []


def test_identify_rejects_short_query(tmp_path, capsys):
    """Min query length is 4 chars (validation)."""
    rc = cmd_identify("abc", projects_dir=tmp_path)
    assert rc == 2
    err = json.loads(capsys.readouterr().err)
    assert err["error"] == "QueryTooShort"


def test_identify_recovers_within_same_score_band_by_recency(tmp_path, capsys):
    """Same score band → most-recently-active first."""
    older = _write_transcript(
        tmp_path, "proj_old", "older-match",
        first_user_message="working on #1074 (older)",
    )
    newer = _write_transcript(
        tmp_path, "proj_new", "newer-match",
        first_user_message="working on #1074 (newer)",
    )
    import os
    os.utime(older, (1_700_000_000, 1_700_000_000))
    os.utime(newer, (1_730_000_000, 1_730_000_000))

    rc = cmd_identify("#1074", projects_dir=tmp_path)
    assert rc == 0
    results = json.loads(capsys.readouterr().out)
    # Both line-1 (score 100); newer first by mtime
    assert results[0]["session_id"] == "newer-match"
    assert results[1]["session_id"] == "older-match"


def test_identify_returns_metadata_fields(tmp_path, capsys):
    """Result contains cwd, entrypoint, first_user_message, last_activity_ts."""
    _write_transcript(
        tmp_path, "proj_a", "metadata-session",
        first_user_message="working on #1074",
        cwd="C:/test/cwd",
        entrypoint="claude-desktop",
    )
    rc = cmd_identify("#1074", projects_dir=tmp_path)
    assert rc == 0
    results = json.loads(capsys.readouterr().out)
    assert len(results) == 1
    r = results[0]
    assert r["cwd"] == "C:/test/cwd"
    assert r["entrypoint"] == "claude-desktop"
    assert "working on #1074" in r["first_user_message"]
    assert r["last_activity_ts"]  # non-empty ISO timestamp


def test_identify_returns_error_when_projects_dir_missing(tmp_path, capsys):
    """Missing projects dir → exit 3 with ProjectsDirNotFound."""
    missing = tmp_path / "does-not-exist"
    rc = cmd_identify("#1074", projects_dir=missing)
    assert rc == 3
    err = json.loads(capsys.readouterr().err)
    assert err["error"] == "ProjectsDirNotFound"
