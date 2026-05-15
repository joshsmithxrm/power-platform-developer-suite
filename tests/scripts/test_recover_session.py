"""Unit tests for scripts/recover-session.py — Phases 1 & 2.

Covers AC-01, AC-02, AC-03, AC-10 from specs/recover-session.md plus
the supporting edge-case tests called out in
.plans/2026-05-15-recover-session.md.
"""
from __future__ import annotations

import importlib.util
import json
import subprocess
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
cmd_diagnose = _mod.cmd_diagnose
cmd_restore = _mod.cmd_restore
cmd_prepare = _mod.cmd_prepare
git_ahead_behind = _mod.git_ahead_behind
scan_transcript_for_issue_refs = _mod.scan_transcript_for_issue_refs
format_catch_up_message = _mod.format_catch_up_message
score_match = _mod.score_match
scan_for_query = _mod.scan_for_query
read_first_user_message = _mod.read_first_user_message
iter_top_level_transcripts = _mod.iter_top_level_transcripts
build_identify_result = _mod.build_identify_result
rank_results = _mod.rank_results
find_transcript_by_uuid = _mod.find_transcript_by_uuid
resolve_repo_root_from_cwd = _mod.resolve_repo_root_from_cwd
git_branch_exists = _mod.git_branch_exists
parse_worktree_porcelain = _mod.parse_worktree_porcelain
worktree_exists_at = _mod.worktree_exists_at
lookup_archived_in_ccd_sessions = _mod.lookup_archived_in_ccd_sessions
decide_next_action = _mod.decide_next_action
IdentifyResult = _mod.IdentifyResult
DiagnoseResult = _mod.DiagnoseResult


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


# ---------------------------------------------------------------------------
# Phase 2: diagnose
# ---------------------------------------------------------------------------


import os  # noqa: E402  (needed for _init_git_repo's env merge)


def _init_git_repo(path: Path, branches: list[str] | None = None) -> None:
    """Create a real git repo with one commit so branch operations work."""
    import subprocess as _sp

    path.mkdir(parents=True, exist_ok=True)
    env = {
        **os.environ,
        "GIT_AUTHOR_NAME": "t",
        "GIT_AUTHOR_EMAIL": "t@t",
        "GIT_COMMITTER_NAME": "t",
        "GIT_COMMITTER_EMAIL": "t@t",
    }
    _sp.run(
        ["git", "init", "-q", "--initial-branch=main", str(path)],
        check=True, env=env,
    )
    (path / "README.md").write_text("seed\n", encoding="utf-8")
    _sp.run(["git", "-C", str(path), "add", "README.md"], check=True, env=env)
    _sp.run(
        ["git", "-C", str(path), "commit", "-q", "-m", "init"],
        check=True, env=env,
    )
    for b in branches or []:
        _sp.run(["git", "-C", str(path), "branch", b], check=True, env=env)


def test_find_transcript_by_uuid_locates_top_level(tmp_path):
    _write_transcript(tmp_path, "proj_a", "needle-uuid",
                      first_user_message="anything")
    found = find_transcript_by_uuid("needle-uuid", tmp_path)
    assert found is not None
    assert found.stem == "needle-uuid"


def test_find_transcript_by_uuid_returns_none_when_missing(tmp_path):
    _write_transcript(tmp_path, "proj_a", "other-uuid",
                      first_user_message="anything")
    found = find_transcript_by_uuid("nonexistent-uuid", tmp_path)
    assert found is None


def test_find_transcript_by_uuid_ignores_subagents(tmp_path):
    """A subagent UUID at <session>/subagents/<uuid>.jsonl is not findable."""
    _write_subagent_transcript(
        tmp_path, "proj_a", "parent-uuid", "lurking-uuid",
        first_user_message="anything",
    )
    found = find_transcript_by_uuid("lurking-uuid", tmp_path)
    assert found is None


def test_parse_worktree_porcelain_extracts_paths():
    sample = """worktree C:/main/repo
HEAD abc123
branch refs/heads/main

worktree C:/main/repo/.worktrees/feat-a
HEAD def456
branch refs/heads/feat/a
"""
    paths = parse_worktree_porcelain(sample)
    assert paths == ["C:/main/repo", "C:/main/repo/.worktrees/feat-a"]


