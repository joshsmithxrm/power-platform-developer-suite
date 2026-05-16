#!/usr/bin/env python3
"""Unit tests for retro_helpers.py signal extraction.

Fixture snippets approximate the PR #1094 and PR #1095 transcript shapes
that the old extractor silently missed due to three bugs:
  1. event_type "human" instead of "user"
  2. "Exit code:" string matching instead of is_error: true
  3. Missing question-form correction patterns

Usage:
    python scripts/test_retro_helpers.py
    python -m pytest scripts/test_retro_helpers.py
"""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from retro_helpers import extract_transcript_signals, write_session_flags

# ---------------------------------------------------------------------------
# Shared fixture events
# ---------------------------------------------------------------------------

# PR #1095: operator question-form correction in a "user" event
_PR1095_OPERATOR_CORRECTION = {
    "type": "user",
    "message": {
        "role": "user",
        "content": (
            "why is the PR ready but the monitor hasn't been run? "
            "gemini comments were not responded to."
        ),
    },
}

# PR #1094: tool failure signalled by is_error: true (not "Exit code:")
_PR1094_TOOL_FAILURE_IS_ERROR = {
    "type": "user",
    "message": {
        "role": "user",
        "content": [
            {
                "type": "tool_result",
                "tool_use_id": "toolu_abc",
                "content": "Error: pr_monitor PID 38784 already running; duplicate POST detected",
                "is_error": True,
            }
        ],
    },
}

# Old "human" event type — the extractor must NOT match this (regression guard)
_HUMAN_TYPE_CORRECTION = {
    "type": "human",
    "message": {
        "role": "user",
        "content": "no, wrong, try again",
    },
}

# Plain direct correction in a "user" event
_DIRECT_CORRECTION = {
    "type": "user",
    "message": {
        "role": "user",
        "content": "no, that's not right — try the other branch",
    },
}

# Edit tool failure via content substring (no is_error flag)
_EDIT_FAILURE_SUBSTRING = {
    "type": "user",
    "message": {
        "role": "user",
        "content": [
            {
                "type": "tool_result",
                "tool_use_id": "toolu_def",
                "content": "Error: old_string not found in scripts/pipeline.py",
                "is_error": False,
            }
        ],
    },
}

# Read tool failure via content substring (no is_error flag)
_READ_FAILURE_SUBSTRING = {
    "type": "user",
    "message": {
        "role": "user",
        "content": [
            {
                "type": "tool_result",
                "tool_use_id": "toolu_ghi",
                "content": "Error: file not found: .workflow/state.json",
                "is_error": False,
            }
        ],
    },
}

# Assistant bash tool-use (for tool_call_count and repetition tracking)
_BASH_TOOL_USE = {
    "type": "assistant",
    "message": {
        "role": "assistant",
        "content": [
            {
                "type": "tool_use",
                "id": "toolu_1",
                "name": "Bash",
                "input": {"command": "git status"},
            }
        ],
    },
}

# Assistant non-Bash tool-use (counts toward tool_call_count but not commands)
_READ_TOOL_USE = {
    "type": "assistant",
    "message": {
        "role": "assistant",
        "content": [
            {
                "type": "tool_use",
                "id": "toolu_2",
                "name": "Read",
                "input": {"file_path": "/some/file.py"},
            }
        ],
    },
}


def _run_on_events(events):
    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".jsonl", delete=False, encoding="utf-8"
    ) as f:
        for e in events:
            f.write(json.dumps(e) + "\n")
        path = f.name
    try:
        return extract_transcript_signals(path)
    finally:
        os.unlink(path)


# ---------------------------------------------------------------------------
# Bug 1: event_type "user" (not "human")
# ---------------------------------------------------------------------------

class TestEventTypeFix(unittest.TestCase):

    def test_user_type_direct_correction_detected(self):
        """Direct correction in a 'user' event is extracted."""
        sigs = _run_on_events([_DIRECT_CORRECTION])
        self.assertEqual(len(sigs["user_corrections"]), 1)
        self.assertIn("no,", sigs["user_corrections"][0]["text"])

    def test_human_type_event_silently_ignored(self):
        """'human' event type (wrong) does not produce corrections — regression guard."""
        sigs = _run_on_events([_HUMAN_TYPE_CORRECTION])
        self.assertEqual(len(sigs["user_corrections"]), 0)

    def test_user_type_list_content_correction(self):
        """Correction in a list-content 'user' event (mixed text + tool_result) is detected."""
        event = {
            "type": "user",
            "message": {
                "role": "user",
                "content": [
                    {"type": "text", "text": "no, wrong approach"},
                    {"type": "tool_result", "tool_use_id": "x", "content": "ok"},
                ],
            },
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["user_corrections"]), 1)


# ---------------------------------------------------------------------------
# Bug 2: is_error: true detection
# ---------------------------------------------------------------------------

