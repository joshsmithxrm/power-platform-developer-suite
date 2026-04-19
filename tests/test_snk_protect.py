"""Tests for snk-protect hook — block writes/edits to .snk files."""
import importlib.util
import json
import os
import subprocess
import sys

import pytest


def _load_hook():
    """Import snk-protect.py as a module."""
    hook_path = os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "snk-protect.py",
    )
    hook_path = os.path.normpath(hook_path)
    spec = importlib.util.spec_from_file_location("snk_protect", hook_path)
    mod = importlib.util.module_from_spec(spec)
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
        "snk-protect.py",
    )
)


def _run_hook(payload: dict) -> tuple[int, str]:
    """Invoke the hook as a subprocess with the given JSON payload."""
    result = subprocess.run(
        [sys.executable, HOOK_PATH],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=5,
    )
    return result.returncode, result.stderr


class TestIsSnkPath:
    def test_windows_native_snk_blocked(self):
        assert hook.is_snk_path("C:\\src\\PPDS.Plugins\\PPDS.Plugins.snk")

    def test_msys_snk_blocked(self):
        assert hook.is_snk_path("/c/src/PPDS.Plugins/PPDS.Plugins.snk")

    def test_posix_snk_blocked(self):
        assert hook.is_snk_path("/home/user/foo.snk")

    def test_uppercase_extension_blocked(self):
        assert hook.is_snk_path("/home/user/FOO.SNK")

    def test_mixed_case_extension_blocked(self):
        assert hook.is_snk_path("C:\\bar.SnK")

    def test_publickey_allowed(self):
        # PPDS.Plugins.PublicKey is the *public* portion — safe to commit.
        assert not hook.is_snk_path("src/PPDS.Plugins/PPDS.Plugins.PublicKey")

    def test_cs_file_allowed(self):
        assert not hook.is_snk_path("src/PPDS.Plugins/Plugin.cs")

    def test_empty_path_allowed(self):
        assert not hook.is_snk_path("")

    def test_snk_substring_in_middle_allowed(self):
        # Only blocks files that *end* in .snk — not paths that mention snk.
        assert not hook.is_snk_path("src/snk-protect.py")
        assert not hook.is_snk_path("docs/snk-rotation.md")


class TestHookSubprocess:
    def test_blocks_snk_write(self):
        code, stderr = _run_hook({"tool_input": {"file_path": "C:/src/Foo.snk"}})
        assert code == 2
        assert "BLOCKED" in stderr
        assert ".snk" in stderr

    def test_allows_other_writes(self):
        code, _ = _run_hook({"tool_input": {"file_path": "C:/src/Foo.cs"}})
        assert code == 0

    def test_handles_legacy_top_level_file_path(self):
        # Older Claude Code envelope had file_path at top level.
        code, _ = _run_hook({"file_path": "/c/src/Foo.snk"})
        assert code == 2

    def test_handles_empty_stdin(self):
        # Empty/garbled stdin must not block — fail-open on malformed input.
        result = subprocess.run(
            [sys.executable, HOOK_PATH],
            input="",
            capture_output=True,
            text=True,
            timeout=5,
        )
        assert result.returncode == 0

    def test_handles_garbled_json(self):
        result = subprocess.run(
            [sys.executable, HOOK_PATH],
            input="{not json",
            capture_output=True,
            text=True,
            timeout=5,
        )
        assert result.returncode == 0

    def test_missing_file_path_allowed(self):
        # No file_path at all — allow (other matchers may apply).
        code, _ = _run_hook({"tool_input": {}})
        assert code == 0