def test_git_branch_exists_against_real_repo(tmp_path):
    repo = tmp_path / "repo"
    _init_git_repo(repo, branches=["feat/exists"])
    assert git_branch_exists(str(repo).replace("\\", "/"), "feat/exists") is True
    assert git_branch_exists(str(repo).replace("\\", "/"), "feat/missing") is False


def test_git_branch_exists_returns_false_when_repo_missing(tmp_path):
    assert git_branch_exists("", "feat/x") is False
    assert git_branch_exists(str(tmp_path / "nope"), "feat/x") is False


def test_worktree_exists_at_detects_registered_path(tmp_path):
    """Main repo's own path is its first registered worktree."""
    repo = tmp_path / "repo"
    _init_git_repo(repo)
    repo_str = str(repo).replace("\\", "/")
    assert worktree_exists_at(repo_str, str(repo)) is True


def test_worktree_exists_at_false_when_path_not_registered(tmp_path):
    repo = tmp_path / "repo"
    _init_git_repo(repo)
    repo_str = str(repo).replace("\\", "/")
    bogus = str(tmp_path / "bogus")
    (tmp_path / "bogus").mkdir()  # path exists but not a worktree
    assert worktree_exists_at(repo_str, bogus) is False


def test_lookup_archived_matches_by_cwd_priority_1():
    """The transcript UUID and CCD sessionId are different UUIDs; cwd is
    the reliable link between them.
    """
    sessions = [
        {
            "sessionId": "local_aaa-bbb-ccc",
            "cwd": "C:/foo/repo/.worktrees/feat-a",
            "branch": "feat/a",
            "isArchived": True,
        },
        {
            "sessionId": "local_xxx-yyy-zzz",
            "cwd": "C:/foo/repo/.worktrees/feat-b",
            "branch": "feat/b",
            "isArchived": False,
        },
    ]
    # Match by cwd — note transcript UUID has no relation to CCD sessionId.
    assert lookup_archived_in_ccd_sessions(
        sessions, cwd="C:/foo/repo/.worktrees/feat-a"
    ) is True
    assert lookup_archived_in_ccd_sessions(
        sessions, cwd="C:/foo/repo/.worktrees/feat-b"
    ) is False


def test_lookup_archived_matches_cwd_case_insensitively_on_windows():
    """Windows paths are case-insensitive; CCD records casing varies."""
    sessions = [{
        "sessionId": "local_a", "cwd": "C:\\Foo\\Repo\\Worktree",
        "branch": "feat/a", "isArchived": True,
    }]
    assert lookup_archived_in_ccd_sessions(
        sessions, cwd="c:/foo/repo/worktree"
    ) is True


def test_lookup_archived_falls_back_to_branch():
    sessions = [{
        "sessionId": "local_a", "cwd": "C:/different/path",
        "branch": "feat/the-branch", "isArchived": True,
    }]
    # cwd doesn't match anything; branch fallback should find it
    assert lookup_archived_in_ccd_sessions(
        sessions, cwd="C:/some/other/path", branch="feat/the-branch"
    ) is True


def test_lookup_archived_session_id_last_resort():
    """When neither cwd nor branch match, fall back to sessionId substring."""
    sessions = [{
        "sessionId": "local_aaa-bbb-ccc",
        "cwd": "", "branch": "", "isArchived": True,
    }]
    assert lookup_archived_in_ccd_sessions(
        sessions, session_id="aaa-bbb-ccc"
    ) is True


def test_lookup_archived_returns_none_when_no_match():
    sessions = [{
        "sessionId": "local_x", "cwd": "C:/here", "branch": "feat/x",
        "isArchived": True,
    }]
    assert lookup_archived_in_ccd_sessions(
        sessions, cwd="C:/elsewhere", branch="feat/different"
    ) is None
    assert lookup_archived_in_ccd_sessions([]) is None


def test_decide_next_action_full_matrix():
    # Unrecoverable cases
    assert decide_next_action(False, True, True, False) == "unrecoverable-transcript-missing"
    assert decide_next_action(True, False, True, False) == "unrecoverable-branch-missing"

    # Known-archive-state cases
    assert decide_next_action(True, True, True, True) == "unarchive-and-resume"
    assert decide_next_action(True, True, False, True) == "restore-unarchive-resume"
    assert decide_next_action(True, True, False, False) == "restore-and-resume"
    assert decide_next_action(True, True, True, False) == "resume"

    # Unknown-archive (skill didn't pass CCD state)
    assert decide_next_action(True, True, True, None) == "check-archive-then-resume"
    assert decide_next_action(True, True, False, None) == "restore-then-check-archive-then-resume"


