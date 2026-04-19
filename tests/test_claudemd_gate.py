"""Tests for scripts/hooks/pre-commit.d/claudemd-gate.sh.

Spawns a fresh git repo, stages a CLAUDE.md, runs the gate as a
subprocess. Verifies marker enforcement and line-cap enforcement.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import textwrap
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


def _run_gate(repo: Path, msg: str) -> subprocess.CompletedProcess:
    """Stage the prepared commit message file and run the gate."""
    # Find the gitdir (handles worktrees too — but here it's a regular repo).
    gitdir_proc = subprocess.run(
        ["git", "rev-parse", "--git-dir"],
        cwd=repo,
        capture_output=True,
        text=True,
    )
    gitdir = (repo / gitdir_proc.stdout.strip()).resolve()
    (gitdir / "COMMIT_EDITMSG").write_text(msg, encoding="utf-8")
    return subprocess.run(
        [BASH, str(GATE_SCRIPT)],
        cwd=repo,
        capture_output=True,
        text=True,
        timeout=10,
    )


@pytest.mark.skipif(BASH is None, reason="Git Bash not available")
class TestClaudeMdGate:
    def test_no_claude_md_change_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "src.cs").write_text("class A {}\n", encoding="utf-8")
        _git(repo, "add", "src.cs")
        result = _run_gate(repo, "feat: add src\n")
        assert result.returncode == 0, result.stderr

    def test_claude_md_change_without_marker_blocked(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("# Project\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_gate(repo, "feat: update CLAUDE.md\n")
        assert result.returncode == 1
        assert "claude-md-reviewed" in result.stderr

    def test_claude_md_change_with_marker_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("# Project\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_gate(
            repo,
            "feat: update CLAUDE.md\n\n[claude-md-reviewed: 2026-04-18]\n",
        )
        assert result.returncode == 0, result.stderr

    def test_claude_md_over_cap_blocked(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        big = "\n".join([f"line {i}" for i in range(120)]) + "\n"
        (repo / "CLAUDE.md").write_text(big, encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        result = _run_gate(
            repo,
            "feat: huge CLAUDE.md\n\n[claude-md-reviewed: 2026-04-18]\n",
        )
        assert result.returncode == 1
        assert "120" in result.stderr or "lines" in result.stderr

    def test_nested_claude_md_detected(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        nested = repo / "src" / "PPDS.Foo"
        nested.mkdir(parents=True)
        (nested / "CLAUDE.md").write_text("# Sub\n\nshort.\n", encoding="utf-8")
        _git(repo, "add", "src/PPDS.Foo/CLAUDE.md")
        result = _run_gate(repo, "feat: add subproject CLAUDE.md\n")
        # No marker -> blocked.
        assert result.returncode == 1
        assert "claude-md-reviewed" in result.stderr

    def test_marker_with_garbage_around_passes(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        msg = textwrap.dedent("""\
            chore: tiny tweak

            Body with stuff.
            [claude-md-reviewed: 2026-04-18] inline marker also fine.

            Co-Authored-By: foo <foo@bar>
            """)
        result = _run_gate(repo, msg)
        assert result.returncode == 0, result.stderr

    def test_marker_wrong_format_blocked(self, tmp_path: Path):
        repo = _init_repo(tmp_path)
        (repo / "CLAUDE.md").write_text("short\n", encoding="utf-8")
        _git(repo, "add", "CLAUDE.md")
        # Wrong date format — not ISO YYYY-MM-DD.
        result = _run_gate(
            repo,
            "feat: x\n\n[claude-md-reviewed: 04/18/2026]\n",
        )
        assert result.returncode == 1
