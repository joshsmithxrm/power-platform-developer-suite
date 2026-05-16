"""Tests for session-start-workflow hook — working-tree state injection (issue #1074).

Bug 1 fix: hook injects fresh git status rather than relying on cached state.
Covers: clean worktree reported, dirty files listed, state reflects changes between sessions.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

HOOK_PATH = (
    Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "session-start-workflow.py"
)

_GIT_ENV = {
    "GIT_AUTHOR_NAME": "Test",
    "GIT_AUTHOR_EMAIL": "test@example.com",
    "GIT_COMMITTER_NAME": "Test",
    "GIT_COMMITTER_EMAIL": "test@example.com",
}


def _init_repo(tmpdir: Path) -> None:
    """Initialise a git repo on fix/test-branch with an empty initial commit.

    Includes a .gitignore that mirrors the real repo (.workflow/* ignored) so
    state.json doesn't show up as an untracked dirty file in git status.
    """
    env = {**os.environ, **_GIT_ENV}
    subprocess.run(["git", "init", "-q", "-b", "fix/test-branch"], cwd=str(tmpdir), check=True, env=env)
    # Mirror real repo: .workflow/* is gitignored so state.json stays invisible.
    (tmpdir / ".gitignore").write_text(".workflow/*\n", encoding="utf-8")
    subprocess.run(["git", "add", ".gitignore"], cwd=str(tmpdir), check=True, env=env)
    subprocess.run(["git", "commit", "--allow-empty", "-q", "-m", "init"], cwd=str(tmpdir), check=True, env=env)


def _write_state(tmpdir: Path) -> None:
    """Write a minimal .workflow/state.json so the hook reaches the full output path."""
    state_dir = tmpdir / ".workflow"
    state_dir.mkdir(exist_ok=True)
    (state_dir / "state.json").write_text(
        json.dumps({"branch": "fix/test-branch"}), encoding="utf-8"
    )


def _run_hook(project_dir: str) -> subprocess.CompletedProcess:
    env = {k: v for k, v in os.environ.items()}
    env["CLAUDE_PROJECT_DIR"] = project_dir
    env.pop("PPDS_PIPELINE", None)
    return subprocess.run(
        [sys.executable, str(HOOK_PATH)],
        input=json.dumps({}),
        capture_output=True,
        text=True,
        timeout=15,
        env=env,
    )


class TestWorkingTreeStateInjection:
    def test_clean_worktree_reported(self, tmp_path):
        """Hook reports 'Working tree clean' when no uncommitted files."""
        _init_repo(tmp_path)
        _write_state(tmp_path)
        result = _run_hook(str(tmp_path))
        assert result.returncode == 0
        assert "Working tree clean" in result.stderr

    def test_dirty_files_listed_in_output(self, tmp_path):
        """Hook lists uncommitted files when working tree is dirty."""
        _init_repo(tmp_path)
        _write_state(tmp_path)
        retros_dir = tmp_path / ".retros"
        retros_dir.mkdir()
        (retros_dir / "summary.json").write_text("{}", encoding="utf-8")
        result = _run_hook(str(tmp_path))
        assert result.returncode == 0
        assert "uncommitted" in result.stderr.lower() or ".retros" in result.stderr

    def test_reflects_fresh_state_after_commit(self, tmp_path):
        """Hook reflects state changes (commit) that happened between sessions.

        Scenario: retro output lands as a dirty file, then /retro commits it.
        Next session must see the working tree as clean.
        """
        _init_repo(tmp_path)
        _write_state(tmp_path)
        retros_dir = tmp_path / ".retros"
        retros_dir.mkdir()
        retro_file = retros_dir / "summary.json"
        retro_file.write_text("{}", encoding="utf-8")

        # Before commit: hook must report a dirty working tree
        result_before = _run_hook(str(tmp_path))
        assert result_before.returncode == 0
        assert "Working tree clean" not in result_before.stderr, (
            "Hook should NOT report clean tree when summary.json is untracked"
        )

        # Simulate what /retro Phase 9 now does: commit the retro output
        env = {**os.environ, **_GIT_ENV}
        subprocess.run(
            ["git", "add", ".retros/summary.json"],
            cwd=str(tmp_path), check=True, env=env,
        )
        subprocess.run(
            ["git", "commit", "-m", "retro: test-branch session summary"],
            cwd=str(tmp_path), check=True, env=env,
        )

        # After commit: hook must now report a clean working tree
        result_after = _run_hook(str(tmp_path))
        assert result_after.returncode == 0
        assert "Working tree clean" in result_after.stderr, (
            "Hook must report clean tree after retro output is committed"
        )