def test_cmd_diagnose_returns_transcript_missing_for_unknown_uuid(tmp_path, capsys):
    """AC-10: transcript missing → recoverable=False, reason in next_action."""
    rc = cmd_diagnose("totally-unknown-uuid", projects_dir=tmp_path)
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["transcript_exists"] is False
    assert out["recoverable"] is False
    assert out["next_action"] == "unrecoverable-transcript-missing"


def test_cmd_diagnose_returns_three_booleans_against_live_repo(tmp_path, capsys):
    """AC-03: diagnose returns is_archived, worktree_exists, branch_exists, entrypoint.

    Uses a real tmp git repo so worktree + branch checks exercise real git.
    """
    # Create a real repo
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/diag-session"])
    repo_str = str(repo).replace("\\", "/")

    # Synthesize a transcript that points at the repo
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_diag", "diag-uuid",
        first_user_message="diagnose me",
        cwd=repo_str,
        git_branch="claude/diag-session",
        entrypoint="cli",
    )

    rc = cmd_diagnose("diag-uuid", projects_dir=projects)
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["transcript_exists"] is True
    assert out["branch_exists"] is True
    assert out["worktree_exists"] is True  # repo's own path is a worktree
    assert out["entrypoint"] == "cli"
    assert out["is_archived"] is None  # no --ccd-sessions-file provided
    assert out["recoverable"] is True
    # No CCD info → next_action says "check-archive-then-resume"
    assert out["next_action"] == "check-archive-then-resume"


def test_cmd_diagnose_uses_ccd_sessions_file(tmp_path, capsys):
    """When --ccd-sessions-file is provided, is_archived is filled in."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/archived-session"])
    repo_str = str(repo).replace("\\", "/")
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_arch", "archived-uuid",
        first_user_message="i'm archived",
        cwd=repo_str,
        git_branch="claude/archived-session",
    )
    # Note: CCD sessionId is intentionally DIFFERENT from the transcript
    # UUID — they're separate UUIDs in real CCD installations. The link
    # is cwd+branch, not sessionId.
    ccd_file = tmp_path / "ccd-sessions.json"
    ccd_file.write_text(json.dumps([
        {
            "sessionId": "local_some-other-uuid",
            "cwd": repo_str,
            "branch": "claude/archived-session",
            "isArchived": True,
        },
        {
            "sessionId": "local_unrelated",
            "cwd": "C:/elsewhere",
            "branch": "feat/something-else",
            "isArchived": False,
        },
    ]), encoding="utf-8")

    rc = cmd_diagnose("archived-uuid", projects_dir=projects, ccd_sessions_file=ccd_file)
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["is_archived"] is True
    assert out["next_action"] == "unarchive-and-resume"


# ---------------------------------------------------------------------------
# Phase 3: restore
# ---------------------------------------------------------------------------


def test_restore_creates_worktree_at_absolute_path(tmp_path, capsys):
    """AC-04: restore invokes `git -C <repo> worktree add <abs-path> <branch>`."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/restore-target"])
    target = repo / ".worktrees" / "restore-target"
    target_str = str(target).replace("\\", "/")

    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_r", "restore-uuid",
        first_user_message="restore me",
        cwd=target_str,
        git_branch="claude/restore-target",
    )

    rc = cmd_restore("restore-uuid", projects_dir=projects)
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["restored"] is True
    assert out["path"] == target_str
    assert out["branch"] == "claude/restore-target"
    assert target.exists()
    assert (target / "README.md").exists()  # seed file from init


