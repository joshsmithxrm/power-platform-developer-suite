"""Tests for scripts/triage_common.py shared triage functions."""
import json
import os
import sys

# Add scripts dir to path so we can import triage_common
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

import subprocess
from unittest.mock import patch

from triage_common import (
    build_triage_prompt,
    detect_gemini_overload,
    format_reply_body,
    get_repo_slug,
    get_unreplied_comments,
    is_triage_complete,
    parse_triage_json,
    parse_triage_jsonl,
    parse_triage_stage_log,
)


# ---------------------------------------------------------------------------
# get_repo_slug
# ---------------------------------------------------------------------------


class TestGetRepoSlug:
    def test_shakedown_returns_test_slug(self):
        result = get_repo_slug("/nonexistent", shakedown=True)
        assert result == "test-owner/test-repo"


# ---------------------------------------------------------------------------
# build_triage_prompt
# ---------------------------------------------------------------------------


class TestBuildTriagePrompt:
    def test_includes_pr_number_and_comments(self, tmp_path):
        comments = [{"id": 1, "body": "Fix this"}]
        prompt = build_triage_prompt(str(tmp_path), 42, comments)
        assert "PR #42" in prompt
        assert '"Fix this"' in prompt

    def test_reads_spec_from_state(self, tmp_path):
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({"spec": "specs/my-spec.md"}))
        prompt = build_triage_prompt(str(tmp_path), 1, [])
        assert "specs/my-spec.md" in prompt

    def test_missing_state_uses_empty_spec(self, tmp_path):
        prompt = build_triage_prompt(str(tmp_path), 1, [])
        assert "Spec (read for design rationale): \n" in prompt

    def test_contains_output_format_instructions(self, tmp_path):
        prompt = build_triage_prompt(str(tmp_path), 1, [])
        assert '"action": "fixed"|"dismissed"' in prompt


# ---------------------------------------------------------------------------
# parse_triage_json
# ---------------------------------------------------------------------------


class TestParseTriageJson:
    def test_finds_json_array(self):
        text = 'Some preamble\n[{"id": 1, "action": "fixed"}]\ntrailing'
        result = parse_triage_json(text)
        assert result == [{"id": 1, "action": "fixed"}]

    def test_returns_none_for_no_bracket(self):
        assert parse_triage_json("no json here") is None

    def test_returns_none_for_invalid_json(self):
        assert parse_triage_json("[invalid json") is None

    def test_finds_last_bracket(self):
        text = 'first [ignore this] then [{"id": 2}]'
        result = parse_triage_json(text)
        assert result == [{"id": 2}]

    def test_returns_none_for_non_list(self):
        # raw_decode on a bracket that starts a non-list should return None
        # This would actually parse as a list, so test with edge case
        assert parse_triage_json("[") is None


# ---------------------------------------------------------------------------
# parse_triage_jsonl
# ---------------------------------------------------------------------------


class TestParseTriageJsonl:
    def test_extracts_from_result_events(self, tmp_path):
        jsonl = tmp_path / "out.jsonl"
        lines = [
            json.dumps({
                "type": "result",
                "result": '[{"id": 1, "action": "dismissed", "description": "not relevant"}]',
            }),
        ]
        jsonl.write_text("\n".join(lines))
        result = parse_triage_jsonl(str(jsonl))
        assert result == [{"id": 1, "action": "dismissed", "description": "not relevant"}]

    def test_extracts_from_assistant_events(self, tmp_path):
        jsonl = tmp_path / "out.jsonl"
        lines = [
            json.dumps({
                "type": "assistant",
                "message": {
                    "content": [
                        {"type": "text", "text": 'Here is the result:'},
                        {"type": "text", "text": '[{"id": 3, "action": "fixed", "commit": "abc"}]'},
                    ],
                },
            }),
        ]
        jsonl.write_text("\n".join(lines))
        result = parse_triage_jsonl(str(jsonl))
        assert result == [{"id": 3, "action": "fixed", "commit": "abc"}]

    def test_returns_none_for_missing_file(self):
        assert parse_triage_jsonl("/nonexistent/path.jsonl") is None

    def test_skips_invalid_json_lines(self, tmp_path):
        jsonl = tmp_path / "out.jsonl"
        content = 'not json\n' + json.dumps({
            "type": "result",
            "result": '[{"id": 1, "action": "fixed"}]',
        })
        jsonl.write_text(content)
        result = parse_triage_jsonl(str(jsonl))
        assert result == [{"id": 1, "action": "fixed"}]

    def test_returns_none_when_no_json_array_in_text(self, tmp_path):
        jsonl = tmp_path / "out.jsonl"
        lines = [
            json.dumps({"type": "result", "result": "no array here"}),
        ]
        jsonl.write_text("\n".join(lines))
        assert parse_triage_jsonl(str(jsonl)) is None


