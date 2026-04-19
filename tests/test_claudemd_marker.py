"""Tests for scripts/hooks/commit-msg.d/claudemd-marker.sh.

The marker check enforces that any commit touching a CLAUDE.md includes a
[claude-md-reviewed: YYYY-MM-DD] marker in its message. It runs in the
commit-msg phase (NOT pre-commit) because pre-commit fires before the
commit message is finalized.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import textwrap
from pathlib import Path

import pytest


MARKER_SCRIPT = (
    Path(__file__).parent.parent
    / "scripts" / "hooks" / "commit-msg.d" / "claudemd-marker.sh"
)
COMMIT_MSG_RUNNER = Path(__file__).parent.parent / "scripts" / "hooks" / "commit-msg"


def _find_bash():
    candidates = [
        r"C:\Program Files\Git\usr\bin\bash.exe",
        r"C:\Program Files\Git\bin\bash.exe",
        r"C:\Program Files (x86)\Git\usr\bin\bash.exe",
        "/usr/bin/bash",
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    return shutil.which("bash")


BASH = _find_bash()


def _git(repo: Path, *args: str) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    return subprocess.run(
        ["git", *args],
        cwd=repo,
        capture_output=True,
        text=True,
        env=env,
        stdin=subprocess.DEVNULL,
    )


def _init_repo(tmp_path: Path) -> Path:
    repo = tmp_path / "repo"
    repo.mkdir()
    _git(repo, "init", "-q", "-b", "main")
    _git(repo, "config", "user.email", "test@example.com")
    _git(repo, "config", "user.name", "Test")
    (repo / "README.md").write_text("seed\n", encoding="utf-8")
    _git(repo, "add", "README.md")
    _git(repo, "commit", "-q", "-m", "seed")
    return repo


def _run_marker(repo: Path, msg: str) -> subprocess.CompletedProcess:
    """Run the marker check directly (mirrors what the commit-msg runner does)."""
    msg_file = repo / ".commit-msg-test"
    msg_file.write_text(msg, encoding="utf-8")
    return subprocess.run(
        [BASH, str(MARKER_SCRIPT), str(msg_file)],
        cwd=repo,
        capture_output=True,
        text=True,
        timeout=10,
        stdin=subprocess.DEVNULL,
    )


def _run_runner(repo: Path, msg: str) -> subprocess.CompletedProcess:
    """Run the commit-msg runner (proves the marker script is wired up)."""
    msg_file = repo / ".commit-msg-test"
    msg_file.write_text(msg, encoding="utf-8")
    return subprocess.run(
        [BASH, str(COMMIT_MSG_RUNNER), str(msg_file)],
        cwd=repo,
        capture_output=True,
        text=True,
        timeout=10,
        stdin=subprocess.DEVNULL,
    )


@pytest.mark.skipif(BASH is None, reason="Git Bash not available")
class TestClaudeMdMarker:
    def test_no_claude_md_change_passes_without_marker(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "src.cs").write_text("class A {}\n", encoding="utf-8")
        _git(repo, "add", "src.cs")
        result = _run_marker(repo, "feat: add src\n")
        assert result.returncode == 0, result.stderr

    def test_claude_md_change_without_marker_blocked(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("# Project\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_marker(repo, "feat: update CLAUDE.md\n")
        assert result.returncode == 1
        assert "claude-md-reviewed" in result.stderr

    def test_claude_md_change_with_marker_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("# Project\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_marker(
            repo,
            "feat: update CLAUDE.md\n\n[claude-md-reviewed: 2026-04-18]\n",
        )
        assert result.returncode == 0, result.stderr

    def test_marker_in_inline_body_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        msg = textwrap.dedent("""\
            chore: tiny tweak

            Body with stuff.
            [claude-md-reviewed: 2026-04-18] inline marker also fine.

            Co-Authored-By: foo <foo@bar>
            """)
        result = _run_marker(repo, msg)
        assert result.returncode == 0, result.stderr

    def test_marker_wrong_date_format_blocked(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_marker(
            repo,
            "feat: x\n\n[claude-md-reviewed: 04/18/2026]\n",
        )
        assert result.returncode == 1

    def test_marker_in_comment_lines_ignored(self, tmp_path: Path):
        """Git comment lines (starting with #) are stripped before the
        commit; a marker buried in a comment must not satisfy the check.
        """
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        msg = (
            "feat: x\n\n"
            "# [claude-md-reviewed: 2026-04-18] (this is a comment)\n"
        )
        result = _run_marker(repo, msg)
        assert result.returncode == 1

    def test_nested_claude_md_requires_marker(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        nested = repo / "src" / "PPDS.Foo"
        nested.mkdir(parents=True)
        (nested / "CLAUDE.md").write_text("# Sub\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "src/PPDS.Foo/CLAUDE.md")
        result = _run_marker(repo, "feat: add subproject CLAUDE.md\n")
        assert result.returncode == 1
        assert "claude-md-reviewed" in result.stderr


@pytest.mark.skipif(BASH is None, reason="Git Bash not available")
class TestCommitMsgRunner:
    """Smoke test that the commit-msg runner discovers and dispatches to
    drop-in scripts under commit-msg.d/."""

    def test_runner_dispatches_to_marker_script(self, tmp_path: Path):
        """The runner must surface the marker script's failure to commit
        when CLAUDE.md is staged without a marker.
        """
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_runner(repo, "feat: change CLAUDE.md\n")
        assert result.returncode == 1
        # The runner reports the failing script.
        assert "claudemd-marker.sh" in result.stdout or "claudemd-marker.sh" in result.stderr

    def test_runner_passes_when_marker_present(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_runner(
            repo,
            "feat: change CLAUDE.md\n\n[claude-md-reviewed: 2026-04-18]\n",
        )
        assert result.returncode == 0, result.stderr

    def test_runner_passes_when_no_claude_md(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "src.cs").write_text("class A {}\n", encoding="utf-8")
        _git(repo, "add", "src.cs")
        result = _run_runner(repo, "feat: add src\n")
        assert result.returncode == 0, result.stderr