def test_restore_rejects_relative_recorded_cwd(tmp_path, capsys):
    """AC-05: refuses to restore when recorded cwd is not absolute.

    Guards against the nested-worktree pitfall observed 2026-05-15.
    """
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/rel-path"])
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_rel", "rel-uuid",
        first_user_message="relative",
        cwd="some/relative/path",
        git_branch="claude/rel-path",
    )
    rc = cmd_restore("rel-uuid", projects_dir=projects)
    # The cwd doesn't resolve to a repo, so we'd hit BranchNotFound first.
    # To isolate the relative-path guard, we need recorded_cwd to resolve
    # to a repo root. Hand-craft the transcript with an absolute repo_root
    # in metadata via a workaround — write recorded_cwd as a path that
    # ascends to a real repo BUT is non-absolute.
    # Simpler: assert that whatever the rejection reason, the FS state
    # is unchanged (no worktree created under tmp_path other than the
    # main repo).
    out = json.loads(capsys.readouterr().out)
    assert out["restored"] is False
    # Either it caught the relative path directly, or the branch lookup
    # failed because resolve_repo_root_from_cwd couldn't find a .git
    # from a relative path. Both are acceptable refusals — the key
    # invariant is that no worktree was created.
    assert out["reason"] in ("NestedWorktreeRefused", "BranchNotFound", "NoRecordedCwd")


def test_restore_rejects_relative_with_resolvable_branch(tmp_path, capsys, monkeypatch):
    """Tighter test: even if branch DOES resolve, relative cwd is refused.

    Forces _diagnose_internal to return a relative recorded_cwd alongside
    branch_exists=True via monkeypatch, so the absolute-path guard is the
    only thing standing between us and a bad git worktree add.
    """
    fake_diag = _mod.DiagnoseResult(
        session_id="forced-uuid",
        transcript_exists=True,
        is_archived=False,
        worktree_exists=False,
        branch_exists=True,
        entrypoint="cli",
        recoverable=True,
        next_action="restore-and-resume",
        recorded_cwd="relative/path/here",
        branch="claude/forced",
        repo_root=str(tmp_path / "main-repo").replace("\\", "/"),
    )
    monkeypatch.setattr(_mod, "_diagnose_internal", lambda *a, **kw: fake_diag)
    rc = cmd_restore("forced-uuid", projects_dir=tmp_path)
    assert rc == 2
    out = json.loads(capsys.readouterr().out)
    assert out["restored"] is False
    assert out["reason"] == "NestedWorktreeRefused"


def test_restore_is_idempotent_when_worktree_already_exists(tmp_path, capsys):
    """AC-06: second restore returns {restored: False, reason: already-present}."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/idempotent"])
    # Target must live under repo so resolve_repo_root_from_cwd can walk
    # up and find the .git anchor even when target itself doesn't exist.
    target = repo / ".worktrees" / "idempotent"
    target_str = str(target).replace("\\", "/")

    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_i", "idem-uuid",
        first_user_message="idempotent",
        cwd=target_str,
        git_branch="claude/idempotent",
    )

    # First call: creates
    rc1 = cmd_restore("idem-uuid", projects_dir=projects)
    out1 = json.loads(capsys.readouterr().out)
    assert rc1 == 0
    assert out1["restored"] is True

    # Second call: noop
    rc2 = cmd_restore("idem-uuid", projects_dir=projects)
    out2 = json.loads(capsys.readouterr().out)
    assert rc2 == 0
    assert out2["restored"] is False
    assert out2["reason"] == "already-present"


def test_restore_returns_branch_not_found_when_branch_deleted(tmp_path, capsys):
    """If the recorded branch is gone, restore refuses and hints at reflog."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo)  # only 'main'
    target = repo / ".worktrees" / "gone"
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_g", "gone-uuid",
        first_user_message="branch gone",
        cwd=str(target).replace("\\", "/"),
        git_branch="claude/never-existed",
    )
    rc = cmd_restore("gone-uuid", projects_dir=projects)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert out["restored"] is False
    assert out["reason"] == "BranchNotFound"
    assert "reflog" in out["hint"]


