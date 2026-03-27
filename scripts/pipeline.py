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
HARD_CEILING = 3600  # absolute maximum stage duration in seconds (60 min)


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
    print(" ".join(console_parts))


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
            ["git", "rev-list", "--count", "main..HEAD"],
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
                    bracket_start = line.index("[") + 1
                    bracket_end = line.index("]")
                    stage_name = line[bracket_start:bracket_end]
                    # Normalize converge round names back to base stage
                    if stage_name.startswith("converge"):
                        stage_name = "converge"
                    elif stage_name.startswith("gates-r"):
                        stage_name = "converge"
                    elif stage_name.startswith("verify-r"):
                        stage_name = "converge"
                    elif stage_name.startswith("qa-"):
                        stage_name = "qa"
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


def get_git_activity(worktree_path):
    """Get git working tree changes and commit count. Returns (changes, commits)."""
    changes = 0
    commits = 0
    try:
        result = subprocess.run(
            ["git", "status", "--porcelain"],
            cwd=worktree_path, capture_output=True, text=True, timeout=5,
        )
        if result.returncode == 0:
            changes = len([l for l in result.stdout.strip().splitlines() if l])
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    try:
        result = subprocess.run(
            ["git", "rev-list", "--count", "main..HEAD"],
            cwd=worktree_path, capture_output=True, text=True, timeout=5,
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
               agent=None):
    """Run `claude -p` in the worktree directory. Returns (exit_code, logger)."""
    full_prompt = HEADLESS_PREAMBLE + prompt

    log(logger, stage, "START")

    if dry_run:
        time.sleep(0.1)  # Simulate
        log(logger, stage, "DONE", exit=0, duration="0s", mode="dry-run")
        return 0, logger

    start = time.time()
    env = os.environ.copy()
    env["MSYS_NO_PATHCONV"] = "1"
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
            if elapsed > HARD_CEILING:
                log(logger, stage, "HARD_TIMEOUT",
                    elapsed=f"{int(elapsed)}s",
                    ceiling=f"{HARD_CEILING}s",
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

                output_grew = current_size > last_log_size
                git_grew = git_changes > last_git_changes
                commits_grew = commits > last_commits

                if output_grew or git_grew or commits_grew:
                    activity = "active"
                    consecutive_idle = 0
                else:
                    consecutive_idle += 1
                    activity = "stalled" if consecutive_idle >= 3 else "idle"

                last_log_size = current_size
                last_git_changes = git_changes
                last_commits = commits

                log(logger, stage, "HEARTBEAT",
                    elapsed=f"{int(elapsed)}s", pid=proc.pid,
                    output_bytes=current_size, git_changes=git_changes,
                    commits=commits, activity=activity)
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

    log(
        logger, "retro", "FINDINGS_SUMMARY",
        auto_fix=len(auto_fixes), draft_fix=len(draft_fixes), issue_only=len(issues),
    )

    for finding in issues:
        desc = finding.get("description", "No description")
        fix = finding.get("fix_description", "")
        finding_id = finding.get("id", "R-??")

        body = f"## Retro Finding {finding_id}\n\n{desc}\n\n**Recommended fix:** {fix}"
        if finding.get("root_cause_chain"):
            body += "\n\n**Root cause chain:**\n"
            for i, cause in enumerate(finding["root_cause_chain"]):
                body += f"{'  ' * i}→ {cause}\n"
        body += "\n\n---\n*Filed automatically by pipeline retro.*"

        try:
            subprocess.run(
                ["gh", "issue", "create", "--title", f"retro: {desc[:70]}", "--body", body],
                cwd=repo_root, capture_output=True, text=True, timeout=30, check=True,
            )
            log(logger, "retro", "ISSUE_CREATED", finding=finding_id)
        except (subprocess.TimeoutExpired, FileNotFoundError, subprocess.CalledProcessError):
            log(logger, "retro", "ISSUE_FAILED", finding=finding_id)

    fixable = auto_fixes + draft_fixes
    if fixable:
        log(logger, "retro", "AUTO_HEAL_AVAILABLE", count=len(fixable),
            note="Auto-heal not yet implemented")


def get_repo_slug(worktree_path):
    """Get owner/repo from git remote. Returns 'owner/repo' or None."""
    try:
        result = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"],
            cwd=worktree_path, capture_output=True, text=True, timeout=10,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    return None


def poll_gemini(worktree_path, pr_number, logger, min_wait=90, max_wait=300):
    """Poll for Gemini review comments. Returns list of comment dicts."""
    repo = get_repo_slug(worktree_path)
    if not repo:
        log(logger, "pr", "ERROR", reason="Cannot determine repo slug")
        return []

    start = time.time()
    last_count = 0
    stable_polls = 0

    while time.time() - start < max_wait:
        elapsed = time.time() - start

        # Don't check before minimum wait
        if elapsed < min_wait:
            time.sleep(30)
            continue

        try:
            result = subprocess.run(
                ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
                 "--jq", "length"],
                cwd=worktree_path, capture_output=True, text=True, timeout=15,
            )
            count = int(result.stdout.strip()) if result.returncode == 0 else 0
        except (subprocess.TimeoutExpired, FileNotFoundError, ValueError, OSError):
            count = 0

        log(logger, "pr", "GEMINI_POLL", elapsed=f"{int(elapsed)}s", comments=count)

        if count > 0 and count == last_count:
            stable_polls += 1
            if stable_polls >= 1:
                break  # Stable — two consecutive polls with same count
        else:
            stable_polls = 0

        last_count = count
        time.sleep(30)

    if last_count == 0:
        log(logger, "pr", "GEMINI_TIMEOUT")
        return []

    # Fetch full comments
    try:
        result = subprocess.run(
            ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
             "--jq", "[.[] | {id, user: .user.login, path, line, body}]"],
            cwd=worktree_path, capture_output=True, text=True, timeout=15,
        )
        if result.returncode == 0:
            return json.loads(result.stdout.strip())
    except (subprocess.TimeoutExpired, FileNotFoundError, json.JSONDecodeError, OSError):
        pass
    return []


