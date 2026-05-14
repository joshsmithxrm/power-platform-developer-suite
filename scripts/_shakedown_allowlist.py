"""Single source of truth for the empirical-shakedown allowlist.

Files listed here spawn `claude` subprocesses. Touching any of them during
a PR triggers `/verify`'s empirical-shakedown step (one real `claude --bg`
against a throwaway target, exit 0 asserted) — see
`.claude/skills/verify/SKILL.md`. The post-`/verify` drift detector in
`scripts/retro_helpers.py` reads the same list to flag fix commits that
touched subprocess-spawning files outside the allowlist.

Paths are repo-relative, forward slashes. Add a new wrapper by appending a
single line here; the gate and the detector both pick it up automatically.
"""
from __future__ import annotations

SHAKEDOWN_ALLOWLIST: tuple[str, ...] = (
    "scripts/claude_dispatch.py",
    "scripts/pipeline.py",
    "scripts/pr_monitor.py",
    "scripts/triage_common.py",
    "scripts/start-bg-spawn.py",
)


def _norm(p: str) -> str:
    s = p.replace("\\", "/")
    while s.startswith("./"):
        s = s[2:]
    return s


def is_allowlisted(path: str) -> bool:
    """Return True if *path* (any separator, optional ``./`` prefix) is in the allowlist."""
    return _norm(path) in SHAKEDOWN_ALLOWLIST
