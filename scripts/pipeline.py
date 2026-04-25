#!/usr/bin/env python3
"""
Deterministic pipeline orchestrator for PPDS development workflow.

Runs /implement → /gates → /verify → /review → /converge → /pr → /retro
as sequential `claude -p` sessions. Each step gets a fresh context window.
The script — not the AI — decides what runs next.

Usage:
    python scripts/pipeline.py --plan <plan-path> [options]
    python scripts/pipeline.py --spec <spec-path> --branch <name> [options]

Options:
    --plan <path>       Path to implementation plan file
    --spec <path>       Path to spec file (implement generates plan from spec)
    --branch <name>     Full branch name (required when no plan)
    --from <step>       Resume from a specific step
    --resume            Auto-resume from last completed stage in pipeline.log
    --no-retro          Skip the post-PR retro step
    --max-converge <n>  Max converge rounds (default: 3)
    --max-stage-seconds N
                        Override HARD_CEILING for this run (default: 7200s).
                        Positive integer. Applies to the claude heartbeat
                        loop, the pr stage, and the pr_monitor subprocess.
    --worktree <path>   Use existing worktree instead of creating one
    --issue <N>         GitHub issue number(s) this work closes (repeatable)
    --dry-run           Run orchestration logic without invoking claude -p
"""
import argparse
import json
import os
import shutil
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
    parse_triage_stage_log,
    poll_gemini_review,
    post_replies as _post_replies_common,
)

STAGES = [
    "worktree",
    "implement",
    "gates",
    "verify",
    "qa",
    "review",
    "converge",
    "pr",
    "retro",
]

HEADLESS_PREAMBLE = (
    "You are running in headless mode via the pipeline orchestrator. "
    "Do not ask clarifying questions — make reasonable decisions and proceed. "
    "Do not suggest skipping any steps in the process.\n\n"
)

# Expected outcomes per stage for verification
STAGE_OUTCOMES = {
    "implement": "new_commits",
    "gates": "gates_passed",
    "verify": "verify_timestamp",
    "qa": "qa_done",
    "review": "review_results",
    "pr": "pr_url",
}

STALL_LIMIT = 5    # consecutive idle heartbeats before kill (5 min at 60s interval)
HARD_CEILING = 7200  # max duration per AI invocation in seconds (120 min, overridable via --max-stage-seconds). Applied per run_claude call; stages with retries or converge rounds can accumulate multiple invocations.


class PipelineFailure(Exception):
    """Raised by _pipeline_fail to exit the stage loop without sys.exit."""
    pass


def timestamp():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def local_time():
    return datetime.now().strftime("%H:%M:%S")


def log(logger, stage, event, **extra):
    """Write structured log entry to pipeline.log and print to console."""
    parts = [f"{timestamp()} [{stage}] {event}"]
    for k, v in extra.items():
        parts.append(f"{k}={v}")
    line = " ".join(parts)
    logger.write(line + "\n")
    logger.flush()
    console_parts = [f"[{local_time()}] {stage}: {event}"]
    for k, v in extra.items():
        console_parts.append(f"{k}={v}")
    print(" ".join(console_parts), file=sys.stderr)


def open_logger(log_path, mode="a"):
    """Open log file. Separates open from use to allow close/reopen between stages."""
    os.makedirs(os.path.dirname(log_path), exist_ok=True)
    return open(log_path, mode)


def read_state(worktree_path):
    """Read .workflow/state.json from the worktree."""
    state_path = os.path.join(worktree_path, ".workflow", "state.json")
    if not os.path.exists(state_path):
        return {}
    try:
        with open(state_path, "r") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError):
        return {}


