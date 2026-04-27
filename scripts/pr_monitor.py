#!/usr/bin/env python3
"""
Decoupled PR monitor — background process for CI polling, Gemini review
triage, retro, and notification after a PR is created.

Designed to run detached from the creating terminal so the caller can
return to the REPL immediately.

Usage:
    python scripts/pr_monitor.py --worktree <path> --pr <number>
    python scripts/pr_monitor.py --worktree <path> --pr <number> --resume

Platform detachment (caller side):
    Windows:  Popen(creationflags=CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP)
    Unix:     Popen(start_new_session=True)
"""
import argparse
import atexit
import json
import os
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from triage_common import (
    GEMINI_BOT_LOGIN,
    build_triage_prompt,
    get_repo_slug as _get_repo_slug,
    get_unreplied_comments,
    parse_triage_jsonl,
    poll_gemini_review,
    post_replies as _post_replies_common,
)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

CI_POLL_INTERVAL = 30       # seconds between CI status checks
CI_MAX_WAIT = 900           # 15 minutes max for CI (AC-125)
CI_ESCALATION_THRESHOLD = 3       # consecutive no-check polls before escalation
CI_ESCALATION_MIN_ELAPSED = 120   # seconds since first error before escalation
GEMINI_POLL_INTERVAL = 30   # seconds between Gemini comment polls
GEMINI_MAX_WAIT = 300       # 5 minutes max for Gemini
MAX_TRIAGE_ITERATIONS = 3   # max triage -> CI re-check cycles

MAX_CI_FIX_ROUNDS = int(os.environ.get("PPDS_MAX_CI_FIX_ROUNDS", "3"))

KNOWN_FLAKE_PATTERNS = [
    "connection reset by peer",
    "connection refused",
    "timeout waiting for",
    "timed out after",
    "ECONNRESET",
    "ETIMEDOUT",
    "read: connection reset",
    "net/http: request canceled",
    "unexpected EOF",
    "i/o timeout",
    "dial tcp: lookup",
    "no such host",
    "temporary failure in name resolution",
    "rate limit exceeded",
    "502 Bad Gateway",
    "503 Service Unavailable",
]

TERMINAL_STATES = (
    "ready",
    "stuck-ci-fix-exhausted",
    "stuck-triage-exhausted",
    "stuck-thrash-detected",
    "stuck-uncommitted-triage",
    "stuck-dirty-worktree-on-ready-flip",
    "ci-timeout",
    "monitor-crash",
)

SHAKEDOWN = os.environ.get("PPDS_SHAKEDOWN", "")

# Substrings that indicate Gemini has "spoken" but has nothing for us to
# address — either a clean approval (top-level review with no inline notes)
# or an explicit decline (file types not currently supported).
# Both are treated as ready-flip-eligible for the Gemini gate. Extend this
# list as new Gemini phrasings are discovered. Stored lower-case; matched
# case-insensitively in ``_gemini_effectively_done`` so capitalisation
# drift in Gemini's output doesn't regress the gate.
_GEMINI_CLEAN_PATTERNS = (
    "i have no feedback to provide",
    "gemini is unable to generate a review",
    # Add future patterns here as discovered (lower-case).
)


def _gemini_effectively_done(review_body):
    """Return True if Gemini's latest review body indicates nothing to address.

    Matches clean-approval phrasing OR explicit "unable to review" declines.
    Does NOT match reviews that flagged issues (those have inline comments
    and require per-comment triage separately).

    Matching is case-insensitive — Gemini's phrasing has historically varied
    in capitalisation (``Gemini is unable...`` vs ``GEMINI is unable...``)
    and we don't want trivial drift to regress the ready-flip gate.
    """
    if not review_body:
        return False
    body_lower = review_body.lower()
    return any(p in body_lower for p in _GEMINI_CLEAN_PATTERNS)

# ---------------------------------------------------------------------------
# Worktree state helpers
# ---------------------------------------------------------------------------


def _get_worktree_dirty_files(worktree):
    """Return list of modified/untracked tracked files in the worktree.

    Uses ``git status --porcelain`` so untracked NEW files (``??`` prefix)
    are NOT reported — only files already known to git that have been
    modified/deleted/staged are returned. This is the right signal for
    AC-92: we care whether the triage agent made changes without committing
    them, not whether scratch files exist in the tree.

    Returns an empty list on any error (fail-open — the gate above this call
    is the hard stop, so a git failure here should not silently pass the gate;
    callers must treat a non-empty list as "dirty" and treat a git failure as
    "unknown" and refuse to proceed).

    Raises ``OSError`` / ``subprocess.SubprocessError`` on subprocess failure
    so callers can distinguish "clean" from "git unavailable".
    """
    result = subprocess.run(
        ["git", "status", "--porcelain"],
        cwd=worktree, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=10,
    )
    if result.returncode != 0:
        raise OSError(
            f"git status --porcelain failed (rc={result.returncode}): "
            f"{result.stderr.strip()[:200]}"
        )
    dirty = []
    for line in result.stdout.splitlines():
        if not line:
            continue
        xy = line[:2]
        path = line[3:].strip().strip('"')
        # Skip purely-untracked files (??); they don't represent uncommitted changes
        if xy == "??":
            continue
        dirty.append(path)
    return dirty


class _UncommittedTriageError(Exception):
    """Raised by _reconcile_replies when the AC-92 dirty-worktree gate fires.

    Propagated up to run_monitor so the terminal state is set and a
    notification is fired — not caught by the generic ``except Exception``
    that handles normal operational errors.
    """


class _DirtyWorktreeOnReadyFlipError(Exception):
    """Raised by _step_ready when a dirty worktree is detected before rebase.

    Propagated up to run_monitor so the terminal state is set to
    ``stuck-dirty-worktree-on-ready-flip`` and a notification is fired.
    Belt-and-suspenders guard for the case where Bug 1 (_UncommittedTriageError)
    somehow did not fire (e.g. the monitor resumed, or a user manually ran with
    dirty state).
    """


# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------


def _timestamp():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _local_time():
    return datetime.now().strftime("%H:%M:%S")


class Logger:
    """Append-only structured logger writing to .workflow/pr-monitor.log."""

    def __init__(self, log_path):
        os.makedirs(os.path.dirname(log_path), exist_ok=True)
        self._fh = open(log_path, "a", encoding="utf-8")

    def log(self, step, event, **extra):
        parts = [f"{_timestamp()} [{step}] {event}"]
        for k, v in extra.items():
            parts.append(f"{k}={v}")
        line = " ".join(parts)
        self._fh.write(line + "\n")
        self._fh.flush()
        # Also print for console when running attached
        console = [f"[{_local_time()}] {step}: {event}"]
        for k, v in extra.items():
            console.append(f"{k}={v}")
        print(" ".join(console), file=sys.stderr)

    def close(self):
        try:
            self._fh.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------
# Result file
# ---------------------------------------------------------------------------


def _result_path(worktree):
    return os.path.join(worktree, ".workflow", "pr-monitor-result.json")


def read_result(worktree):
    path = _result_path(worktree)
    if not os.path.exists(path):
        return _empty_result()
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return _empty_result()


def write_result(worktree, result):
    path = _result_path(worktree)
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=2)
            f.write("\n")
    except OSError as e:
        # Fail-open: a write_result error must not crash the monitor.
        sys.stderr.write(
            f"[pr-monitor] write_result failed: {e}\n"
        )


def _empty_result():
    return {
        "status": "pending",
        "steps_completed": {},
        "ci_result": None,
        "comment_counts": {},
        "triage_summary": [],
        "retro_status": None,
        # Set to True by _step_gemini whenever a poll returns
        # status="review_received" (meta-retro finding #2 gate).
        "gemini_review_posted": False,
        # Latest Gemini top-level review body (string). Captured so the
        # ready-flip gate can distinguish clean-approval / declined reviews
        # from reviews that flagged issues needing replies.
        "gemini_review_body": "",
        "timestamp": _timestamp(),
    }


# ---------------------------------------------------------------------------
# PID file management
# ---------------------------------------------------------------------------


def _pid_path(worktree):
    return os.path.join(worktree, ".workflow", "pr-monitor.pid")


def write_pid(worktree):
    path = _pid_path(worktree)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(str(os.getpid()))


def cleanup_pid(worktree):
    path = _pid_path(worktree)
    try:
        os.remove(path)
    except OSError:
        pass


# ---------------------------------------------------------------------------
# GitHub helpers
# ---------------------------------------------------------------------------


def get_repo_slug(worktree):
    """Get owner/repo from gh CLI. Returns 'owner/repo' or None."""
    return _get_repo_slug(worktree, shakedown=bool(SHAKEDOWN))


def _escalate_ci_no_checks(worktree, pr_number, logger, consecutive_errors,
                           first_error_time):
    """Fire a notification when CI checks are not being posted."""
    elapsed = int(time.time() - first_error_time)
    logger.log("ci", "ESCALATION",
               consecutive_errors=consecutive_errors,
               elapsed_since_first=f"{elapsed}s")
    msg = (
        f"PR #{pr_number} has had no CI checks reported for "
        f">{elapsed}s after {consecutive_errors} polls. Investigate: "
        f"(a) CI workflow posting, (b) branch protection rules, "
        f"(c) workflow file syntax."
    )
    try:
        run_notify(worktree, pr_number, logger, message=msg)
    except Exception:  # noqa: BLE001 — fire-and-forget
        logger.log("ci", "ESCALATION_NOTIFY_ERROR")


