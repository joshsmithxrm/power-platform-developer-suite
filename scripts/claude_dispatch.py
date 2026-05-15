"""Single routing point for claude subprocess invocations.

See specs/dispatch-routing.md (Core Requirements #1-#9, ACs 01-10).

Every PPDS script that spawns claude calls spawn() here. The
dispatcher branches between interactive (claude --bg) and headless
(claude -p) modes. Interactive is the default; headless is opt-in
and emits a loud stderr warning plus an append-only JSONL audit row
in .claude/state/sdk-spend.jsonl.

After the 2026-06-15 billing split, headless invocations draw from
the metered Agent SDK credit pool; interactive invocations stay on
the subscription pool. This module is the single layer that knows
the difference.

Public API:
    spawn(*, mode, prompt, caller, name=None, agent=None, model=None,
          cwd=".", dangerous=False, stage_log=None, env=None) -> DispatchHandle
    require_min_version(min_ver=MIN_VERSION) -> None
    DispatchHandle (abstract base)
    BgHandle, HeadlessHandle (concrete implementations)
    DispatchError, BlockedSessionError, DispatchFallbackError
"""
from __future__ import annotations

import abc
import contextlib
import json
import os
import re
import subprocess
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import IO, Literal, Mapping, Optional, Union

import bg_transcript

MIN_VERSION = (2, 1, 139)
JOBS_DIR = Path(os.path.expanduser("~/.claude/jobs"))
SPEND_LOG = Path(".claude/state/sdk-spend.jsonl")
ANSI_RE = re.compile(r"\x1b(?:\[[0-9;?]*[A-Za-z]|\][^\x07]*\x07|[^[])")
BANNER_RE = re.compile(r"backgrounded\s+·\s+([0-9a-f]{8})\b")
NAME_RE = re.compile(r"^[A-Za-z0-9/_.\-]+$")
STATE_POLL_BUDGET_SEC = 5.0
STATE_POLL_INTERVAL_SEC = 0.1
BG_RUN_TIMEOUT_SEC = 30
VERSION_RUN_TIMEOUT_SEC = 10

WARNING_TEMPLATE = (
    "WARN SDK pool: claude -p invoked from {caller} "
    "(model={model}, agent={agent}) — "
    "counts against monthly Agent SDK credit, not subscription."
)

Mode = Literal["interactive", "headless"]
PollState = Literal["working", "done", "blocked", "error"]


class DispatchError(Exception):
    """Dispatcher error: bad version, invalid mode, missing state file, etc."""

    def __init__(self, message: str, exit_code: int = 1) -> None:
        super().__init__(message)
        self.exit_code = exit_code


class BlockedSessionError(DispatchError):
    """Raised when a --bg session transitions to state=blocked."""

    def __init__(self, short: str, needs: str) -> None:
        super().__init__(f"session {short} blocked: {needs}", exit_code=1)
        self.short = short
        self.needs = needs


class DispatchFallbackError(DispatchError):
    """Raised when --bg exits non-zero in pr_monitor. Operator must intervene."""

    def __init__(self, stderr: str) -> None:
        message = "claude --bg failed (operator intervention required): " + (stderr or "")[:500]
        super().__init__(message, exit_code=1)
        self.stderr = stderr


def _norm(p: str) -> str:
    return p.replace("\\", "/").rstrip("/")


# Slug derivation: ~/.claude/projects/<slug>/<sessionId>.jsonl where slug
# replaces every non-[A-Za-z0-9-] character in the cwd with `-`. Used as a
# fallback when state.json has no linkScanPath field — true for sessions of
# template="bg" (pr_monitor, pipeline, triage_common dispatches). For
# template="start" sessions claude writes linkScanPath explicitly and we
# prefer that path. Pattern verified empirically against live sessions on
# Windows 2026-05-14.
_SLUG_CHAR_RE = re.compile(r"[^A-Za-z0-9-]")


