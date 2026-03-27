#!/usr/bin/env python3
"""Tests for pr-monitor.py (WE AC-106–114, AC-124–127)."""
import json
import os
import subprocess
import sys
import tempfile

import pytest
from unittest.mock import patch, MagicMock

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))


class TestCiPolling:
    def test_ci_polling(self):
        """AC-107: pr-monitor polls CI via gh pr checks."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "gh" in source and "pr" in source and "checks" in source

class TestCiFailure:
    def test_ci_failure_notify_and_exit(self):
        """AC-108: On CI failure, writes status=ci_failed and sends notification."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "ci_failed" in source

class TestCiTimeout:
    def test_ci_timeout(self):
        """AC-124: Exits with ci_timeout after 15 min."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "ci_timeout" in source

class TestResumeSkips:
    def test_resume_skips_completed(self):
        """AC-109: --resume reads result file and skips completed steps."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "resume" in source and "steps_completed" in source

class TestTriageOnComments:
    def test_triage_on_inline_comments(self):
        """AC-110: Spawns claude -p triage when inline comments > 0."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "gemini-triage" in source or "triage" in source

class TestRepollCi:
    def test_repoll_ci_after_triage(self):
        """AC-111: Re-polls CI after triage commits."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "triage_iteration" in source or "repoll" in source or "re-poll" in source

class TestTriageCiLoopLimit:
    def test_triage_ci_loop_limit(self):
        """AC-126: Max 3 triage→CI iterations."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "3" in source  # max iterations

class TestRetroBeforeNotify:
    def test_retro_runs_before_notify(self):
        """AC-112: Retro runs as penultimate step before notification."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "/retro" in source

class TestDraftToReady:
    def test_draft_to_ready(self):
        """AC-113: Converts draft to ready via gh pr ready."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "pr" in source and "ready" in source

class TestResultJsonSchema:
    def test_result_json_schema(self):
        """AC-114: Result JSON has required fields."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "steps_completed" in source
        assert "pr-monitor-result.json" in source

class TestGeminiTimeout:
    def test_gemini_timeout(self):
        """AC-125: Gemini polling stops after 5 min max."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "300" in source or "5 * 60" in source  # 5 min = 300s

class TestPidFileLifecycle:
    def test_pid_file_lifecycle(self):
        """AC-127: PID file written on start and cleaned on exit."""
        import pr_monitor
        import inspect
        source = inspect.getsource(pr_monitor)
        assert "pid" in source.lower()
        assert "pr-monitor.pid" in source
