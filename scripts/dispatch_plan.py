#!/usr/bin/env python3
"""Helpers for the ``/backlog dispatch`` subverb.

A *dispatch plan* is a markdown file at ``.claude/state/dispatch-plan.md``
that records the worktrees the dispatcher session intends to launch in
response to ``/backlog dispatch``. The plan is durable — it survives
the session that authored it — so that a subsequent dispatcher run can
see what is in flight, resume an interrupted launch wave, or simply
audit what happened.

Why a markdown file (not JSON) at ``.claude/state/``:

* ``.plans/`` is gitignored; ``.claude/state/`` is the agreed durable
  location for cross-session state (the in-flight registry from PR #813
  also lives there).
* Markdown is human-skimmable in code review, which matters because the
  dispatcher session asks the operator to confirm the plan before any
  worktree create. A JSON dump would be opaque in chat.
* The format is regular enough to parse mechanically (see
  :func:`parse_plan`), so the skill can update entry status after each
  launch without losing operator-edited context.

The plan is *generated* by Phase A of the dispatch flow (after the
backlog triage identifies what is ready), *gated* by Phase B's per-
entry conflict check against the in-flight registry, *executed* in
Phase D when the operator confirms, and *amended* in Phase E with
launch timestamps and session IDs.

This module is intentionally pure-Python and side-effect-free at import
time so the unit tests can exercise it without spawning subprocesses.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from contextlib import contextmanager
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Iterable, Iterator

# Reuse the cross-platform locking primitives from inflight_common rather
# than duplicating them: same OS-level fcntl/msvcrt strategy, single
# source of truth for the lock implementation. The plan file lives next
# to the in-flight registry and shares the same coordination needs
# (cross-session, cross-worktree, read-modify-write windows during
# Phase D of the dispatch flow).
from inflight_common import _lock, _unlock  # noqa: E402

PLAN_SCHEMA_VERSION = 2  # bump if file format changes
#
# Schema v2 (current): splits the v1 ``Session:`` slot — which was
# overloaded to carry branch name / session ID / PR URL depending on
# caller — into two semantic fields, ``SessionId:`` (Claude session
# identifier) and ``PR:`` (PR URL, optional). The parser still accepts
# v1 plans (``Session:`` maps to ``session_id``) for one release so that
# an existing on-disk plan file survives the upgrade.

# Status vocabulary. Kept narrow so the parser can validate; downstream
# tooling (status reports, future TUI surface) reads the same set.
STATUS_PLANNED = "planned"
STATUS_CONFLICT = "conflict"
STATUS_IN_FLIGHT = "in-flight"
STATUS_DONE = "done"
STATUS_SKIPPED = "skipped"

VALID_STATUSES = {
    STATUS_PLANNED,
    STATUS_CONFLICT,
    STATUS_IN_FLIGHT,
    STATUS_DONE,
    STATUS_SKIPPED,
}


def repo_root() -> Path:
    """Return the repository root (mirror of :mod:`inflight_common`)."""
    here = Path(__file__).resolve().parent
    for candidate in [here, *here.parents]:
        if (candidate / ".claude").is_dir():
            return candidate
    return here.parent


def plan_path() -> Path:
    """Absolute path to the dispatch plan file.

    The plan is shared across all worktrees because it represents the
    dispatcher session's intent for the *whole* repository, not any one
    branch. Living next to ``in-flight-issues.json`` keeps the two
    cross-session artifacts colocated.
    """
    p = repo_root() / ".claude" / "state" / "dispatch-plan.md"
    p.parent.mkdir(parents=True, exist_ok=True)
    return p


def now_utc_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------


@dataclass
class PlanEntry:
    """One worktree the dispatcher intends to launch."""

    worktree: str  # branch-name-style key, e.g. "feat/issue-660"
    issues: list[int] = field(default_factory=list)
    areas: list[str] = field(default_factory=list)
    intent: str = ""
    status: str = STATUS_PLANNED
    conflict_detail: str = ""
    launched_at: str = ""
    # Schema v2 fields. ``session_id`` is the Claude session identifier
    # of the launched worker (D.2 path) or the agent-isolation session
    # that ran the work (D.1 path). ``pr_url`` is the pull request URL
    # once one has been opened — optional, populated by the dispatcher
    # or by the async PR-state wake-up in Phase E.
    session_id: str = ""
    pr_url: str = ""
    # DEPRECATED: use ``session_id``; kept for one release for schema v1
    # compat with any callers / cached dataclass instances that still
    # reference the old field name. Populated by ``parse_plan`` from the
    # legacy v1 ``Session:`` key but never emitted by ``as_markdown``.
    launched_by_session: str = ""

    def as_markdown(self) -> str:
        issues_str = ", ".join(f"#{i}" for i in self.issues) if self.issues else "(none)"
        areas_str = ", ".join(self.areas) if self.areas else "(none)"
        lines = [
            f"### Worktree: {self.worktree}",
            f"- Issues: {issues_str}",
            f"- Areas: {areas_str}",
            f"- Intent: {self.intent or '(unspecified)'}",
            f"- Status: {self.status}",
        ]
        if self.conflict_detail:
            lines.append(f"- Conflict: {self.conflict_detail}")
        if self.launched_at:
            lines.append(f"- Launched: {self.launched_at}")
        # Schema v2: emit SessionId / PR as separate lines. Empty values
        # are omitted so hand-readable plans stay compact. The legacy
        # ``Session:`` line is NOT emitted — new plans are v2-native.
        if self.session_id:
            lines.append(f"- SessionId: {self.session_id}")
        if self.pr_url:
            lines.append(f"- PR: {self.pr_url}")
        return "\n".join(lines)


@dataclass
class DispatchPlan:
    """The full plan: header metadata + ordered list of entries."""

    generated: str = ""
    generator: str = ""
    entries: list[PlanEntry] = field(default_factory=list)
    version: int = PLAN_SCHEMA_VERSION

    def as_markdown(self) -> str:
        header = [
            "# Dispatch Plan",
            "",
            f"Schema: {self.version}",
            f"Generated: {self.generated or now_utc_iso()}",
            f"Generator: {self.generator or '(unknown)'}",
            "",
            "Status legend: planned | conflict | in-flight | done | skipped",
            "",
            "## Planned",
            "",
        ]
        if not self.entries:
            header.append("_(no worktrees planned)_")
            return "\n".join(header) + "\n"
        body = "\n\n".join(e.as_markdown() for e in self.entries)
        return "\n".join(header) + body + "\n"

    def find(self, worktree: str) -> PlanEntry | None:
        for e in self.entries:
            if e.worktree == worktree:
                return e
        return None

    def summary_counts(self) -> dict[str, int]:
        counts = {s: 0 for s in VALID_STATUSES}
        for e in self.entries:
            counts[e.status] = counts.get(e.status, 0) + 1
        return counts


# ---------------------------------------------------------------------------
# Serialization
# ---------------------------------------------------------------------------


_ENTRY_HEADER_RE = re.compile(r"^###\s+Worktree:\s+(.+)$")
_KV_RE = re.compile(r"^-\s+(?P<key>[A-Za-z][A-Za-z\- ]*?):\s*(?P<value>.*)$")


def parse_plan(text: str) -> DispatchPlan:
    """Parse a previously-written plan markdown file back into a DispatchPlan.

    The parser is forgiving: unknown lines inside an entry block are
    ignored so a human can edit the file (e.g. add a note) without
    breaking subsequent ``write_plan`` round-trips. Required keys
    (``Status``) are validated; an unknown status is coerced to
    ``planned`` so the dispatcher does not silently treat a typo as
    "done".
    """
    plan = DispatchPlan()
    current: PlanEntry | None = None

    def commit():
        nonlocal current
        if current is not None:
            plan.entries.append(current)
            current = None

    for raw in text.splitlines():
        line = raw.rstrip()
        if not line:
            continue
        if line.startswith("Generated:"):
            plan.generated = line.split(":", 1)[1].strip()
            continue
        if line.startswith("Generator:"):
            plan.generator = line.split(":", 1)[1].strip()
            continue
        if line.startswith("Schema:"):
            try:
                plan.version = int(line.split(":", 1)[1].strip())
            except ValueError:
                pass
            continue
        m = _ENTRY_HEADER_RE.match(line)
        if m:
            commit()
            current = PlanEntry(worktree=m.group(1).strip())
            continue
        if current is None:
            continue
        kv = _KV_RE.match(line)
        if not kv:
            continue
        key = kv.group("key").strip().lower()
        value = kv.group("value").strip()
        if key == "issues":
            current.issues = _parse_issues(value)
        elif key == "areas":
            current.areas = [] if value in ("(none)", "") else [
                a.strip() for a in value.split(",") if a.strip()
            ]
        elif key == "intent":
            current.intent = "" if value == "(unspecified)" else value
        elif key == "status":
            current.status = value if value in VALID_STATUSES else STATUS_PLANNED
        elif key == "conflict":
            current.conflict_detail = value
        elif key == "launched":
            current.launched_at = value
        elif key == "sessionid":
            # Schema v2 primary key. If a malformed plan has BOTH
            # ``Session:`` and ``SessionId:`` lines in one entry,
            # ``SessionId:`` wins regardless of line order — we
            # overwrite whatever the legacy ``session`` branch might
            # have set earlier or later in the block.
            current.session_id = value
            current.launched_by_session = value  # back-compat mirror
        elif key == "pr":
            current.pr_url = value
        elif key == "session":
            # Schema v1 legacy key. Map to ``session_id`` unless the v2
            # ``SessionId:`` key has already claimed the slot (line
            # order: SessionId seen first → skip). Keep mirroring to
            # the deprecated ``launched_by_session`` field so any code
            # path that still reads it sees the same value.
            if not current.session_id:
                current.session_id = value
                current.launched_by_session = value
    commit()
    return plan


def _parse_issues(value: str) -> list[int]:
    if value in ("(none)", ""):
        return []
    out: list[int] = []
    for token in value.split(","):
        token = token.strip().lstrip("#")
        if token.isdigit():
            out.append(int(token))
    return out


def _write_plan_payload(path: Path, payload: str) -> None:
    """Write-then-rename so a partial write never leaves a half-parsed file."""
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(payload, encoding="utf-8")
    tmp.replace(path)


def write_plan(plan: DispatchPlan, path: Path | None = None) -> Path:
    """Atomically (best-effort) write ``plan`` to ``path`` (default location).

    Uses a write-then-rename pattern so a partial write never leaves the
    file in a half-parsed state — important because the dispatcher edits
    the plan after every launch.

    Holds an exclusive OS-level lock for the duration of the write so
    that two concurrent dispatcher runs (or an operator hand-edit racing
    with the dispatcher) cannot interleave and lose updates.
    """
    if path is None:
        path = plan_path()
    if not plan.generated:
        plan.generated = now_utc_iso()
    payload = plan.as_markdown()

    # Use a sidecar lock file: we cannot lock the plan file itself across
    # the rename without losing the lock on the renamed inode. The lock
    # file is durable next to the plan and never written to except as a
    # lock target. Open in a+ so it gets created on first use.
    lock_path = path.with_suffix(path.suffix + ".lock")
    with open(lock_path, "a+", encoding="utf-8") as lockfp:
        _lock(lockfp)
        try:
            _write_plan_payload(path, payload)
        finally:
            _unlock(lockfp)
    return path


def load_plan(path: Path | None = None) -> DispatchPlan:
    """Read and parse the plan from ``path`` (default location).

    Returns an empty plan if the file does not exist — the caller can
    then populate ``entries`` and call :func:`write_plan` for the first
    time without special-casing missing-file logic.

    Acquires the same sidecar lock as :func:`write_plan` so a concurrent
    writer cannot have the file half-renamed when we read it.
    """
    if path is None:
        path = plan_path()
    if not path.exists():
        return DispatchPlan()
    lock_path = path.with_suffix(path.suffix + ".lock")
    with open(lock_path, "a+", encoding="utf-8") as lockfp:
        _lock(lockfp)
        try:
            return parse_plan(path.read_text(encoding="utf-8"))
        finally:
            _unlock(lockfp)


@contextmanager
def locked_plan(path: Path | None = None) -> Iterator[DispatchPlan]:
    """Read-modify-write helper that holds the plan lock across the window.

    Yields the parsed :class:`DispatchPlan`; on context exit, the plan is
    written back atomically. Use this from any caller that needs to load
    the plan, mutate it, and persist the change without another process
    racing in between (the typical Phase D pattern).

    Example::

        with locked_plan() as plan:
            mark_launched(plan, "feat/foo", session_id="session-abc",
                          pr_url="https://github.com/x/y/pull/1")
    """
    if path is None:
        path = plan_path()
    lock_path = path.with_suffix(path.suffix + ".lock")
    with open(lock_path, "a+", encoding="utf-8") as lockfp:
        _lock(lockfp)
        try:
            if path.exists():
                plan = parse_plan(path.read_text(encoding="utf-8"))
            else:
                plan = DispatchPlan()
            yield plan
            if not plan.generated:
                plan.generated = now_utc_iso()
            _write_plan_payload(path, plan.as_markdown())
        finally:
            _unlock(lockfp)


# ---------------------------------------------------------------------------
# Conflict check integration
# ---------------------------------------------------------------------------


# Exit codes from inflight-check.py:
#   0 = no conflict, 1 = conflict, 2 = bad args.
INFLIGHT_OK = 0
INFLIGHT_CONFLICT = 1
INFLIGHT_BADARGS = 2


def _default_runner(cmd: list[str]) -> tuple[int, str, str]:
    """Subprocess runner used by :func:`run_conflict_check` in production.

    Tests inject a callable that returns ``(returncode, stdout, stderr)``
    so the suite never spawns ``python scripts/inflight-check.py``.
    """
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    return proc.returncode, proc.stdout, proc.stderr


def run_conflict_check(
    entry: PlanEntry,
    *,
    runner: Callable[[list[str]], tuple[int, str, str]] | None = None,
    script_path: Path | None = None,
) -> tuple[bool, list[dict]]:
    """Run ``inflight-check.py`` for one entry; return ``(ok, conflicts)``.

    ``ok`` is True when no conflict was detected (exit 0). ``conflicts``
    is the JSON list parsed from stdout when the script reports one
    (exit 1); empty otherwise.

    The check fans out one invocation per (issue, area) pair so that a
    single entry covering multiple areas doesn't accidentally mask an
    overlap on a sibling area. Issue conflicts are checked first because
    they are usually the cleaner signal (whole-issue duplication).
    """
    runner = runner or _default_runner
    if script_path is None:
        script_path = repo_root() / "scripts" / "inflight-check.py"
    all_conflicts: list[dict] = []

    queries: list[list[str]] = []
    for issue in entry.issues:
        queries.append(["--issue", str(issue)])
    for area in entry.areas:
        queries.append(["--area", area])
    if not queries:
        # No issue and no area means nothing to check — treat as ok.
        return True, []

    for q in queries:
        cmd = [sys.executable, str(script_path), *q]
        rc, stdout, _stderr = runner(cmd)
        if rc == INFLIGHT_OK:
            continue
        if rc == INFLIGHT_CONFLICT:
            try:
                payload = json.loads(stdout) if stdout.strip() else {}
            except json.JSONDecodeError:
                payload = {}
            for c in payload.get("conflicts", []) or []:
                all_conflicts.append(c)
            continue
        # rc == 2 or anything else: treat as a hard failure rather than
        # a silent pass — the operator should know the gate could not run.
        # Surface stderr in the error so the operator can diagnose without
        # re-running the subprocess by hand (Gemini #3106679471).
        stderr_msg = (_stderr or "").strip()
        raise RuntimeError(
            f"inflight-check.py exited with rc={rc} for cmd={cmd!r}"
            + (f"; stderr: {stderr_msg}" if stderr_msg else "")
        )
    return (len(all_conflicts) == 0), all_conflicts


def annotate_with_conflicts(
    plan: DispatchPlan,
    *,
    runner: Callable[[list[str]], tuple[int, str, str]] | None = None,
) -> DispatchPlan:
    """Walk every ``planned`` entry and update its status from a check.

    Entries already at a terminal status (``in-flight``, ``done``,
    ``skipped``) are left alone — the operator may have hand-curated
    them, or a previous dispatch wave already executed them.
    """
    for entry in plan.entries:
        if entry.status != STATUS_PLANNED:
            continue
        ok, conflicts = run_conflict_check(entry, runner=runner)
        if ok:
            continue
        entry.status = STATUS_CONFLICT
        # Render a one-line description so the operator sees who owns
        # the overlapping work without opening another tool.
        entry.conflict_detail = "; ".join(
            f"session {c.get('session_id', '?')} on {c.get('branch', '?')}"
            f" ({c.get('intent') or 'no intent'})"
            for c in conflicts
        )
    return plan


# ---------------------------------------------------------------------------
# Post-dispatch annotation
# ---------------------------------------------------------------------------


def mark_launched(
    plan: DispatchPlan,
    worktree: str,
    *,
    session_id: str = "",
    pr_url: str = "",
    when: str | None = None,
) -> PlanEntry:
    """Flip an entry to ``in-flight`` and stamp launch metadata.

    Schema v2 takes ``session_id`` and ``pr_url`` as separate arguments.
    D.2 callers supply ``session_id`` (the Claude session identifier);
    D.1 callers supply ``pr_url`` (the PR URL the isolated agent
    returned) and may optionally also supply ``session_id``. Passing
    neither is legal — the entry is still marked in-flight so the
    dispatcher can record the launch even when the identifier is not
    yet known.

    Raises ``KeyError`` if no entry matches ``worktree`` — defensive
    against a typo in the SKILL.md prompt that would otherwise silently
    drop the launch from the plan.
    """
    entry = plan.find(worktree)
    if entry is None:
        raise KeyError(f"no plan entry for worktree {worktree!r}")
    entry.status = STATUS_IN_FLIGHT
    entry.launched_at = when or now_utc_iso()
    entry.session_id = session_id
    # Mirror to the deprecated v1 field so any caller still reading it
    # (e.g. cached dataclass pickles) sees the same value. Safe to
    # remove after one release.
    entry.launched_by_session = session_id
    if pr_url:
        entry.pr_url = pr_url
    return entry


def build_plan_from_dicts(
    items: Iterable[dict],
    *,
    generator: str = "",
    generated: str = "",
) -> DispatchPlan:
    """Construct a :class:`DispatchPlan` from a list of dict descriptors.

    Used by the SKILL.md instruction sequence: the dispatcher session
    triages the backlog, builds an in-memory list of dicts, and hands
    them here. Keeping the constructor in the helper means we don't
    duplicate validation in two places when a future caller wires this
    up.
    """
    plan = DispatchPlan(generator=generator, generated=generated or now_utc_iso())
    for raw in items:
        # AI-generated JSON often emits explicit ``null`` for optional
        # list/string fields; ``raw.get(k, default)`` returns ``None`` in
        # that case, which would TypeError downstream. The ``or default``
        # idiom collapses both missing and explicit-null to the safe
        # default (Gemini #3106679474).
        plan.entries.append(
            PlanEntry(
                worktree=str(raw["worktree"]),
                issues=[int(i) for i in (raw.get("issues") or [])],
                areas=[str(a) for a in (raw.get("areas") or [])],
                intent=str(raw.get("intent") or ""),
                status=str(raw.get("status") or STATUS_PLANNED),
            )
        )
    return plan


# ---------------------------------------------------------------------------
# CLI for ad-hoc inspection
# ---------------------------------------------------------------------------


def _format_summary(plan: DispatchPlan) -> str:
    counts = plan.summary_counts()
    parts = [f"{k}={v}" for k, v in sorted(counts.items()) if v]
    if not parts:
        return "(empty plan)"
    return ", ".join(parts)


def main(argv: list[str] | None = None) -> int:
    """Tiny CLI: ``python scripts/dispatch_plan.py [show|summary]``.

    ``show`` prints the raw markdown (useful from a non-interactive
    runner). ``summary`` prints the per-status counts. Anything else
    (or no args) prints the summary.
    """
    args = list(sys.argv[1:] if argv is None else argv)
    plan = load_plan()
    if args and args[0] == "show":
        sys.stdout.write(plan.as_markdown())
        return 0
    sys.stdout.write(_format_summary(plan) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
