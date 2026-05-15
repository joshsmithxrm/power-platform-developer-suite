"""Empirical shakedown gate for /verify.

When the change set touches any file in ``SHAKEDOWN_ALLOWLIST`` (the
subprocess-spawning wrappers — see ``scripts/_shakedown_allowlist.py``),
spawn one real ``claude --bg`` session against a throwaway prompt and
assert exit 0. This is the gate that PR #1051 Phase B exhibited: the
auto-stubbed ``claude_dispatch.spawn`` in ``tests/conftest.py`` masked
seven real failures, so the unit-test suite passed while the real
boundary was broken. The shakedown deliberately exercises the real
boundary — no patches, no stubs, real subprocess.

Subscription pool only — uses ``claude --bg``, never ``-p``. Bounded by
``--timeout`` (default 5 min) so the /verify runtime cost is capped.

CLI:
    python scripts/verify_shakedown.py [--base <ref>] [--timeout <sec>]

Exit codes:
    0  shakedown not required, or required and passed.
    1  shakedown required and the spawned --bg session did not reach
       state=done within the timeout.
    2  setup error (cannot reach git, dispatcher import failed, etc.).
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

_SCRIPTS_DIR = os.path.dirname(os.path.abspath(__file__))
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from _shakedown_allowlist import SHAKEDOWN_ALLOWLIST, is_allowlisted

DEFAULT_TIMEOUT_SEC = 300
THROWAWAY_PROMPT = "Reply with the word OK and stop."


def _changed_files(base: str | None) -> list[str]:
    """Return repo-relative changed paths vs *base*.

    Uses ``git diff --name-only <base>...HEAD`` when *base* is provided,
    otherwise falls back to ``git status --porcelain`` (uncommitted work).
    """
    if base:
        out = subprocess.run(
            ["git", "diff", "--name-only", f"{base}...HEAD"],
            capture_output=True, text=True, timeout=30,
        )
        if out.returncode == 0:
            return [line.strip() for line in out.stdout.splitlines() if line.strip()]
    out = subprocess.run(
        ["git", "status", "--porcelain"],
        capture_output=True, text=True, timeout=30,
    )
    if out.returncode != 0:
        return []
    files = []
    for line in out.stdout.splitlines():
        # porcelain format: "XY path" or "XY orig -> new"
        if len(line) < 4:
            continue
        rest = line[3:]
        if " -> " in rest:
            rest = rest.split(" -> ", 1)[1]
        files.append(rest.strip().strip('"'))
    return files


def _detect_base() -> str | None:
    """Guess the merge-base ref. Returns None when we cannot determine one."""
    for ref in ("origin/main", "main"):
        out = subprocess.run(
            ["git", "rev-parse", "--verify", ref],
            capture_output=True, text=True, timeout=10,
        )
        if out.returncode == 0:
            return ref
    return None


def run_shakedown(timeout: float = DEFAULT_TIMEOUT_SEC) -> int:
    """Spawn one ``claude --bg`` session, wait for done, return exit code."""
    # Import lazily so --help / no-change-set paths don't require dispatcher.
    import claude_dispatch  # noqa: E402

    handle = claude_dispatch.spawn(
        mode="interactive",
        prompt=THROWAWAY_PROMPT,
        caller="verify_shakedown",
        name="verify-shakedown",
        dangerous=True,
    )
    sys.stderr.write(
        f"verify_shakedown: spawned claude --bg short={handle.short} "
        f"(timeout={timeout}s)\n"
    )
    start = time.time()
    rc = handle.wait(timeout=timeout)
    elapsed = time.time() - start
    sys.stderr.write(
        f"verify_shakedown: claude --bg short={handle.short} "
        f"exit={rc} elapsed={elapsed:.1f}s\n"
    )
    return rc


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Empirical shakedown gate for /verify.")
    parser.add_argument("--base", default=None,
                        help="Git ref to diff against (default: auto-detect origin/main).")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_SEC,
                        help=f"Spawn timeout in seconds (default {DEFAULT_TIMEOUT_SEC}).")
    parser.add_argument("--require", action="store_true",
                        help="Force run even when no allowlist file changed.")
    args = parser.parse_args(argv)

    base = args.base if args.base else _detect_base()
    changed = _changed_files(base)
    touched = sorted({p for p in changed if is_allowlisted(p)})

    if not touched and not args.require:
        sys.stderr.write(
            "verify_shakedown: skip — no allowlist file touched "
            f"(allowlist={list(SHAKEDOWN_ALLOWLIST)}, base={base or 'unknown'})\n"
        )
        return 0

    sys.stderr.write(
        "verify_shakedown: run — allowlist files touched: "
        f"{touched or ['(forced)']}\n"
    )
    try:
        rc = run_shakedown(timeout=args.timeout)
    except Exception as exc:  # noqa: BLE001 — gate must surface dispatcher errors
        sys.stderr.write(f"verify_shakedown: setup error: {exc}\n")
        return 2
    return 0 if rc == 0 else 1


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
