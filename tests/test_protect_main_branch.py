"""Tests for protect-main-branch hook — MSYS path normalization."""
import importlib.util
import os
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