# ---------------------------------------------------------------------------
# parse_triage_stage_log
# ---------------------------------------------------------------------------


class TestParseTriageStageLog:
    def test_reads_stage_log_file(self, tmp_path):
        log = tmp_path / "triage.log"
        log.write_text('stuff\n[{"id": 2, "action": "fixed", "commit": "abc123"}]\n')
        result = parse_triage_stage_log(str(log))
        assert result == [{"id": 2, "action": "fixed", "commit": "abc123"}]

    def test_returns_none_for_missing_file(self):
        assert parse_triage_stage_log("/nonexistent/triage.log") is None

    def test_returns_none_for_no_json(self, tmp_path):
        log = tmp_path / "triage.log"
        log.write_text("just some text with no arrays\n")
        assert parse_triage_stage_log(str(log)) is None


# ---------------------------------------------------------------------------
# format_reply_body
# ---------------------------------------------------------------------------


class TestFormatReplyBody:
    def test_fixed_with_commit(self):
        body = format_reply_body({
            "action": "fixed", "commit": "abc", "description": "done",
        })
        assert body == "Fixed in abc \u2014 done"

    def test_dismissed(self):
        body = format_reply_body({
            "action": "dismissed", "description": "not relevant",
        })
        assert body == "Not applicable \u2014 not relevant"

    def test_unknown_with_description(self):
        body = format_reply_body({
            "action": "unknown", "description": "needs review",
        })
        assert body == "needs review"

    def test_unknown_no_description(self):
        assert format_reply_body({}) == "Reviewed."

    def test_fixed_without_commit_uses_description(self):
        body = format_reply_body({
            "action": "fixed", "description": "patched",
        })
        assert body == "patched"


# ---------------------------------------------------------------------------
# get_unreplied_comments (AC-139)
# ---------------------------------------------------------------------------


class TestGetUnrepliedComments:
    def test_returns_unreplied_bot_comments(self, tmp_path):
        """AC-139: get_unreplied_comments returns Gemini + CodeQL comments with no reply."""
        comments = [
            {"id": 1, "user": {"login": "gemini-code-assist[bot]"}, "in_reply_to_id": None,
             "path": "src/foo.py", "line": 10, "body": "Issue here"},
            {"id": 2, "user": {"login": "github-advanced-security[bot]"}, "in_reply_to_id": None,
             "path": "src/bar.py", "line": 5, "body": "Security issue"},
            {"id": 3, "user": {"login": "human"}, "in_reply_to_id": 1,
             "path": "src/foo.py", "line": 10, "body": "Fixed"},
        ]
        # Comment 1 has a reply (id 3), comment 2 does not
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")

        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)

        assert len(result) == 1
        assert result[0]["id"] == 2

    def test_returns_empty_in_shakedown(self, tmp_path):
        """AC-139: shakedown mode returns empty list without calling GitHub."""
        result = get_unreplied_comments(str(tmp_path), 42, shakedown=True)
        assert result == []

    def test_returns_empty_on_no_bot_comments(self, tmp_path):
        """AC-139: returns empty when no bot comments exist."""
        comments = [
            {"id": 1, "user": {"login": "human"}, "in_reply_to_id": None,
             "path": "src/foo.py", "line": 10, "body": "Comment"},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)
        assert result == []

    def test_returns_empty_when_all_bot_comments_replied(self, tmp_path):
        """AC-139: returns empty when every bot comment has a reply."""
        comments = [
            {"id": 1, "user": {"login": "gemini-code-assist[bot]"}, "in_reply_to_id": None,
             "path": "src/a.py", "line": 1, "body": "Issue A"},
            {"id": 2, "user": {"login": "human"}, "in_reply_to_id": 1,
             "path": "src/a.py", "line": 1, "body": "Addressed"},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)
        assert result == []

    def test_is_triage_complete_delegates(self, tmp_path):
        """AC-139: is_triage_complete returns True when no unreplied comments."""
        with patch("triage_common.get_unreplied_comments", return_value=[]):
            assert is_triage_complete(str(tmp_path), 42) is True

        with patch("triage_common.get_unreplied_comments",
                   return_value=[{"id": 1}]):
            assert is_triage_complete(str(tmp_path), 42) is False