def poll_ci(worktree, pr_number, logger):
    """Poll CI checks until all pass, any fail, or timeout.

    Returns: "pass", "fail", or "timeout"
    """
    if SHAKEDOWN:
        logger.log("ci", "SHAKEDOWN_SKIPPED")
        return "pass"

    start = time.time()
    consecutive_errors = 0
    first_error_time = None
    escalated = False

    def _track_error():
        nonlocal consecutive_errors, first_error_time, escalated
        consecutive_errors += 1
        if first_error_time is None:
            first_error_time = time.time()
        if (not escalated
                and consecutive_errors >= CI_ESCALATION_THRESHOLD
                and time.time() - first_error_time >= CI_ESCALATION_MIN_ELAPSED):
            _escalate_ci_no_checks(worktree, pr_number, logger,
                                   consecutive_errors, first_error_time)
            escalated = True

    while time.time() - start < CI_MAX_WAIT:
        try:
            result = subprocess.run(
                ["gh", "pr", "checks", str(pr_number),
                 "--json", "name,state,bucket"],
                cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
            )
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
            logger.log("ci", "POLL_ERROR", error=str(e))
            _track_error()
            time.sleep(CI_POLL_INTERVAL)
            continue

        if result.returncode != 0:
            logger.log("ci", "POLL_ERROR", stderr=result.stderr.strip()[:200])
            _track_error()
            time.sleep(CI_POLL_INTERVAL)
            continue

        try:
            checks = json.loads(result.stdout)
        except json.JSONDecodeError:
            logger.log("ci", "PARSE_ERROR", stdout=result.stdout.strip()[:200])
            time.sleep(CI_POLL_INTERVAL)
            continue

        if not checks:
            logger.log("ci", "NO_CHECKS_YET",
                       elapsed=f"{int(time.time() - start)}s")
            _track_error()
            time.sleep(CI_POLL_INTERVAL)
            continue

        # Checks found — reset error tracking and log recovery if escalated
        if escalated:
            logger.log("ci", "RECOVERED",
                       ci_checks_posting=True, after_polls=consecutive_errors)
        consecutive_errors = 0
        first_error_time = None
        escalated = False

        # `gh pr checks` returns `bucket` as the conclusion category:
        #   pass | fail | pending | skipping | cancel
        # `state` indicates COMPLETED vs IN_PROGRESS vs QUEUED.
        total = len(checks)
        completed = [c for c in checks
                     if c.get("state") in ("COMPLETED", "SUCCESS", "FAILURE")
                     or c.get("bucket") in ("pass", "fail", "skipping", "cancel")]
        failed = [c for c in completed if c.get("bucket") == "fail"]
        pending = total - len(completed)

        logger.log("ci", "POLL",
                   total=total, completed=len(completed),
                   failed=len(failed), pending=pending,
                   elapsed=f"{int(time.time() - start)}s")

        if failed:
            names = ", ".join(c.get("name", "?") for c in failed)
            logger.log("ci", "FAILED", checks=names)
            return "fail"

        if pending == 0:
            logger.log("ci", "ALL_PASSED",
                       elapsed=f"{int(time.time() - start)}s")
            return "pass"

        time.sleep(CI_POLL_INTERVAL)

    logger.log("ci", "TIMEOUT", elapsed=f"{int(time.time() - start)}s")
    return "timeout"


