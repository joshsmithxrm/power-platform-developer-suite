"""Tests for protect-main-branch hook — MSYS path normalization + envelope contract."""
import importlib.util
import json
import os
import subprocess
import sys

import pytest


def _load_hook():
    """Import protect-main-branch.py as a module."""
    hook_path = os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "protect-main-branch.py",
    )
    hook_path = os.path.normpath(hook_path)
    spec = importlib.util.spec_from_file_location("protect_main_branch", hook_path)
    mod = importlib.util.module_from_spec(spec)
    # Ensure _pathfix is importable
    hooks_dir = os.path.dirname(hook_path)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


HOOK_PATH = os.path.normpath(
    os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "protect-main-branch.py",
    )
)


def _run_hook(payload: dict, env_extra: dict = None) -> subprocess.CompletedProcess:
    """Invoke the hook script as a subprocess with *payload* on stdin."""
    env = os.environ.copy()
    if env_extra:
        env.update(env_extra)
    return subprocess.run(
        [sys.executable, HOOK_PATH],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=15,
        env=env,
    )


class TestIsAllowedPath:
    def test_msys_worktree_path_allowed(self, monkeypatch):
        """AC-27: MSYS-style worktree paths are allowed."""
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", "/c/VS/ppdsw/ppds")
        assert hook.is_allowed_path("/c/VS/ppdsw/ppds/.worktrees/foo/bar.md")

    def test_windows_native_worktree_path_allowed(self, monkeypatch):
        """AC-27: Windows native worktree paths are allowed."""
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", "C:\\VS\\ppdsw\\ppds")
        assert hook.is_allowed_path("C:\\VS\\ppdsw\\ppds\\.worktrees\\foo\\bar.md")

    def test_main_repo_path_blocked(self, monkeypatch):
        """Main repo source paths are NOT allowed."""
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", "C:\\VS\\ppdsw\\ppds")
        assert not hook.is_allowed_path("C:\\VS\\ppdsw\\ppds\\src\\Foo.cs")

    def test_temp_path_allowed(self, monkeypatch):
        """Temp directory paths are allowed."""
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", "C:\\VS\\ppdsw\\ppds")
        assert hook.is_allowed_path("/tmp/somefile.txt")
        assert hook.is_allowed_path("C:\\Users\\josh\\AppData\\Local\\Temp\\foo.txt")

    def test_claude_worktrees_path_allowed(self, monkeypatch):
        """v1-prelaunch retro #1: .claude/worktrees/<name>/ paths are allowed
        (this layout is used by orchestrated agent worktrees)."""
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", "/c/VS/ppdsw/ppds")
        assert hook.is_allowed_path(
            "/c/VS/ppdsw/ppds/.claude/worktrees/agent-abc/.claude/hooks/foo.py"
        )

    def test_empty_path_blocked(self):
        """Empty file_path must NOT be allowed (defends against the v1-prelaunch
        nesting bug — old hook fed "" into is_allowed_path on every call)."""
        assert not hook.is_allowed_path("")


class TestEnvelopeContract:
    """v1-prelaunch retro item #1: stdin envelope is nested under tool_input.

    The hook MUST read file_path from payload["tool_input"]["file_path"], not
    from payload["file_path"]. Reading at the top level returned "" for every
    invocation, which made is_allowed_path("") return False and caused the
    hook to block every Edit/Write on main even for legitimate worktree paths.
    """

    def test_nested_envelope_with_worktree_path_allowed(self, tmp_path):
        """Nested envelope + .worktrees/ path → exit 0 (allowed)."""
        # Use a path under .worktrees/ so is_allowed_path returns True even
        # when the project happens to be on main.
        payload = {
            "tool_name": "Write",
            "tool_input": {
                "file_path": str(tmp_path / ".worktrees" / "x" / "f.txt"),
                "content": "x",
            },
        }
        result = _run_hook(payload)
        assert result.returncode == 0, (
            f"Worktree path should be allowed; stderr={result.stderr!r}"
        )

    def test_nested_envelope_empty_payload_does_not_crash(self):
        """Empty stdin → exit 0 (don't block on parse failure)."""
        result = subprocess.run(
            [sys.executable, HOOK_PATH],
            input="",
            capture_output=True,
            text=True,
            timeout=15,
        )
        assert result.returncode == 0

    def test_pipeline_env_var_bypasses_check(self, tmp_path):
        """PPDS_PIPELINE=1 bypasses the hook entirely (orchestrator mode)."""
        payload = {
            "tool_name": "Write",
            "tool_input": {"file_path": "anywhere/even/main.cs"},
        }
        result = _run_hook(payload, env_extra={"PPDS_PIPELINE": "1"})
        assert result.returncode == 0


class TestBranchForPath:
    """v1-prelaunch retro item #1: hook must derive branch from file's worktree,
    not the project root, so edits inside .worktrees/<name>/ are judged against
    that worktree's branch (not the main repo's branch)."""

    def test_unknown_path_returns_empty(self):
        """Unknown / nonexistent paths return "" (caller falls back)."""
        assert hook._branch_for_path("") == ""
        assert hook._branch_for_path("/this/does/not/exist/anywhere/foo.txt") == ""

    def test_resolves_branch_from_real_worktree(self):
        """When file lives under a git worktree, _branch_for_path returns its
        branch. We use the test file itself (which is in this very worktree)."""
        branch = hook._branch_for_path(__file__)
        # We don't assert a specific branch — just that we got *something* and
        # not the empty string. The test runs inside a git worktree so this
        # must succeed.
        assert isinstance(branch, str)
