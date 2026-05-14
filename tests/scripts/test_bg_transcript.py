"""Unit tests for scripts/bg_transcript.py — ACs 11-12 plus regression."""
from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
_MOD_PATH = _REPO / "scripts" / "bg_transcript.py"

_spec = importlib.util.spec_from_file_location("bg_transcript", str(_MOD_PATH))
_mod = importlib.util.module_from_spec(_spec)
sys.modules["bg_transcript"] = _mod
_spec.loader.exec_module(_mod)

parse_outcome = _mod.parse_outcome
iter_assistant_text = _mod.iter_assistant_text
extract_text_from_jsonl = _mod.extract_text_from_jsonl


def _write_jsonl(path: Path, events: list[dict]) -> None:
    path.write_text("\n".join(json.dumps(e) for e in events) + "\n", encoding="utf-8")


def _assistant(text: str) -> dict:
    return {"type": "assistant", "message": {"content": [{"type": "text", "text": text}]}}


def _result(text: str) -> dict:
    return {"type": "result", "result": text}


# -- AC-11 -----------------------------------------------------------------

def test_bg_transcript_prefers_result(tmp_path):
    """AC-11: when both ``type:result`` and ``type:assistant`` events exist,
    parse_outcome returns the result text, not the assistant text."""
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [
        _assistant("partial thinking 1"),
        _assistant("partial thinking 2"),
        _result("final clean answer"),
    ])
    assert parse_outcome(p) == "final clean answer"


def test_parse_outcome_concatenates_multiple_result_events(tmp_path):
    """Multiple result events (rare but possible) are joined with newline."""
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [_result("first"), _result("second")])
    assert parse_outcome(p) == "first\nsecond"


# -- AC-12 -----------------------------------------------------------------

def test_bg_transcript_assembles_assistant_on_timeout(tmp_path):
    """AC-12: with no result event (timeout / crash), assistant text blocks
    are joined with double-newline."""
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [_assistant("turn 1 text"), _assistant("turn 2 text")])
    assert parse_outcome(p) == "turn 1 text\n\nturn 2 text"


def test_parse_outcome_ignores_non_text_blocks(tmp_path):
    """tool_use and tool_result blocks are filtered out of the assembled text."""
    p = tmp_path / "session.jsonl"
    events = [
        {"type": "assistant", "message": {"content": [
            {"type": "text", "text": "answer"},
            {"type": "tool_use", "name": "Bash", "input": {}},
        ]}},
    ]
    _write_jsonl(p, events)
    assert parse_outcome(p) == "answer"


# -- Edge cases ------------------------------------------------------------

def test_parse_outcome_empty_file_returns_empty_string(tmp_path):
    p = tmp_path / "empty.jsonl"
    p.write_text("", encoding="utf-8")
    assert parse_outcome(p) == ""


def test_parse_outcome_missing_file_returns_empty_string(tmp_path):
    p = tmp_path / "nonexistent.jsonl"
    assert parse_outcome(p) == ""


def test_parse_outcome_malformed_lines_skipped(tmp_path):
    """JSONDecodeError on a line is swallowed; subsequent valid lines still parsed."""
    p = tmp_path / "session.jsonl"
    p.write_text("{not valid json}\n" + json.dumps(_result("survived")) + "\n", encoding="utf-8")
    assert parse_outcome(p) == "survived"


def test_parse_outcome_skips_empty_assistant_text(tmp_path):
    """A text block with empty string contributes nothing."""
    p = tmp_path / "session.jsonl"
    events = [
        _assistant(""),
        _assistant("real content"),
    ]
    _write_jsonl(p, events)
    assert parse_outcome(p) == "real content"


# -- iter_assistant_text ---------------------------------------------------

def test_iter_assistant_text_yields_offsets(tmp_path):
    """iter_assistant_text yields (post-line byte offset, text) for assistant events."""
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [_assistant("alpha"), _assistant("beta")])
    yielded = list(iter_assistant_text(p))
    assert len(yielded) == 2
    assert [t for _, t in yielded] == ["alpha", "beta"]
    # Offsets must be monotonically increasing.
    assert yielded[0][0] < yielded[1][0]


def test_iter_assistant_text_resumes_from_offset(tmp_path):
    """Passing a prior offset resumes mid-file."""
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [_assistant("first"), _assistant("second")])
    first_offset, first_text = next(iter_assistant_text(p))
    assert first_text == "first"
    rest = list(iter_assistant_text(p, offset=first_offset))
    assert [t for _, t in rest] == ["second"]


def test_iter_assistant_text_skips_result_events(tmp_path):
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [_assistant("included"), _result("excluded")])
    yielded = list(iter_assistant_text(p))
    assert [t for _, t in yielded] == ["included"]


# -- Regression: pipeline.py keeps working via re-export -------------------

def test_pipeline_re_export_keeps_existing_calls_working(tmp_path):
    """scripts/pipeline.py imports extract_text_from_jsonl from bg_transcript;
    callers using the old name on a fixture JSONL must still work."""
    _PIPELINE_PATH = _REPO / "scripts" / "pipeline.py"
    _SCRIPTS_DIR = str(_REPO / "scripts")
    if _SCRIPTS_DIR not in sys.path:
        sys.path.insert(0, _SCRIPTS_DIR)
    spec = importlib.util.spec_from_file_location("pipeline", str(_PIPELINE_PATH))
    mod = importlib.util.module_from_spec(spec)
    sys.modules["pipeline"] = mod
    spec.loader.exec_module(mod)
    p = tmp_path / "session.jsonl"
    _write_jsonl(p, [_result("re-exported call works")])
    assert mod.extract_text_from_jsonl(str(p)) == "re-exported call works"


def test_extract_text_from_jsonl_alias_is_parse_outcome():
    """extract_text_from_jsonl is exactly parse_outcome (backwards-compat alias)."""
    assert extract_text_from_jsonl is parse_outcome