def _get_pr_created_at(worktree, pr_number):
    """Return the PR's created_at ISO timestamp, or "" on failure.

    Used by poll_gemini_comments to filter out stale Gemini reviews from
    earlier force-pushes (v1-prelaunch retro item #3).
    """
    try:
        result = subprocess.run(
            ["gh", "pr", "view", str(pr_number),
             "--json", "createdAt", "--jq", ".createdAt"],
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15,
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    return ""


def poll_gemini_comments(worktree, pr_number, logger):
    """Poll for a Gemini review until received or timeout.

    v1-prelaunch retro item #3: delegates to ``triage_common.poll_gemini_review``
    which polls all three GitHub endpoints (reviews, pulls/comments,
    issues/comments). The previous implementation only polled
    ``pulls/comments`` (inline review comments), so it never saw Gemini's
    top-level review (posted via ``pulls/reviews``) and timed out at 5
    minutes on every PR.

    Returns: list of inline comment dicts (may be empty even on success
    when Gemini posts only a top-level review with no inline notes).
    """
    if SHAKEDOWN:
        logger.log("gemini", "SHAKEDOWN_SKIPPED")
        return []

    pr_created_at = _get_pr_created_at(worktree, pr_number)

    def _log(event, **kwargs):
        logger.log("gemini", event, **kwargs)

    comments, status = poll_gemini_review(
        worktree, pr_number, pr_created_at,
        max_wait=GEMINI_MAX_WAIT,
        poll_interval=GEMINI_POLL_INTERVAL,
        shakedown=bool(SHAKEDOWN),
        log_fn=_log,
    )
    # Expose the review status on the logger so _step_gemini can record it.
    # (Stashed on the logger instance to avoid changing the return shape —
    # _step_gemini reads and clears it before writing to result.)
    logger.last_gemini_status = status
    # Capture the latest Gemini top-level review body for the ready-flip
    # gate's clean/declined pattern detection. Best-effort — on any error
    # we fall back to "" (which _gemini_effectively_done treats as "not done").
    logger.last_gemini_review_body = (
        _fetch_latest_gemini_review_body(worktree, pr_number)
        if status == "review_received" else ""
    )
    return comments


def _fetch_latest_gemini_review_body(worktree, pr_number):
    """Return the body of Gemini's most recent top-level review on the PR.

    Returns "" on any error or if no Gemini review is found. Used only by
    the ready-flip gate to detect clean-approval / declined review phrasing.
    """
    repo = get_repo_slug(worktree)
    if not repo:
        return ""
    try:
        proc = subprocess.run(
            ["gh", "api", f"repos/{repo}/pulls/{pr_number}/reviews",
             "--paginate", "--slurp"],
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )
        if proc.returncode != 0:
            return ""
        payload = json.loads(proc.stdout or "[]")
        # --paginate --slurp returns a list of pages; flatten one level.
        if isinstance(payload, list) and payload and isinstance(payload[0], list):
            reviews = [r for page in payload for r in page]
        else:
            reviews = payload or []
    except (subprocess.TimeoutExpired, FileNotFoundError,
            json.JSONDecodeError, ValueError, OSError):
        return ""

    latest = None
    for r in reviews:
        if (r.get("user") or {}).get("login") != GEMINI_BOT_LOGIN:
            continue
        ts = r.get("submitted_at") or r.get("created_at") or ""
        if latest is None or ts > (latest.get("submitted_at")
                                   or latest.get("created_at") or ""):
            latest = r
    return (latest or {}).get("body") or ""


def _build_triage_cmd(full_prompt):
    """Build argv for the gemini-triage claude session.

    Extracted so tests can assert on the cmd shape without mocking the full
    run_triage flow (subprocess, file I/O, polling).
    """
    return [
        "claude", "-p", full_prompt,
        "--verbose", "--output-format", "stream-json",
        "--model", "sonnet",
        "--agent", "gemini-triage",
    ]


def run_triage(worktree, pr_number, comments, logger):
    """Spawn claude -p with gemini-triage agent. Returns triage result list or None."""
    if SHAKEDOWN:
        logger.log("triage", "SHAKEDOWN_SKIPPED")
        return []

    prompt = build_triage_prompt(worktree, pr_number, comments)

    full_prompt = (
        "You are running in headless mode via the pr-monitor background process. "
        "Do not ask clarifying questions — make reasonable decisions and proceed.\n\n"
        + prompt
    )

    env = os.environ.copy()
    env["PPDS_PIPELINE"] = "1"
    env["CLAUDE_PROJECT_DIR"] = str(Path(worktree).resolve())

    # Stage log for triage output
    stage_log_dir = os.path.join(worktree, ".workflow", "stages")
    os.makedirs(stage_log_dir, exist_ok=True)
    stage_jsonl_path = os.path.join(stage_log_dir, "pr-monitor-triage.jsonl")

    cmd = _build_triage_cmd(full_prompt)

    logger.log("triage", "START", comments=len(comments))

    try:
        stage_log_file = open(stage_jsonl_path, "a", encoding="utf-8")  # Append — preserve previous triage rounds
    except OSError as e:
        logger.log("triage", "ERROR", reason=f"Cannot open stage log: {e}")
        return None

    try:
        proc = subprocess.Popen(
            cmd,
            cwd=worktree,
            env=env,
            stdout=stage_log_file,
            stderr=subprocess.DEVNULL,
        )
        proc.wait(timeout=1800)  # 30 min hard ceiling
        exit_code = proc.returncode
    except subprocess.TimeoutExpired:
        proc.terminate()
        try:
            proc.wait(30)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
        logger.log("triage", "TIMEOUT")
        stage_log_file.close()
        return None
    except FileNotFoundError:
        logger.log("triage", "ERROR", reason="claude command not found")
        stage_log_file.close()
        return None
    except OSError as e:
        logger.log("triage", "ERROR", reason=str(e))
        stage_log_file.close()
        return None
    finally:
        if not stage_log_file.closed:
            stage_log_file.close()

    logger.log("triage", "DONE", exit=exit_code)

    if exit_code != 0:
        return None

    # Extract triage results from JSONL output
    results = parse_triage_jsonl(stage_jsonl_path)
    if results is None:
        logger.log("triage", "PARSE_FAILED",
                   reason="No JSON array found in triage output")
    return results


def _detect_base_branch(worktree, pr_number, logger):
    """Detect the PR's base branch via ``gh pr view``. Falls back to 'main'."""
    try:
        result = subprocess.run(
            ["gh", "pr", "view", str(pr_number),
             "--json", "baseRefName", "--jq", ".baseRefName"],
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("rebase", "BASE_DETECT_ERROR", reason=str(e))
        return "main"
    if result.returncode != 0:
        logger.log("rebase", "BASE_DETECT_ERROR",
                   stderr=result.stderr.strip()[:200])
        return "main"
    base = result.stdout.strip()
    if not base:
        logger.log("rebase", "BASE_DETECT_EMPTY")
        return "main"
    return base


def _rebase_source_branch(worktree, pr_number, logger):
    """Rebase source onto the PR's base branch + force-with-lease (meta-retro #6).

    Detects the base branch via ``gh pr view`` (Gemini review #3107696762)
    so PRs targeting non-main branches rebase correctly. Runs
    ``git fetch origin <base>`` -> ``git rebase origin/<base>`` ->
    ``git push --force-with-lease origin HEAD:<branch>`` (explicit refspec
    so push.default=simple accepts it when local name differs from upstream).
    On rebase conflict, aborts cleanly and returns False (caller must NOT
    flip-to-ready). Returns True only on clean rebase+push. SHAKEDOWN
    short-circuits True.
    """
    if SHAKEDOWN:
        logger.log("rebase", "SHAKEDOWN_SKIPPED")
        return True

    def _run(cmd, timeout=60):
        return subprocess.run(
            cmd, cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout,
        )

    base = _detect_base_branch(worktree, pr_number, logger)
    logger.log("rebase", "BASE", branch=base)

    # 1. Fetch origin/<base>
    try:
        fetch = _run(["git", "fetch", "origin", "--", base])
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("rebase", "FETCH_ERROR", reason=str(e), base=base)
        return False
    if fetch.returncode != 0:
        logger.log("rebase", "FETCH_ERROR", base=base,
                   stderr=fetch.stderr.strip()[:200])
        return False

    # 2. Stash workflow state files that are routinely dirty in agent worktrees.
    #    Only stash known-safe paths — if other files are dirty the rebase
    #    should fail so the user is aware of unexpected uncommitted changes.
    _STASHABLE = (".claude/state/", ".workflow/", ".retros/")
    stashed = False
    try:
        status = _run(["git", "status", "--porcelain"])
        if status.returncode == 0 and status.stdout.strip():
            # Exclude untracked (??) files — they don't block rebase and
            # shouldn't poison the safety check for modified files.
            dirty = [l.strip().split(maxsplit=1)[-1].strip('"')
                     for l in status.stdout.strip().splitlines()
                     if l.strip() and not l.strip().startswith("??")]
            safe = all(any(f.startswith(p) for p in _STASHABLE) for f in dirty)
            if safe and dirty:
                stash = _run(["git", "stash", "push", "-m", "pr-monitor-rebase",
                              "--", *dirty])
                stashed = stash.returncode == 0
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass

    def _pop_stash():
        if stashed:
            _run(["git", "stash", "pop"])

    # 3. Rebase onto origin/<base>
    rebase_ref = f"origin/{base}"
    try:
        rebase = _run(["git", "rebase", "--", rebase_ref], timeout=120)
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("rebase", "REBASE_ERROR", reason=str(e), base=base)
        # Best-effort abort in case git is mid-rebase
        try:
            _run(["git", "rebase", "--abort"])
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            pass
        _pop_stash()
        return False
    if rebase.returncode != 0:
        logger.log("rebase", "CONFLICT", base=base,
                   stderr=rebase.stderr.strip()[:200])
        # Abort so the working tree is clean. Do NOT force through.
        try:
            abort = _run(["git", "rebase", "--abort"])
            if abort.returncode != 0:
                logger.log("rebase", "ABORT_ERROR",
                           stderr=abort.stderr.strip()[:200])
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
            logger.log("rebase", "ABORT_ERROR", reason=str(e))
        _pop_stash()
        return False

    # 4. Resolve the local branch name so we can push with an explicit
    # refspec. Bare ``git push`` fails under push.default=simple when the
    # local branch name differs from its upstream tracking ref.
    try:
        rev = _run(["git", "rev-parse", "--abbrev-ref", "HEAD"], timeout=10)
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("rebase", "BRANCH_DETECT_ERROR", reason=str(e))
        _pop_stash()
        return False
    current = rev.stdout.strip()
    if rev.returncode != 0 or not current or current == "HEAD":
        reason = "Detached HEAD" if current == "HEAD" else "Empty output"
        logger.log("rebase", "BRANCH_DETECT_ERROR",
                   stderr=rev.stderr.strip()[:200] if rev.returncode != 0 else reason)
        _pop_stash()
        return False

    # 5. Fetch the feature branch so the local tracking ref is current.
    #    Without this, --force-with-lease rejects the push when another
    #    process (e.g. the triage agent) has pushed to the remote since
    #    this worktree last fetched.
    try:
        fetch_src = _run(["git", "fetch", "origin", "--", current])
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("rebase", "FETCH_SOURCE_ERROR", reason=str(e), branch=current)
        _pop_stash()
        return False
    if fetch_src.returncode != 0:
        logger.log("rebase", "FETCH_SOURCE_ERROR", branch=current,
                   stderr=fetch_src.stderr.strip()[:200])
        _pop_stash()
        return False

    # 6. Push with lease using explicit origin + HEAD:<branch> refspec.
    try:
        push = _run(
            ["git", "push", "--force-with-lease", "origin", f"HEAD:{current}"],
            timeout=60,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("rebase", "PUSH_ERROR", reason=str(e))
        _pop_stash()
        return False
    if push.returncode != 0:
        logger.log("rebase", "PUSH_ERROR", stderr=push.stderr.strip()[:200])
        _pop_stash()
        return False

    _pop_stash()

    logger.log("rebase", "DONE", base=base)
    return True


def mark_pr_ready(worktree, pr_number, logger):
    """Convert draft PR to ready for review."""
    if SHAKEDOWN:
        logger.log("ready", "SHAKEDOWN_SKIPPED")
        return True

    try:
        result = subprocess.run(
            ["gh", "pr", "ready", str(pr_number)],
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )
        if result.returncode == 0:
            logger.log("ready", "DONE")
            return True
        else:
            logger.log("ready", "ERROR", stderr=result.stderr.strip()[:200])
            return False
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("ready", "ERROR", reason=str(e))
        return False


# In-process dedupe set for POSTed replies (meta-retro finding #3).
# Key: (pr_number, comment_id). PR #864 regression: the key used to include
# ``action``, which let the same comment receive two replies across triage
# rounds when the LLM re-classified it (e.g., fixed → dismissed). Once any
# reply is POSTed for a comment in this monitor run, further replies are
# dropped regardless of action. Monitor is per-PR subprocess, so an
# in-memory set suffices — it's reset on every monitor spawn.
_POSTED_REPLY_KEYS: "set[tuple]" = set()


def _reply_body_for(item):
    """Mirror of triage_common.format_reply_body for pre-POST filtering."""
    action = item.get("action", "unknown")
    description = item.get("description", "")
    commit_sha = item.get("commit")
    if action == "fixed" and commit_sha:
        return f"Fixed in {commit_sha} \u2014 {description}"
    if action == "dismissed":
        return f"Not applicable \u2014 {description}"
    return description or "Reviewed."


def post_replies(worktree, pr_number, triage_results, logger):
    """Post threaded replies to Gemini review comments.

    Applies two guards before delegating to _post_replies_common:
      1. Empty-body guard (meta-retro #1) — skip whitespace-only bodies.
      2. Dedupe on (pr, comment_id) (meta-retro #3; PR #864 hardening).

    The dedupe key is recorded only after a successful POST event so
    transient failures (network, API timeout) don't permanently block
    a comment from the reconciliation retry path (PR #865 review).
    """
    def _log_fn(event, **kwargs):
        if event == "POSTED":
            cid = kwargs.get("comment_id")
            if cid is not None:
                _POSTED_REPLY_KEYS.add((pr_number, cid))
        logger.log("replies", event, **kwargs)

    filtered = []
    for item in triage_results or []:
        comment_id = item.get("id")
        action = item.get("action", "unknown")

        body = _reply_body_for(item)
        if not body or not body.strip():
            _log_fn("SKIPPED_EMPTY_BODY", comment_id=comment_id, action=action)
            continue

        key = (pr_number, comment_id)
        if key in _POSTED_REPLY_KEYS:
            _log_fn("SKIPPED_DUPLICATE", comment_id=comment_id, action=action)
            continue
        filtered.append(item)

    if not filtered:
        return

    _post_replies_common(
        worktree, pr_number, filtered, _log_fn,
        shakedown=bool(SHAKEDOWN),
    )


def _reconcile_replies(worktree, pr_number, triage_results, logger, result,
                       triage_iteration):
    """Post replies then reconcile unreplied comments (up to 3 rounds).

    1. AC-92 gate: refuse to post replies if the working tree has uncommitted
       tracked changes post-triage.  A dirty tree means the triage agent made
       fixes without committing them; posting "Fixed in <SHA>" with a stale SHA
       would be misleading.  Sets result status to ``stuck-uncommitted-triage``
       and returns without posting.
    2. Posts replies for the initial triage_results.
    3. Checks for unreplied comments via get_unreplied_comments().
    4. If any remain, re-triages with only the delta comments (up to 3 rounds).
    5. After 3 rounds with still-unreplied, posts "manual review needed".
    """
    MAX_RECONCILIATION_ROUNDS = 3

    # --- AC-92 gate: abort if triage agent left uncommitted changes ---
    # Skip this check in shakedown mode (no real git repo).
    if not SHAKEDOWN:
        try:
            dirty = _get_worktree_dirty_files(worktree)
        except (OSError, subprocess.SubprocessError) as _e:
            # git status unavailable (e.g. not a git repo, git not installed).
            # Fail-open: we can't verify cleanness but we also can't confirm
            # dirtiness. Log and continue — the gate only blocks when we have
            # positive evidence of dirty files.
            logger.log("replies", "AC92_GIT_STATUS_UNAVAILABLE",
                       error=str(_e)[:200], iteration=triage_iteration)
            dirty = []
        if dirty:
            logger.log("replies", "AC92_BLOCKED_DIRTY_WORKTREE",
                       files=",".join(dirty[:10]),
                       iteration=triage_iteration)
            result["status"] = "stuck-uncommitted-triage"
            result["stuck_reason"] = (
                f"AC-92: {len(dirty)} uncommitted file(s) post-triage — "
                "replies refused. Dirty files: " + ", ".join(dirty[:5])
            )
            mark_step(result, f"replies_{triage_iteration}", "blocked_dirty")
            write_result(worktree, result)
            # Signal to run_monitor that we hit a terminal state
            raise _UncommittedTriageError(
                f"AC-92 violation: {len(dirty)} dirty file(s) post-triage: "
                + ", ".join(dirty[:5])
            )

    # Initial reply posting
    try:
        post_replies(worktree, pr_number, triage_results, logger)
        mark_step(result, f"replies_{triage_iteration}", "done")
    except _UncommittedTriageError:
        raise  # propagate up — do not swallow
    except Exception as e:
        logger.log("replies", "EXCEPTION", error=str(e))
        mark_step(result, f"replies_{triage_iteration}", "error")
    write_result(worktree, result)

    # Reconciliation loop
    for recon_round in range(1, MAX_RECONCILIATION_ROUNDS + 1):
        unreplied = get_unreplied_comments(
            worktree, pr_number, shakedown=bool(SHAKEDOWN),
        )
        if not unreplied:
            logger.log("reconcile", "COMPLETE",
                       round=recon_round, iteration=triage_iteration)
            return

        logger.log("reconcile", "DELTA_TRIAGE",
                   round=recon_round, unreplied=len(unreplied),
                   iteration=triage_iteration)

        # Re-triage with only unreplied comments
        delta_results = run_triage(worktree, pr_number, unreplied, logger)
        recon_key = f"reconcile_{triage_iteration}_r{recon_round}"

        if delta_results:
            try:
                post_replies(worktree, pr_number, delta_results, logger)
                mark_step(result, recon_key, "done")
            except Exception as e:
                logger.log("reconcile", "REPLY_ERROR",
                           round=recon_round, error=str(e))
                mark_step(result, recon_key, "error")
        else:
            logger.log("reconcile", "TRIAGE_FAILED", round=recon_round)
            mark_step(result, recon_key, "error")
        write_result(worktree, result)

    # After max rounds, check for any still-unreplied
    still_unreplied = get_unreplied_comments(
        worktree, pr_number, shakedown=bool(SHAKEDOWN),
    )
    if still_unreplied:
        logger.log("reconcile", "MANUAL_REVIEW_NEEDED",
                   remaining=len(still_unreplied),
                   iteration=triage_iteration)
        # Post a PR comment noting manual review is needed
        repo = get_repo_slug(worktree)
        if repo:
            body = (
                f"**Workflow note:** {len(still_unreplied)} inline review "
                f"comment(s) could not be triaged after "
                f"{MAX_RECONCILIATION_ROUNDS} reconciliation rounds. "
                f"Manual review needed."
            )
            try:
                subprocess.run(
                    ["gh", "api", f"repos/{repo}/issues/{pr_number}/comments",
                     "-f", f"body={body}"],
                    cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15,
                )
            except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
                pass
        mark_step(result, f"reconcile_{triage_iteration}", "manual_review")
        write_result(worktree, result)


def _build_retro_cmd(prompt):
    """Build argv for the retro claude session.

    Extracted so tests can assert on the cmd shape without mocking the full
    run_retro flow.
    """
    return [
        "claude", "-p", prompt, "--verbose",
        "--output-format", "stream-json",
        "--model", "sonnet",
    ]


def run_retro(worktree, logger):
    """Run claude -p '/retro' for retrospective."""
    if SHAKEDOWN:
        logger.log("retro", "SHAKEDOWN_SKIPPED")
        return "skipped"

    env = os.environ.copy()
    env["PPDS_PIPELINE"] = "1"
    env["CLAUDE_PROJECT_DIR"] = str(Path(worktree).resolve())

    prompt = (
        "You are running in headless mode via the pr-monitor background process. "
        "Do not ask clarifying questions — make reasonable decisions and proceed.\n\n"
        "/retro"
    )

    stage_log_dir = os.path.join(worktree, ".workflow", "stages")
    os.makedirs(stage_log_dir, exist_ok=True)
    stage_jsonl_path = os.path.join(stage_log_dir, "pr-monitor-retro.jsonl")

    cmd = _build_retro_cmd(prompt)

    logger.log("retro", "START")

    try:
        stage_log_file = open(stage_jsonl_path, "w", encoding="utf-8")
    except OSError as e:
        logger.log("retro", "ERROR", reason=f"Cannot open stage log: {e}")
        return "error"

    try:
        proc = subprocess.Popen(
            cmd,
            cwd=worktree,
            env=env,
            stdout=stage_log_file,
            stderr=subprocess.DEVNULL,
        )
        proc.wait(timeout=600)  # 10 min ceiling
        exit_code = proc.returncode
    except subprocess.TimeoutExpired:
        proc.terminate()
        try:
            proc.wait(30)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
        logger.log("retro", "TIMEOUT")
        stage_log_file.close()
        return "timeout"
    except (FileNotFoundError, OSError) as e:
        logger.log("retro", "ERROR", reason=str(e))
        stage_log_file.close()
        return "error"
    finally:
        if not stage_log_file.closed:
            stage_log_file.close()

    status = "done" if exit_code == 0 else "error"
    logger.log("retro", "DONE", exit=exit_code, status=status)
    return status


def run_notify(worktree, pr_number, logger, message=None):
    """Run the notify hook script.

    When ``message`` is None (default), the notification says the PR is
    ready. Callers on skip paths (CI red, Gemini missing, unreplied
    comments, rebase failure) should pass a specific message so the user
    understands why the PR is still in draft (Gemini review #3107696760).
    """
    notify_script = os.path.join(worktree, ".claude", "hooks", "notify.py")
    if not os.path.exists(notify_script):
        logger.log("notify", "SKIPPED", reason="notify.py not found")
        return

    # Read PR URL from workflow state
    state_path = os.path.join(worktree, ".workflow", "state.json")
    pr_url = None
    try:
        with open(state_path, "r", encoding="utf-8") as f:
            state = json.load(f)
        pr_url = (state.get("pr") or {}).get("url")
    except (json.JSONDecodeError, OSError):
        pass

    msg = message if message else f"PR #{pr_number} is ready"
    cmd = [sys.executable, notify_script,
           "--title", "PR Monitor Complete",
           "--msg", msg]
    if pr_url:
        cmd.extend(["--url", pr_url])

    try:
        result = subprocess.run(
            cmd, cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
        )
        logger.log("notify", "DONE", exit=result.returncode)
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("notify", "ERROR", reason=str(e))


# ---------------------------------------------------------------------------
# Step orchestration
# ---------------------------------------------------------------------------

STEPS = ["ci", "gemini", "triage", "ready", "retro", "notify"]


_SUCCESS_STATUSES = frozenset({"done", "pass"})


def step_completed(result, step_name):
    """Check if a step completed successfully (for --resume).

    Only terminal success statuses count. Failed/skipped/errored steps
    are retried on resume — the conditions that caused the failure may
    have changed (e.g. dirty files cleaned up, new commits pushed).
    """
    step = result.get("steps_completed", {}).get(step_name)
    if step is None:
        return False
    return step.get("status") in _SUCCESS_STATUSES


def mark_step(result, step_name, status):
    """Mark a step as completed in the result dict."""
    result.setdefault("steps_completed", {})[step_name] = {
        "status": status,
        "timestamp": _timestamp(),
    }


_DEREGISTERED_BRANCHES: "set[str]" = set()


def _deregister_inflight(worktree, logger):
    """Deregister the worktree's branch from the in-flight registry.

    Called before terminal notification so the registry reflects "no
    longer working on this" before the user sees the notification.
    Best-effort — failures do not block the notify path.

    Idempotent (I-8): once a branch has been deregistered in this
    process, subsequent calls early-return so multiple terminal paths
    (CI fail, ready-flip, retro/notify) cannot double-deregister.
    """
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=worktree, capture_output=True, text=True, timeout=10,
        )
        branch = (result.stdout or "").strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return
    if not branch:
        return
    if branch in _DEREGISTERED_BRANCHES:
        return
    try:
        subprocess.run(
            [sys.executable, "scripts/inflight-deregister.py", "--branch", branch],
            cwd=worktree, capture_output=True, text=True, timeout=15,
        )
        logger.log("inflight", "DEREGISTERED", branch=branch)
        _DEREGISTERED_BRANCHES.add(branch)
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("inflight", "DEREGISTER_FAILED",
                   branch=branch, error=str(e))


def _notify_terminal(worktree, pr_number, logger, message):
    """Best-effort notification on any terminal non-success state.

    Wraps ``run_notify`` in a try/except so a notify failure cannot
    cascade into the monitor's own error path. Keeps the user informed
    when the monitor aborts (CI fail, CI timeout, exception) — previously
    only the clean-success path fired a notification, so crashes were
    invisible until the user happened to check the PR.

    Also deregisters the worktree's branch from the in-flight registry
    (AC-178) so terminal states clean up automatically without waiting
    for `gh pr merge` or manual `/cleanup`.
    """
    _deregister_inflight(worktree, logger)
    try:
        run_notify(worktree, pr_number, logger, message=message)
    except Exception as notify_err:  # noqa: BLE001 — best-effort
        logger.log("notify", "TERMINAL_NOTIFY_ERROR", error=str(notify_err))


def _check_flake(worktree, failure_log, commit_sha):
    """Classify a CI failure as a flake or a real failure.

    On the first match of any KNOWN_FLAKE_PATTERNS substring in failure_log
    for this commit_sha, returns "flake-rerun" and records the match.
    On a second match for the same commit, returns "flake-second-match" (treated
    as a real failure by the caller). When no pattern matches, returns "real".

    Rerun history is persisted in .workflow/ci-fix-rerun-history.json.
    """
    if not failure_log:
        return "real"

    failure_lower = failure_log.lower()
    matched = any(p.lower() in failure_lower for p in KNOWN_FLAKE_PATTERNS)
    if not matched:
        return "real"

    history_path = os.path.join(worktree, ".workflow", "ci-fix-rerun-history.json")
    try:
        with open(history_path, "r", encoding="utf-8") as f:
            history = json.load(f)
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        history = {}

    if commit_sha in history:
        return "flake-second-match"

    history[commit_sha] = _timestamp()
    try:
        os.makedirs(os.path.dirname(history_path), exist_ok=True)
        with open(history_path, "w", encoding="utf-8") as f:
            json.dump(history, f, indent=2)
    except OSError:
        pass

    return "flake-rerun"


def _dispatch_ci_fix_agent(worktree, pr_number, failure_log, commit_sha,
                            round_num, gemini_comments):
    """Invoke the CI-fix agent via triage_common.dispatch_subagent.

    Returns the agent's decision dict with fields:
        action: "fix" | "escalate"
        files_touched: list[str]
        lines_added: int
        lines_removed: int
        escalation_reason: str | None
        scope_violation: bool
        failure_summary: str

    Falls back to a "escalate" decision on dispatch failure.
    """
    from triage_common import dispatch_subagent

    # Build the diff for scope context (G1)
    diff_output = ""
    try:
        diff_proc = subprocess.run(
            ["git", "diff", "origin/main...HEAD"],
            cwd=worktree, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=30,
        )
        if diff_proc.returncode == 0:
            diff_output = diff_proc.stdout
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass

    # Read branch ACs from workflow state
    branch_acs = ""
    state_path = os.path.join(worktree, ".workflow", "state.json")
    try:
        with open(state_path, "r", encoding="utf-8") as f:
            state = json.load(f)
        branch_acs = state.get("spec", "")
    except (json.JSONDecodeError, OSError):
        pass

    payload = {
        "failure_summary": failure_log[:4000],   # truncated for token budget
        "diff": diff_output[:8000],
        "branch_acs": branch_acs,
        "gemini_comments": gemini_comments or [],
        "constitution": "specs/CONSTITUTION.md",
        "commit_sha": commit_sha,
    }

    result = dispatch_subagent(
        profile_name="ci-fix",
        payload=payload,
        model="sonnet",
        worktree=worktree,
        timeout=1800,
    )

    if result["exit_code"] != 0:
        return {
            "action": "escalate",
            "files_touched": [],
            "lines_added": 0,
            "lines_removed": 0,
            "escalation_reason": f"agent dispatch failed: {result['stderr'][:200]}",
            "scope_violation": False,
            "failure_summary": failure_log[:200],
        }

    # Parse agent output — look for JSON object in stdout
    from triage_common import parse_triage_json_obj
    decision = parse_triage_json_obj(result["stdout"])
    if not isinstance(decision, dict):
        # Fallback: treat missing/malformed output as escalation
        return {
            "action": "escalate",
            "files_touched": [],
            "lines_added": 0,
            "lines_removed": 0,
            "escalation_reason": "agent output did not contain a valid JSON decision",
            "scope_violation": False,
            "failure_summary": failure_log[:200],
        }
    # Coalesce explicit null values to safe defaults (agent may omit or null optional fields)
    if decision.get("files_touched") is None:
        decision["files_touched"] = []
    if decision.get("lines_added") is None:
        decision["lines_added"] = 0
    if decision.get("lines_removed") is None:
        decision["lines_removed"] = 0
    if decision.get("scope_violation") is None:
        decision["scope_violation"] = False
    if not decision.get("failure_summary"):
        decision["failure_summary"] = failure_log[:200]
    return decision


def _check_thrash(worktree, pr_number, round_num, current_decision):
    """Return True if this CI-fix round is repeating the same files as the prior round.

    For round_num >= 2 (AC-186), loads round_num-1's decision file and compares
    files_touched as a set. Order-independent. Round 1 always returns False.

    Decision files are keyed by SHA (the failing commit before each dispatch), so
    the prior round is found by scanning for any file whose "round" and "pr" fields
    match, preventing false thrash detection across concurrent PRs.
    """
    if round_num < 2:
        return False

    prior = _load_prior_decision(worktree, pr_number, round_num - 1)
    if prior is None:
        return False

    current_files = set(current_decision.get("files_touched") or [])
    prior_files = set(prior.get("files_touched") or [])

    return bool(current_files and current_files == prior_files)


def _write_ci_fix_decision(worktree, sha, round_num, payload):
    """Write a CI-fix decision file to .workflow/ci-fix-decisions/<sha>.json.

    Single-write (non-atomic) for v1 per spec note. v2 will add write-temp+rename.
    Returns the Path written.
    """
    decisions_dir = os.path.join(worktree, ".workflow", "ci-fix-decisions")
    path = os.path.join(decisions_dir, f"{sha}.json")
    record = {
        "round": round_num,
        "timestamp": _timestamp(),
        "pr": payload.get("pr"),
        "failure_summary": payload.get("failure_summary", ""),
        "files_touched": payload.get("files_touched", []),
        "lines_added": payload.get("lines_added", 0),
        "lines_removed": payload.get("lines_removed", 0),
        "action": payload.get("action", "fix"),
        "escalation_reason": payload.get("escalation_reason"),
        "scope_violation": bool(payload.get("scope_violation", False)),
    }
    try:
        os.makedirs(decisions_dir, exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(record, f, indent=2)
            f.write("\n")
    except OSError:
        pass  # fail-open; audit trail is best-effort
    return Path(path)


def _load_prior_decision(worktree, pr_number, prior_round):
    """Load the CI-fix decision file for the given PR and round number.

    Scans .workflow/ci-fix-decisions/ for any .json file whose "pr" field
    matches pr_number AND "round" field equals prior_round. Returns the parsed
    dict or None if not found. PR-scoped lookup prevents false thrash detection
    when the decisions directory is shared across concurrent PRs.
    """
    decisions_dir = os.path.join(worktree, ".workflow", "ci-fix-decisions")
    if not os.path.isdir(decisions_dir):
        return None
    for fname in os.listdir(decisions_dir):
        if not fname.endswith(".json"):
            continue
        fpath = os.path.join(decisions_dir, fname)
        try:
            with open(fpath, "r", encoding="utf-8") as f:
                data = json.load(f)
            if data.get("pr") == pr_number and data.get("round") == prior_round:
                return data
        except (json.JSONDecodeError, OSError):
            continue
    return None


def _classify(ci_result, has_gemini_comments):
    """Map (ci_result, has_gemini_comments) to an Action string.

    Decision table (AC-182):
      ci_result="running" / None → "wait"
      ci_result="timeout"        → "terminal_state"
      ci_result="fail"           → "dispatch_ci_fix"
      ci_result="pass", comments → "dispatch_gemini_triage"
      ci_result="pass", no comments → "done"
    """
    if ci_result in ("running", None):
        return "wait"
    if ci_result == "timeout":
        return "terminal_state"
    if ci_result == "fail":
        return "dispatch_ci_fix"
    if ci_result == "pass":
        return "dispatch_gemini_triage" if has_gemini_comments else "done"
    return "wait"


def _build_terminal_notification(pr_number, terminal_state, result,
                                  ci_fix_rounds_used, triage_rounds_used, worktree):
    """Build the terminal notification body per AC-193."""
    ci = result.get("ci_result") or "unknown"
    gemini_raw = "none"
    if result.get("gemini_review_posted"):
        gemini_raw = "triaged" if result.get("triage_summary") else "pending"

    decisions_dir = os.path.join(worktree, ".workflow", "ci-fix-decisions")
    last_decision = "-"
    if os.path.isdir(decisions_dir):
        files = sorted(
            (os.path.join(decisions_dir, f) for f in os.listdir(decisions_dir)
             if f.endswith(".json")),
            key=os.path.getmtime,
        )
        if files:
            last_decision = os.path.relpath(files[-1], worktree)

    return (
        f"PR #{pr_number}: {terminal_state}\n"
        f"  CI: {ci}\n"
        f"  Gemini: {gemini_raw}\n"
        f"  CI-fix rounds used: {ci_fix_rounds_used}/{MAX_CI_FIX_ROUNDS}\n"
        f"  Triage rounds used: {triage_rounds_used}/{MAX_TRIAGE_ITERATIONS}\n"
        f"  Last decision: {last_decision}"
    )


def _parse_github_repo(remote_url):
    """Parse an owner/repo slug from a GitHub remote URL.

    Handles:
      - HTTPS: ``https://github.com/owner/repo.git`` → ``owner/repo``
      - HTTPS (no .git): ``https://github.com/owner/repo`` → ``owner/repo``
      - SSH: ``git@github.com:owner/repo.git`` → ``owner/repo``
      - SSH (no .git): ``git@github.com:owner/repo`` → ``owner/repo``

    GitHub Enterprise and non-standard SSH ports are out of scope.
    Returns ``None`` if the URL cannot be parsed.
    """
    if not remote_url:
        return None
    url = remote_url.strip()

    import re
    # HTTPS pattern: https://github.com/owner/repo[.git]
    https_match = re.match(
        r"https?://[^/]+/([^/]+/[^/]+?)(?:\.git)?$", url
    )
    if https_match:
        return https_match.group(1)

    # SSH pattern: git@github.com:owner/repo[.git]
    ssh_match = re.match(
        r"git@[^:]+:([^/]+/[^/]+?)(?:\.git)?$", url
    )
    if ssh_match:
        return ssh_match.group(1)

    return None


def run_monitor(worktree, pr_number, resume=False, repo=None):
    """Main monitor loop — state-machine orchestrator (v9.1).

    Args:
        worktree: absolute path to the git worktree
        pr_number: pull request number
        resume: if True, skip already-completed steps
        repo: optional ``owner/repo`` slug for ``gh`` calls; resolved from
            ``git remote get-url origin`` if not provided.  Passing it
            explicitly avoids a subprocess call and makes unit tests cleaner.
    """
    workflow_dir = os.path.join(worktree, ".workflow")
    os.makedirs(workflow_dir, exist_ok=True)

    log_path = os.path.join(workflow_dir, "pr-monitor.log")
    logger = Logger(log_path)

    write_pid(worktree)
    atexit.register(cleanup_pid, worktree)

    # Resolve GitHub repo slug once at startup for gh run list / view calls
    # (Bug 2 fix: gh CLI needs --repo when cwd has no git remote context)
    if repo is None:
        try:
            remote_proc = subprocess.run(
                ["git", "remote", "get-url", "origin"],
                cwd=worktree, capture_output=True, text=True,
                encoding="utf-8", errors="replace", timeout=5,
            )
            if remote_proc.returncode == 0:
                repo = _parse_github_repo(remote_proc.stdout.strip())
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            pass
    repo_args = ["--repo", repo] if repo else []

    result = read_result(worktree) if resume else _empty_result()
    result["status"] = "running"
    result["timestamp"] = _timestamp()
    write_result(worktree, result)

    logger.log("monitor", "START", pr=pr_number, resume=resume, pid=os.getpid())

    ci_fix_rounds_used = 0
    triage_rounds_used = 0

    try:
        # --- Phase A: Initial CI poll ---
        if not (resume and step_completed(result, "ci")):
            ci_status = _step_ci(worktree, pr_number, logger)
            result["ci_result"] = ci_status
            mark_step(result, "ci", ci_status)
            write_result(worktree, result)
        else:
            ci_status = result.get("ci_result", "pass")

        # ci-timeout from initial poll → terminal immediately
        if ci_status == "timeout":
            result["status"] = "ci-timeout"
            write_result(worktree, result)
            _notify_terminal(worktree, pr_number, logger,
                             _build_terminal_notification(
                                 pr_number, "ci-timeout", result,
                                 ci_fix_rounds_used, triage_rounds_used, worktree))
            logger.log("monitor", "ABORT", reason="CI timeout")
            logger.close()
            return 1

        # --- Phase B: Initial Gemini poll ---
        comments = []
        if not (resume and step_completed(result, "gemini")):
            comments = _step_gemini(worktree, pr_number, logger, result)
        else:
            logger.log("gemini", "RESUMED")
            comments = poll_gemini_comments(worktree, pr_number, logger)

        # --- Phase C: Classifier loop ---
        HARD_CEILING = MAX_CI_FIX_ROUNDS + MAX_TRIAGE_ITERATIONS + 4
        prev_action = None

        for _iteration in range(HARD_CEILING):
            ci_status = result.get("ci_result", "pass")
            action = _classify(ci_status, bool(comments))

            # Log state transitions (AC bonus)
            if action != prev_action:
                logger.log("monitor", "STATE_TRANSITION",
                           **{"from": prev_action or "start", "to": action})
                prev_action = action

            if action == "terminal_state":
                result["status"] = "ci-timeout"
                write_result(worktree, result)
                _notify_terminal(worktree, pr_number, logger,
                                 _build_terminal_notification(
                                     pr_number, "ci-timeout", result,
                                     ci_fix_rounds_used, triage_rounds_used, worktree))
                logger.close()
                return 1

            elif action == "wait":
                time.sleep(CI_POLL_INTERVAL)
                ci_status = _step_ci(worktree, pr_number, logger)
                result["ci_result"] = ci_status
                write_result(worktree, result)
                continue

            elif action == "dispatch_ci_fix":
                if ci_fix_rounds_used >= MAX_CI_FIX_ROUNDS:
                    result["status"] = "stuck-ci-fix-exhausted"
                    write_result(worktree, result)
                    _notify_terminal(
                        worktree, pr_number, logger,
                        _build_terminal_notification(
                            pr_number, "stuck-ci-fix-exhausted", result,
                            ci_fix_rounds_used, triage_rounds_used, worktree))
                    logger.close()
                    return 1

                ci_fix_rounds_used += 1
                logger.log("monitor", "CI_FIX_DISPATCH",
                           round=ci_fix_rounds_used, of=MAX_CI_FIX_ROUNDS)

                # Get current commit SHA for decision file and flake history
                try:
                    sha_proc = subprocess.run(
                        ["git", "rev-parse", "HEAD"],
                        cwd=worktree, capture_output=True, text=True,
                        encoding="utf-8", errors="replace", timeout=10,
                    )
                    commit_sha = sha_proc.stdout.strip() if sha_proc.returncode == 0 else "unknown"
                except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
                    commit_sha = "unknown"

                # Resolve current branch for run filtering (shared by log fetch + flake rerun)
                current_branch = ""
                try:
                    br_proc = subprocess.run(
                        ["git", "rev-parse", "--abbrev-ref", "HEAD"],
                        cwd=worktree, capture_output=True, text=True,
                        encoding="utf-8", errors="replace", timeout=10,
                    )
                    if br_proc.returncode == 0:
                        current_branch = br_proc.stdout.strip()
                except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
                    pass

                # Fetch the most recent failed-job log via gh run view --log-failed (best-effort)
                failure_log = ""
                try:
                    branch_args = ["--branch", current_branch] if current_branch else []
                    id_proc2 = subprocess.run(
                        ["gh", "run", "list", *repo_args, *branch_args, "--limit", "1",
                         "--json", "databaseId", "--jq", ".[0].databaseId"],
                        cwd=worktree, capture_output=True, text=True,
                        encoding="utf-8", errors="replace", timeout=30,
                    )
                    if id_proc2.returncode == 0:
                        run_id2 = id_proc2.stdout.strip()
                        if run_id2:
                            log_proc = subprocess.run(
                                ["gh", "run", "view", *repo_args, run_id2, "--log-failed"],
                                cwd=worktree, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=60,
                            )
                            if log_proc.returncode == 0:
                                failure_log = log_proc.stdout
                            else:
                                failure_log = f"(log fetch failed: {log_proc.stderr[:200]})"
                    else:
                        failure_log = f"(run list failed: {id_proc2.stderr[:200]})"
                except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
                    pass

                # Flake check (AC-184)
                flake_result = _check_flake(worktree, failure_log, commit_sha)
                logger.log("monitor", "FLAKE_CHECK",
                           result=flake_result, round=ci_fix_rounds_used)

                if flake_result == "flake-rerun":
                    # Trigger a rerun of failed CI checks (no notification, no round consumed)
                    ci_fix_rounds_used -= 1  # don't consume a round for a flake
                    logger.log("monitor", "FLAKE_RERUN",
                               commit=commit_sha, round=ci_fix_rounds_used)
                    try:
                        branch_args = ["--branch", current_branch] if current_branch else []
                        id_proc = subprocess.run(
                            ["gh", "run", "list", *repo_args, *branch_args, "--limit", "1",
                             "--json", "databaseId", "--jq", ".[0].databaseId"],
                            cwd=worktree, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=30,
                        )
                        if id_proc.returncode == 0:
                            run_id = id_proc.stdout.strip()
                            if run_id:
                                subprocess.run(
                                    ["gh", "run", "rerun", *repo_args, run_id, "--failed"],
                                    cwd=worktree, capture_output=True, text=True,
                                    encoding="utf-8", errors="replace", timeout=30,
                                )
                    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
                        pass
                    # Re-poll CI after rerun
                    ci_status = _step_ci(worktree, pr_number, logger,
                                         step_suffix=f"_flake_r{ci_fix_rounds_used}")
                    result["ci_result"] = ci_status
                    write_result(worktree, result)
                    if ci_status in ("pass", "timeout"):
                        if ci_status == "timeout":
                            result["status"] = "ci-timeout"
                            write_result(worktree, result)
                            _notify_terminal(
                                worktree, pr_number, logger,
                                _build_terminal_notification(
                                    pr_number, "ci-timeout", result,
                                    ci_fix_rounds_used, triage_rounds_used, worktree))
                            logger.close()
                            return 1
                        comments = _step_gemini(worktree, pr_number, logger, result,
                                                step_suffix=f"_post_flake_{ci_fix_rounds_used}")
                    continue

                # Real failure — dispatch CI-fix agent (AC-185)
                decision = _dispatch_ci_fix_agent(
                    worktree, pr_number, failure_log, commit_sha,
                    ci_fix_rounds_used, comments,
                )

                # Thrash check (AC-186) — BEFORE writing decision file so
                # _load_prior_decision reads round N-1, not the current round.
                if _check_thrash(worktree, pr_number, ci_fix_rounds_used, decision):
                    logger.log("monitor", "THRASH_DETECTED",
                               round=ci_fix_rounds_used, files=decision.get("files_touched"))
                    result["status"] = "stuck-thrash-detected"
                    write_result(worktree, result)
                    _notify_terminal(
                        worktree, pr_number, logger,
                        _build_terminal_notification(
                            pr_number, "stuck-thrash-detected", result,
                            ci_fix_rounds_used, triage_rounds_used, worktree))
                    logger.close()
                    return 1

                # Write decision file after thrash check (AC-187)
                decision["pr"] = pr_number
                _write_ci_fix_decision(worktree, commit_sha, ci_fix_rounds_used, decision)
                logger.log("monitor", "CI_FIX_DECISION",
                           action=decision.get("action"),
                           files=len(decision.get("files_touched") or []))

                if decision.get("action") == "escalate":
                    logger.log("monitor", "CI_FIX_ESCALATED",
                               reason=decision.get("escalation_reason", ""))
                    result["status"] = "stuck-ci-fix-exhausted"
                    write_result(worktree, result)
                    _notify_terminal(
                        worktree, pr_number, logger,
                        _build_terminal_notification(
                            pr_number, "stuck-ci-fix-exhausted", result,
                            ci_fix_rounds_used, triage_rounds_used, worktree))
                    logger.close()
                    return 1

                # Re-poll CI after agent fix attempt
                ci_status = _step_ci(worktree, pr_number, logger,
                                     step_suffix=f"_ci_fix_r{ci_fix_rounds_used}")
                result["ci_result"] = ci_status
                write_result(worktree, result)

                if ci_status == "timeout":
                    result["status"] = "ci-timeout"
                    write_result(worktree, result)
                    _notify_terminal(
                        worktree, pr_number, logger,
                        _build_terminal_notification(
                            pr_number, "ci-timeout", result,
                            ci_fix_rounds_used, triage_rounds_used, worktree))
                    logger.close()
                    return 1

                # Re-poll Gemini after CI-fix attempt
                comments = _step_gemini(worktree, pr_number, logger, result,
                                        step_suffix=f"_post_ci_fix_{ci_fix_rounds_used}")
                continue

            elif action == "dispatch_gemini_triage":
                if triage_rounds_used >= MAX_TRIAGE_ITERATIONS:
                    result["status"] = "stuck-triage-exhausted"
                    write_result(worktree, result)
                    _notify_terminal(
                        worktree, pr_number, logger,
                        _build_terminal_notification(
                            pr_number, "stuck-triage-exhausted", result,
                            ci_fix_rounds_used, triage_rounds_used, worktree))
                    logger.close()
                    return 1

                triage_rounds_used += 1
                step_key = f"triage_{triage_rounds_used}"
                logger.log("triage", "ITERATION",
                           round=triage_rounds_used, comments=len(comments))

                triage_results = _step_triage(
                    worktree, pr_number, comments, logger, result,
                    step_key, triage_rounds_used,
                )

                if triage_results:
                    try:
                        _reconcile_replies(
                            worktree, pr_number, triage_results, logger, result,
                            triage_rounds_used,
                        )
                    except _UncommittedTriageError:
                        # AC-92: triage agent left uncommitted changes.
                        # result["status"] already set to stuck-uncommitted-triage
                        # by _reconcile_replies. Fire terminal notification and exit.
                        _notify_terminal(
                            worktree, pr_number, logger,
                            _build_terminal_notification(
                                pr_number, "stuck-uncommitted-triage", result,
                                ci_fix_rounds_used, triage_rounds_used, worktree))
                        logger.close()
                        return 1

                # Re-poll CI after triage commits
                ci_recheck_key = f"ci_recheck_{triage_rounds_used}"
                ci_status = _step_ci(worktree, pr_number, logger,
                                     step_suffix=f"_r{triage_rounds_used}")
                result["ci_result"] = ci_status
                mark_step(result, ci_recheck_key, ci_status)
                write_result(worktree, result)

                if ci_status == "timeout":
                    result["status"] = "ci-timeout"
                    write_result(worktree, result)
                    _notify_terminal(
                        worktree, pr_number, logger,
                        _build_terminal_notification(
                            pr_number, "ci-timeout", result,
                            ci_fix_rounds_used, triage_rounds_used, worktree))
                    logger.close()
                    return 1

                # Re-poll Gemini for remaining unreplied comments
                try:
                    comments = get_unreplied_comments(
                        worktree, pr_number, shakedown=bool(SHAKEDOWN),
                    )
                    result["comment_counts"][f"gemini_r{triage_rounds_used}"] = len(comments)
                    mark_step(result, f"gemini_r{triage_rounds_used}", "done")
                except Exception as e:  # noqa: BLE001
                    logger.log("triage", "POLL_ERROR",
                               round=triage_rounds_used, error=str(e))
                    mark_step(result, f"gemini_r{triage_rounds_used}", "error")
                    comments = []
                write_result(worktree, result)
                continue

            elif action == "done":
                break

        # --- Phase D: CI still failing after loop? ---
        if result.get("ci_result") == "fail":
            result["status"] = "stuck-ci-fix-exhausted"
            write_result(worktree, result)
            _notify_terminal(worktree, pr_number, logger,
                             _build_terminal_notification(
                                 pr_number, "stuck-ci-fix-exhausted", result,
                                 ci_fix_rounds_used, triage_rounds_used, worktree))
            logger.log("monitor", "COMPLETE_CI_FAILED", pr=pr_number)
            logger.close()
            return 1

        # --- Phase E: Ready → Retro → Notify ---
        if not (resume and step_completed(result, "ready")):
            _step_ready(worktree, pr_number, logger, result)

        if not (resume and step_completed(result, "retro")):
            _step_retro(worktree, logger, result)

        if not (resume and step_completed(result, "notify")):
            _deregister_inflight(worktree, logger)
            _step_notify(worktree, pr_number, logger, result)

        result["status"] = "ready"
        write_result(worktree, result)
        logger.log("monitor", "COMPLETE", pr=pr_number)
        logger.close()
        return 0

    except _UncommittedTriageError as e:
        # result["status"] already set to stuck-uncommitted-triage by _reconcile_replies.
        # This path is only hit if the inner try/except in dispatch_gemini_triage
        # did not catch it (belt-and-suspenders).
        write_result(worktree, result)
        logger.log("monitor", "ABORT", reason=f"uncommitted-triage: {e}")
        _notify_terminal(
            worktree, pr_number, logger,
            _build_terminal_notification(
                pr_number, "stuck-uncommitted-triage", result,
                ci_fix_rounds_used, triage_rounds_used, worktree,
            ) + f"\n  {e}")
        logger.close()
        return 1

    except _DirtyWorktreeOnReadyFlipError as e:
        # result["status"] already set to stuck-dirty-worktree-on-ready-flip
        write_result(worktree, result)
        logger.log("monitor", "ABORT", reason=f"dirty-worktree-ready-flip: {e}")
        _notify_terminal(
            worktree, pr_number, logger,
            _build_terminal_notification(
                pr_number, "stuck-dirty-worktree-on-ready-flip", result,
                ci_fix_rounds_used, triage_rounds_used, worktree,
            ) + f"\n  {e}")
        logger.close()
        return 1

    except KeyboardInterrupt:
        result["status"] = "interrupted"
        write_result(worktree, result)
        logger.log("monitor", "INTERRUPTED")
        logger.close()
        return 130

    except Exception as e:
        result["status"] = "monitor-crash"
        result["error"] = str(e)
        write_result(worktree, result)
        logger.log("monitor", "ERROR", error=str(e))
        _notify_terminal(worktree, pr_number, logger,
                         _build_terminal_notification(
                             pr_number, "monitor-crash", result,
                             ci_fix_rounds_used, triage_rounds_used, worktree,
                         ) + f"\n  Exception: {type(e).__name__}: {str(e)[:120]}")
        logger.close()
        return 1


# ---------------------------------------------------------------------------
# Individual step wrappers (try/except isolation)
# ---------------------------------------------------------------------------


def _step_ci(worktree, pr_number, logger, step_suffix=""):
    """CI polling step. Returns ci status string."""
    step_name = f"ci{step_suffix}"
    try:
        logger.log(step_name, "BEGIN")
        return poll_ci(worktree, pr_number, logger)
    except Exception as e:
        logger.log(step_name, "EXCEPTION", error=str(e))
        return "error"


def _step_gemini(worktree, pr_number, logger, result, step_suffix=""):
    """Gemini comment polling step. Returns comment list."""
    step_name = f"gemini{step_suffix}"
    try:
        logger.log(step_name, "BEGIN")
        comments = poll_gemini_comments(worktree, pr_number, logger)
        result["comment_counts"][step_name] = len(comments)
        # Capture review-received status for the ready-flip gate
        # (meta-retro finding #2). Any poll that saw a Gemini review
        # flips this flag true for the remainder of the monitor run.
        status = getattr(logger, "last_gemini_status", None)
        if status == "review_received":
            result["gemini_review_posted"] = True
        # Persist the latest review body (may be empty string) so the
        # ready-flip gate can detect clean/declined approval patterns.
        body = getattr(logger, "last_gemini_review_body", "")
        if body:
            result["gemini_review_body"] = body
        mark_step(result, step_name, "done")
        write_result(worktree, result)
        return comments
    except Exception as e:
        logger.log(step_name, "EXCEPTION", error=str(e))
        mark_step(result, step_name, "error")
        write_result(worktree, result)
        return []


def _step_triage(worktree, pr_number, comments, logger, result,
                 step_key, iteration):
    """Triage step. Returns triage results list or None."""
    try:
        triage_results = run_triage(worktree, pr_number, comments, logger)
        summary_entry = {
            "iteration": iteration,
            "comments_in": len(comments),
            "results": triage_results or [],
        }
        result["triage_summary"].append(summary_entry)
        mark_step(result, step_key,
                  "done" if triage_results is not None else "error")
        write_result(worktree, result)
        return triage_results
    except Exception as e:
        logger.log("triage", "EXCEPTION", error=str(e))
        mark_step(result, step_key, "error")
        write_result(worktree, result)
        return None


def _ready_flip_gates(worktree, pr_number, result, logger):
    """Evaluate the gates for auto-ready-flip (meta-retro #2).

    Returns (ok, failing_reasons). ok is True only when all gates pass:
      - CI green, AND
      - Gemini dimension is satisfied — either the latest review body
        matches a clean-approval / declined pattern
        (see ``_gemini_effectively_done``), OR Gemini posted a review, AND
      - no unreplied bot comments remain from ANY reviewer (Gemini,
        CodeQL / github-advanced-security, or other aggregated sources
        returned by ``get_unreplied_comments``). A Gemini clean/declined
        review satisfies the "Gemini reviewed" dimension but does NOT
        bypass unreplied comments from other reviewers — see PR #846
        review (gemini-code-assist HIGH, 2026-04-20).
    """
    reasons = []
    if result.get("ci_result") != "pass":
        reasons.append(f"ci_result={result.get('ci_result')!r}")

    review_body = result.get("gemini_review_body") or ""
    gemini_done_clean = _gemini_effectively_done(review_body)
    if not gemini_done_clean and not result.get("gemini_review_posted"):
        reasons.append("gemini_review_not_posted")

    try:
        unreplied = get_unreplied_comments(
            worktree, pr_number, shakedown=bool(SHAKEDOWN),
        )
    except Exception as e:  # noqa: BLE001 — defensive, don't crash monitor
        logger.log("ready", "UNREPLIED_CHECK_ERROR", error=str(e))
        unreplied = []
        reasons.append("unreplied_check_error")
    # Always enforce the unreplied gate — regardless of Gemini's own
    # state. ``get_unreplied_comments`` aggregates findings across
    # multiple bots, and a clean Gemini review must not mask CodeQL or
    # other unreplied bot findings.
    if unreplied:
        reasons.append(f"unreplied_comments={len(unreplied)}")
    return (not reasons), reasons


def _step_ready(worktree, pr_number, logger, result):
    """Auto-ready-flip gated on 3 conditions (#2), preceded by rebase (#6)."""
    try:
        ok, reasons = _ready_flip_gates(worktree, pr_number, result, logger)
        if not ok:
            logger.log("ready", "SKIPPED_GATES", reasons=",".join(reasons))
            mark_step(result, "ready", "skipped")
            result["ready_skip_reasons"] = reasons
            write_result(worktree, result)
            # Notify the user so they know the PR is still in draft.
            # (Gemini review #3107696760 — use a message that explains why.)
            msg = (f"PR #{pr_number} still in draft: "
                   + ", ".join(reasons))
            try:
                run_notify(worktree, pr_number, logger, message=msg)
            except Exception as e:  # noqa: BLE001
                logger.log("ready", "NOTIFY_ERROR", error=str(e))
            return

        # Bug 4 / belt-and-suspenders: refuse rebase+ready-flip if the worktree
        # has uncommitted tracked changes.  A dirty tree at this point means
        # either AC-92 failed to catch an uncommitted triage, or the user/another
        # process modified the worktree without committing.  Rebase will abort
        # immediately with "cannot rebase: You have unstaged changes" and the
        # abort itself would fail ("no rebase in progress"), leaving the tree in
        # an unknown state.  Hard-stop here instead.
        if not SHAKEDOWN:
            try:
                dirty = _get_worktree_dirty_files(worktree)
            except (OSError, subprocess.SubprocessError) as _e:
                # git status unavailable — fail-open (same policy as AC-92 gate).
                logger.log("ready", "GIT_STATUS_UNAVAILABLE", error=str(_e)[:200])
                dirty = []
            if dirty:
                logger.log("ready", "BLOCKED_DIRTY_WORKTREE",
                           files=",".join(dirty[:10]))
                result["status"] = "stuck-dirty-worktree-on-ready-flip"
                result["stuck_reason"] = (
                    f"{len(dirty)} uncommitted file(s) at ready-flip: "
                    + ", ".join(dirty[:5])
                )
                mark_step(result, "ready", "blocked_dirty")
                write_result(worktree, result)
                raise _DirtyWorktreeOnReadyFlipError(
                    f"{len(dirty)} dirty file(s) at ready-flip: "
                    + ", ".join(dirty[:5])
                )

        # Finding #6: rebase source branch before flipping to ready.
        rebased = _rebase_source_branch(worktree, pr_number, logger)
        if not rebased:
            logger.log("ready", "SKIPPED_REBASE_FAILED")
            mark_step(result, "ready", "rebase_failed")
            result["ready_skip_reasons"] = ["rebase_failed_or_conflict"]
            write_result(worktree, result)
            msg = (f"PR #{pr_number} still in draft: "
                   "rebase failed or conflict")
            try:
                run_notify(worktree, pr_number, logger, message=msg)
            except Exception as e:  # noqa: BLE001
                logger.log("ready", "NOTIFY_ERROR", error=str(e))
            return

        success = mark_pr_ready(worktree, pr_number, logger)
        mark_step(result, "ready", "done" if success else "error")
        write_result(worktree, result)
    except (_DirtyWorktreeOnReadyFlipError, _UncommittedTriageError):
        raise  # propagate terminal-state errors to run_monitor
    except Exception as e:
        logger.log("ready", "EXCEPTION", error=str(e))
        mark_step(result, "ready", "error")
        write_result(worktree, result)


def _step_retro(worktree, logger, result):
    """Run retro step."""
    try:
        retro_status = run_retro(worktree, logger)
        result["retro_status"] = retro_status
        mark_step(result, "retro", retro_status)
        write_result(worktree, result)
    except Exception as e:
        logger.log("retro", "EXCEPTION", error=str(e))
        result["retro_status"] = "error"
        mark_step(result, "retro", "error")
        write_result(worktree, result)


def _step_notify(worktree, pr_number, logger, result):
    """Run notification step."""
    try:
        run_notify(worktree, pr_number, logger)
        mark_step(result, "notify", "done")
        write_result(worktree, result)
    except Exception as e:
        logger.log("notify", "EXCEPTION", error=str(e))
        mark_step(result, "notify", "error")
        write_result(worktree, result)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(
        description="Background PR monitor — CI, Gemini triage, retro, notify"
    )
    parser.add_argument("--worktree", required=True,
                        help="Path to the git worktree")
    parser.add_argument("--pr", required=True, type=int,
                        help="Pull request number")
    parser.add_argument("--resume", action="store_true",
                        help="Resume from last completed step")
    parser.add_argument("--repo",
                        help="GitHub owner/repo slug (e.g. owner/repo). "
                             "Resolved from git remote if omitted.")
    args = parser.parse_args()

    worktree = str(Path(args.worktree).resolve())

    if not os.path.isdir(worktree):
        print(f"ERROR: Worktree path does not exist: {worktree}",
              file=sys.stderr)
        sys.exit(1)

    exit_code = run_monitor(worktree, args.pr, resume=args.resume, repo=args.repo)
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
