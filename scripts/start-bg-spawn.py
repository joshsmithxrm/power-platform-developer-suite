"""Spawn a `claude --bg` session for /start. See specs/start-launch.md."""
from __future__ import annotations
import argparse, json, os, re, subprocess, sys, time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

# Allow direct script execution: ensure sibling modules (claude_dispatch) are importable
# when this file is loaded by absolute path (tests, /start helper).
_SCRIPT_DIR = str(Path(__file__).resolve().parent)
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

from claude_dispatch import MIN_VERSION, BANNER_RE, _norm, _parse_version  # noqa: E402,F401

JOBS_DIR = Path(os.path.expanduser("~/.claude/jobs"))
ANSI_RE = re.compile(r"\x1b(?:\[[0-9;?]*[A-Za-z]|\][^\x07]*\x07|[^[])")
POLL_BUDGET_SEC = 5.0
POLL_INTERVAL_SEC = 0.1
FALLBACK_AGE_SEC = 10


@dataclass(frozen=True)
class SpawnResult:
    short: str
    sessionId: str
    cwd: str


class SpawnError(Exception):
    def __init__(self, msg: str, code: int):
        super().__init__(msg)
        self.code = code


def require_min_version(min_ver: tuple[int, int, int] = MIN_VERSION) -> None:
    """SpawnError-raising wrapper around the shared version check.

    claude_dispatch.require_min_version raises DispatchError; /start callers
    here have a SpawnError(msg, code) contract baked into main() and tests.
    Keep the wrapper local; share _parse_version via the import above.
    """
    try:
        result = subprocess.run(
            ["claude", "--version"], capture_output=True, text=True, timeout=10
        )
    except FileNotFoundError:
        raise SpawnError(
            "claude executable not found on PATH. Install Claude Code "
            ">=2.1.139 via `npm i -g @anthropic-ai/claude-code` and rerun.",
            1,
        )
    if result.returncode != 0:
        raise SpawnError(f"`claude --version` failed: {result.stderr}", 1)
    try:
        found = _parse_version(result.stdout)
    except Exception as e:
        raise SpawnError(str(e), 1) from e
    if found < min_ver:
        raise SpawnError(
            f"/start requires Claude Code >={'.'.join(map(str, min_ver))} "
            f"(found {'.'.join(map(str, found))}). Update via "
            f"`npm i -g @anthropic-ai/claude-code` and rerun.",
            1,
        )


def parse_banner(stdout: str) -> str | None:
    stripped = ANSI_RE.sub("", stdout)
    m = BANNER_RE.search(stripped)
    return m.group(1) if m else None


def _read_state(short: str, jobs_dir: Path | None = None) -> dict | None:
    if jobs_dir is None:
        jobs_dir = JOBS_DIR
    path = jobs_dir / short / "state.json"
    if not path.exists():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return None
    return data if data.get("sessionId") else None


def _scan_for_cwd(target_cwd: str, since: float, jobs_dir: Path | None = None) -> tuple[str, dict] | None:
    if jobs_dir is None:
        jobs_dir = JOBS_DIR
    if not jobs_dir.exists():
        return None
    for child in jobs_dir.iterdir():
        if not child.is_dir():
            continue
        sp = child / "state.json"
        if not sp.exists():
            continue
        # Fast pre-reject: skip files older than `since` by mtime before parsing JSON.
        if sp.stat().st_mtime < since:
            continue
        data = _read_state(child.name, jobs_dir)
        if not data:
            continue
        # Per spec AC-04: verify recency via state["createdAt"] (ISO-8601).
        # Fall back to mtime (already pre-filtered above) when the field is absent.
        created_at_str = data.get("createdAt")
        if created_at_str:
            try:
                created_dt = datetime.fromisoformat(created_at_str.replace("Z", "+00:00"))
                if created_dt.timestamp() < since:
                    continue
            except (ValueError, OSError):
                pass  # mtime pre-filter already applied
        if _norm(data.get("cwd") or "") == _norm(target_cwd):
            return child.name, data
    return None


