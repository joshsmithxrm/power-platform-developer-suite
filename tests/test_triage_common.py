"""Tests for scripts/triage_common.py shared triage functions."""
import json
import os
import sys

# Add scripts dir to path so we can import triage_common
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

from triage_common import (
    build_triage_prompt,
    format_reply_body,
    get_repo_slug,
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
