"""Tests for .claude/hooks/sdk-spend-warn.py. See specs/dispatch-routing.md ACs 19-20.

The hook is invoked the way Claude Code invokes it in production: as a
subprocess that receives a JSON envelope on stdin. Each test uses
`tmp_path` as the subprocess `cwd` so the hook's relative spend-log path
(.claude/state/sdk-spend.jsonl) lands inside the test sandbox.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
_HOOK = _REPO / ".claude" / "hooks" / "sdk-spend-warn.py"

# AC-03 stderr format (em-dash U+2014, NOT hyphen-minus).
_WARN_RE = re.compile(
    r"^WARN SDK pool: claude -p invoked from .+ "
    r"\(model=.+, agent=.+\) — counts against monthly "
    r"Agent SDK credit, not subscription\.$"
)

_SESSION_ID = "abc12345-test-session"


def _payload(command: str) -> str:
    return json.dumps(
        {
            "tool_name": "Bash",
            "tool_input": {"command": command},
            "session_id": _SESSION_ID,
        }
    )


def _run_hook(payload: str, cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(_HOOK)],
        input=payload,
        text=True,
        encoding="utf-8",
        capture_output=True,
        cwd=str(cwd),
    )


def _spend_log(cwd: Path) -> Path:
    return cwd / ".claude" / "state" / "sdk-spend.jsonl"


# ---------------------------------------------------------------------------
# AC-19 — positive cases (hook fires)
# ---------------------------------------------------------------------------

_cases_positive = [
    "claude -p 'do thing'",
    "  claude -p hi",
    "/usr/local/bin/claude -p hi",
    "claude.exe -p hi",
    "FOO=bar claude -p hi",
]


@pytest.mark.parametrize("command", _cases_positive)
def test_sdk_spend_warn_hook_pattern(tmp_path: Path, command: str) -> None:
    """AC-19: hook fires for claude -p variants; emits warning + JSONL row."""
    result = _run_hook(_payload(command), tmp_path)
    assert result.returncode == 0, (
        f"hook must always exit 0 (informational); stderr={result.stderr!r}"
    )
    # stderr matches the AC-03 regex (em-dash literal).
    stderr_line = result.stderr.strip().splitlines()[-1] if result.stderr.strip() else ""
    assert _WARN_RE.match(stderr_line), (
        f"stderr did not match AC-03 regex: {stderr_line!r}"
    )
    # JSONL row appended.
    log = _spend_log(tmp_path)
    assert log.exists(), "spend log was not created"
    rows = [
        json.loads(line)
        for line in log.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    assert len(rows) >= 1, "no JSONL row appended"
    row = rows[-1]
    assert row["caller"].startswith("bash:"), (
        f"caller must start with 'bash:' — got {row['caller']!r}"
    )
    assert _SESSION_ID in row["caller"], "caller must include session_id"
    for key in ("ts", "caller", "model", "agent", "est_input_tokens"):
        assert key in row, f"JSONL row missing key {key!r}"


# ---------------------------------------------------------------------------
# AC-20 — negative cases (hook does NOT fire)
# ---------------------------------------------------------------------------

_cases_negative = [
    "claude --bg --name x -- hi",
    "claude",
    "grep 'claude -p' README.md",
    "echo x | claude -p hi",
]


@pytest.mark.parametrize("command", _cases_negative)
def test_sdk_spend_warn_hook_negatives(tmp_path: Path, command: str) -> None:
    """AC-20: hook does NOT fire on --bg, bare claude, grep mentions, or pipes."""
    result = _run_hook(_payload(command), tmp_path)
    assert result.returncode == 0, (
        f"hook must always exit 0 (informational); stderr={result.stderr!r}"
    )
    assert result.stderr == "", (
        f"hook should not emit stderr for negative cases; got: {result.stderr!r}"
    )
    log = _spend_log(tmp_path)
    if log.exists():
        rows = [
            line
            for line in log.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        assert rows == [], f"hook must not append rows for negative cases; got {rows!r}"
