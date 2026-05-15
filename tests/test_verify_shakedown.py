#!/usr/bin/env python3
"""Tests for verify_shakedown gate (Sub-task C).

These tests cover the *decision* layer — whether the gate decides to run
the empirical spawn — without actually spawning a real claude process.
The spawn itself is exercised by the conftest auto-stub.
"""
import os
import subprocess
import sys
import tempfile
import unittest.mock as mock

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

import verify_shakedown  # noqa: E402


def _init_repo(tmpdir):
    env = {
        **os.environ,
        "GIT_AUTHOR_NAME": "Test",
        "GIT_AUTHOR_EMAIL": "test@example.com",
        "GIT_COMMITTER_NAME": "Test",
        "GIT_COMMITTER_EMAIL": "test@example.com",
    }
    subprocess.run(["git", "init", "-q", "-b", "main"], cwd=tmpdir, check=True, env=env)
    subprocess.run(
        ["git", "commit", "-q", "--allow-empty", "-m", "init"],
        cwd=tmpdir, check=True, env=env,
    )
    return env


def _touch_and_commit(tmpdir, env, files, subject):
    for rel, content in files.items():
        full = os.path.join(tmpdir, rel)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8") as f:
            f.write(content)
        subprocess.run(["git", "add", rel], cwd=tmpdir, check=True, env=env)
    subprocess.run(["git", "commit", "-q", "-m", subject], cwd=tmpdir, check=True, env=env)


def test_skips_when_no_allowlist_file_changed(monkeypatch, capsys):
    """No spawn when the diff misses every allowlist entry."""
    monkeypatch.setattr(verify_shakedown, "_changed_files", lambda base: ["docs/readme.md", "src/foo.cs"])
    monkeypatch.setattr(verify_shakedown, "_detect_base", lambda: "origin/main")
    spawn = mock.MagicMock()
    monkeypatch.setattr(verify_shakedown, "run_shakedown", spawn)

    rc = verify_shakedown.main([])
    assert rc == 0
    spawn.assert_not_called()


def test_runs_when_allowlist_file_changed(monkeypatch):
    """Touching any allowlist entry triggers the spawn."""
    monkeypatch.setattr(
        verify_shakedown, "_changed_files",
        lambda base: ["scripts/claude_dispatch.py", "tests/conftest.py"],
    )
    monkeypatch.setattr(verify_shakedown, "_detect_base", lambda: "origin/main")
    spawn = mock.MagicMock(return_value=0)
    monkeypatch.setattr(verify_shakedown, "run_shakedown", spawn)

    rc = verify_shakedown.main([])
    assert rc == 0
    spawn.assert_called_once()


def test_propagates_spawn_failure(monkeypatch):
    """Non-zero spawn exit becomes gate failure (rc=1)."""
    monkeypatch.setattr(verify_shakedown, "_changed_files", lambda base: ["scripts/pipeline.py"])
    monkeypatch.setattr(verify_shakedown, "_detect_base", lambda: "origin/main")
    monkeypatch.setattr(verify_shakedown, "run_shakedown", lambda timeout: 1)

    rc = verify_shakedown.main([])
    assert rc == 1


def test_setup_error_surfaces_as_rc2(monkeypatch):
    """Dispatcher import / spawn raising surfaces as a distinct exit code."""
    monkeypatch.setattr(verify_shakedown, "_changed_files", lambda base: ["scripts/pipeline.py"])
    monkeypatch.setattr(verify_shakedown, "_detect_base", lambda: "origin/main")

    def boom(timeout):
        raise RuntimeError("dispatcher unavailable")
    monkeypatch.setattr(verify_shakedown, "run_shakedown", boom)

    rc = verify_shakedown.main([])
    assert rc == 2


def test_changed_files_diff_path(monkeypatch):
    """End-to-end: a real git repo where /verify diff includes an allowlist file."""
    with tempfile.TemporaryDirectory() as tmpdir:
        env = _init_repo(tmpdir)
        _touch_and_commit(
            tmpdir, env,
            {"scripts/claude_dispatch.py": "import subprocess\n"},
            "feat: bring dispatcher in",
        )
        # Diff vs the empty initial commit
        base = subprocess.run(
            ["git", "rev-list", "--max-parents=0", "HEAD"],
            cwd=tmpdir, capture_output=True, text=True, check=True,
        ).stdout.strip()
        # _changed_files uses `git diff <base>...HEAD`
        files = []
        out = subprocess.run(
            ["git", "diff", "--name-only", f"{base}...HEAD"],
            cwd=tmpdir, capture_output=True, text=True, check=True,
        )
        files = [line.strip() for line in out.stdout.splitlines() if line.strip()]
        assert "scripts/claude_dispatch.py" in files
        # Verify the allowlist matcher agrees
        from _shakedown_allowlist import is_allowlisted
        assert is_allowlisted("scripts/claude_dispatch.py")