def run_triage(worktree_path, pr_number, comments, logger, dry_run=False):
    """Invoke gemini-triage agent to fix/dismiss comments. Returns list or None."""
    state = read_state(worktree_path)
    spec_path = state.get("spec", "")

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

    exit_code, logger_out = run_claude(
        worktree_path, prompt, logger, "pr-triage",
        dry_run=dry_run, agent="gemini-triage",
    )

    if exit_code != 0:
        return None

    # Parse structured output from the human-readable stage log
    stage_log_dir = os.path.join(worktree_path, ".workflow", "stages")
    stage_log_path = os.path.join(stage_log_dir, "pr-triage.log")
    try:
        with open(stage_log_path, "r", errors="replace") as f:
            content = f.read()
        # Find JSON array using raw_decode (handles trailing text correctly)
        last_bracket = content.rfind("[")
        if last_bracket != -1:
            decoder = json.JSONDecoder()
            obj, _ = decoder.raw_decode(content[last_bracket:])
            if isinstance(obj, list):
                return obj
    except (OSError, json.JSONDecodeError, ValueError):
        pass
    return None


def post_replies(worktree_path, pr_number, triage_results, logger):
    """Post threaded replies to Gemini comments from triage results."""
    repo = get_repo_slug(worktree_path)
    if not repo:
        return

    for item in triage_results:
        comment_id = item.get("id")
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
            subprocess.run(
                ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
                 "-F", f"in_reply_to={comment_id}", "-f", f"body={body}"],
                cwd=worktree_path, capture_output=True, text=True, timeout=15,
            )
            log(logger, "pr", "REPLY_POSTED", comment_id=comment_id, action=action)
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            log(logger, "pr", "REPLY_FAILED", comment_id=comment_id)