def derive_slug(cwd: str) -> str:
    """Return Claude Code's per-project slug for ``cwd``.

    Replaces every non-[A-Za-z0-9-] character with `-`. This is the same
    encoding Claude Code uses for ``~/.claude/projects/<slug>/``. Other
    PPDS scripts (e.g. retro transcript discovery) must call this helper
    rather than re-deriving the rule. ``cwd`` is normalized to an absolute
    path first — Claude Code's slug is keyed off the absolute cwd, so a
    relative input would otherwise produce a slug that fails to match.
    """
    return _SLUG_CHAR_RE.sub("-", os.path.abspath(cwd))


def _derive_transcript_path(cwd: str, session_id: str) -> Path:
    """Return ~/.claude/projects/<slug>/<sessionId>.jsonl for the given cwd."""
    return Path(os.path.expanduser("~/.claude/projects")) / derive_slug(cwd) / f"{session_id}.jsonl"


def _parse_version(out: str) -> tuple[int, int, int]:
    m = re.match(r"\s*(\d+)\.(\d+)\.(\d+)", out)
    if not m:
        raise DispatchError(f"could not parse `claude --version` output: {out!r}")
    return tuple(int(g) for g in m.groups())


_VERSION_CHECKED = False


def require_min_version(min_ver: tuple[int, int, int] = MIN_VERSION) -> None:
    """Raise DispatchError if installed claude < min_ver. Cached after first ok check."""
    global _VERSION_CHECKED
    if _VERSION_CHECKED:
        return
    try:
        result = subprocess.run(
            ["claude", "--version"],
            capture_output=True,
            text=True,
            timeout=VERSION_RUN_TIMEOUT_SEC,
        )
    except FileNotFoundError:
        raise DispatchError(
            "claude executable not found on PATH. Install Claude Code "
            ">=" + ".".join(map(str, min_ver)) + " via "
            "`npm i -g @anthropic-ai/claude-code` and rerun."
        )
    if result.returncode != 0:
        raise DispatchError(f"`claude --version` failed: {result.stderr}")
    found = _parse_version(result.stdout)
    if found < min_ver:
        raise DispatchError(
            "claude < " + ".".join(map(str, min_ver)) + " (found "
            + ".".join(map(str, found)) + "). Update via "
            "`npm i -g @anthropic-ai/claude-code` and rerun."
        )
    _VERSION_CHECKED = True


def _reset_version_cache() -> None:
    """Test helper - clears the version-check cache. Not public API."""
    global _VERSION_CHECKED
    _VERSION_CHECKED = False


def _parse_banner(stdout: str) -> Optional[str]:
    stripped = ANSI_RE.sub("", stdout)
    m = BANNER_RE.search(stripped)
    return m.group(1) if m else None


def _resolve_mode(cli_flag: Optional[str], env: Optional[Mapping[str, str]] = None) -> Mode:
    """Resolve dispatch mode: flag > env > default interactive. Raise on invalid."""
    if env is None:
        env = os.environ
    if cli_flag is not None:
        if cli_flag not in ("interactive", "headless"):
            raise DispatchError(f"invalid --mode value: {cli_flag!r}")
        return cli_flag  # type: ignore[return-value]
    env_val = env.get("PPDS_DISPATCH_MODE")
    if env_val is not None and env_val != "":
        if env_val not in ("interactive", "headless"):
            raise DispatchError(f"invalid PPDS_DISPATCH_MODE: {env_val!r}")
        return env_val  # type: ignore[return-value]
    return "interactive"


def _emit_headless_warning(
    caller: str,
    model: Optional[str],
    agent: Optional[str],
    est_input_tokens: int = 0,
    spend_log_path: Optional[Path] = None,
) -> None:
    """Stderr warning + JSONL audit row. Implements Req #7 layer (a), ACs 03-04."""
    model_str = model if model else "none"
    agent_str = agent if agent else "none"
    msg = WARNING_TEMPLATE.format(caller=caller, model=model_str, agent=agent_str) + "\n"
    try:
        sys.stderr.buffer.write(msg.encode("utf-8"))
        sys.stderr.buffer.flush()
    except (AttributeError, OSError):
        sys.stderr.write(msg)
        sys.stderr.flush()
    target = spend_log_path if spend_log_path is not None else SPEND_LOG
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        row = {
            "ts": datetime.now(timezone.utc).isoformat(),
            "caller": caller,
            "model": model_str,
            "agent": agent_str,
            "est_input_tokens": int(est_input_tokens or 0),
        }
        with open(target, "a", encoding="utf-8") as f:
            f.write(json.dumps(row) + "\n")
    except OSError:
        pass


