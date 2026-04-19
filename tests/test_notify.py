"""Tests for .claude/hooks/notify.py state-aware behavior.

v1-prelaunch retro item #6: notify.py used to fire a "PR Ready" toast on
every idle_prompt event whenever workflow state had pr.url set, even if
the PR was a draft. The spurious notifications eroded user trust. The
fix queries ``gh pr view --json isDraft,state`` (cached 30s) and only
fires when state == OPEN and isDraft == False; a separate "PR merged"
toast fires when state == MERGED.
"""
import importlib.util
import json
import os
import subprocess
import sys
from unittest.mock import patch, MagicMock

import pytest


def _load_notify():
    hook_path = os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "notify.py",
    )
    hook_path = os.path.normpath(hook_path)
    spec = importlib.util.spec_from_file_location("notify", hook_path)
    mod = importlib.util.module_from_spec(spec)
    hooks_dir = os.path.dirname(hook_path)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec.loader.exec_module(mod)
    return mod


notify = _load_notify()


@pytest.fixture(autouse=True)
def _clear_pr_state_cache():
    """Each test starts with a fresh cache."""
    notify._PR_STATE_CACHE.clear()
    yield
    notify._PR_STATE_CACHE.clear()


def _gh_pr_view(is_draft, state):
    return subprocess.CompletedProcess(
        args=[], returncode=0,
        stdout=json.dumps({"isDraft": is_draft, "state": state}),
        stderr="",
    )


class TestPrNumberFromUrl:
    def test_extracts_trailing_number(self):
        assert notify._pr_number_from_url(
            "https://github.com/owner/repo/pull/803") == "803"

    def test_strips_trailing_slash(self):
        assert notify._pr_number_from_url(
            "https://github.com/o/r/pull/42/") == "42"

    def test_returns_none_for_non_numeric_tail(self):
        assert notify._pr_number_from_url(
            "https://github.com/o/r/pull/foo") is None

    def test_returns_none_for_empty(self):
        assert notify._pr_number_from_url("") is None
        assert notify._pr_number_from_url(None) is None


class TestFetchPrState:
    def test_returns_state_for_open_non_draft(self, tmp_path):
        with patch("notify.subprocess.run",
                   return_value=_gh_pr_view(False, "OPEN")):
            result = notify.fetch_pr_state(str(tmp_path), "42")
        assert result == {"isDraft": False, "state": "OPEN"}

    def test_returns_state_for_draft(self, tmp_path):
        with patch("notify.subprocess.run",
                   return_value=_gh_pr_view(True, "OPEN")):
            result = notify.fetch_pr_state(str(tmp_path), "42")
        assert result == {"isDraft": True, "state": "OPEN"}

    def test_returns_state_for_merged(self, tmp_path):
        with patch("notify.subprocess.run",
                   return_value=_gh_pr_view(False, "MERGED")):
            result = notify.fetch_pr_state(str(tmp_path), "42")
        assert result == {"isDraft": False, "state": "MERGED"}

    def test_returns_none_when_gh_fails(self, tmp_path):
        fail = subprocess.CompletedProcess(
            args=[], returncode=1, stdout="", stderr="not found")
        with patch("notify.subprocess.run", return_value=fail):
            assert notify.fetch_pr_state(str(tmp_path), "42") is None

    def test_returns_none_when_gh_missing(self, tmp_path):
        with patch("notify.subprocess.run",
                   side_effect=FileNotFoundError):
            assert notify.fetch_pr_state(str(tmp_path), "42") is None

    def test_returns_none_when_pr_number_missing(self, tmp_path):
        assert notify.fetch_pr_state(str(tmp_path), None) is None
        assert notify.fetch_pr_state(str(tmp_path), "") is None

    def test_caches_within_ttl(self, tmp_path):
        """Two calls within TTL produce only one subprocess call."""
        mock = MagicMock(return_value=_gh_pr_view(False, "OPEN"))
        with patch("notify.subprocess.run", mock):
            r1 = notify.fetch_pr_state(str(tmp_path), "42", now=1000.0)
            r2 = notify.fetch_pr_state(str(tmp_path), "42", now=1015.0)
        assert r1 == r2
        assert mock.call_count == 1, "Cache hit must skip the subprocess call"

    def test_cache_expires_after_ttl(self, tmp_path):
        """A call past TTL forces a fresh subprocess call."""
        mock = MagicMock(return_value=_gh_pr_view(False, "OPEN"))
        with patch("notify.subprocess.run", mock):
            notify.fetch_pr_state(str(tmp_path), "42", now=1000.0)
            notify.fetch_pr_state(str(tmp_path), "42", now=1031.0)  # past 30s
        assert mock.call_count == 2