def get_commit_count(worktree_path):
    """Get number of commits ahead of main."""
    try:
        result = subprocess.run(
            ["git", "rev-list", "--count", "origin/main..HEAD"],
            cwd=worktree_path,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            return int(result.stdout.strip())
    except (subprocess.TimeoutExpired, FileNotFoundError, ValueError):
        pass
    return 0


def verify_outcome(worktree_path, stage, pre_commit_count):
    """Verify that a stage produced its expected outcome. Returns True if OK."""
    expected = STAGE_OUTCOMES.get(stage)
    if not expected:
        return True  # No outcome check for this stage

    state = read_state(worktree_path)

    if expected == "new_commits":
        return get_commit_count(worktree_path) > pre_commit_count
    elif expected == "gates_passed":
        gates = state.get("gates", {})
        return bool(gates.get("passed"))
    elif expected == "verify_timestamp":
        verify = state.get("verify", {})
        return any(v for v in verify.values() if v)
    elif expected == "qa_done":
        qa = state.get("qa", {})
        return any(v for v in qa.values() if v)
    elif expected == "review_results":
        review = state.get("review", {})
        return bool(review.get("passed")) or bool(review.get("findings"))
    elif expected == "pr_url":
        pr = state.get("pr", {})
        return bool(pr.get("url"))
    return True


def should_converge(state):
    """Decide whether converge stage should run based on review state.

    Returns (should_run, reason) tuple. Extracted from main() for testability.
    """
    review = state.get("review", {})
    review_passed = review.get("passed", False)
    review_findings = review.get("findings", 0)
    try:
        review_findings = int(review_findings)
    except (TypeError, ValueError):
        review_findings = 0

    if review_passed and review_findings == 0:
        return False, "review passed with zero findings"
    elif review_passed and review_findings > 0:
        return True, f"review passed with {review_findings} findings"
    else:
        return True, "review FAIL"


def find_last_completed_stage(log_path):
    """Parse pipeline.log to find the last completed stage for --resume."""
    if not os.path.exists(log_path):
        return None
    last_done = None
    try:
        with open(log_path, "r") as f:
            for line in f:
                if "] DONE" in line:
                    # Extract stage name from "[stage] DONE"
                    try:
                        bracket_start = line.index("[") + 1
                        bracket_end = line.index("]")
                    except ValueError:
                        continue  # Malformed log line — skip
                    stage_name = line[bracket_start:bracket_end]
                    # Normalize converge round names back to base stage
                    if stage_name.startswith("converge"):
                        stage_name = "converge"
                    elif stage_name.startswith("gates-r"):
                        stage_name = "converge"
                    elif stage_name.startswith("verify-r"):
                        stage_name = "converge"
                    elif stage_name.startswith("qa-r"):
                        stage_name = "converge"
                    elif stage_name.startswith("review-r"):
                        stage_name = "converge"
                    if stage_name in STAGES:
                        last_done = stage_name
    except OSError:
        return None
    return last_done


def is_pid_alive(pid):
    """Check if a process with the given PID is alive. Cross-platform."""
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True  # Process exists but we can't signal it
    except OSError:
        return False


def acquire_lock(lock_path, logger):
    """Acquire pipeline lock. Returns True if acquired, False if conflict."""
    if os.path.exists(lock_path):
        try:
            with open(lock_path, "r") as f:
                existing_pid = int(f.read().strip())
            if is_pid_alive(existing_pid):
                print(
                    f"ERROR: Pipeline already running (PID {existing_pid}). "
                    f"Delete {lock_path} if this is stale.",
                    file=sys.stderr,
                )
                return False
            else:
                log(logger, "pipeline", "STALE_LOCK", pid=existing_pid)
                os.remove(lock_path)
        except (ValueError, OSError):
            os.remove(lock_path)  # Corrupted lock file

    os.makedirs(os.path.dirname(lock_path), exist_ok=True)
    with open(lock_path, "w") as f:
        f.write(str(os.getpid()))
    return True


def release_lock(lock_path):
    """Release pipeline lock."""
    try:
        os.remove(lock_path)
    except OSError:
        pass


def get_child_process_count(pid):
    """Count active child processes of a given PID."""
    try:
        if sys.platform == "win32":
            result = subprocess.run(
                ["wmic", "process", "where", f"ParentProcessId={pid}",
                 "get", "ProcessId"],
                capture_output=True, text=True, timeout=5,
                encoding="utf-8", errors="replace",
            )
            if result.returncode == 0:
                lines = [l.strip() for l in result.stdout.strip().splitlines()
                         if l.strip().isdigit()]
                return len(lines)
        else:
            result = subprocess.run(
                ["pgrep", "-P", str(pid)],
                capture_output=True, text=True, timeout=5,
                encoding="utf-8", errors="replace",
            )
            if result.returncode == 0:
                return len(result.stdout.strip().splitlines())
    except (subprocess.TimeoutExpired, FileNotFoundError, ValueError, OSError):
        pass
    return 0


def classify_activity(current_size, last_size, git_changes, last_git_changes,
                      commits, last_commits, consecutive_idle, *,
                      has_children=False):
    """Classify heartbeat activity based on multi-signal detection.

    Returns (activity_string, updated_consecutive_idle).
    """
    output_grew = current_size > last_size
    git_grew = git_changes > last_git_changes
    commits_grew = commits > last_commits

    if output_grew or git_grew or commits_grew or has_children:
        return "active", 0
    else:
        consecutive_idle += 1
        activity = "stalled" if consecutive_idle >= 3 else "idle"
        return activity, consecutive_idle


def get_git_activity(worktree_path):
    """Get git working tree changes and commit count. Returns (changes, commits)."""
    changes = 0
    commits = 0
    try:
        result = subprocess.run(
            ["git", "status", "--porcelain"],
            cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=5,
        )
        if result.returncode == 0:
            changes = len([l for l in result.stdout.strip().splitlines() if l])
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    try:
        result = subprocess.run(
            ["git", "rev-list", "--count", "origin/main..HEAD"],
            cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=5,
        )
        if result.returncode == 0:
            commits = int(result.stdout.strip())
    except (subprocess.TimeoutExpired, FileNotFoundError, ValueError, OSError):
        pass
    return changes, commits


def extract_text_from_jsonl(jsonl_path):
    """Extract assistant text from stream-json JSONL file.

    Prefers 'result' events (clean exit) but falls back to assembling text
    from 'assistant' message events (timeout/crash). Claude Code's stream-json
    format emits whole-message events — 'assistant' events contain complete
    content arrays with 'text' blocks on every turn.
    """
    result_text_parts = []
    assistant_text_parts = []
    try:
        with open(jsonl_path, "r", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue  # Skip malformed lines (partial writes, stderr)

                event_type = event.get("type")

                if event_type == "result":
                    result_text = event.get("result", "")
                    if result_text:
                        result_text_parts.append(result_text)
                elif event_type == "assistant":
                    # assistant events have message.content array
                    content = event.get("message", {}).get("content", [])
                    for block in content:
                        if block.get("type") == "text":
                            text = block.get("text", "")
                            if text:
                                assistant_text_parts.append(text)
    except OSError:
        pass

    # Prefer result (clean exit) over assembled assistant text (timeout)
    if result_text_parts:
        return "\n".join(result_text_parts)
    return "\n\n".join(assistant_text_parts)


def run_claude(worktree_path, prompt, logger, stage, dry_run=False,
               agent=None, ceiling=None):
    """Run `claude -p` in the worktree directory. Returns (exit_code, logger).

    ``ceiling`` overrides ``HARD_CEILING`` for this invocation; ``None`` means
    use the module-level default. The effective ceiling is recorded on the
    stage START log so operators can confirm which value is in effect.
    """
    full_prompt = HEADLESS_PREAMBLE + prompt

    effective_ceiling = ceiling if ceiling is not None else HARD_CEILING
    log(logger, stage, "START", ceiling=f"{effective_ceiling}s")

    if dry_run:
        time.sleep(0.1)  # Simulate
        log(logger, stage, "DONE", exit=0, duration="0s", mode="dry-run")
        return 0, logger

    start = time.time()
    env = os.environ.copy()
    env["PPDS_PIPELINE"] = "1"
    env["CLAUDE_PROJECT_DIR"] = str(Path(worktree_path).resolve())

    # Create stage log directory and file
    stage_log_dir = os.path.join(worktree_path, ".workflow", "stages")
    os.makedirs(stage_log_dir, exist_ok=True)
    stage_jsonl_path = os.path.join(stage_log_dir, f"{stage}.jsonl")

    try:
        stage_log_file = open(stage_jsonl_path, "w")
    except OSError as e:
        log(logger, stage, "ERROR", reason=f"Cannot open stage log: {e}")
        return 1, logger

    cmd = ["claude", "-p", full_prompt, "--verbose",
           "--output-format", "stream-json"]
    if agent:
        cmd.extend(["--agent", agent])

    try:
        proc = subprocess.Popen(
            cmd,
            cwd=worktree_path,
            env=env,
            stdout=stage_log_file,
            stderr=subprocess.STDOUT,
        )
    except FileNotFoundError:
        stage_log_file.close()
        log(logger, stage, "ERROR", reason="claude command not found")
        print("\nERROR: 'claude' command not found. Is Claude Code installed and on PATH?", file=sys.stderr)
        return 1, logger
    except Exception:
        stage_log_file.close()
        raise

    # Polling loop — no threading, just poll + sleep
    last_heartbeat = start
    last_log_size = 0
    last_git_changes = 0
    last_commits = 0
    consecutive_idle = 0
    activity = "unknown"
    exit_code = None

    try:
        while True:
            exit_code = proc.poll()
            if exit_code is not None:
                break

            elapsed = time.time() - start

            # Hard ceiling check — absolute maximum regardless of activity
            if elapsed > effective_ceiling:
                log(logger, stage, "HARD_TIMEOUT",
                    elapsed=f"{int(elapsed)}s",
                    ceiling=f"{effective_ceiling}s",
                    activity=activity)
                proc.terminate()
                try:
                    proc.wait(30)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait()
                exit_code = -1
                break

            # Heartbeat every 60s — multi-signal activity detection
            if time.time() - last_heartbeat >= 60:
                try:
                    current_size = os.path.getsize(stage_jsonl_path)
                except OSError:
                    current_size = 0

                git_changes, commits = get_git_activity(worktree_path)
                child_count = get_child_process_count(proc.pid)

                activity, consecutive_idle = classify_activity(
                    current_size, last_log_size,
                    git_changes, last_git_changes,
                    commits, last_commits,
                    consecutive_idle,
                    has_children=child_count > 0,
                )

                last_log_size = current_size
                last_git_changes = git_changes
                last_commits = commits

                log(logger, stage, "HEARTBEAT",
                    elapsed=f"{int(elapsed)}s", pid=proc.pid,
                    output_bytes=current_size, git_changes=git_changes,
                    commits=commits, children=child_count,
                    activity=activity)
                last_heartbeat = time.time()

                # Stall timeout — kill after STALL_LIMIT consecutive idle heartbeats
                if consecutive_idle >= STALL_LIMIT:
                    log(logger, stage, "STALL_TIMEOUT",
                        elapsed=f"{int(elapsed)}s",
                        idle_minutes=consecutive_idle,
                        last_output_bytes=current_size)
                    proc.terminate()
                    try:
                        proc.wait(30)
                    except subprocess.TimeoutExpired:
                        proc.kill()
                        proc.wait()
                    exit_code = -1
                    break

            time.sleep(5)
    except KeyboardInterrupt:
        proc.terminate()
        proc.wait()
        raise
    finally:
        stage_log_file.close()
    duration = int(time.time() - start)

    # Post-process: extract human-readable text from JSONL
    stage_log_path = os.path.join(stage_log_dir, f"{stage}.log")
    extracted_text = extract_text_from_jsonl(stage_jsonl_path)
    try:
        with open(stage_log_path, "w", errors="replace") as f:
            f.write(extracted_text)
    except OSError:
        pass

    # Read last 20 lines of human-readable log (not JSONL) for pipeline.log
    try:
        with open(stage_log_path, "r", errors="replace") as f:
            lines = f.readlines()
            for line in lines[-20:]:
                log(logger, stage, "OUTPUT", line=line.strip()[:200])
    except OSError:
        pass

    log(logger, stage, "DONE", exit=exit_code, duration=f"{duration}s")
    return exit_code, logger


def derive_name(path):
    """Derive worktree/branch name from plan or spec filename."""
    stem = Path(path).stem
    parts = stem.split("-")
    if len(parts) >= 4:
        try:
            int(parts[0])
            int(parts[1])
            int(parts[2])
            return "-".join(parts[3:])
        except ValueError:
            pass
    return stem


def create_worktree(repo_root, name, branch, logger):
    """Create a git worktree and initialize workflow state."""
    worktree_path = os.path.join(repo_root, ".worktrees", name)

    if os.path.exists(worktree_path):
        log(logger, "worktree", "EXISTS", path=worktree_path, branch=branch)
        return worktree_path

    result = subprocess.run(
        ["git", "branch", "--list", branch],
        cwd=repo_root,
        capture_output=True,
        text=True,
    )
    branch_exists = bool(result.stdout.strip())

    log(logger, "worktree", "CREATING", path=worktree_path, branch=branch)

    if branch_exists:
        cmd = ["git", "worktree", "add", worktree_path, branch]
    else:
        cmd = ["git", "worktree", "add", worktree_path, "-b", branch]

    result = subprocess.run(cmd, cwd=repo_root, capture_output=True, text=True)
    if result.returncode != 0:
        log(logger, "worktree", "FAILED", error=result.stderr.strip())
        return None

    # Initialize workflow state
    result = subprocess.run(
        ["python", "scripts/workflow-state.py", "init", branch],
        cwd=worktree_path,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        log(logger, "worktree", "STATE_INIT_FAILED", error=result.stderr.strip())
        return None

    log(logger, "worktree", "CREATED", path=worktree_path, branch=branch)
    return worktree_path


def copy_file_to_worktree(src_path, worktree_path, dest_rel, logger):
    """Copy a file from main to the worktree if it exists."""
    if not os.path.exists(src_path):
        return
    dest = os.path.join(worktree_path, dest_rel)
    if os.path.exists(dest) and os.path.samefile(src_path, dest):
        log(logger, "worktree", "FILE_SKIPPED_SAME", path=dest_rel)
        return
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    shutil.copy2(src_path, dest)
    log(logger, "worktree", "FILE_COPIED", src=src_path, dest=dest_rel)


def copy_plan_to_main(worktree_path, repo_root, logger):
    """Copy generated plan from worktree back to main .plans/ as artifact."""
    plans_dir = os.path.join(worktree_path, ".plans")
    if not os.path.exists(plans_dir):
        return
    main_plans = os.path.join(repo_root, ".plans")
    os.makedirs(main_plans, exist_ok=True)
    for f in os.listdir(plans_dir):
        if f.endswith(".md"):
            src = os.path.join(plans_dir, f)
            dest = os.path.join(main_plans, f)
            if not os.path.exists(dest):
                shutil.copy2(src, dest)
                log(logger, "implement", "PLAN_ARTIFACT", file=f)


def check_review_passed(worktree_path):
    """Check if review passed in workflow state."""
    state = read_state(worktree_path)
    review = state.get("review", {})
    return review.get("passed", False)


def check_pr_created(worktree_path):
    """Check if PR was created in workflow state."""
    state = read_state(worktree_path)
    pr = state.get("pr", {})
    return pr.get("url")


def _find_duplicate_issue(title, repo_root):
    """Check for existing open issue with matching title prefix."""
    prefix = title[:50]
    try:
        result = subprocess.run(
            ["gh", "issue", "list", "--search", prefix, "--state", "open",
             "--json", "number,title", "--limit", "5"],
            cwd=repo_root, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15,
        )
        if result.returncode != 0:
            return None
        issues = json.loads(result.stdout)
        for issue in issues:
            if issue["title"].startswith(prefix):
                return issue["number"]
    except (subprocess.TimeoutExpired, json.JSONDecodeError, OSError):
        pass
    return None


def _handle_duplicate(finding, existing_issue_number, repo_root, logger, worktree_path):
    """Post structured comment on existing issue for duplicate findings."""
    if os.environ.get("PPDS_SHAKEDOWN"):
        log(logger, "retro", "SHAKEDOWN_SKIPPED_COMMENT",
            finding=finding.get("id", "R-??"), issue=existing_issue_number)
        return

    state = read_state(worktree_path) if worktree_path else {}
    branch = state.get("branch", "unknown")
    finding_id = finding.get("id", "R-??")
    desc = finding.get("description", "No description")

    comment_body = (
        f"## Also observed: {finding_id}\n\n"
        f"**Branch:** `{branch}`\n"
        f"**Finding:** {finding_id}\n"
        f"**Evidence:** {desc}\n\n"
        "---\n*Also observed by pipeline retro.*"
    )

    gh_env = os.environ.copy()
    gh_env["MSYS_NO_PATHCONV"] = "1"

    try:
        result = subprocess.run(
            ["gh", "issue", "comment", str(existing_issue_number),
             "--body", comment_body],
            cwd=repo_root, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
            env=gh_env,
        )
        if result.returncode == 0:
            log(logger, "retro", "ISSUE_UPDATED_DUPLICATE",
                finding=finding_id, existing=f"#{existing_issue_number}")
        else:
            log(logger, "retro", "ISSUE_COMMENT_FAILED",
                finding=finding_id, issue=existing_issue_number,
                error=result.stderr[:200] if result.stderr else "unknown")
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        log(logger, "retro", "ISSUE_COMMENT_FAILED",
            finding=finding_id, issue=existing_issue_number)


def process_retro_findings(worktree_path, logger, repo_root):
    """Process retro findings for auto-heal."""
    findings_path = os.path.join(worktree_path, ".workflow", "retro-findings.json")
    if not os.path.exists(findings_path):
        log(logger, "retro", "NO_FINDINGS_FILE")
        return

    try:
        with open(findings_path, "r") as f:
            data = json.load(f)
    except (json.JSONDecodeError, OSError):
        log(logger, "retro", "FINDINGS_PARSE_ERROR")
        return

    findings = data.get("findings", [])
    if not findings:
        log(logger, "retro", "NO_FINDINGS")
        return

    auto_fixes = [f for f in findings if f.get("tier") == "auto-fix"]
    draft_fixes = [f for f in findings if f.get("tier") == "draft-fix"]
    issues = [f for f in findings if f.get("tier") == "issue-only"]
    observations = [f for f in findings if f.get("tier") == "observation"]

    log(
        logger, "retro", "FINDINGS_SUMMARY",
        auto_fix=len(auto_fixes), draft_fix=len(draft_fixes),
        issue_only=len(issues), observation=len(observations),
    )

    # PPDS_SHAKEDOWN suppresses all issue filing and commenting
    if os.environ.get("PPDS_SHAKEDOWN"):
        log(logger, "retro", "SHAKEDOWN_SKIPPED_ALL_ISSUES",
            issue_count=len(issues))
        return

    gh_env = os.environ.copy()
    gh_env["MSYS_NO_PATHCONV"] = "1"

    for finding in issues:
        desc = finding.get("description", "No description")
        fix = finding.get("fix_description", "")
        finding_id = finding.get("id", "R-??")
        title = f"retro: {desc[:70]}"

        try:
            existing = _find_duplicate_issue(title, repo_root)
        except Exception:
            existing = None  # Best-effort dedup — file new issue on failure
        if existing:
            _handle_duplicate(finding, existing, repo_root, logger, worktree_path)
            continue

        body = f"## Retro Finding {finding_id}\n\n{desc}\n\n**Recommended fix:** {fix}"
        if finding.get("root_cause_chain"):
            body += "\n\n**Root cause chain:**\n"
            for i, cause in enumerate(finding["root_cause_chain"]):
                body += f"{'  ' * i}→ {cause}\n"
        body += "\n\n---\n*Filed automatically by pipeline retro.*"

        try:
            subprocess.run(
                ["gh", "issue", "create", "--title", title, "--body", body],
                cwd=repo_root, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=True,
                env=gh_env,
            )
            log(logger, "retro", "ISSUE_CREATED", finding=finding_id)
        except (subprocess.TimeoutExpired, FileNotFoundError, subprocess.CalledProcessError):
            log(logger, "retro", "ISSUE_FAILED", finding=finding_id)

    fixable = auto_fixes + draft_fixes
    if fixable:
        log(logger, "retro", "AUTO_HEAL_AVAILABLE", count=len(fixable),
            note="Auto-heal not yet implemented")


def ensure_retro_summary_updated(worktree_path, logger, repo_root):
    """Fallback: update .retros/summary.json if retro skill didn't reach Step 10.

    The retro skill is responsible for writing summary.json in its Step 10, but if
    the headless retro session times out before that step, the persistent store is
    never updated. This function checks whether the store was updated today, and if
    not, performs a minimal append of findings from retro-findings.json.
    """
    findings_path = os.path.join(worktree_path, ".workflow", "retro-findings.json")
    if not os.path.exists(findings_path):
        return  # No findings to persist

    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    summary_path = os.path.join(repo_root, ".retros", "summary.json")

    # Check if summary.json was already updated today (by the retro skill's Step 10)
    if os.path.exists(summary_path):
        try:
            with open(summary_path, "r") as f:
                store = json.load(f)
            if store.get("last_updated") == today:
                return  # Already updated — retro skill completed Step 10
        except (json.JSONDecodeError, OSError):
            store = None
    else:
        store = None

    # Load findings
    try:
        with open(findings_path, "r") as f:
            data = json.load(f)
    except (json.JSONDecodeError, OSError):
        return

    findings = data.get("findings", [])
    if not findings:
        return

    # Seed or reuse existing store
    if store is None or store.get("schema_version") != 1:
        store = {
            "schema_version": 1,
            "total_retros": 0,
            "findings_by_category": {},
            "metrics": {
                "avg_fix_ratio": 0.0,
                "pipeline_success_rate": 0.0,
                "avg_convergence_rounds": 0.0,
            },
            "last_updated": "",
        }

    # Determine branch name from worktree
    branch = os.path.basename(worktree_path)

    # Append findings by category
    for finding in findings:
        category = finding.get("category", "uncategorized")
        entry = {
            "date": today,
            "branch": branch,
            "finding_id": finding.get("id", "R-??"),
        }
        store.setdefault("findings_by_category", {}).setdefault(category, []).append(entry)

    store["total_retros"] = store.get("total_retros", 0) + 1
    store["last_updated"] = today

    # Write back
    os.makedirs(os.path.dirname(summary_path), exist_ok=True)
    try:
        with open(summary_path, "w") as f:
            json.dump(store, f, indent=2)
        log(logger, "retro", "SUMMARY_FALLBACK_WRITTEN", path=summary_path)
    except OSError as e:
        log(logger, "retro", "SUMMARY_FALLBACK_FAILED", reason=str(e))


def get_repo_slug(worktree_path):
    """Get owner/repo from gh CLI. Returns 'owner/repo' or None."""
    return _get_repo_slug(worktree_path)


def _get_pr_created_at(worktree_path, pr_number):
    """Return the PR's created_at ISO timestamp, or "" on failure."""
    try:
        result = subprocess.run(
            ["gh", "pr", "view", str(pr_number),
             "--json", "createdAt", "--jq", ".createdAt"],
            cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15,
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    return ""


def poll_gemini(worktree_path, pr_number, logger, min_wait=90, max_wait=300):
    """Poll for a Gemini review across all three GitHub endpoints.

    v1-prelaunch retro item #3: delegates to ``triage_common.poll_gemini_review``
    which polls reviews + pulls/comments + issues/comments. The previous
    implementation only polled ``pulls/comments`` (inline review comments)
    so it never saw Gemini's top-level review (posted via ``pulls/reviews``)
    and timed out at 5 minutes on every PR.
    """
    pr_created_at = _get_pr_created_at(worktree_path, pr_number)

    def _log(event, **kwargs):
        log(logger, "pr", f"GEMINI_{event}", **kwargs)

    comments, _status = poll_gemini_review(
        worktree_path, pr_number, pr_created_at,
        max_wait=max_wait,
        poll_interval=30,
        min_wait=min_wait,
        shakedown=bool(os.environ.get("PPDS_SHAKEDOWN")),
        log_fn=_log,
    )
    return comments


def run_triage(worktree_path, pr_number, comments, logger, dry_run=False,
               ceiling=None):
    """Invoke gemini-triage agent to fix/dismiss comments. Returns list or None.

    ``ceiling`` is forwarded to ``run_claude`` for symmetry with other stage
    wrappers; ``None`` means use the module-level ``HARD_CEILING``.
    """
    prompt = build_triage_prompt(worktree_path, pr_number, comments)

    exit_code, logger_out = run_claude(
        worktree_path, prompt, logger, "pr-triage",
        dry_run=dry_run, agent="gemini-triage", ceiling=ceiling,
    )

    if exit_code != 0:
        return None

    # Parse structured output from the human-readable stage log
    stage_log_path = os.path.join(
        worktree_path, ".workflow", "stages", "pr-triage.log")
    return parse_triage_stage_log(stage_log_path)


def post_replies(worktree_path, pr_number, triage_results, logger):
    """Post threaded replies to Gemini comments from triage results."""
    def _log_fn(event, **kwargs):
        # Map triage_common log events to pipeline's log(logger, step, event) format
        if event == "POSTED":
            log(logger, "pr", "REPLY_POSTED", **kwargs)
        elif event == "FAILED":
            log(logger, "pr", "REPLY_FAILED", **kwargs)
        else:
            log(logger, "pr", event, **kwargs)

    _post_replies_common(worktree_path, pr_number, triage_results, _log_fn)


def _poll_codeql_check(worktree_path, pr_number, logger):
    """Wait for CodeQL check to complete (5 min timeout)."""
    if os.environ.get("PPDS_SHAKEDOWN"):
        return
    start = time.time()
    while time.time() - start < 300:
        try:
            result = subprocess.run(
                ["gh", "pr", "checks", str(pr_number),
                 "--json", "name,state,conclusion"],
                cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
            )
            if result.returncode == 0:
                checks = json.loads(result.stdout)
                codeql = [c for c in checks
                          if "codeql" in c.get("name", "").lower()]
                if codeql and all(c.get("state") == "COMPLETED" for c in codeql):
                    log(logger, "pr", "CODEQL_COMPLETE",
                        elapsed=f"{int(time.time() - start)}s")
                    return
        except (subprocess.TimeoutExpired, FileNotFoundError,
                json.JSONDecodeError, OSError):
            pass
        time.sleep(30)
    log(logger, "pr", "CODEQL_TIMEOUT")


def run_pr_stage(worktree_path, logger, dry_run=False, ceiling=None):
    """Scripted PR stage: draft → poll Gemini → triage → ready → notify.

    ``ceiling`` overrides ``HARD_CEILING`` for the stage's internal timeout
    checks and the PR monitor subprocess timeout; ``None`` means use the
    module-level default.
    """
    effective_ceiling = ceiling if ceiling is not None else HARD_CEILING
    log(logger, "pr", "START", ceiling=f"{effective_ceiling}s")
    start = time.time()

    # PPDS_SHAKEDOWN: skip PR creation entirely
    if os.environ.get("PPDS_SHAKEDOWN"):
        log(logger, "pr", "PR_SKIPPED_SHAKEDOWN")
        log(logger, "pr", "DONE", exit=0, duration="0s", mode="shakedown")
        return 0, logger

    def _check_timeout():
        """Check if stage hard ceiling exceeded. Logs and returns True if timed out."""
        if (time.time() - start) > effective_ceiling:
            log(logger, "pr", "HARD_TIMEOUT",
                elapsed=f"{int(time.time() - start)}s",
                ceiling=f"{effective_ceiling}s")
            return True
        return False

    if dry_run:
        log(logger, "pr", "DONE", exit=0, duration="0s", mode="dry-run")
        return 0, logger

    # 1. Rebase on main
    fetch = subprocess.run(
        ["git", "fetch", "origin", "main"],
        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
    )
    if fetch.returncode != 0:
        log(logger, "pr", "FETCH_FAILED", error=fetch.stderr[:200] if fetch.stderr else "unknown")
        # Continue anyway — rebase will use whatever origin/main we have
    result = subprocess.run(
        ["git", "rebase", "origin/main"],
        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
    )
    if result.returncode != 0:
        log(logger, "pr", "REBASE_CONFLICT", error=result.stderr.strip()[:200])
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger

    # 2. Push branch
    branch = subprocess.run(
        ["git", "rev-parse", "--abbrev-ref", "HEAD"],
        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
    ).stdout.strip()
    push_result = subprocess.run(
        ["git", "push", "-u", "origin", branch, "--force-with-lease"],
        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
    )
    if push_result.returncode != 0:
        log(logger, "pr", "PUSH_FAILED", error=push_result.stderr.strip()[:200])
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger

    # 3. Read issues from state
    issues_result = subprocess.run(
        ["python", "scripts/workflow-state.py", "get", "issues"],
        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
    )
    issues = []
    try:
        issues = json.loads(issues_result.stdout.strip())
    except (json.JSONDecodeError, ValueError):
        pass

    # 4. Build PR body and create draft
    state = read_state(worktree_path)
    closes = "\n".join(f"Closes #{n}" for n in issues) if issues else ""

    # Generate summary via a quick claude call
    summary_prompt = (
        "Generate a PR title (under 70 chars, conventional commit format) and "
        "3 bullet-point summary for the changes on this branch vs main. "
        "Output ONLY: first line = title, then blank line, then bullet points."
    )
    exit_code, logger = run_claude(
        worktree_path, summary_prompt, logger, "pr-summary",
        dry_run=dry_run,
        ceiling=int(max(0, effective_ceiling - (time.time() - start))),
    )

    # Read the generated summary
    summary_log = os.path.join(worktree_path, ".workflow", "stages", "pr-summary.log")
    pr_title = f"feat: {branch}"
    pr_body_summary = ""
    try:
        with open(summary_log, "r", errors="replace") as f:
            lines = [l.strip() for l in f.readlines() if l.strip()]
            if lines:
                pr_title = lines[0][:70]
                pr_body_summary = "\n".join(lines[1:])
    except OSError:
        pass

    body_parts = ["## Summary", pr_body_summary]
    if closes:
        body_parts.append(f"\n{closes}")
    body_parts.append("\n## Verification")
    gates = state.get("gates", {})
    verify = state.get("verify", {})
    qa = state.get("qa", {})
    review = state.get("review", {})
    body_parts.append(f"- [{'x' if gates.get('passed') else ' '}] /gates passed")
    verify_done = isinstance(verify, dict) and any(v for v in verify.values() if isinstance(v, str) and v)
    qa_done = isinstance(qa, dict) and any(v for v in qa.values() if isinstance(v, str) and v)
    body_parts.append(f"- [{'x' if verify_done else ' '}] /verify completed")
    body_parts.append(f"- [{'x' if qa_done else ' '}] /qa completed")
    body_parts.append(f"- [{'x' if review.get('passed') else ' '}] /review completed")
    body_parts.append("\n🤖 Generated with [Claude Code](https://claude.com/claude-code)")
    pr_body = "\n".join(body_parts)

    result = subprocess.run(
        ["gh", "pr", "create", "--draft", "--title", pr_title, "--body", pr_body],
        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
    )
    if result.returncode != 0:
        log(logger, "pr", "CREATE_FAILED", error=result.stderr.strip()[:200])
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger

    # gh pr create may output warnings on earlier lines; URL is always the last line
    pr_url = result.stdout.strip().splitlines()[-1].strip() if result.stdout.strip() else ""
    # Extract PR number from URL (expect format: https://github.com/owner/repo/pull/123)
    pr_number = pr_url.rstrip("/").split("/")[-1]
    if not pr_number.isdigit():
        log(logger, "pr", "PR_NUMBER_INVALID", url=pr_url, extracted=pr_number)
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger
    log(logger, "pr", "PR_CREATED", url=pr_url, draft=True)

    # Write workflow state immediately
    for cmd_args in [
        ["set", "pr.url", pr_url],
        ["set", "pr.created", "now"],
    ]:
        subprocess.run(
            ["python", "scripts/workflow-state.py"] + cmd_args,
            cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
        )

    # Check timeout before polling
    if _check_timeout():
        log(logger, "pr", "DONE", exit=-1, duration=f"{int(time.time() - start)}s")
        return -1, logger

    # 5–8. Delegate the post-PR-creation polling loop (CI → Gemini → triage →
    # ready → retro → notify) to pr_monitor.py. v1-prelaunch retro item #4:
    # The two scripts had drifted apart and the duplicated polling logic was
    # the root drift cause. Pipeline now retains *orchestration* (start, push,
    # PR creation, wait); pr_monitor.py owns the *polling loop*. Single
    # canonical implementation.
    monitor_exit = _delegate_to_pr_monitor(
        worktree_path, pr_number, logger, dry_run=dry_run,
        ceiling=int(max(0, effective_ceiling - (time.time() - start))))
    if monitor_exit not in (0,):
        # pr_monitor failed (CI failure, timeout, etc.); pipeline still
        # records the PR URL but reports stage failure so the orchestrator
        # can decide what to do.
        log(logger, "pr", "DONE", exit=monitor_exit,
            duration=f"{int(time.time() - start)}s",
            via="pr_monitor")
        return monitor_exit, logger

    log(logger, "pr", "PR_READY", url=pr_url)
    duration = int(time.time() - start)
    log(logger, "pr", "DONE", exit=0, duration=f"{duration}s", via="pr_monitor")
    return 0, logger


def _delegate_to_pr_monitor(worktree_path, pr_number, logger, dry_run=False,
                            ceiling=None):
    """Invoke ``scripts/pr_monitor.py`` as a subprocess for the polling loop.

    v1-prelaunch retro item #4: the previous pipeline.run_pr_stage embedded a
    full copy of pr_monitor.py's polling logic (CI → Gemini → triage →
    reconcile → ready → retro → notify). Two implementations of the same
    logic drifted apart over time. Now pipeline delegates the polling phase
    to pr_monitor.py via subprocess; pr_monitor.py is the single canonical
    implementation.

    Args:
        worktree_path: absolute path to the worktree
        pr_number: PR number (str or int)
        logger: pipeline log handle
        dry_run: if True, log the planned invocation but skip exec
        ceiling: override HARD_CEILING for the subprocess timeout; ``None``
            means use the module-level default.

    Returns:
        Exit code: 0 on success, non-zero on failure.
    """
    effective_ceiling = ceiling if ceiling is not None else HARD_CEILING
    script_path = os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "pr_monitor.py")
    cmd = [
        sys.executable, script_path,
        "--worktree", worktree_path,
        "--pr", str(pr_number),
    ]
    log(logger, "pr", "MONITOR_DELEGATE",
        script="pr_monitor.py", pr=pr_number)

    if dry_run:
        log(logger, "pr", "MONITOR_DRY_RUN", cmd=" ".join(cmd))
        return 0

    env = os.environ.copy()
    # Propagate orchestrator hints. PPDS_PIPELINE keeps Claude hooks in
    # headless mode within the spawned monitor and any downstream `claude -p`.
    env.setdefault("PPDS_PIPELINE", "1")

    try:
        proc = subprocess.run(
            cmd,
            cwd=worktree_path,
            env=env,
            capture_output=True,
            text=True,
            timeout=effective_ceiling,
        )
    except subprocess.TimeoutExpired:
        log(logger, "pr", "MONITOR_TIMEOUT", ceiling=f"{effective_ceiling}s")
        return -1
    except (FileNotFoundError, OSError) as e:
        log(logger, "pr", "MONITOR_ERROR", error=str(e))
        return 1

    # Surface stderr tail in the pipeline log so failures are diagnosable
    # without opening the monitor log.
    if proc.stderr:
        for line in proc.stderr.strip().splitlines()[-10:]:
            log(logger, "pr", "MONITOR_STDERR", line=line[:200])

    log(logger, "pr", "MONITOR_DONE", exit=proc.returncode)
    return proc.returncode


def _read_last_lines(worktree_path, stage_name, n=50):
    """Read last N lines from a stage's .log file. Returns list of strings."""
    stage_dir = Path(worktree_path) / ".workflow" / "stages"
    log_path = stage_dir / f"{stage_name}.log"

    # For converge stage, the actual logs are converge-r1.log, converge-r2.log, etc.
    # Fall back to the most recent converge-r*.log if converge.log doesn't exist.
    if not log_path.exists() and stage_name == "converge":
        candidates = sorted(stage_dir.glob("converge-r*.log"))
        if candidates:
            log_path = candidates[-1]

    try:
        with open(log_path, "r", errors="replace") as f:
            lines = f.readlines()
            return [line.rstrip() for line in lines[-n:] if line.strip()]
    except OSError:
        return []


def write_result(worktree_path, status, duration, stages, pr_url=None,
                  failed_stage=None, error=None, last_output=None):
    """Write pipeline-result.json and invoke notify.py (best-effort)."""
    result = {
        "status": status,
        "duration": duration,
        "stages": stages,
        "pr_url": pr_url,
        "timestamp": timestamp(),
    }
    if failed_stage:
        result["failed_stage"] = failed_stage
    if error:
        result["error"] = error
    if last_output is not None:
        result["last_output"] = last_output

    # Include partial QA results from state if QA failed
    if failed_stage and "qa" in failed_stage:
        state = read_state(worktree_path)
        qa_partial = state.get("qa_partial")
        if qa_partial:
            result["partial_results"] = qa_partial

    result_path = os.path.join(worktree_path, ".workflow", "pipeline-result.json")
    try:
        with open(result_path, "w") as f:
            json.dump(result, f, indent=2)
    except OSError:
        pass

    # Best-effort notification
    notify_script = os.path.join(worktree_path, ".claude", "hooks", "notify.py")
    if os.path.exists(notify_script):
        title = "Pipeline Complete" if status == "complete" else "Pipeline Failed"
        msg = f"PR: {pr_url}" if pr_url else f"Failed at {failed_stage}: {error}"
        try:
            subprocess.run(
                ["python", notify_script, "--title", title, "--msg", msg],
                cwd=worktree_path, timeout=10, capture_output=True,
            )
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            pass  # Non-blocking


def main():
    parser = argparse.ArgumentParser(description="PPDS Deterministic Pipeline Orchestrator")
    parser.add_argument("--plan", help="Path to implementation plan file")
    parser.add_argument("--spec", help="Path to spec file (implement generates plan)")
    parser.add_argument("--branch", help="Full branch name (required when no plan)")
    parser.add_argument("plan_positional", nargs="?", help="Plan path (positional, for backward compat)")
    parser.add_argument("--from", dest="from_stage", choices=STAGES, help="Resume from a specific stage")
    parser.add_argument("--resume", action="store_true", help="Auto-resume from last completed stage")
    parser.add_argument("--name", help="Override worktree name (default: derived from plan/spec)")
    parser.add_argument("--no-retro", action="store_true", help="Skip the post-PR retro step")
    parser.add_argument("--max-converge", type=int, default=3, help="Max converge rounds (default: 3)")
    parser.add_argument(
        "--max-stage-seconds",
        type=int,
        default=None,
        metavar="N",
        help=(
            "Override the per-stage hard ceiling for this run "
            f"(default: {HARD_CEILING}s). Must be a positive integer. "
            "Applies to the claude heartbeat loop, the pr stage, and the "
            "pr_monitor subprocess timeout."
        ),
    )
    parser.add_argument("--worktree", help="Use existing worktree instead of creating one")
    parser.add_argument("--issue", type=int, action="append", default=[], help="GitHub issue number(s) (repeatable)")
    parser.add_argument("--dry-run", action="store_true", help="Run orchestration without invoking claude -p")
    args = parser.parse_args()

    if args.max_stage_seconds is not None and args.max_stage_seconds <= 0:
        parser.error("--max-stage-seconds must be a positive integer")
    stage_ceiling = args.max_stage_seconds  # None → use HARD_CEILING default

    # Handle backward compat: positional plan arg
    plan_path = args.plan or args.plan_positional
    spec_path = args.spec

    if not plan_path and not spec_path:
        print("ERROR: Provide --plan or --spec (or positional plan path).", file=sys.stderr)
        sys.exit(1)

    # Resolve paths
    repo_root = os.getcwd()
    if plan_path and not os.path.isabs(plan_path):
        plan_path = os.path.join(repo_root, plan_path)
    if spec_path and not os.path.isabs(spec_path):
        spec_path = os.path.join(repo_root, spec_path)

    if plan_path and not os.path.exists(plan_path):
        print(f"ERROR: Plan file not found: {plan_path}", file=sys.stderr)
        sys.exit(1)
    if spec_path and not os.path.exists(spec_path):
        print(f"ERROR: Spec file not found: {spec_path}", file=sys.stderr)
        sys.exit(1)

    # Derive name and branch
    source_path = plan_path or spec_path
    try:
        source_rel = os.path.relpath(source_path, repo_root)
    except ValueError:
        source_rel = source_path

    name = args.name or derive_name(source_path)
    branch = args.branch or (derive_name(plan_path) if plan_path else None)
    if not branch:
        print("ERROR: --branch is required when using --spec without --plan.", file=sys.stderr)
        sys.exit(1)

    # Determine start stage
    start_idx = 0
    if args.from_stage:
        start_idx = STAGES.index(args.from_stage)

    # Handle --resume
    if args.resume:
        candidate_log = os.path.join(repo_root, ".worktrees", name, ".workflow", "pipeline.log")
        last_done = find_last_completed_stage(candidate_log)
        if last_done and last_done in STAGES:
            start_idx = STAGES.index(last_done) + 1
            print(f"Resuming after '{last_done}' (stage {start_idx + 1}/{len(STAGES)})", file=sys.stderr)
        else:
            print("No completed stages found in pipeline.log, starting from beginning.", file=sys.stderr)

    # Set up worktree
    if args.worktree:
        worktree_path = os.path.abspath(args.worktree)
        if not os.path.exists(worktree_path):
            print(f"ERROR: Worktree not found: {worktree_path}", file=sys.stderr)
            sys.exit(1)
    elif start_idx > 0 or args.resume:
        worktree_path = os.path.join(repo_root, ".worktrees", name)
        if not os.path.exists(worktree_path):
            print(f"ERROR: Worktree not found at {worktree_path}. Use --worktree to specify.", file=sys.stderr)
            sys.exit(1)
    else:
        worktree_path = None

    # Open log file
    if worktree_path:
        log_dir = os.path.join(worktree_path, ".workflow")
    else:
        log_dir = os.path.join(repo_root, ".worktrees", name, ".workflow")
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.join(log_dir, "pipeline.log")

    mode = "a" if (args.from_stage or args.resume) else "w"
    # Initialize before try so finally block never hits NameError
    pipeline_start = time.time()
    pr_url = None
    stage_durations = {}
    _failed_stage = None
    _failed_log_stage = None  # actual log filename (may differ from display name)
    _failed_reason = None
    _result_written = False
    lock_acquired = False

    logger = open_logger(log_path, mode)
    try:

        log(
            logger, "pipeline",
            "START" if not (args.from_stage or args.resume) else "RESUME",
            plan=source_rel, name=name, branch=branch,
            from_stage=args.from_stage or ("auto" if args.resume else "worktree"),
        )

        # Acquire pipeline lock
        lock_path = os.path.join(log_dir, "pipeline.lock")
        if not acquire_lock(lock_path, logger):
            logger.close()
            sys.exit(1)
        lock_acquired = True

        # Write pipeline phase to state (if worktree exists)
        if worktree_path and os.path.exists(worktree_path):
            subprocess.run(
                ["python", "scripts/workflow-state.py", "set", "phase", "pipeline"],
                cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
            )

        def _pipeline_fail(stage_name, reason=None, log_stage=None):
            nonlocal _failed_stage, _failed_log_stage, _failed_reason
            _failed_stage = stage_name
            _failed_log_stage = log_stage or stage_name
            _failed_reason = reason
            raise PipelineFailure(f"{stage_name}: {reason}")

        for i, stage in enumerate(STAGES):
            if i < start_idx:
                continue

            if stage == "retro" and args.no_retro:
                log(logger, "retro", "SKIPPED", reason="--no-retro flag")
                continue

            stage_start_time = time.time()
            exit_code = 0  # Default; overwritten by run_claude() calls

            if stage == "worktree":
                if worktree_path and os.path.exists(worktree_path):
                    log(logger, "worktree", "EXISTS", path=worktree_path)
                else:
                    worktree_path = create_worktree(repo_root, name, branch, logger)
                    if not worktree_path:
                        log(logger, "pipeline", "FAILED", failed_stage="worktree")
                        _pipeline_fail("worktree")

                # Relocate log to worktree
                new_log_dir = os.path.join(worktree_path, ".workflow")
                os.makedirs(new_log_dir, exist_ok=True)
                new_log_path = os.path.join(new_log_dir, "pipeline.log")
                if new_log_path != log_path:
                    logger.close()
                    if os.path.exists(log_path):
                        shutil.copy2(log_path, new_log_path)
                    log_path = new_log_path
                    logger = open_logger(log_path)

                # P9: Copy spec/plan from main to worktree
                if spec_path:
                    spec_rel = os.path.relpath(spec_path, repo_root)
                    copy_file_to_worktree(spec_path, worktree_path, spec_rel, logger)
                if plan_path:
                    plan_rel = os.path.relpath(plan_path, repo_root)
                    copy_file_to_worktree(plan_path, worktree_path, plan_rel, logger)

                # Set workflow state
                for state_args in [
                    ["set", "plan", source_rel],
                    ["set", "started", "now"],
                ]:
                    subprocess.run(
                        ["python", "scripts/workflow-state.py"] + state_args,
                        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace",
                    )

                if args.issue:
                    cmd = ["python", "scripts/workflow-state.py", "append", "issues"] + [str(n) for n in args.issue]
                    subprocess.run(cmd, cwd=worktree_path, capture_output=True, text=True)
                    log(logger, "worktree", "ISSUES_LINKED", issues=args.issue)

            elif stage == "implement":
                pre_commits = get_commit_count(worktree_path)
                if plan_path:
                    plan_rel = os.path.relpath(plan_path, repo_root)
                    prompt = f"/implement {plan_rel}"
                else:
                    prompt = "/implement"  # Will generate plan from spec
                exit_code, logger = run_claude(worktree_path, prompt, logger, "implement", args.dry_run, ceiling=stage_ceiling)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", failed_stage="implement")
                    _pipeline_fail("implement")

                # P4: Outcome verification + retry
                if not verify_outcome(worktree_path, "implement", pre_commits) and not args.dry_run:
                    log(logger, "implement", "OUTCOME_MISS", reason="no new commits, retrying")
                    exit_code, logger = run_claude(worktree_path, prompt, logger, "implement-retry", args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0 or not verify_outcome(worktree_path, "implement", pre_commits):
                        log(logger, "pipeline", "FAILED", failed_stage="implement", reason="outcome verification failed")
                        _pipeline_fail("implement", "outcome verification failed")

                # P8: Copy plan artifact back to main
                copy_plan_to_main(worktree_path, repo_root, logger)

            elif stage in ("gates", "verify", "qa", "review"):
                prompt = f"/{stage}"
                exit_code, logger = run_claude(worktree_path, prompt, logger, stage, args.dry_run, ceiling=stage_ceiling)
                if exit_code != 0:
                    # Review FAIL must advance to converge, not abort (AC-20)
                    if stage == "review":
                        log(logger, "review", "FAIL_WILL_CONVERGE",
                            reason="review failed — advancing to converge stage")
                    else:
                        log(logger, "pipeline", "FAILED", failed_stage=stage)
                        _pipeline_fail(stage)

                # P4: Outcome verification + retry (skip for review — converge handles it)
                if stage != "review" and not verify_outcome(worktree_path, stage, 0) and not args.dry_run:
                    log(logger, stage, "OUTCOME_MISS", reason="expected state not set, retrying")
                    exit_code, logger = run_claude(worktree_path, prompt, logger, f"{stage}-retry", args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage=stage)
                        _pipeline_fail(stage)

            elif stage == "converge":
                state = read_state(worktree_path)
                run_converge, reason = should_converge(state)

                if not run_converge:
                    log(logger, "converge", "SKIPPED", reason=reason)
                    continue
                log(logger, "converge", "TRIGGERED", reason=reason)

                for round_num in range(args.max_converge):
                    log(logger, "converge", "ROUND_START", round=round_num + 1, max=args.max_converge)

                    converge_log = f"converge-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/converge", logger, converge_log, args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="converge")
                        _pipeline_fail("converge", log_stage=converge_log)

                    gates_log = f"gates-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/gates", logger, gates_log, args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="gates-reconverge")
                        _pipeline_fail("gates-reconverge", log_stage=gates_log)

                    verify_log = f"verify-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/verify", logger, verify_log, args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="verify-reconverge")
                        _pipeline_fail("verify-reconverge", log_stage=verify_log)

                    qa_log = f"qa-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/qa", logger, qa_log, args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="qa-reconverge")
                        _pipeline_fail("qa-reconverge", log_stage=qa_log)

                    review_log = f"review-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/review", logger, review_log, args.dry_run, ceiling=stage_ceiling)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="review-reconverge")
                        _pipeline_fail("review-reconverge", log_stage=review_log)

                    if check_review_passed(worktree_path):
                        log(logger, "converge", "CONVERGED", rounds=round_num + 1)
                        break
                else:
                    log(logger, "converge", "FAILED_TO_CONVERGE", max_rounds=args.max_converge)
                    log(logger, "pipeline", "FAILED", failed_stage="converge", reason="max rounds exceeded")
                    print(f"\nFAILED: Could not converge after {args.max_converge} rounds.", file=sys.stderr)
                    _pipeline_fail("converge", "max rounds exceeded",
                                    log_stage=f"review-r{args.max_converge}")

            elif stage == "pr":
                exit_code, logger = run_pr_stage(
                    worktree_path, logger, dry_run=args.dry_run,
                    ceiling=stage_ceiling)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", failed_stage="pr")
                    _pipeline_fail("pr")
                pr_url = check_pr_created(worktree_path)

            elif stage == "retro":
                exit_code, logger = run_claude(worktree_path, "/retro", logger, "retro", args.dry_run, ceiling=stage_ceiling)
                if exit_code != 0:
                    log(logger, "retro", "FAILED_NON_BLOCKING")
                else:
                    process_retro_findings(worktree_path, logger, repo_root)
                # Fallback: update summary.json if retro skill timed out before Step 10
                ensure_retro_summary_updated(worktree_path, logger, repo_root)

            # Auto-commit stranded files between stages (#717)
            if worktree_path and stage not in ("worktree", "retro", "pr"):
                dirty = subprocess.run(
                    ["git", "status", "--porcelain"],
                    cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
                )
                if dirty.returncode == 0 and dirty.stdout.strip():
                    subprocess.run(["git", "add", "-u"], cwd=worktree_path,
                                   capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10)
                    commit = subprocess.run(
                        ["git", "commit", "-m", f"chore({stage}): auto-commit stage changes"],
                        cwd=worktree_path, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
                    )
                    if commit.returncode == 0:
                        log(logger, stage, "AUTO_COMMIT", reason="stranded files committed")
                    elif "nothing to commit" not in (commit.stdout + commit.stderr):
                        log(logger, stage, "AUTO_COMMIT_FAILED", reason=commit.stderr.strip()[:200])

            # Track stage duration with exit code and last output line
            stage_dur = f"{int(time.time() - stage_start_time)}s"
            last_lines = _read_last_lines(worktree_path, stage, 1) if worktree_path else []
            stage_durations[stage] = {
                "duration": stage_dur,
                "exit": exit_code,
                "last_line": last_lines[0] if last_lines else "",
            }

        duration = int(time.time() - pipeline_start)
        log(logger, "pipeline", "COMPLETE", duration=f"{duration}s", pr=pr_url or "none")
        if worktree_path:
            write_result(worktree_path, "complete", duration, stage_durations,
                         pr_url=pr_url)
            _result_written = True
        print(f"\nPipeline complete in {duration}s.", file=sys.stderr)
        if pr_url:
            print(f"PR: {pr_url}", file=sys.stderr)

    except PipelineFailure:
        duration = int(time.time() - pipeline_start)
        log(logger, "pipeline", "FAILED",
            failed_stage=_failed_stage, reason=_failed_reason,
            duration=f"{duration}s")
        if worktree_path:
            last_output = _read_last_lines(worktree_path, _failed_log_stage, 50)
            write_result(worktree_path, "failed", duration, stage_durations,
                         failed_stage=_failed_stage, error=_failed_reason,
                         last_output=last_output)
            _result_written = True

            # Best-effort failure retro — safe to spawn subprocesses here
            log(logger, "retro", "START", mode="failure-retro")
            try:
                exit_code, logger = run_claude(
                    worktree_path, "/retro", logger, "retro", args.dry_run,
                    ceiling=stage_ceiling)
                if exit_code != 0:
                    log(logger, "retro", "FAILED_NON_BLOCKING")
                else:
                    process_retro_findings(worktree_path, logger, repo_root)
                # Fallback: update summary.json if retro skill timed out before Step 10
                ensure_retro_summary_updated(worktree_path, logger, repo_root)
            except Exception:
                log(logger, "retro", "FAILED_NON_BLOCKING")

        print(f"\nPipeline FAILED at stage '{_failed_stage}'.", file=sys.stderr)
        if _failed_reason:
            print(f"  Reason: {_failed_reason}", file=sys.stderr)
        sys.exit(1)

    except KeyboardInterrupt:
        duration = int(time.time() - pipeline_start)
        log(logger, "pipeline", "INTERRUPTED")
        if worktree_path:
            write_result(worktree_path, "interrupted", duration, stage_durations,
                         error="KeyboardInterrupt")
            _result_written = True
        print("\nPipeline interrupted by user.", file=sys.stderr)
        sys.exit(130)
    finally:
        # Safety net — write failure result if not already written
        if not _result_written and _failed_stage and worktree_path:
            duration = int(time.time() - pipeline_start)
            write_result(worktree_path, "failed", duration, stage_durations,
                         failed_stage=_failed_stage, error=_failed_reason)
        if lock_acquired:
            release_lock(lock_path)
        logger.close()


if __name__ == "__main__":
    main()
