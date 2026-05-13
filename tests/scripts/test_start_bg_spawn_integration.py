"""Integration test for scripts/start-bg-spawn.py — AC-08.

Requires `claude` on PATH (Claude Code >= 2.1.139). Skip otherwise.
"""
import json
import shutil
import subprocess
from pathlib import Path

import pytest


def _claude_available() -> bool:
    return shutil.which("claude") is not None


requires_claude_bg = pytest.mark.skipif(
    not _claude_available(), reason="requires `claude` on PATH (CC >=2.1.139)"
)


@requires_claude_bg
def test_prompt_verbatim_5k(tmp_path):
    """AC-08: 5K prompt with trap chars arrives byte-for-byte in state.json intent."""
    prompt = (
        "PROBE_HEAD\n"
        + "'@\n"
        + "Don't forget Claude's apostrophes.\n"
        + "$prompt should not interpolate.\n"
        + "Backticks `like this`.\n"
        + "x" * (5000 - 200)
        + "\nPROBE_TAIL"
    )
    pf = tmp_path / "prompt.txt"
    pf.write_text(prompt, encoding="utf-8")
    branch = "feat/integration-test-bg"

    # Run from repo root so inflight helpers can resolve; worktree-abs is tmp_path
    repo_root = Path(__file__).resolve().parents[2]
    result = subprocess.run(
        [
            "python",
            "scripts/start-bg-spawn.py",
            "--worktree-abs",
            str(tmp_path),
            "--branch",
            branch,
            "--prompt-file",
            str(pf),
        ],
        capture_output=True,
        text=True,
        cwd=str(repo_root),
        timeout=60,
        stdin=subprocess.DEVNULL,
    )
    assert result.returncode == 0, (
        f"start-bg-spawn.py failed (exit {result.returncode})\n"
        f"stdout: {result.stdout}\nstderr: {result.stderr}"
    )

    spawn_info = json.loads(result.stdout)
    state_path = (
        Path.home() / ".claude" / "jobs" / spawn_info["short"] / "state.json"
    )
    assert state_path.exists(), f"state.json not found at {state_path}"
    state = json.loads(state_path.read_text(encoding="utf-8"))

    assert state.get("intent") == prompt, (
        "AC-08: prompt was not delivered byte-for-byte. "
        f"Expected length {len(prompt)}, got length {len(state.get('intent', ''))}"
    )

    # Cleanup: stop the bg session we just spawned.
    subprocess.run(
        ["claude", "stop", spawn_info["short"]],
        check=False,
        capture_output=True,
    )