class TestToolFailureFix(unittest.TestCase):

    def test_is_error_true_flagged(self):
        """tool_result blocks with is_error: true are flagged as tool failures (PR #1094 fixture)."""
        sigs = _run_on_events([_PR1094_TOOL_FAILURE_IS_ERROR])
        self.assertEqual(len(sigs["tool_failures"]), 1)
        self.assertIn("38784", sigs["tool_failures"][0]["error"])
        self.assertEqual(sigs["tool_failures"][0]["tool"], "unknown")

    def test_is_error_false_clean_result_not_flagged(self):
        """tool_result with is_error: false and clean content is not flagged."""
        event = {
            "type": "user",
            "message": {
                "role": "user",
                "content": [
                    {
                        "type": "tool_result",
                        "tool_use_id": "toolu_ok",
                        "content": "main branch up to date",
                        "is_error": False,
                    }
                ],
            },
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["tool_failures"]), 0)

    def test_edit_failure_via_content_substring(self):
        """tool_result without is_error but with 'old_string not found' content is flagged."""
        sigs = _run_on_events([_EDIT_FAILURE_SUBSTRING])
        self.assertEqual(len(sigs["tool_failures"]), 1)
        self.assertEqual(sigs["tool_failures"][0]["tool"], "Edit")

    def test_read_failure_via_content_substring(self):
        """tool_result without is_error but with 'file not found' content is flagged."""
        sigs = _run_on_events([_READ_FAILURE_SUBSTRING])
        self.assertEqual(len(sigs["tool_failures"]), 1)
        self.assertEqual(sigs["tool_failures"][0]["tool"], "Read")

    def test_multiple_is_error_failures_all_captured(self):
        """Multiple is_error: true blocks in one session are all captured."""
        sigs = _run_on_events([_PR1094_TOOL_FAILURE_IS_ERROR, _PR1094_TOOL_FAILURE_IS_ERROR])
        self.assertEqual(len(sigs["tool_failures"]), 2)

    def test_is_error_true_with_identifiable_content_attributes_tool(self):
        """is_error: true with content matching a known tool marker attributes to that tool, not 'unknown'."""
        event = {
            "type": "user",
            "message": {
                "role": "user",
                "content": [
                    {
                        "type": "tool_result",
                        "tool_use_id": "toolu_jkl",
                        "content": "old_string not found in scripts/pipeline.py",
                        "is_error": True,
                    }
                ],
            },
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["tool_failures"]), 1)
        self.assertEqual(sigs["tool_failures"][0]["tool"], "Edit")


# ---------------------------------------------------------------------------
# Bug 3: question-form correction patterns
# ---------------------------------------------------------------------------

class TestQuestionFormCorrections(unittest.TestCase):

    def test_pr1095_operator_correction_detected(self):
        """Question-form correction from PR #1095 ('why is the PR ready...') is detected."""
        sigs = _run_on_events([_PR1095_OPERATOR_CORRECTION])
        self.assertEqual(len(sigs["user_corrections"]), 1)

    def test_why_prefix_triggers_correction(self):
        event = {
            "type": "user",
            "message": {"role": "user", "content": "why didn't you run the tests first?"},
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["user_corrections"]), 1)

    def test_shouldnt_prefix_triggers_correction(self):
        event = {
            "type": "user",
            "message": {"role": "user", "content": "shouldn't you have checked the logs?"},
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["user_corrections"]), 1)

    def test_didnt_you_prefix_triggers_correction(self):
        event = {
            "type": "user",
            "message": {"role": "user", "content": "didn't you already push that branch?"},
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["user_corrections"]), 1)

    def test_werent_you_supposed_triggers_correction(self):
        event = {
            "type": "user",
            "message": {"role": "user", "content": "weren't you supposed to wait for review?"},
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["user_corrections"]), 1)

    def test_isnt_this_triggers_correction(self):
        event = {
            "type": "user",
            "message": {"role": "user", "content": "isn't this the wrong file to edit?"},
        }
        sigs = _run_on_events([event])
        self.assertEqual(len(sigs["user_corrections"]), 1)


# ---------------------------------------------------------------------------
# Escalation flags
# ---------------------------------------------------------------------------