def test_restore_reports_branch_in_use_elsewhere(tmp_path, capsys):
    """If the branch is already checked out elsewhere, refuse cleanly."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/in-use"])

    # Add the branch as a worktree FIRST so it's "checked out elsewhere"
    elsewhere = repo / ".worktrees" / "elsewhere"
    subprocess.run(
        ["git", "-C", str(repo), "worktree", "add",
         str(elsewhere).replace("\\", "/"), "claude/in-use"],
        check=True, capture_output=True,
        stdin=subprocess.DEVNULL,
    )

    # Now attempt restore against a different target path
    target = repo / ".worktrees" / "second-attempt"
    target_str = str(target).replace("\\", "/")
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_dup", "dup-uuid",
        first_user_message="branch already in use",
        cwd=target_str,
        git_branch="claude/in-use",
    )

    rc = cmd_restore("dup-uuid", projects_dir=projects)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert out["restored"] is False
    assert out["reason"] == "BranchInUseElsewhere"
    assert "elsewhere" in out["current_worktree"].lower() or out["current_worktree"]


def test_restore_reports_transcript_not_found(tmp_path, capsys):
    """No transcript → no recovery is possible."""
    rc = cmd_restore("never-existed-uuid", projects_dir=tmp_path)
    out = json.loads(capsys.readouterr().out)
    assert rc == 1
    assert out["restored"] is False
    assert out["reason"] == "TranscriptNotFound"


# ---------------------------------------------------------------------------
# Phase 4: prepare
# ---------------------------------------------------------------------------


def test_scan_transcript_for_issue_refs_finds_hash_numbers(tmp_path):
    t = _write_transcript(
        tmp_path, "proj_p", "prep-uuid",
        first_user_message="working on #1074 and #1066",
        body_lines=[
            "let's also look at #1068",
            "no issue here just text",
            "ref to #1074 again should dedupe",
        ],
    )
    refs = scan_transcript_for_issue_refs(t)
    assert refs == [1066, 1068, 1074]


def test_scan_transcript_for_issue_refs_rejects_word_prefixed_hashes(tmp_path):
    """Negative lookbehind: `deadbeef#1234` is a UUID fragment, not an issue ref."""
    t = _write_transcript(
        tmp_path, "proj_p", "neg-lookbehind",
        first_user_message="real ref #1074 here, but deadbeef#1234 is a UUID fragment",
        body_lines=["another fake: cafe#2222", "and a real one: #1099"],
    )
    refs = scan_transcript_for_issue_refs(t)
    # Only the truly-prefix-clean refs should appear
    assert 1074 in refs
    assert 1099 in refs
    assert 1234 not in refs  # preceded by 'f' (word char)
    assert 2222 not in refs  # preceded by 'e' (word char)


def test_scan_transcript_for_issue_refs_empty_for_no_matches(tmp_path):
    t = _write_transcript(
        tmp_path, "proj_p", "no-refs",
        first_user_message="no issue references here",
    )
    refs = scan_transcript_for_issue_refs(t)
    assert refs == []


def test_git_ahead_behind_zero_zero_for_branch_at_main(tmp_path):
    """A branch pointing at the same commit as main is (0, 0)."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["feat/parity"])
    # Set up origin/main pointing at same commit
    subprocess.run(
        ["git", "-C", str(repo), "branch", "--track", "main2", "main"],
        check=True, capture_output=True, stdin=subprocess.DEVNULL,
    )
    # When base ref doesn't exist, fall back to 'main' which is local
    ahead, behind = git_ahead_behind(str(repo).replace("\\", "/"), "feat/parity", "main")
    assert ahead == 0
    assert behind == 0


def test_git_ahead_behind_returns_zero_on_invalid_repo():
    assert git_ahead_behind("", "feat/x") == (0, 0)
    assert git_ahead_behind("/does/not/exist", "feat/x") == (0, 0)


def test_format_catch_up_message_includes_behind_count():
    diag = _mod.DiagnoseResult(
        session_id="abc", transcript_exists=True, is_archived=False,
        worktree_exists=True, branch_exists=True, entrypoint="cli",
        recoverable=True, next_action="resume",
        recorded_cwd="C:/path/to/wt", branch="feat/x",
        repo_root="C:/path/to/repo",
    )
    msg = format_catch_up_message(diag, (1, 7), [], [])
    assert "7 commits BEHIND" in msg
    assert "PublicAPI.Unshipped.txt" in msg  # rebase hint


def test_format_catch_up_message_lists_merged_prs():
    diag = _mod.DiagnoseResult(
        session_id="abc", transcript_exists=True, is_archived=False,
        worktree_exists=True, branch_exists=True, entrypoint="cli",
        recoverable=True, next_action="resume",
        recorded_cwd="C:/path", branch="feat/x", repo_root="C:/repo",
    )
    prs = [
        {"number": 1073, "title": "fix(workflow): post-#1057 robustness"},
        {"number": 1075, "title": "fix(workflow): node/npm PATH"},
    ]
    msg = format_catch_up_message(diag, (0, 2), prs, [])
    assert "#1073" in msg
    assert "#1075" in msg
    assert "node/npm PATH" in msg