class DispatchHandle(abc.ABC):
    """Uniform interface returned by spawn()."""

    transcript_path: Path

    @abc.abstractmethod
    def poll(self) -> PollState:
        """Return working, done, blocked, or error."""

    @abc.abstractmethod
    def terminate(self) -> None:
        """Best-effort stop of the underlying session."""

    @abc.abstractmethod
    def wait(self, timeout: Optional[float] = None) -> int:
        """Block until poll != working. Returns exit code.

        Raises BlockedSessionError when poll returns blocked.
        """

    @abc.abstractmethod
    def output(self) -> str:
        """Return assembled assistant text from the transcript."""


_STATE_MAP = {
    "working": "working",
    "done": "done",
    "blocked": "blocked",
    "error": "error",
}


@dataclass
class BgHandle(DispatchHandle):
    """Wraps a claude --bg session via its state.json file."""

    short: str
    session_id: str
    state_path: Path
    transcript_path: Path
    _last_state: Optional[dict] = field(default=None, repr=False, compare=False)

    def _read_state(self) -> dict:
        try:
            data = json.loads(self.state_path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            return self._last_state or {}
        self._last_state = data
        return data

    def poll(self) -> PollState:
        data = self._read_state()
        raw = data.get("state", "")
        mapped = _STATE_MAP.get(raw)
        if mapped is None:
            sys.stderr.write(
                f"WARN unknown state.json state for {self.short}: {raw!r}\n"
            )
            return "error"
        return mapped  # type: ignore[return-value]

    def needs(self) -> str:
        data = self._last_state if self._last_state is not None else self._read_state()
        return data.get("needs", "") or ""

    def terminate(self) -> None:
        subprocess.run(
            ["claude", "stop", self.short],
            capture_output=True,
            text=True,
            timeout=BG_RUN_TIMEOUT_SEC,
        )

    def wait(self, timeout: Optional[float] = None) -> int:
        deadline = None if timeout is None else (time.time() + timeout)
        while True:
            state = self.poll()
            if state == "working":
                if deadline is not None and time.time() >= deadline:
                    raise DispatchError(
                        f"BgHandle.wait timed out after {timeout}s for {self.short}"
                    )
                time.sleep(0.5)
                continue
            if state == "blocked":
                needs = self.needs()
                # A real "stage asked a question" block populates `needs`.
                # An empty `needs` means the daemon transiently flipped to
                # blocked during startup or between phases; treat as still
                # working so the existing timeout budget gates progress.
                if not needs.strip():
                    if deadline is not None and time.time() >= deadline:
                        raise DispatchError(
                            f"BgHandle.wait timed out after {timeout}s for "
                            f"{self.short} (last state=blocked, no needs text)"
                        )
                    time.sleep(0.5)
                    continue
                self.terminate()
                raise BlockedSessionError(self.short, needs)
            if state == "done":
                return 0
            return 1

    def output(self) -> str:
        return bg_transcript.parse_outcome(self.transcript_path)


@dataclass
class HeadlessHandle(DispatchHandle):
    """Wraps a claude -p subprocess. stage_log doubles as transcript."""

    proc: subprocess.Popen
    transcript_path: Path
    _stdout_file: Optional[IO] = field(default=None, repr=False, compare=False)

    def poll(self) -> PollState:
        rc = self.proc.poll()
        if rc is None:
            return "working"
        self._close_stdout()
        if rc == 0:
            return "done"
        return "error"

    def terminate(self) -> None:
        with contextlib.suppress(ProcessLookupError, OSError):
            self.proc.terminate()
        with contextlib.suppress(subprocess.TimeoutExpired, OSError):
            self.proc.wait(timeout=30)
        self._close_stdout()

    def wait(self, timeout: Optional[float] = None) -> int:
        try:
            rc = self.proc.wait(timeout=timeout)
        finally:
            self._close_stdout()
        return rc

    def _close_stdout(self) -> None:
        if self._stdout_file is not None and not self._stdout_file.closed:
            with contextlib.suppress(OSError):
                self._stdout_file.close()

    def output(self) -> str:
        return bg_transcript.parse_outcome(self.transcript_path)


def _identify_bg_session(
    short_hint: Optional[str],
    cwd_abs: str,
    jobs_dir: Path,
    spawn_started_at: Optional[float] = None,
    poll_budget: float = STATE_POLL_BUDGET_SEC,
) -> tuple[str, dict]:
    """Resolve the short+state.json for the just-spawned --bg session.

    short_hint is the value parsed from the `backgrounded · <short>` banner.
    spawn_started_at is the time the subprocess.run was issued; the fallback
    cwd-scan only considers sessions whose state.json *createdAt* field is
    >= spawn_started_at - 1s (small clock skew tolerance). Without that
    filter, an old session whose state.json is being actively rewritten
    (e.g. the live Claude Code conversation) trivially passes an mtime
    filter and would be mistaken for the newly spawned session.
    """
    deadline = time.time() + poll_budget
    if spawn_started_at is None:
        spawn_started_at = time.time() - 1
    floor = spawn_started_at - 1.0  # 1s clock-skew tolerance
    while time.time() < deadline:
        if short_hint:
            p = jobs_dir / short_hint / "state.json"
            if p.exists():
                try:
                    data = json.loads(p.read_text(encoding="utf-8"))
                except (json.JSONDecodeError, OSError):
                    data = None
                if data and data.get("sessionId"):
                    return short_hint, data
        else:
            if jobs_dir.exists():
                for child in jobs_dir.iterdir():
                    sp = child / "state.json"
                    if not sp.exists():
                        continue
                    try:
                        if sp.stat().st_mtime < floor:
                            continue
                        data = json.loads(sp.read_text(encoding="utf-8"))
                    except (json.JSONDecodeError, OSError):
                        continue
                    if not data.get("sessionId"):
                        continue
                    if _norm(data.get("cwd") or "") != _norm(cwd_abs):
                        continue
                    # Per-session createdAt filter — mirrors start-bg-spawn
                    # (mtime alone is not enough; active sessions get touched
                    # constantly even when they were created hours ago).
                    created_at = data.get("createdAt")
                    if created_at:
                        try:
                            created_ts = datetime.fromisoformat(
                                str(created_at).replace("Z", "+00:00")
                            ).timestamp()
                        except (ValueError, OSError):
                            created_ts = sp.stat().st_mtime
                        if created_ts < floor:
                            continue
                    return child.name, data
        time.sleep(STATE_POLL_INTERVAL_SEC)
    raise DispatchError(
        f"daemon state file did not appear within {poll_budget}s "
        f"({'short=' + short_hint if short_hint else 'cwd=' + cwd_abs})"
    )

def spawn(
    *,
    mode: Mode,
    prompt: str,
    caller: str,
    name: Optional[str] = None,
    agent: Optional[str] = None,
    model: Optional[str] = None,
    cwd: Union[str, Path] = ".",
    dangerous: bool = False,
    stage_log: Optional[Union[str, Path]] = None,
    env: Optional[Mapping[str, str]] = None,
    jobs_dir: Optional[Path] = None,
    spend_log_path: Optional[Path] = None,
) -> DispatchHandle:
    """Spawn a claude session in the requested mode.

    Args:
        mode: interactive (claude --bg) or headless (claude -p).
        prompt: Text passed verbatim to claude after --.
        caller: Required tag identifying the call site.
        name: --name value for interactive sessions. Required when
            mode=interactive. Must match [A-Za-z0-9/_.-]+.
        agent: Optional --agent value (headless mode).
        model: Optional --model value (headless mode).
        cwd: Working directory for the subprocess.
        dangerous: If True, adds --dangerously-skip-permissions (unattended only).
        stage_log: Required for mode=headless. Streamed stdout path; also
            read back by output().
        env: Optional env mapping (defaults to os.environ).

    Returns: BgHandle (interactive) or HeadlessHandle (headless).
    Raises: DispatchError on invalid mode, old version, missing state file.
    """
    if mode not in ("interactive", "headless"):
        raise DispatchError(f"invalid mode: {mode!r}")
    if not caller:
        raise DispatchError("spawn() requires a non-empty caller string")
    require_min_version()
    cwd_str = str(Path(cwd).resolve()) if cwd else str(Path(".").resolve())

    if mode == "headless":
        if stage_log is None:
            raise DispatchError("headless mode requires stage_log=<path>")
        stage_log_path = Path(stage_log)
        stage_log_path.parent.mkdir(parents=True, exist_ok=True)
        _emit_headless_warning(
            caller=caller,
            model=model,
            agent=agent,
            est_input_tokens=len(prompt) // 4,
            spend_log_path=spend_log_path,
        )
        argv = ["claude", "-p", prompt, "--verbose", "--output-format", "stream-json"]
        if model:
            argv.extend(["--model", model])
        if agent:
            argv.extend(["--agent", agent])
        stdout_file = open(stage_log_path, "ab")
        proc = subprocess.Popen(
            argv,
            cwd=cwd_str,
            stdout=stdout_file,
            stderr=subprocess.PIPE,
            shell=False,
            env=dict(env) if env is not None else None,
        )
        return HeadlessHandle(
            proc=proc,
            transcript_path=stage_log_path,
            _stdout_file=stdout_file,
        )

    if not name:
        raise DispatchError("interactive mode requires name=<stage-or-agent>")
    if not NAME_RE.match(name):
        raise DispatchError(f"invalid --name value: {name!r}")
    argv = ["claude", "--bg", "--name", name]
    if dangerous:
        argv.append("--dangerously-skip-permissions")
    argv.extend(["--", prompt])
    spawn_started_at = time.time()
    proc = subprocess.run(
        argv,
        cwd=cwd_str,
        capture_output=True,
        text=True,
        timeout=BG_RUN_TIMEOUT_SEC,
        shell=False,
        env=dict(env) if env is not None else None,
    )
    if proc.returncode != 0:
        raise DispatchError(f"claude --bg failed: {proc.stderr}")
    short_hint = _parse_banner(proc.stdout)
    if not short_hint:
        # Banner parse failure is suspicious — log the stdout snippet so the
        # operator can see what claude actually emitted. We still try the
        # cwd-scan fallback, but it is bounded by the spawn_started_at floor.
        sys.stderr.write(
            f"WARN claude --bg banner parse failed; stdout head: "
            f"{(proc.stdout or '')[:200]!r}\n"
        )
    jd = jobs_dir if jobs_dir is not None else JOBS_DIR
    short, state = _identify_bg_session(
        short_hint, cwd_str, jd, spawn_started_at=spawn_started_at
    )
    # Transcript path resolution, in priority order:
    #   1. state.json["linkScanPath"] — populated by template="start"
    #      sessions (e.g. /start helper). When present, it is authoritative.
    #   2. Brief re-poll of state.json — covers a rare staged-write race.
    #   3. Slug-derived path ~/.claude/projects/<slug>/<sessionId>.jsonl —
    #      template="bg" sessions never populate linkScanPath, so derivation
    #      is the only option. Verified against live ci-fix sessions.
    link_scan_path = state.get("linkScanPath")
    if not link_scan_path:
        state_path = jd / short / "state.json"
        lsp_deadline = time.time() + 1.0  # brief re-poll, not full budget
        while time.time() < lsp_deadline:
            try:
                data = json.loads(state_path.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                data = None
            if data and data.get("linkScanPath"):
                state = data
                link_scan_path = data["linkScanPath"]
                break
            time.sleep(STATE_POLL_INTERVAL_SEC)
    if not link_scan_path:
        link_scan_path = str(_derive_transcript_path(
            state.get("cwd") or cwd_str, state["sessionId"]
        ))
    return BgHandle(
        short=short,
        session_id=state["sessionId"],
        state_path=jd / short / "state.json",
        transcript_path=Path(link_scan_path),
    )