def run_pr_stage(worktree_path, logger, dry_run=False):
    """Scripted PR stage: draft → poll Gemini → triage → ready → notify."""
    log(logger, "pr", "START")
    start = time.time()

    def _check_timeout():
        """Check if stage hard ceiling exceeded. Logs and returns True if timed out."""
        if (time.time() - start) > HARD_CEILING:
            log(logger, "pr", "HARD_TIMEOUT",
                elapsed=f"{int(time.time() - start)}s",
                ceiling=f"{HARD_CEILING}s")
            return True
        return False

    if dry_run:
        log(logger, "pr", "DONE", exit=0, duration="0s", mode="dry-run")
        return 0, logger

    # 1. Rebase on main
    subprocess.run(
        ["git", "fetch", "origin", "main"],
        cwd=worktree_path, capture_output=True, text=True, timeout=30,
    )
    result = subprocess.run(
        ["git", "rebase", "origin/main"],
        cwd=worktree_path, capture_output=True, text=True, timeout=60,
    )
    if result.returncode != 0:
        log(logger, "pr", "REBASE_CONFLICT", error=result.stderr.strip()[:200])
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger

    # 2. Push branch
    branch = subprocess.run(
        ["git", "rev-parse", "--abbrev-ref", "HEAD"],
        cwd=worktree_path, capture_output=True, text=True, timeout=10,
    ).stdout.strip()
    push_result = subprocess.run(
        ["git", "push", "-u", "origin", branch, "--force-with-lease"],
        cwd=worktree_path, capture_output=True, text=True, timeout=60,
    )
    if push_result.returncode != 0:
        log(logger, "pr", "PUSH_FAILED", error=push_result.stderr.strip()[:200])
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger

    # 3. Read issues from state
    issues_result = subprocess.run(
        ["python", "scripts/workflow-state.py", "get", "issues"],
        cwd=worktree_path, capture_output=True, text=True, timeout=10,
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
    body_parts.append(f"- [{'x' if any(verify.values()) else ' '}] /verify completed")
    body_parts.append(f"- [{'x' if any(qa.values()) else ' '}] /qa completed")
    body_parts.append(f"- [{'x' if review.get('passed') else ' '}] /review completed")
    body_parts.append("\n🤖 Generated with [Claude Code](https://claude.com/claude-code)")
    pr_body = "\n".join(body_parts)

    result = subprocess.run(
        ["gh", "pr", "create", "--draft", "--title", pr_title, "--body", pr_body],
        cwd=worktree_path, capture_output=True, text=True, timeout=30,
    )
    if result.returncode != 0:
        log(logger, "pr", "CREATE_FAILED", error=result.stderr.strip()[:200])
        log(logger, "pr", "DONE", exit=1, duration=f"{int(time.time() - start)}s")
        return 1, logger

    pr_url = result.stdout.strip()
    # Extract PR number from URL
    pr_number = pr_url.rstrip("/").split("/")[-1]
    log(logger, "pr", "PR_CREATED", url=pr_url, draft=True)

    # Write workflow state immediately
    for cmd_args in [
        ["set", "pr.url", pr_url],
        ["set", "pr.created", "now"],
    ]:
        subprocess.run(
            ["python", "scripts/workflow-state.py"] + cmd_args,
            cwd=worktree_path, capture_output=True, text=True, timeout=10,
        )

    # Check timeout before polling
    if _check_timeout():
        log(logger, "pr", "DONE", exit=-1, duration=f"{int(time.time() - start)}s")
        return -1, logger

    # 5. Poll for Gemini comments
    comments = poll_gemini(worktree_path, pr_number, logger)

    # 6. Handle triage or annotation
    annotation = None
    if not comments:
        annotation = "\n\n**Gemini:** no review received within 5 minutes."
    else:
        triage_results = run_triage(worktree_path, pr_number, comments, logger,
                                    dry_run=dry_run)
        if triage_results is None:
            annotation = "\n\n**Gemini:** triage incomplete — manual review needed."
        else:
            # Verify push before posting replies
            try:
                local_head = subprocess.run(
                    ["git", "rev-parse", "HEAD"],
                    cwd=worktree_path, capture_output=True, text=True, timeout=10,
                ).stdout.strip()
                remote_result = subprocess.run(
                    ["git", "ls-remote", "origin", branch],
                    cwd=worktree_path, capture_output=True, text=True, timeout=10,
                )
                remote_head = remote_result.stdout.split()[0] if remote_result.stdout.strip() else ""
                if local_head == remote_head:
                    post_replies(worktree_path, pr_number, triage_results, logger)
                else:
                    log(logger, "pr", "PUSH_MISMATCH",
                        local=local_head[:8], remote=remote_head[:8])
                    annotation = ("\n\n**Gemini:** triage fixes committed locally "
                                  "but push may have failed. Check branch.")
            except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
                annotation = "\n\n**Gemini:** could not verify push status."

    # Check timeout before finalizing
    if _check_timeout():
        # Still convert to ready so the PR isn't stuck as draft
        subprocess.run(
            ["gh", "pr", "ready", pr_number],
            cwd=worktree_path, capture_output=True, text=True, timeout=30,
        )
        log(logger, "pr", "DONE", exit=-1, duration=f"{int(time.time() - start)}s")
        return -1, logger

    # Append annotation to PR body if needed
    if annotation:
        subprocess.run(
            ["gh", "pr", "edit", pr_number, "--body", pr_body + annotation],
            cwd=worktree_path, capture_output=True, text=True, timeout=30,
        )

    # 7. Convert draft → ready
    subprocess.run(
        ["gh", "pr", "ready", pr_number],
        cwd=worktree_path, capture_output=True, text=True, timeout=30,
    )
    log(logger, "pr", "PR_READY", url=pr_url)

    # 8. Write final workflow state
    subprocess.run(
        ["python", "scripts/workflow-state.py", "set", "pr.gemini_triaged", "true"],
        cwd=worktree_path, capture_output=True, text=True, timeout=10,
    )

    duration = int(time.time() - start)
    log(logger, "pr", "DONE", exit=0, duration=f"{duration}s")
    return 0, logger


def _read_last_lines(worktree_path, stage_name, n=50):
    """Read last N lines from a stage's .log file. Returns list of strings."""
    log_path = os.path.join(worktree_path, ".workflow", "stages", f"{stage_name}.log")
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
    parser.add_argument("--worktree", help="Use existing worktree instead of creating one")
    parser.add_argument("--issue", type=int, action="append", default=[], help="GitHub issue number(s) (repeatable)")
    parser.add_argument("--dry-run", action="store_true", help="Run orchestration without invoking claude -p")
    parser.add_argument("--stage-timeout", type=int, help="(Deprecated — ignored. Timeouts are now activity-based.)")
    args = parser.parse_args()

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
            print(f"Resuming after '{last_done}' (stage {start_idx + 1}/{len(STAGES)})")
        else:
            print("No completed stages found in pipeline.log, starting from beginning.")

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
    logger = open_logger(log_path, mode)

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

    pipeline_start = time.time()
    pr_url = None
    stage_durations = {}
    _failed_stage = None
    _failed_log_stage = None  # actual log filename (may differ from display name)
    _failed_reason = None
    _result_written = False

    def _pipeline_fail(stage_name, reason=None, log_stage=None):
        nonlocal _failed_stage, _failed_log_stage, _failed_reason
        _failed_stage = stage_name
        _failed_log_stage = log_stage or stage_name
        _failed_reason = reason
        raise PipelineFailure(f"{stage_name}: {reason}")

    try:
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
                        cwd=worktree_path, capture_output=True, text=True,
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
                exit_code, logger = run_claude(worktree_path, prompt, logger, "implement", args.dry_run)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", failed_stage="implement")
                    _pipeline_fail("implement")

                # P4: Outcome verification + retry
                if not verify_outcome(worktree_path, "implement", pre_commits) and not args.dry_run:
                    log(logger, "implement", "OUTCOME_MISS", reason="no new commits, retrying")
                    exit_code, logger = run_claude(worktree_path, prompt, logger, "implement-retry", args.dry_run)
                    if exit_code != 0 or not verify_outcome(worktree_path, "implement", pre_commits):
                        log(logger, "pipeline", "FAILED", failed_stage="implement", reason="outcome verification failed")
                        _pipeline_fail("implement", "outcome verification failed")

                # P8: Copy plan artifact back to main
                copy_plan_to_main(worktree_path, repo_root, logger)

            elif stage in ("gates", "verify", "qa", "review"):
                prompt = f"/{stage}"
                exit_code, logger = run_claude(worktree_path, prompt, logger, stage, args.dry_run)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", failed_stage=stage)
                    _pipeline_fail(stage)

                # P4: Outcome verification + retry
                if not verify_outcome(worktree_path, stage, 0) and not args.dry_run:
                    log(logger, stage, "OUTCOME_MISS", reason="expected state not set, retrying")
                    exit_code, logger = run_claude(worktree_path, prompt, logger, f"{stage}-retry", args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage=stage)
                        _pipeline_fail(stage)

            elif stage == "converge":
                if check_review_passed(worktree_path):
                    log(logger, "converge", "SKIPPED", reason="review already passed")
                    continue

                for round_num in range(args.max_converge):
                    log(logger, "converge", "ROUND_START", round=round_num + 1, max=args.max_converge)

                    converge_log = f"converge-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/converge", logger, converge_log, args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="converge")
                        _pipeline_fail("converge", log_stage=converge_log)

                    gates_log = f"gates-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/gates", logger, gates_log, args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="gates-reconverge")
                        _pipeline_fail("gates-reconverge", log_stage=gates_log)

                    verify_log = f"verify-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/verify", logger, verify_log, args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="verify-reconverge")
                        _pipeline_fail("verify-reconverge", log_stage=verify_log)

                    qa_log = f"qa-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/qa", logger, qa_log, args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", failed_stage="qa-reconverge")
                        _pipeline_fail("qa-reconverge", log_stage=qa_log)

                    review_log = f"review-r{round_num + 1}"
                    exit_code, logger = run_claude(worktree_path, "/review", logger, review_log, args.dry_run)
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
                    worktree_path, logger, dry_run=args.dry_run)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", failed_stage="pr")
                    _pipeline_fail("pr")
                pr_url = check_pr_created(worktree_path)

            elif stage == "retro":
                exit_code, logger = run_claude(worktree_path, "/retro", logger, "retro", args.dry_run)
                if exit_code != 0:
                    log(logger, "retro", "FAILED_NON_BLOCKING")
                else:
                    process_retro_findings(worktree_path, logger, repo_root)

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
        print(f"\nPipeline complete in {duration}s.")
        if pr_url:
            print(f"PR: {pr_url}")

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
                    worktree_path, "/retro", logger, "retro", args.dry_run)
                if exit_code != 0:
                    log(logger, "retro", "FAILED_NON_BLOCKING")
                else:
                    process_retro_findings(worktree_path, logger, repo_root)
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
        release_lock(lock_path)
        logger.close()


if __name__ == "__main__":
    main()