# ---------------------------------------------------------------------------
# reconciliation_loop (AC-140)
# ---------------------------------------------------------------------------


class TestReconciliationLoop:
    def test_successive_calls_reflect_reply_state(self, tmp_path):
        """AC-140: get_unreplied_comments returns different results as replies appear."""
        # First call: one unreplied bot comment
        comments_before = [
            {"id": 10, "user": {"login": "gemini-code-assist[bot]"},
             "in_reply_to_id": None, "path": "x.py", "line": 1, "body": "Fix"},
        ]
        # Second call: same bot comment now has a reply
        comments_after = [
            {"id": 10, "user": {"login": "gemini-code-assist[bot]"},
             "in_reply_to_id": None, "path": "x.py", "line": 1, "body": "Fix"},
            {"id": 11, "user": {"login": "ppds-bot"}, "in_reply_to_id": 10,
             "path": "x.py", "line": 1, "body": "Fixed in abc"},
        ]

        mock_result_before = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments_before), stderr="")
        mock_result_after = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments_after), stderr="")

        with patch("triage_common.get_repo_slug", return_value="owner/repo"):
            with patch("triage_common.subprocess.run", return_value=mock_result_before):
                first = get_unreplied_comments(str(tmp_path), 42)
            with patch("triage_common.subprocess.run", return_value=mock_result_after):
                second = get_unreplied_comments(str(tmp_path), 42)

        assert len(first) == 1
        assert first[0]["id"] == 10
        assert len(second) == 0


# ---------------------------------------------------------------------------
# detect_gemini_overload (AC-141)
# ---------------------------------------------------------------------------