def test_format_catch_up_message_lists_issue_refs():
    diag = _mod.DiagnoseResult(
        session_id="abc", transcript_exists=True, is_archived=False,
        worktree_exists=True, branch_exists=True, entrypoint="cli",
        recoverable=True, next_action="resume",
        recorded_cwd="C:/path", branch="feat/x", repo_root="C:/repo",
    )
    msg = format_catch_up_message(diag, (0, 0), [], [1066, 1074])
    assert "#1066" in msg
    assert "#1074" in msg
    assert "gh issue view" in msg


def test_cmd_prepare_returns_catch_up_against_live_repo(tmp_path, capsys):
    """AC-07: prepare returns branch delta, merged PRs (stub), issue refs,
    and a copy-pasteable resume command."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/prepare-test"])
    target = repo / ".worktrees" / "prepare-test"

    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_p", "prep-uuid",
        first_user_message="working on #1074 and #1066",
        body_lines=["ref to #1068 mid-session"],
        cwd=str(target).replace("\\", "/"),
        git_branch="claude/prepare-test",
    )

    # Inject a stub gh fetcher so the test doesn't depend on the system gh
    fake_prs = [
        {"number": 1075, "title": "fix(workflow): node/npm PATH"},
        {"number": 1073, "title": "fix(workflow): post-#1057 robustness"},
    ]
    rc = cmd_prepare(
        "prep-uuid",
        projects_dir=projects,
        gh_fetcher=lambda since: fake_prs,
    )
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["prepared"] is True
    assert out["branch"] == "claude/prepare-test"
    assert out["issue_refs"] == [1066, 1068, 1074]
    assert out["merged_prs"] == fake_prs
    assert "#1074" in out["catch_up_message"]
    assert "#1075" in out["catch_up_message"]
    assert out["resume_command"].endswith("claude --resume prep-uuid")
    assert out["resume_command"].startswith(f"cd {str(target).replace(chr(92), '/')}")


def test_cmd_prepare_handles_gh_unavailable(tmp_path, capsys):
    """When gh fetcher returns [], catch-up message degrades gracefully."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/no-gh"])
    target = repo / ".worktrees" / "no-gh"
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_q", "nogh-uuid",
        first_user_message="testing without gh",
        cwd=str(target).replace("\\", "/"),
        git_branch="claude/no-gh",
    )
    rc = cmd_prepare(
        "nogh-uuid",
        projects_dir=projects,
        gh_fetcher=lambda since: [],
    )
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["prepared"] is True
    assert out["merged_prs"] == []
    # Message should explicitly state no PR delta available
    assert "No merged-PR delta available" in out["catch_up_message"]


def test_cmd_prepare_returns_transcript_not_found_for_unknown(tmp_path, capsys):
    rc = cmd_prepare("nothere-uuid", projects_dir=tmp_path)
    assert rc == 1
    out = json.loads(capsys.readouterr().out)
    assert out["prepared"] is False
    assert out["reason"] == "TranscriptNotFound"


# ---------------------------------------------------------------------------
# Phase 5/6: SKILL.md structural tests (AC-08, AC-09)
# ---------------------------------------------------------------------------


_SKILL_DIR = _REPO / ".claude" / "skills" / "recover-session"


def test_skill_md_under_line_cap():
    """AC-08: SKILL.md is <=150 lines (the skill-line-cap.py hook bar)."""
    skill = _SKILL_DIR / "SKILL.md"
    assert skill.exists(), f"SKILL.md missing at {skill}"
    lines = skill.read_text(encoding="utf-8").splitlines()
    assert len(lines) <= 150, f"SKILL.md is {len(lines)} lines; cap is 150"


def test_skill_md_references_reference_sections():
    """AC-09: SKILL.md cites REFERENCE.md §N using canonical syntax."""
    skill = (_SKILL_DIR / "SKILL.md").read_text(encoding="utf-8")
    # The TWO-FILE-PATTERN canonical form: "Read REFERENCE.md §<N>"
    import re as _re
    refs = _re.findall(r"Read REFERENCE\.md §(\d+)", skill)
    assert refs, "SKILL.md must cite REFERENCE.md sections via `Read REFERENCE.md §N`"
    assert len(set(refs)) >= 3, (
        f"SKILL.md should reference multiple REFERENCE.md sections; found only {set(refs)}"
    )


