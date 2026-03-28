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

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

CI_POLL_INTERVAL = 30       # seconds between CI status checks
CI_MAX_WAIT = 900           # 15 minutes max for CI
GEMINI_POLL_INTERVAL = 30   # seconds between Gemini comment polls
GEMINI_MAX_WAIT = 300       # 5 minutes max for Gemini
GEMINI_STABLE_POLLS = 2     # consecutive polls with same count = stable
MAX_TRIAGE_ITERATIONS = 3   # max triage -> CI re-check cycles

SHAKEDOWN = os.environ.get("PPDS_SHAKEDOWN", "")

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
        self._fh = open(log_path, "a")

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
        with open(path, "r") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return _empty_result()


def write_result(worktree, result):
    path = _result_path(worktree)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        json.dump(result, f, indent=2)
        f.write("\n")


def _empty_result():
    return {
        "status": "pending",
        "steps_completed": {},
        "ci_result": None,
        "comment_counts": {},
        "triage_summary": [],
        "retro_status": None,
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
    with open(path, "w") as f:
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
    if SHAKEDOWN:
        return "test-owner/test-repo"
    try:
        result = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner",
             "--jq", ".nameWithOwner"],
            cwd=worktree, capture_output=True, text=True, timeout=10,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    return None


def poll_ci(worktree, pr_number, logger):
    """Poll CI checks until all pass, any fail, or timeout.

    Returns: "pass", "fail", or "timeout"
    """
    if SHAKEDOWN:
        logger.log("ci", "SHAKEDOWN_SKIPPED")
        return "pass"

    start = time.time()
    while time.time() - start < CI_MAX_WAIT:
        try:
            result = subprocess.run(
                ["gh", "pr", "checks", str(pr_number),
                 "--json", "name,state,conclusion"],
                cwd=worktree, capture_output=True, text=True, timeout=30,
            )
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
            logger.log("ci", "POLL_ERROR", error=str(e))
            time.sleep(CI_POLL_INTERVAL)
            continue

        if result.returncode != 0:
            logger.log("ci", "POLL_ERROR", stderr=result.stderr.strip()[:200])
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
            time.sleep(CI_POLL_INTERVAL)
            continue

        total = len(checks)
        completed = [c for c in checks if c.get("state") == "COMPLETED"]
        failed = [c for c in completed
                  if c.get("conclusion") not in ("SUCCESS", "NEUTRAL", "SKIPPED")]
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


def poll_gemini_comments(worktree, pr_number, logger):
    """Poll for Gemini review comments until stable count or timeout.

    Returns: list of comment dicts (may be empty).
    """
    if SHAKEDOWN:
        logger.log("gemini", "SHAKEDOWN_SKIPPED")
        return []

    repo = get_repo_slug(worktree)
    if not repo:
        logger.log("gemini", "ERROR", reason="Cannot determine repo slug")
        return []

    start = time.time()
    last_count = -1
    stable_polls = 0

    while time.time() - start < GEMINI_MAX_WAIT:
        try:
            result = subprocess.run(
                ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
                 "--jq", "length"],
                cwd=worktree, capture_output=True, text=True, timeout=15,
            )
            count = int(result.stdout.strip()) if result.returncode == 0 else 0
        except (subprocess.TimeoutExpired, FileNotFoundError, ValueError, OSError):
            count = 0

        elapsed = int(time.time() - start)
        logger.log("gemini", "POLL", elapsed=f"{elapsed}s", comments=count)

        if count == last_count:
            stable_polls += 1
            if stable_polls >= GEMINI_STABLE_POLLS:
                logger.log("gemini", "STABLE", comments=count)
                break
        else:
            stable_polls = 0

        last_count = count
        time.sleep(GEMINI_POLL_INTERVAL)

    if last_count <= 0:
        logger.log("gemini", "NO_COMMENTS")
        return []

    # Fetch full comment data
    try:
        result = subprocess.run(
            ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
             "--jq", "[.[] | {id, user: .user.login, path, line, body}]"],
            cwd=worktree, capture_output=True, text=True, timeout=15,
        )
        if result.returncode == 0:
            return json.loads(result.stdout.strip())
    except (subprocess.TimeoutExpired, FileNotFoundError,
            json.JSONDecodeError, OSError):
        pass
    return []