class TestEscalationFlags(unittest.TestCase):

    def test_needs_manual_review_on_user_correction(self):
        """needs_manual_review is True when user_corrections > 0."""
        sigs = _run_on_events([_DIRECT_CORRECTION])
        self.assertTrue(sigs["needs_manual_review"])

    def test_needs_manual_review_on_tool_failures_above_threshold(self):
        """needs_manual_review is True when tool_failures > 2."""
        sigs = _run_on_events([_PR1094_TOOL_FAILURE_IS_ERROR] * 3)
        self.assertTrue(sigs["needs_manual_review"])

    def test_needs_manual_review_on_repeated_commands_above_threshold(self):
        """needs_manual_review is True when >3 distinct commands are each repeated 3+ times."""
        def _cmd(c):
            return {
                "type": "assistant",
                "message": {
                    "role": "assistant",
                    "content": [{"type": "tool_use", "id": "t", "name": "Bash", "input": {"command": c}}],
                },
            }
        events = []
        for cmd in ["git status", "git log", "git diff", "ls -la"]:
            events.extend([_cmd(cmd)] * 3)
        sigs = _run_on_events(events)
        self.assertTrue(sigs["needs_manual_review"])

    def test_frustration_hits_content_captured(self):
        """frustration_hits list contains the matched text when a frustration pattern fires."""
        event = {
            "type": "user",
            "message": {"role": "user", "content": "wtf why isn't this working"},
        }
        sigs = _run_on_events([event])
        self.assertGreaterEqual(len(sigs["frustration_hits"]), 1)
        self.assertIn("wtf", sigs["frustration_hits"][0]["text"])

    def test_needs_manual_review_false_on_clean_session(self):
        """needs_manual_review is False when all signals are empty."""
        sigs = _run_on_events([_READ_TOOL_USE])
        self.assertFalse(sigs["needs_manual_review"])

    def test_signal_extractor_suspect_on_heavy_clean_session(self):
        """signal_extractor_suspect fires when >50 tool calls produce zero signals.

        Uses Read tool calls (non-Bash) so the command-repetition detector stays quiet.
        """
        sigs = _run_on_events([_READ_TOOL_USE] * 51)
        self.assertTrue(sigs["signal_extractor_suspect"])

    def test_signal_extractor_suspect_false_when_signals_present(self):
        """signal_extractor_suspect does not fire when at least one signal exists."""
        sigs = _run_on_events([_READ_TOOL_USE] * 51 + [_DIRECT_CORRECTION])
        self.assertFalse(sigs["signal_extractor_suspect"])

    def test_signal_extractor_suspect_false_below_threshold(self):
        """signal_extractor_suspect does not fire on small clean sessions."""
        sigs = _run_on_events([_BASH_TOOL_USE] * 5)
        self.assertFalse(sigs["signal_extractor_suspect"])

    def test_both_flags_in_return_value(self):
        """Both escalation flags are always present in the return value."""
        sigs = _run_on_events([])
        self.assertIn("needs_manual_review", sigs)
        self.assertIn("signal_extractor_suspect", sigs)


# ---------------------------------------------------------------------------
# Repeated command detection
# ---------------------------------------------------------------------------

class TestRepeatedCommands(unittest.TestCase):

    def test_command_3_times_flagged(self):
        sigs = _run_on_events([_BASH_TOOL_USE] * 3)
        self.assertEqual(len(sigs["repeated_commands"]), 1)
        self.assertEqual(sigs["repeated_commands"][0]["count"], 3)

    def test_command_2_times_not_flagged(self):
        sigs = _run_on_events([_BASH_TOOL_USE] * 2)
        self.assertEqual(len(sigs["repeated_commands"]), 0)

    def test_empty_transcript_all_empty(self):
        sigs = _run_on_events([])
        self.assertEqual(sigs["user_corrections"], [])
        self.assertEqual(sigs["tool_failures"], [])
        self.assertEqual(sigs["repeated_commands"], [])
        self.assertFalse(sigs["needs_manual_review"])
        self.assertFalse(sigs["signal_extractor_suspect"])


# ---------------------------------------------------------------------------
# write_session_flags
# ---------------------------------------------------------------------------

class TestWriteSessionFlags(unittest.TestCase):

    def test_creates_file_with_flags(self):
        """write_session_flags creates retro-findings.json with escalation flags."""
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "retro-findings.json")
            write_session_flags(path, {"needs_manual_review": True, "signal_extractor_suspect": False})
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        self.assertTrue(data["needs_manual_review"])
        self.assertFalse(data["signal_extractor_suspect"])

    def test_preserves_existing_keys(self):
        """write_session_flags preserves existing keys like allowlist_drift."""
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "retro-findings.json")
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"allowlist_drift": [{"file": "x.py"}]}, f)
            write_session_flags(path, {"needs_manual_review": True})
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        self.assertIn("allowlist_drift", data)
        self.assertTrue(data["needs_manual_review"])

    def test_creates_parent_dir_if_missing(self):
        """write_session_flags creates the parent directory if it doesn't exist."""
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "subdir", "retro-findings.json")
            write_session_flags(path, {"needs_manual_review": False})
            self.assertTrue(os.path.exists(path))

    def test_overwrites_stale_flags(self):
        """write_session_flags overwrites a previously written flag value."""
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "retro-findings.json")
            write_session_flags(path, {"needs_manual_review": False})
            write_session_flags(path, {"needs_manual_review": True})
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        self.assertTrue(data["needs_manual_review"])


if __name__ == "__main__":
    unittest.main()
