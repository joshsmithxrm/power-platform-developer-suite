"""Shared test fixtures.

Auto-skips claude_dispatch version check AND auto-stubs claude_dispatch.spawn
for unit tests that don't explicitly override it. Tests that need to inspect
spawn() arguments still apply their own patch, which takes precedence.
"""
from __future__ import annotations

import sys
import unittest.mock
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parent.parent
_SCRIPTS = _REPO / "scripts"
if str(_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS))


class _AutoDispatchStub:
    """Minimal DispatchHandle stub used by the default spawn() mock.

    Returns "done" on first poll, exit code 0, empty output. Tests that want
    specific behavior wrap claude_dispatch.spawn in their own patch.
    """
    def __init__(self, transcript_path=None, **_):
        self.transcript_path = transcript_path or Path("/dev/null")
        self.terminate = unittest.mock.MagicMock()
        self.short = "stubshort"
        self.proc = unittest.mock.MagicMock(pid=99999)

    def poll(self):
        return "done"

    def wait(self, timeout=None):
        return 0

    def output(self):
        return ""

    def needs(self):
        return ""


@pytest.fixture(autouse=True)
def _dispatch_unit_test_defaults(request):
    """Skip version check + provide a no-op spawn() default for unit tests."""
    try:
        import claude_dispatch
    except ImportError:
        yield
        return

    # Tests in tests/scripts/test_claude_dispatch* explicitly test the real
    # dispatcher; they need the actual spawn function unmocked.
    test_file = str(request.node.path) if hasattr(request.node, "path") else ""
    skip_spawn_mock = (
        "test_claude_dispatch" in test_file
        or "test_bg_transcript" in test_file
        or "test_start_bg_spawn" in test_file
        or any(m.name == "real_dispatch_spawn" for m in request.node.iter_markers())
    )

    orig_version = claude_dispatch._VERSION_CHECKED
    orig_spawn = claude_dispatch.spawn
    claude_dispatch._VERSION_CHECKED = True
    if not skip_spawn_mock:
        def _stub_spawn(**kw):
            return _AutoDispatchStub(transcript_path=kw.get("stage_log"))
        claude_dispatch.spawn = _stub_spawn
    try:
        yield
    finally:
        claude_dispatch._VERSION_CHECKED = orig_version
        claude_dispatch.spawn = orig_spawn
