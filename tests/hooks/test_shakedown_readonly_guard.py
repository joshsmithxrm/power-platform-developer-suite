"""Tests for shakedown-readonly-guard Stop hook (Finding #27c).

Blocks session stop when ``phase == "shakedown"`` and any ``src/`` files
have changed. Distinct from shakedown-readonly.py (PreToolUse, ppds-CLI
Dataverse-write guard).

Run: ``pytest tests/hooks/test_shakedown_readonly_guard.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path


HOOK_PATH = (
    Path(__file__).resolve().parents[2]
    / ".claude" / "hooks" / "shakedown-readonly-guard.py"
)


def _load_hook():
    hooks_dir = str(HOOK_PATH.parent)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec = importlib.util.spec_from_file_location("shakedown_readonly_guard", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


def _git(tmp_path, *args, check=True):
    return subprocess.run(
        ["git", "-C", str(tmp_path), *args],
        check=check, capture_output=True, text=True, stdin=subprocess.DEVNULL,
    )


def _init_project(tmp_path, phase=None, src_files=None):
    """Init a git repo, optionally write state + src/ files. Returns path."""
    subprocess.run(
        ["git", "init", "-q", "-b", "main", str(tmp_path)],
        check=True, capture_output=True, stdin=subprocess.DEVNULL,
    )
    (tmp_path / "README.md").write_text("init\n", encoding="utf-8")
    _git(tmp_path, "add", "README.md")
    _git(tmp_path, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init")
    _git(tmp_path, "branch", "origin/main")

    if phase is not None:
        (tmp_path / ".workflow").mkdir()
        (tmp_path / ".workflow" / "state.json").write_text(
            json.dumps({"phase": phase}), encoding="utf-8",
        )

    for rel in (src_files or []):
        target = tmp_path / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("// changed\n", encoding="utf-8")

    return tmp_path


def _run_hook(project_dir, stop_hook_active=False, stdin_raw=None):
    body = {}
    if stop_hook_active:
        body["stop_hook_active"] = True
    env = os.environ.copy()
    env["CLAUDE_PROJECT_DIR"] = str(project_dir)
    return subprocess.run(
        [sys.executable, str(HOOK_PATH)],
        input=stdin_raw if stdin_raw is not None else json.dumps(body),
        capture_output=True, text=True, timeout=10, env=env, cwd=str(project_dir),
    )


class TestNoOpWhenNotShakedown:
    """Hook only enforces when state.phase == 'shakedown'."""

    def test_no_state_allows(self, tmp_path):
        _init_project(tmp_path)
        r = _run_hook(tmp_path)
        assert r.returncode == 0, r.stderr

    def test_other_phase_allows_even_with_src_changes(self, tmp_path):
        _init_project(tmp_path, phase="implementing", src_files=["src/foo.ts"])
        r = _run_hook(tmp_path)
        assert r.returncode == 0, r.stderr


class TestShakedownBlocksSrcChanges:
    """When phase == 'shakedown', any src/ change must block stop."""

    def test_src_change_blocks(self, tmp_path):
        _init_project(tmp_path, phase="shakedown", src_files=["src/PPDS.Cli/Program.cs"])
        r = _run_hook(tmp_path)
        assert r.returncode == 2, r.stderr
        payload = json.loads(r.stdout)
        assert payload["decision"] == "block"
        assert "src/PPDS.Cli/Program.cs" in payload["reason"]
        assert "shakedown" in payload["reason"].lower()

    def test_multiple_src_changes_all_listed(self, tmp_path):
        _init_project(tmp_path, phase="shakedown", src_files=["src/a.cs", "src/b.ts"])
        r = _run_hook(tmp_path)
        assert r.returncode == 2
        reason = json.loads(r.stdout)["reason"]
        assert "src/a.cs" in reason and "src/b.ts" in reason

    def test_non_src_changes_allowed(self, tmp_path):
        # Doc/spec edits are fine during shakedown — only src/ is guarded.
        p = _init_project(tmp_path, phase="shakedown")
        (p / "docs").mkdir(); (p / "docs" / "n.md").write_text("x\n", encoding="utf-8")
        r = _run_hook(p)
        assert r.returncode == 0, r.stderr

    def test_no_changes_allowed(self, tmp_path):
        _init_project(tmp_path, phase="shakedown")
        r = _run_hook(tmp_path)
        assert r.returncode == 0, r.stderr


class TestSafety:
    def test_stop_hook_active_short_circuits(self, tmp_path):
        # Re-entry guard: even a block-worthy scenario must pass through.
        _init_project(tmp_path, phase="shakedown", src_files=["src/foo.cs"])
        r = _run_hook(tmp_path, stop_hook_active=True)
        assert r.returncode == 0, r.stderr

    def test_corrupt_state_allows(self, tmp_path):
        _init_project(tmp_path)
        (tmp_path / ".workflow").mkdir(exist_ok=True)
        (tmp_path / ".workflow" / "state.json").write_text("{bad", encoding="utf-8")
        r = _run_hook(tmp_path)
        assert r.returncode == 0, r.stderr

    def test_garbled_stdin_allows(self, tmp_path):
        _init_project(tmp_path, phase="shakedown")
        r = _run_hook(tmp_path, stdin_raw="not{json")
        assert r.returncode == 0, r.stderr