def identify_session(
    short_hint: str | None,
    worktree_abs: str,
    jobs_dir: Path | None = None,
) -> tuple[str, dict]:
    if jobs_dir is None:
        jobs_dir = JOBS_DIR
    deadline = time.time() + POLL_BUDGET_SEC
    while time.time() < deadline:
        if short_hint:
            data = _read_state(short_hint, jobs_dir)
            if data:
                return short_hint, data
        else:
            # Recompute each iteration so the 10-second window stays anchored to now.
            hit = _scan_for_cwd(worktree_abs, time.time() - FALLBACK_AGE_SEC, jobs_dir)
            if hit:
                return hit
        time.sleep(POLL_INTERVAL_SEC)
    raise SpawnError(
        f"daemon state file did not appear within {POLL_BUDGET_SEC}s "
        f"for {'short ' + short_hint if short_hint else 'cwd ' + worktree_abs}",
        2,
    )


def spawn(
    worktree_abs: str,
    branch: str,
    prompt: str,
    jobs_dir: Path | None = None,
) -> SpawnResult:
    if jobs_dir is None:
        jobs_dir = JOBS_DIR
    require_min_version()
    proc = subprocess.run(
        ["claude", "--bg", "--name", branch, "--", prompt],
        cwd=worktree_abs, capture_output=True, text=True, timeout=30,
    )
    if proc.returncode != 0:
        raise SpawnError(f"claude --bg failed: {proc.stderr}", 2)
    short_hint = parse_banner(proc.stdout)
    short, state = identify_session(short_hint, worktree_abs, jobs_dir)
    if _norm(state.get("cwd") or "") != _norm(worktree_abs):
        subprocess.run(["claude", "stop", short], capture_output=True, text=True)
        raise SpawnError(
            f"daemon cwd mismatch: expected {_norm(worktree_abs)}, "
            f"got {_norm(state.get('cwd') or '')}",
            2,
        )
    return SpawnResult(short=short, sessionId=state["sessionId"], cwd=_norm(state["cwd"]))


def _validate(args: argparse.Namespace, prompt: str) -> None:
    if not os.path.isabs(args.worktree_abs) or not os.path.isdir(args.worktree_abs):
        raise SpawnError(f"worktree path does not exist: {args.worktree_abs}", 1)
    if not args.branch or not re.match(r"^[A-Za-z0-9/_.\-]+$", args.branch):
        raise SpawnError(
            "--branch must be non-empty and contain only [A-Za-z0-9/_.-]", 1
        )
    if not prompt.strip():
        raise SpawnError("prompt file is empty or missing", 1)


class _ArgParser(argparse.ArgumentParser):
    """argparse default exits 2 on errors; spec InvalidArg is exit 1."""

    def error(self, message: str) -> None:  # type: ignore[override]
        self.print_usage(sys.stderr)
        sys.stderr.write(f"{self.prog}: error: {message}\n")
        sys.exit(1)


def main(argv: list[str] | None = None) -> int:
    p = _ArgParser(description="Spawn a claude --bg session for /start.")
    p.add_argument("--worktree-abs", required=True)
    p.add_argument("--branch", required=True)
    p.add_argument("--prompt-file", required=True)
    args = p.parse_args(argv)
    try:
        try:
            prompt = Path(args.prompt_file).read_text(encoding="utf-8")
        except OSError as exc:
            raise SpawnError(f"could not read prompt file: {exc}", 1)
        _validate(args, prompt)
        result = spawn(args.worktree_abs, args.branch, prompt)
        json.dump(
            {"short": result.short, "sessionId": result.sessionId, "cwd": result.cwd},
            sys.stdout,
        )
        sys.stdout.write("\n")
        return 0
    except SpawnError as e:
        sys.stderr.write(str(e) + "\n")
        return e.code


if __name__ == "__main__":
    sys.exit(main())