def run_triage(worktree, pr_number, comments, logger):
    """Spawn claude -p with gemini-triage agent. Returns triage result list or None."""
    if SHAKEDOWN:
        logger.log("triage", "SHAKEDOWN_SKIPPED")
        return []

    # Read spec from workflow state for context
    state_path = os.path.join(worktree, ".workflow", "state.json")
    spec_path = ""
    try:
        with open(state_path, "r") as f:
            state = json.load(f)
        spec_path = state.get("spec", "")
    except (json.JSONDecodeError, OSError):
        pass

    prompt = (
        f"Triage these Gemini review comments on PR #{pr_number}.\n\n"
        f"Spec (read for design rationale): {spec_path}\n\n"
        f"Comments:\n{json.dumps(comments, indent=2)}\n\n"
        "For each comment:\n"
        "1. Read the referenced file at the specified line\n"
        "2. Evaluate: is this a valid finding?\n"
        "3. If valid: fix the code and commit\n"
        "4. If invalid: compose a brief dismissal rationale\n\n"
        "After all comments, push fixes and output this JSON:\n"
        '[{"id": <id>, "action": "fixed"|"dismissed", '
        '"description": "...", "commit": "<sha>"|null}]'
    )

    full_prompt = (
        "You are running in headless mode via the pr-monitor background process. "
        "Do not ask clarifying questions — make reasonable decisions and proceed.\n\n"
        + prompt
    )

    env = os.environ.copy()
    env["MSYS_NO_PATHCONV"] = "1"
    env["PPDS_PIPELINE"] = "1"
    env["CLAUDE_PROJECT_DIR"] = str(Path(worktree).resolve())

    # Stage log for triage output
    stage_log_dir = os.path.join(worktree, ".workflow", "stages")
    os.makedirs(stage_log_dir, exist_ok=True)
    stage_jsonl_path = os.path.join(stage_log_dir, "pr-monitor-triage.jsonl")

    cmd = [
        "claude", "-p", full_prompt,
        "--verbose", "--output-format", "stream-json",
        "--agent", "gemini-triage",
    ]

    logger.log("triage", "START", comments=len(comments))

    try:
        stage_log_file = open(stage_jsonl_path, "a")  # Append — preserve previous triage rounds
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
    return _parse_triage_output(stage_jsonl_path, logger)


