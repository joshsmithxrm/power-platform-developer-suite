#!/usr/bin/env python3
"""Tests for retro_helpers.py (RF AC-10–13)."""
import json
import os
import sys
import tempfile

import pytest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))


class TestExtractTranscriptSignals:
    def test_extract_transcript_signals(self):
        """RF AC-10: Returns structured signals from JSONL."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "session.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"human","message":{"content":"no, that is wrong"}}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build"}}]}}\n')

            signals = retro_helpers.extract_transcript_signals(jsonl_path)
            assert "user_corrections" in signals
            assert "tool_failures" in signals
            assert "repeated_commands" in signals


class TestUserCorrectionDetection:
    def test_user_correction_detection(self):
        """RF AC-11: Detects user correction patterns."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "session.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"human","message":{"content":"no, try again"}}\n')
                f.write('{"type":"human","message":{"content":"that is wrong"}}\n')
                f.write('{"type":"human","message":{"content":"great work!"}}\n')

            signals = retro_helpers.extract_transcript_signals(jsonl_path)
            assert len(signals["user_corrections"]) >= 2


class TestToolFailureDetection:
    def test_tool_failure_detection(self):
        """RF AC-12: Detects tool failures (non-zero exit, file not found, old_string not found)."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "session.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"tool_result","content":[{"type":"tool_result","content":"Exit code: 1\\nError: build failed"}]}\n')
                f.write('{"type":"tool_result","content":[{"type":"tool_result","content":"file not found: foo.txt"}]}\n')

            signals = retro_helpers.extract_transcript_signals(jsonl_path)
            assert len(signals["tool_failures"]) >= 2


class TestEnforcementSignalExtraction:
    def test_enforcement_signal_extraction(self):
        """RF AC-13: Reads stop_hook_count from state."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            state_path = os.path.join(tmpdir, "state.json")
            with open(state_path, "w") as f:
                json.dump({
                    "stop_hook_blocked": True,
                    "stop_hook_count": 5,
                    "stop_hook_last": "2026-03-27T10:00:00Z",
                }, f)

            signals = retro_helpers.extract_enforcement_signals(state_path)
            assert signals["stop_hook_count"] == 5
            assert signals["stop_hook_blocked"] is True
            assert signals["stop_hook_last"] == "2026-03-27T10:00:00Z"

    def test_missing_state_returns_defaults(self):
        """RF AC-13: Missing state file returns safe defaults."""
        import retro_helpers
        signals = retro_helpers.extract_enforcement_signals("/nonexistent/state.json")
        assert signals["stop_hook_count"] == 0
        assert signals["stop_hook_blocked"] is False


class TestDiscoverTranscripts:
    def test_discover_transcripts(self):
        """discover_transcripts finds JSONL files in .workflow/stages."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            stages_dir = os.path.join(tmpdir, ".workflow", "stages")
            os.makedirs(stages_dir)
            with open(os.path.join(stages_dir, "implement.jsonl"), "w") as f:
                f.write('{"type":"test"}\n')

            transcripts = retro_helpers.discover_transcripts(tmpdir)
            assert any("implement.jsonl" in t for t in transcripts)