class TestDetectGeminiOverload:
    def test_detects_overload_message(self, tmp_path):
        """AC-141: detect_gemini_overload returns True on overload message."""
        comments = [
            {"user": {"login": "gemini-code-assist[bot]"},
             "body": "We're experiencing higher than usual traffic. Unable to create review."},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                assert detect_gemini_overload(str(tmp_path), 42) is True

    def test_returns_false_on_normal_comments(self, tmp_path):
        """AC-141: detect_gemini_overload returns False on normal review comments."""
        comments = [
            {"user": {"login": "gemini-code-assist[bot]"},
             "body": "Here is a summary of the review."},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                assert detect_gemini_overload(str(tmp_path), 42) is False

    def test_returns_false_in_shakedown(self, tmp_path):
        """AC-141: shakedown mode returns False without calling GitHub."""
        assert detect_gemini_overload(str(tmp_path), 42, shakedown=True) is False

    def test_detects_unable_to_create_variant(self, tmp_path):
        """AC-141: detect_gemini_overload matches 'unable to create' substring."""
        comments = [
            {"user": {"login": "gemini-code-assist[bot]"},
             "body": "Sorry, I was unable to create a review at this time."},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                assert detect_gemini_overload(str(tmp_path), 42) is True

    def test_ignores_non_gemini_bot_overload(self, tmp_path):
        """AC-141: overload message from a non-Gemini user is ignored."""
        comments = [
            {"user": {"login": "some-other-bot"},
             "body": "We're experiencing higher than usual traffic."},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                assert detect_gemini_overload(str(tmp_path), 42) is False


# ---------------------------------------------------------------------------
# v1-prelaunch retro item #5: paginate --slurp flatten
# ---------------------------------------------------------------------------


class TestPaginateSlurpFlatten:
    """v1-prelaunch retro item #5: gh api --paginate --slurp returns a
    list-of-pages where each page is itself a list. Without flattening,
    callers crash with AttributeError: 'list' object has no attribute 'get'.

    These tests verify get_unreplied_comments and detect_gemini_overload
    survive paginated responses (the actual production-API shape).
    """

    def test_get_unreplied_comments_handles_paginated_response(self, tmp_path):
        """gh api --paginate --slurp returns [[...], [...]] — must flatten."""
        page1 = [
            {"id": 1, "user": {"login": "gemini-code-assist[bot]"},
             "in_reply_to_id": None,
             "path": "a.py", "line": 1, "body": "Issue A"},
        ]
        page2 = [
            {"id": 2, "user": {"login": "github-advanced-security[bot]"},
             "in_reply_to_id": None,
             "path": "b.py", "line": 1, "body": "Issue B"},
        ]
        # Slurp returns list-of-pages
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps([page1, page2]), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)
        # Both bot comments unreplied -> both returned (no AttributeError)
        assert len(result) == 2
        ids = {c["id"] for c in result}
        assert ids == {1, 2}

    def test_get_unreplied_comments_handles_flat_response(self, tmp_path):
        """Already-flat response is returned unchanged."""
        flat = [
            {"id": 1, "user": {"login": "gemini-code-assist[bot]"},
             "in_reply_to_id": None,
             "path": "a.py", "line": 1, "body": "Issue"},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(flat), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)
        assert len(result) == 1

    def test_detect_overload_handles_paginated_response(self, tmp_path):
        """detect_gemini_overload must also flatten paginate-slurp output."""
        page1 = [{"user": {"login": "user1"}, "body": "hello"}]
        page2 = [{"user": {"login": "gemini-code-assist[bot]"},
                  "body": "We're experiencing higher than usual traffic"}]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps([page1, page2]), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                assert detect_gemini_overload(str(tmp_path), 42) is True


# ---------------------------------------------------------------------------
# unified triage pass (AC-145)
# ---------------------------------------------------------------------------


class TestUnifiedTriagePass:
    def test_handles_both_gemini_and_codeql_comments(self, tmp_path):
        """AC-145: get_unreplied_comments returns both Gemini and CodeQL unreplied."""
        comments = [
            {"id": 100, "user": {"login": "gemini-code-assist[bot]"},
             "in_reply_to_id": None,
             "path": "src/a.py", "line": 5, "body": "Style issue"},
            {"id": 200, "user": {"login": "github-advanced-security[bot]"},
             "in_reply_to_id": None,
             "path": "src/b.py", "line": 20, "body": "Potential vulnerability"},
            {"id": 300, "user": {"login": "human"}, "in_reply_to_id": None,
             "path": "src/c.py", "line": 1, "body": "Human comment"},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)

        assert len(result) == 2
        ids = {c["id"] for c in result}
        assert ids == {100, 200}

    def test_unified_triage_filters_replied_from_both_bots(self, tmp_path):
        """AC-145: replied comments from either bot are excluded."""
        comments = [
            {"id": 100, "user": {"login": "gemini-code-assist[bot]"},
             "in_reply_to_id": None,
             "path": "src/a.py", "line": 5, "body": "Style issue"},
            {"id": 200, "user": {"login": "github-advanced-security[bot]"},
             "in_reply_to_id": None,
             "path": "src/b.py", "line": 20, "body": "Potential vuln"},
            {"id": 300, "user": {"login": "dev"}, "in_reply_to_id": 100,
             "path": "src/a.py", "line": 5, "body": "Fixed"},
            {"id": 400, "user": {"login": "dev"}, "in_reply_to_id": 200,
             "path": "src/b.py", "line": 20, "body": "Dismissed"},
        ]
        mock_result = subprocess.CompletedProcess(
            args=[], returncode=0, stdout=json.dumps(comments), stderr="")
        with patch("triage_common.subprocess.run", return_value=mock_result):
            with patch("triage_common.get_repo_slug", return_value="owner/repo"):
                result = get_unreplied_comments(str(tmp_path), 42)

        assert result == []


# ---------------------------------------------------------------------------
# dispatch_subagent — AC-15 (dual-mode)
# ---------------------------------------------------------------------------

from unittest.mock import MagicMock
import pytest


class _FakeHandle:
    def __init__(self, *, exit_code=0, output="result-text", transcript=None):
        self._exit_code = exit_code
        self._output = output
        self.transcript_path = transcript

    def wait(self, timeout=None):
        return self._exit_code

    def output(self):
        return self._output

    def terminate(self):
        pass


@pytest.mark.parametrize("mode", ["interactive", "headless"])
def test_dispatch_subagent_both_modes(mode, tmp_path):
    """AC-15: dispatch_subagent forwards mode and shape to claude_dispatch.spawn,
    returning {stdout, stderr, exit_code}. The prompt is a wrapped form
    (directive + profile body + payload), not the raw JSON payload."""
    from triage_common import dispatch_subagent
    import claude_dispatch

    captured = {}

    def fake_spawn(**kw):
        captured.update(kw)
        return _FakeHandle(exit_code=0, output="ok", transcript=tmp_path / "t.jsonl")

    with patch.object(claude_dispatch, "spawn", fake_spawn):
        result = dispatch_subagent(
            profile_name="reviewer",
            payload={"key": "value"},
            mode=mode,
            worktree=str(tmp_path),
            caller="test.dispatch_subagent",
        )
    assert result == {"stdout": "ok", "stderr": "", "exit_code": 0}
    assert captured["mode"] == mode
    assert captured["caller"] == "test.dispatch_subagent"
    assert captured["agent"] == "reviewer"
    assert captured["name"] == "reviewer"
    # dangerous=True is required so permission prompts don't strand the session.
    assert captured["dangerous"] is True
    # Prompt is wrapped: directive + profile section + payload section,
    # with the payload embedded as pretty-printed JSON.
    prompt = captured["prompt"]
    assert "headless mode" in prompt
    assert "no operator to answer questions" in prompt
    assert "'reviewer' agent" in prompt
    assert "=== AGENT PROFILE ===" in prompt
    assert "=== PAYLOAD ===" in prompt
    assert '"key": "value"' in prompt


def test_dispatch_subagent_inlines_profile_body(tmp_path):
    """#1086: interactive mode silently drops --agent, so the profile body must
    be inlined into the prompt. dispatch_subagent reads
    .claude/agents/<name>.md from the worktree and embeds the body
    (frontmatter stripped) under the AGENT PROFILE marker."""
    from triage_common import dispatch_subagent
    import claude_dispatch

    agents_dir = tmp_path / ".claude" / "agents"
    agents_dir.mkdir(parents=True)
    (agents_dir / "demo.md").write_text(
        "---\nname: demo\nmodel: sonnet\n---\n\n"
        "# Demo Agent\n\nDo the demo task.\n",
        encoding="utf-8",
    )

    captured = {}
    def fake_spawn(**kw):
        captured.update(kw)
        return _FakeHandle()
    with patch.object(claude_dispatch, "spawn", fake_spawn):
        dispatch_subagent(profile_name="demo", payload={}, worktree=str(tmp_path),
                          mode="interactive")
    prompt = captured["prompt"]
    assert "# Demo Agent" in prompt
    assert "Do the demo task." in prompt
    # Frontmatter MUST be stripped — the model only sees the body.
    assert "model: sonnet" not in prompt


def test_dispatch_subagent_missing_profile_does_not_crash(tmp_path):
    """When the profile file is missing the prompt still has directive + payload —
    callers may use ad-hoc profile names for one-off agents."""
    from triage_common import dispatch_subagent
    import claude_dispatch
    captured = {}
    def fake_spawn(**kw):
        captured.update(kw)
        return _FakeHandle()
    with patch.object(claude_dispatch, "spawn", fake_spawn):
        dispatch_subagent(profile_name="nonexistent", payload={"x": 1},
                          worktree=str(tmp_path), mode="interactive")
    prompt = captured["prompt"]
    assert "headless mode" in prompt
    assert "'nonexistent' agent" in prompt
    assert '"x": 1' in prompt


def test_dispatch_subagent_caller_default(tmp_path):
    """When caller is None, default to 'triage_common.dispatch_subagent'."""
    from triage_common import dispatch_subagent
    import claude_dispatch
    captured = {}
    def fake_spawn(**kw):
        captured.update(kw)
        return _FakeHandle()
    with patch.object(claude_dispatch, "spawn", fake_spawn):
        dispatch_subagent(profile_name="p", payload={}, worktree=str(tmp_path),
                          mode="interactive")
    assert captured["caller"] == "triage_common.dispatch_subagent"


def test_dispatch_subagent_dispatch_error_returns_error_shape(tmp_path):
    """DispatchError from spawn propagates as exit_code != 0, stderr captured."""
    from triage_common import dispatch_subagent
    import claude_dispatch
    def fake_spawn(**kw):
        raise claude_dispatch.DispatchError("nope", exit_code=2)
    with patch.object(claude_dispatch, "spawn", fake_spawn):
        result = dispatch_subagent(profile_name="p", payload={},
                                   worktree=str(tmp_path), mode="interactive")
    assert result["exit_code"] == 2
    assert "nope" in result["stderr"]


def test_dispatch_subagent_blocked_returns_loud_shape(tmp_path):
    """BlockedSessionError surfaces as exit_code=1, stderr includes needs text."""
    from triage_common import dispatch_subagent
    import claude_dispatch

    class _BlockedHandle(_FakeHandle):
        def wait(self, timeout=None):
            raise claude_dispatch.BlockedSessionError("abc12345", "what env?")

    with patch.object(claude_dispatch, "spawn", lambda **kw: _BlockedHandle()):
        result = dispatch_subagent(profile_name="p", payload={},
                                   worktree=str(tmp_path), mode="interactive")
    assert result["exit_code"] == 1
    assert "blocked" in result["stderr"]
    assert "what env?" in result["stderr"]
