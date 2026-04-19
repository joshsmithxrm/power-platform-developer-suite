"""Tests for scripts/hooks/pre-commit.d/claudemd-gate.sh.

This script enforces the CLAUDE.md line-cap (<=100 lines) against the
staged (index) version of the file. The commit-message marker check lives
in a separate commit-msg hook — see test_claudemd_marker.py.

Spawns a fresh git repo, stages a CLAUDE.md, runs the gate as a
subprocess.
"""
from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import pytest


GATE_SCRIPT = Path(__file__).parent.parent / "scripts" / "hooks" / "pre-commit.d" / "claudemd-gate.sh"


def _find_bash():
    """Locate Git Bash explicitly. PATH on Windows can pick up WSL bash, which
    cannot read Windows paths and breaks under capture_output (catastrophic
    subprocess failure). Prefer Git Bash if present.
    """
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


def _git(repo: Path, *args: str, env_extra: dict | None = None) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    if env_extra:
        env.update(env_extra)
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
    # Seed an initial commit so HEAD exists.
    (repo / "README.md").write_text("seed\n", encoding="utf-8")
    _git(repo, "add", "README.md")
    _git(repo, "commit", "-q", "-m", "seed")
    return repo


def _run_gate(repo: Path) -> subprocess.CompletedProcess:
    """Run the line-cap gate. The pre-commit gate no longer touches the
    commit message — that is the commit-msg hook's job."""
    return subprocess.run(
        [BASH, str(GATE_SCRIPT)],
        cwd=repo,
        capture_output=True,
        text=True,
        timeout=10,
        stdin=subprocess.DEVNULL,
    )


@pytest.mark.skipif(BASH is None, reason="Git Bash not available")
class TestClaudeMdLineCapGate:
    def test_no_claude_md_change_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "src.cs").write_text("class A {}\n", encoding="utf-8")
        _git(repo, "add", "src.cs")
        result = _run_gate(repo)
        assert result.returncode == 0, result.stderr

    def test_claude_md_under_cap_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("# Project\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_gate(repo)
        assert result.returncode == 0, result.stderr

    def test_claude_md_over_cap_blocked(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        big = "\n".join([f"line {i}" for i in range(120)]) + "\n"
        (repo / "CLAUDE.md").write_text(big, encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_gate(repo)
        assert result.returncode == 1
        assert "120" in result.stderr or "lines" in result.stderr

    def test_nested_claude_md_under_cap_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        nested = repo / "src" / "PPDS.Foo"
        nested.mkdir(parents=True)
        (nested / "CLAUDE.md").write_text("# Sub\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "src/PPDS.Foo/CLAUDE.md")
        result = _run_gate(repo)
        assert result.returncode == 0, result.stderr

    def test_line_cap_uses_staged_content_not_working_dir(self, tmp_path: Path):
        """If the working directory has fewer lines than the staged version,
        the gate must STILL block on the staged version. This proves we read
        the index, not the filesystem.
        """
        repo = _init_repo(tmp_path)
        # Stage a 120-line CLAUDE.md.
        big = "\n".join([f"line {i}" for i in range(120)]) + "\n"
        (repo / "CLAUDE.md").write_text(big, encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        # Then revert the working dir to a tiny version (NOT staged).
        (repo / "CLAUDE.md").write_text("tiny\n", encoding="utf-8")
        result = _run_gate(repo)
        # Must block — gate should see the 120-line staged content.
        assert result.returncode == 1, (
            f"Gate read working dir instead of index. "
            f"stdout={result.stdout!r} stderr={result.stderr!r}"
        )
        assert "120" in result.stderr

    def test_line_cap_ignores_unstaged_oversize(self, tmp_path: Path):
        """Inverse: if the working dir is huge but the staged version is
        small, the gate should pass — only the staged content matters.
        """
        repo = _init_repo(tmp_path)
        # Stage a small CLAUDE.md.
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        # Now make the working dir huge (NOT staged).
        big = "\n".join([f"line {i}" for i in range(200)]) + "\n"
        (repo / "CLAUDE.md").write_text(big, encoding="utf-8")
        result = _run_gate(repo)
        assert result.returncode == 0, (
            f"Gate read working dir instead of index. "
            f"stdout={result.stdout!r} stderr={result.stderr!r}"
        )

    def test_line_cap_at_exactly_100_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        content = "\n".join([f"line{i}" for i in range(100)]) + "\n"
        (repo / "CLAUDE.md").write_text(content, encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_gate(repo)
        assert result.returncode == 0, result.stderr

    def test_gate_does_not_check_commit_message(self, tmp_path: Path):
        """The line-cap gate must not read the commit message at all — the
        marker check moved to commit-msg.d/claudemd-marker.sh.
        """
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        # Write an empty commit message; gate should still pass on size alone.
        gitdir_proc = subprocess.run(
            ["git", "rev-parse", "--git-dir"],
            cwd=repo,
            capture_output=True,
            text=True,
            stdin=subprocess.DEVNULL,
        )
        gitdir = (repo / gitdir_proc.stdout.strip()).resolve()
        (gitdir / "COMMIT_EDITMSG").write_text("no marker here\n", encoding="utf-8")
        result = _run_gate(repo)
        assert result.returncode == 0, result.stderr