def _parse_triage_output(jsonl_path, logger):
    """Parse structured triage JSON from stage JSONL output."""
    # First extract text from JSONL events
    text_parts = []
    try:
        with open(jsonl_path, "r", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if event.get("type") == "result":
                    result_text = event.get("result", "")
                    if result_text:
                        text_parts.append(result_text)
                elif event.get("type") == "assistant":
                    content = event.get("message", {}).get("content", [])
                    for block in content:
                        if block.get("type") == "text":
                            text = block.get("text", "")
                            if text:
                                text_parts.append(text)
    except OSError:
        return None

    combined = "\n".join(text_parts)
    # Find JSON array using raw_decode
    last_bracket = combined.rfind("[")
    if last_bracket != -1:
        try:
            decoder = json.JSONDecoder()
            obj, _ = decoder.raw_decode(combined[last_bracket:])
            if isinstance(obj, list):
                return obj
        except (json.JSONDecodeError, ValueError):
            pass

    logger.log("triage", "PARSE_FAILED",
               reason="No JSON array found in triage output")
    return None


def mark_pr_ready(worktree, pr_number, logger):
    """Convert draft PR to ready for review."""
    if SHAKEDOWN:
        logger.log("ready", "SHAKEDOWN_SKIPPED")
        return True

    try:
        result = subprocess.run(
            ["gh", "pr", "ready", str(pr_number)],
            cwd=worktree, capture_output=True, text=True, timeout=30,
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


def post_replies(worktree, pr_number, triage_results, logger):
    """Post threaded replies to Gemini review comments from triage results."""
    if SHAKEDOWN:
        logger.log("replies", "SHAKEDOWN_SKIPPED")
        return

    repo = get_repo_slug(worktree)
    if not repo:
        logger.log("replies", "ERROR", reason="Cannot determine repo slug")
        return

    for item in triage_results:
        comment_id = item.get("id")
        if not comment_id:
            logger.log("replies", "SKIPPED", reason="missing comment id")
            continue
        action = item.get("action", "unknown")
        description = item.get("description", "")
        commit_sha = item.get("commit")

        if action == "fixed" and commit_sha:
            body = f"Fixed in {commit_sha} — {description}"
        elif action == "dismissed":
            body = f"Not applicable — {description}"
        else:
            body = description or "Reviewed."

        try:
            result = subprocess.run(
                ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
                 "-F", f"in_reply_to={comment_id}", "-f", f"body={body}"],
                cwd=worktree, capture_output=True, text=True, timeout=15,
            )
            if result.returncode != 0:
                logger.log("replies", "FAILED", comment_id=comment_id,
                           error=result.stderr.strip()[:100])
            else:
                logger.log("replies", "POSTED", comment_id=comment_id, action=action)
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            logger.log("replies", "FAILED", comment_id=comment_id)


def run_retro(worktree, logger):
    """Run claude -p '/retro' for retrospective."""
    if SHAKEDOWN:
        logger.log("retro", "SHAKEDOWN_SKIPPED")
        return "skipped"

    env = os.environ.copy()
    env["MSYS_NO_PATHCONV"] = "1"
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

    cmd = ["claude", "-p", prompt, "--verbose",
           "--output-format", "stream-json"]

    logger.log("retro", "START")

    try:
        stage_log_file = open(stage_jsonl_path, "w")
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


def run_notify(worktree, pr_number, logger):
    """Run the notify hook script."""
    notify_script = os.path.join(worktree, ".claude", "hooks", "notify.py")
    if not os.path.exists(notify_script):
        logger.log("notify", "SKIPPED", reason="notify.py not found")
        return

    # Read PR URL from workflow state
    state_path = os.path.join(worktree, ".workflow", "state.json")
    pr_url = None
    try:
        with open(state_path, "r") as f:
            state = json.load(f)
        pr_url = (state.get("pr") or {}).get("url")
    except (json.JSONDecodeError, OSError):
        pass

    cmd = [sys.executable, notify_script,
           "--title", "PR Monitor Complete",
           "--msg", f"PR #{pr_number} is ready"]
    if pr_url:
        cmd.extend(["--url", pr_url])

    try:
        result = subprocess.run(
            cmd, cwd=worktree, capture_output=True, text=True, timeout=30,
        )
        logger.log("notify", "DONE", exit=result.returncode)
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        logger.log("notify", "ERROR", reason=str(e))


# ---------------------------------------------------------------------------
# Step orchestration
# ---------------------------------------------------------------------------

STEPS = ["ci", "gemini", "triage", "ready", "retro", "notify"]


def step_completed(result, step_name):
    """Check if a step was already completed (for --resume)."""
    return result.get("steps_completed", {}).get(step_name) is not None


def mark_step(result, step_name, status):
    """Mark a step as completed in the result dict."""
    result.setdefault("steps_completed", {})[step_name] = {
        "status": status,
        "timestamp": _timestamp(),
    }


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
                _step_notify(worktree, pr_number, logger, result)
                write_result(worktree, result)
                logger.log("monitor", "ABORT", reason="CI failed")
                logger.close()
                return 1

            if ci_status == "timeout":
                result["status"] = "ci_timeout"
                write_result(worktree, result)
                logger.log("monitor", "ABORT", reason="CI timeout")
                logger.close()
                return 1

        # ---- Step 2: Gemini comment polling ----
        comments = []
        if not (resume and step_completed(result, "gemini")):
            comments = _step_gemini(worktree, pr_number, logger, result)
        else:
            # Resumed — read last known comment count
            logger.log("gemini", "RESUMED")

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

            # Post threaded replies to Gemini comments
            if triage_results:
                try:
                    post_replies(worktree, pr_number, triage_results, logger)
                    mark_step(result, f"replies_{triage_iteration}", "done")
                except Exception as e:
                    logger.log("replies", "EXCEPTION", error=str(e))
                    mark_step(result, f"replies_{triage_iteration}", "error")
                write_result(worktree, result)

            # Re-poll CI after triage commits
            ci_recheck_key = f"ci_recheck_{triage_iteration}"
            ci_status = _step_ci(worktree, pr_number, logger,
                                 step_suffix=f"_r{triage_iteration}")
            result["ci_result"] = ci_status
            mark_step(result, ci_recheck_key, ci_status)
            write_result(worktree, result)

            if ci_status == "fail":
                result["status"] = "ci_failed"
                _step_notify(worktree, pr_number, logger, result)
                write_result(worktree, result)
                logger.log("monitor", "ABORT",
                           reason=f"CI failed after triage round {triage_iteration}")
                logger.close()
                return 1

            if ci_status == "timeout":
                result["status"] = "ci_timeout"
                write_result(worktree, result)
                logger.log("monitor", "ABORT",
                           reason=f"CI timeout after triage round {triage_iteration}")
                logger.close()
                return 1

            # Re-poll Gemini for new comments after triage
            comments = _step_gemini(worktree, pr_number, logger, result,
                                    step_suffix=f"_r{triage_iteration}")
            inline_count = len(comments)

        # ---- Step 4: Mark PR ready ----
        if not (resume and step_completed(result, "ready")):
            _step_ready(worktree, pr_number, logger, result)

        # ---- Step 5: Retro ----
        if not (resume and step_completed(result, "retro")):
            _step_retro(worktree, logger, result)

        # ---- Step 6: Notify ----
        if not (resume and step_completed(result, "notify")):
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


def _step_ready(worktree, pr_number, logger, result):
    """Mark PR as ready for review."""
    try:
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
