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

    1. Posts replies for the initial triage_results.
    2. Checks for unreplied comments via get_unreplied_comments().
    3. If any remain, re-triages with only the delta comments (up to 3 rounds).
    4. After 3 rounds with still-unreplied, posts "manual review needed".
    """
    MAX_RECONCILIATION_ROUNDS = 3

    # Initial reply posting
    try:
        post_replies(worktree, pr_number, triage_results, logger)
        mark_step(result, f"replies_{triage_iteration}", "done")
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


def run_monitor(worktree, pr_number, resume=False):
    """Main monitor loop — run all steps in sequence."""
    workflow_dir = os.path.join(worktree, ".workflow")
    os.makedirs(workflow_dir, exist_ok=True)

    log_path = os.path.join(workflow_dir, "pr-monitor.log")
    logger = Logger(log_path)

    # PID management
    write_pid(worktree)
    atexit.register(cleanup_pid, worktree)

    result = read_result(worktree) if resume else _empty_result()
    result["status"] = "running"
    result["timestamp"] = _timestamp()
    write_result(worktree, result)

    logger.log("monitor", "START",
               pr=pr_number, resume=resume, pid=os.getpid())

    triage_iteration = 0

    try:
        # ---- Step 1: CI polling ----
        if not (resume and step_completed(result, "ci")):
            ci_status = _step_ci(worktree, pr_number, logger)
            result["ci_result"] = ci_status
            mark_step(result, "ci", ci_status)
            write_result(worktree, result)

            if ci_status == "fail":
                result["status"] = "ci_failed"
                write_result(worktree, result)
                _notify_terminal(worktree, pr_number, logger,
                                 f"PR #{pr_number} CI failed — continuing to triage")
                logger.log("monitor", "CI_FAILED_CONTINUE",
                           reason="CI failed, continuing to Gemini triage (AC-108)")

            if ci_status == "timeout":
                result["status"] = "ci_timeout"
                write_result(worktree, result)
                _notify_terminal(worktree, pr_number, logger,
                                 f"PR #{pr_number} CI timed out after "
                                 f"{CI_MAX_WAIT // 60} min")
                logger.log("monitor", "ABORT", reason="CI timeout")
                logger.close()
                return 1

        # ---- Step 2: Gemini comment polling ----
        comments = []
        if not (resume and step_completed(result, "gemini")):
            comments = _step_gemini(worktree, pr_number, logger, result)
        else:
            # Resumed — re-fetch comments so the triage loop can resume
            # pending iterations (otherwise inline_count stays 0 and the
            # loop is skipped entirely).
            logger.log("gemini", "RESUMED")
            comments = poll_gemini_comments(worktree, pr_number, logger)

        # ---- Step 3: Triage loop (if inline comments) ----
        inline_count = len(comments)
        while inline_count > 0 and triage_iteration < MAX_TRIAGE_ITERATIONS:
            triage_iteration += 1
            step_key = f"triage_{triage_iteration}"

            if resume and step_completed(result, step_key):
                logger.log("triage", "RESUMED", iteration=triage_iteration)
                # Re-poll to get fresh comments for next iteration;
                # without this, the loop would reuse stale comments/inline_count.
                comments = _step_gemini(worktree, pr_number, logger, result,
                                        step_suffix=f"_r{triage_iteration}")
                inline_count = len(comments)
                continue

            logger.log("triage", "ITERATION",
                       round=triage_iteration, comments=inline_count)

            triage_results = _step_triage(
                worktree, pr_number, comments, logger, result,
                step_key, triage_iteration,
            )

            # Post threaded replies + reconciliation loop (AC-140)
            if triage_results:
                _reconcile_replies(
                    worktree, pr_number, triage_results, logger, result,
                    triage_iteration,
                )

            # Re-poll CI after triage commits
            ci_recheck_key = f"ci_recheck_{triage_iteration}"
            ci_status = _step_ci(worktree, pr_number, logger,
                                 step_suffix=f"_r{triage_iteration}")
            result["ci_result"] = ci_status
            mark_step(result, ci_recheck_key, ci_status)
            write_result(worktree, result)

            if ci_status == "fail":
                result["status"] = "ci_failed"
                write_result(worktree, result)
                _notify_terminal(
                    worktree, pr_number, logger,
                    f"PR #{pr_number} CI failed after triage round "
                    f"{triage_iteration}")
                logger.log("monitor", "CI_FAILED_CONTINUE",
                           reason=f"CI failed after triage round "
                           f"{triage_iteration}, exiting triage loop")
                break

            if ci_status == "timeout":
                result["status"] = "ci_timeout"
                write_result(worktree, result)
                _notify_terminal(worktree, pr_number, logger,
                                 f"PR #{pr_number} CI timed out after triage "
                                 f"round {triage_iteration}")
                logger.log("monitor", "ABORT",
                           reason=f"CI timeout after triage round {triage_iteration}")
                logger.close()
                return 1

            # Re-poll Gemini for new comments after triage. PR #864
            # double-reply regression: the pulls/comments endpoint returns
            # every Gemini-authored comment, including ones we already
            # replied to, so the loop used to re-ingest them and post a
            # second reply (often with a different LLM-classified action).
            # On rounds 2+, restrict to genuinely unreplied bot comments.
            # Fail-open on transient errors — don't abort the monitor run
            # (matches _step_gemini and _ready_flip_gates patterns).
            poll_step = f"gemini_r{triage_iteration}"
            try:
                comments = get_unreplied_comments(
                    worktree, pr_number, shakedown=bool(SHAKEDOWN),
                )
                result["comment_counts"][poll_step] = len(comments)
                mark_step(result, poll_step, "done")
            except Exception as e:  # noqa: BLE001 — fail-open per repo pattern
                logger.log("triage", "POLL_ERROR",
                           round=triage_iteration, error=str(e))
                mark_step(result, poll_step, "error")
                comments = []
            write_result(worktree, result)
            logger.log("triage", "POLL_UNREPLIED",
                       round=triage_iteration, comments=len(comments))
            inline_count = len(comments)

        # Triage-complete notification when CI was failing (#860)
        if result.get("ci_result") == "fail" and result.get("triage_summary"):
            total = sum(len(s.get("results", []))
                        for s in result["triage_summary"])
            _notify_terminal(
                worktree, pr_number, logger,
                f"PR #{pr_number} triage complete — "
                f"{total} item(s) triaged (CI still failing)")

        # ---- Step 4: Mark PR ready ----
        if not (resume and step_completed(result, "ready")):
            _step_ready(worktree, pr_number, logger, result)

        # ---- Step 5: Retro ----
        if not (resume and step_completed(result, "retro")):
            _step_retro(worktree, logger, result)

        # Exit early on CI failure — _step_ready already sent a descriptive
        # notification explaining why the PR remains in draft. Skipping
        # _step_notify avoids a misleading "PR is ready" message right
        # before the monitor exits with an error code.
        if result.get("ci_result") == "fail":
            result["status"] = "ci_failed"
            write_result(worktree, result)
            logger.log("monitor", "COMPLETE_CI_FAILED", pr=pr_number)
            logger.close()
            return 1

        # ---- Step 6: Notify ----
        if not (resume and step_completed(result, "notify")):
            _deregister_inflight(worktree, logger)
            _step_notify(worktree, pr_number, logger, result)

        result["status"] = "complete"
        write_result(worktree, result)
        logger.log("monitor", "COMPLETE", pr=pr_number)
        logger.close()
        return 0

    except KeyboardInterrupt:
        result["status"] = "interrupted"
        write_result(worktree, result)
        logger.log("monitor", "INTERRUPTED")
        logger.close()
        return 130

    except Exception as e:
        result["status"] = "error"
        result["error"] = str(e)
        write_result(worktree, result)
        logger.log("monitor", "ERROR", error=str(e))
        _notify_terminal(worktree, pr_number, logger,
                         f"PR #{pr_number} monitor crashed: "
                         f"{type(e).__name__}: {str(e)[:120]}")
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
    args = parser.parse_args()

    worktree = str(Path(args.worktree).resolve())

    if not os.path.isdir(worktree):
        print(f"ERROR: Worktree path does not exist: {worktree}",
              file=sys.stderr)
        sys.exit(1)

    exit_code = run_monitor(worktree, args.pr, resume=args.resume)
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