class TestNotifyMainStateAware:
    """v1-prelaunch retro item #6: main MUST consult GH state before firing."""

    def _run_main_with_stdin(self, stdin_payload, gh_state, tmp_path,
                              monkeypatch):
        # Mark cwd as a worktree (.git as file)
        wt = tmp_path / "wt"
        wt.mkdir()
        (wt / ".git").write_text("gitdir: ../bare/.git")
        wf = wt / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "pr": {"url": "https://github.com/o/r/pull/777"},
        }))
        # Build stdin
        stdin_payload = dict(stdin_payload)
        stdin_payload.setdefault("cwd", str(wt))

        captured_toasts = []

        def fake_show_toast(title, msg, url):
            captured_toasts.append((title, msg, url))

        gh_responses = (
            _gh_pr_view(gh_state["isDraft"], gh_state["state"])
            if gh_state is not None else
            subprocess.CompletedProcess(args=[], returncode=1, stdout="",
                                        stderr="")
        )

        monkeypatch.setattr(sys, "argv", ["notify.py"])
        monkeypatch.setattr(sys, "stdin",
                             type("X", (), {"read": lambda self: ""})())
        # Use an io-style object for json.load(sys.stdin)
        import io
        monkeypatch.setattr(sys, "stdin",
                             io.StringIO(json.dumps(stdin_payload)))
        monkeypatch.setattr(notify, "show_toast", fake_show_toast)
        monkeypatch.setattr(notify.subprocess, "run",
                             lambda *a, **kw: gh_responses)
        monkeypatch.delenv("PPDS_PIPELINE", raising=False)
        monkeypatch.delenv("PPDS_SHAKEDOWN", raising=False)

        notify.main()
        return captured_toasts

    def test_open_non_draft_fires_pr_ready_toast(self, tmp_path, monkeypatch):
        toasts = self._run_main_with_stdin(
            {}, {"isDraft": False, "state": "OPEN"}, tmp_path, monkeypatch)
        assert len(toasts) == 1
        title, msg, url = toasts[0]
        assert "PR" in title or "Ready" in title
        assert "777" in url

    def test_draft_does_not_fire(self, tmp_path, monkeypatch):
        """v1-prelaunch retro #6: drafts MUST NOT fire 'PR Ready'."""
        toasts = self._run_main_with_stdin(
            {}, {"isDraft": True, "state": "OPEN"}, tmp_path, monkeypatch)
        assert toasts == []

    def test_merged_fires_separate_toast(self, tmp_path, monkeypatch):
        """v1-prelaunch retro #6: MERGED state fires a distinct 'PR merged' toast."""
        toasts = self._run_main_with_stdin(
            {}, {"isDraft": False, "state": "MERGED"}, tmp_path, monkeypatch)
        assert len(toasts) == 1
        title, msg, url = toasts[0]
        assert "merged" in title.lower()
        assert "777" in url

    def test_closed_does_not_fire(self, tmp_path, monkeypatch):
        toasts = self._run_main_with_stdin(
            {}, {"isDraft": False, "state": "CLOSED"}, tmp_path, monkeypatch)
        assert toasts == []

    def test_gh_failure_suppresses_toast(self, tmp_path, monkeypatch):
        """When gh CLI fails we suppress the toast rather than guessing."""
        toasts = self._run_main_with_stdin({}, None, tmp_path, monkeypatch)
        assert toasts == []

    def test_no_pr_url_in_state_does_not_fire(self, tmp_path, monkeypatch):
        """Without pr.url in workflow state, we never even reach gh."""
        wt = tmp_path / "wt"
        wt.mkdir()
        (wt / ".git").write_text("gitdir: ../bare/.git")
        wf = wt / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({}))

        captured_toasts = []
        monkeypatch.setattr(notify, "show_toast",
                             lambda *a: captured_toasts.append(a))
        import io
        monkeypatch.setattr(sys, "stdin",
                             io.StringIO(json.dumps({"cwd": str(wt)})))
        monkeypatch.setattr(sys, "argv", ["notify.py"])
        monkeypatch.delenv("PPDS_PIPELINE", raising=False)
        monkeypatch.delenv("PPDS_SHAKEDOWN", raising=False)

        # subprocess.run must NOT be called when there's no PR URL
        called = [False]
        def fake_run(*a, **kw):
            called[0] = True
            return subprocess.CompletedProcess(args=[], returncode=0,
                                                stdout="{}", stderr="")
        monkeypatch.setattr(notify.subprocess, "run", fake_run)

        notify.main()

        assert captured_toasts == []
        assert called[0] is False, (
            "Should not query GitHub when workflow state has no PR URL"
        )


class TestDirectInvocation:
    """--url arg bypasses state checks entirely (caller-trusted mode)."""

    def test_direct_url_fires_toast(self, monkeypatch):
        captured = []
        monkeypatch.setattr(notify, "show_toast",
                             lambda *a: captured.append(a))
        monkeypatch.setattr(sys, "argv",
                             ["notify.py", "--title", "T", "--msg", "M",
                              "--url", "https://example.com"])
        monkeypatch.delenv("PPDS_PIPELINE", raising=False)
        monkeypatch.delenv("PPDS_SHAKEDOWN", raising=False)
        notify.main()
        assert captured == [("T", "M", "https://example.com")]

    def test_pipeline_env_short_circuits(self, monkeypatch):
        captured = []
        monkeypatch.setattr(notify, "show_toast",
                             lambda *a: captured.append(a))
        monkeypatch.setattr(sys, "argv",
                             ["notify.py", "--url", "x"])
        monkeypatch.setenv("PPDS_PIPELINE", "1")
        with pytest.raises(SystemExit) as exc:
            notify.main()
        assert exc.value.code == 0
        assert captured == []