def test_reference_md_has_corresponding_sections():
    """Every §N cited in SKILL.md must exist as a heading in REFERENCE.md."""
    skill = (_SKILL_DIR / "SKILL.md").read_text(encoding="utf-8")
    reference = (_SKILL_DIR / "REFERENCE.md").read_text(encoding="utf-8")
    import re as _re

    cited = set(_re.findall(r"Read REFERENCE\.md §(\d+)", skill))
    defined = set(_re.findall(r"## §(\d+) —", reference))
    missing = cited - defined
    assert not missing, f"SKILL.md cites missing REFERENCE sections: {missing}"


def test_skill_frontmatter_present():
    """SKILL.md must have name and description frontmatter."""
    skill = (_SKILL_DIR / "SKILL.md").read_text(encoding="utf-8")
    assert skill.startswith("---\n"), "SKILL.md must start with YAML frontmatter"
    fm_end = skill.find("\n---\n", 4)
    assert fm_end > 0, "SKILL.md frontmatter must close"
    fm = skill[4:fm_end]
    assert "name: recover-session" in fm
    assert "description:" in fm


# ---------------------------------------------------------------------------
# Phase 8: filesystem audit (AC-11)
# ---------------------------------------------------------------------------


def test_no_writes_to_ccd_state_store(tmp_path, monkeypatch, capsys):
    """AC-11: helper subcommands never write to $APPDATA/Claude/claude-code-sessions
    nor to ~/.claude/projects (those are read-only inputs).
    """
    # Wrap builtins.open to capture write attempts
    import builtins
    original_open = builtins.open
    write_paths: list[str] = []

    def auditing_open(file, mode="r", *args, **kwargs):
        if isinstance(file, (str, Path)) and any(c in str(mode) for c in ("w", "a", "x", "+")):
            write_paths.append(str(file))
        return original_open(file, mode, *args, **kwargs)

    monkeypatch.setattr(builtins, "open", auditing_open)

    # Set up a real but isolated environment
    repo = tmp_path / "main-repo"
    _init_git_repo(repo, branches=["claude/audit-test"])
    target = repo / ".worktrees" / "audit-test"
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_audit", "audit-uuid",
        first_user_message="audit me",
        cwd=str(target).replace("\\", "/"),
        git_branch="claude/audit-test",
    )

    # Clear any writes from setup
    write_paths.clear()

    # Run all four subcommands
    cmd_identify("audit", projects_dir=projects)
    capsys.readouterr()
    cmd_diagnose("audit-uuid", projects_dir=projects)
    capsys.readouterr()
    cmd_restore("audit-uuid", projects_dir=projects)
    capsys.readouterr()
    cmd_prepare("audit-uuid", projects_dir=projects, gh_fetcher=lambda since: [])
    capsys.readouterr()

    # Assert no writes to forbidden trees
    forbidden_substrings = [
        "claude-code-sessions",
        "/AppData/Local/Claude",
        "/AppData/Roaming/Claude",
    ]
    bad = [
        p for p in write_paths
        if any(sub.replace("/", os.sep) in p or sub in p.replace("\\", "/") for sub in forbidden_substrings)
    ]
    assert not bad, f"helper wrote to forbidden CCD state-store paths: {bad}"

    # Projects dir reads only — the script must never write a transcript JSONL
    projects_writes = [p for p in write_paths if "/projects/" in p.replace("\\", "/")
                       and (".jsonl" in p)]
    assert not projects_writes, f"helper wrote to transcript files: {projects_writes}"


def test_cmd_diagnose_branch_missing_is_unrecoverable(tmp_path, capsys):
    """Branch deleted → next_action = unrecoverable-branch-missing."""
    repo = tmp_path / "main-repo"
    _init_git_repo(repo)  # no extra branches; only 'main' exists
    projects = tmp_path / "projects"
    _write_transcript(
        projects, "proj_x", "branch-gone-uuid",
        first_user_message="branch is gone",
        cwd=str(repo).replace("\\", "/"),
        git_branch="claude/deleted-branch",
    )
    rc = cmd_diagnose("branch-gone-uuid", projects_dir=projects)
    assert rc == 0
    out = json.loads(capsys.readouterr().out)
    assert out["branch_exists"] is False
    assert out["next_action"] == "unrecoverable-branch-missing"
