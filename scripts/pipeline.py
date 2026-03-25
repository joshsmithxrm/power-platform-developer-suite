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
    "review": "review_results",
    "pr": "pr_url",
}


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
                    elif stage_name.startswith("review-r"):
                        stage_name = "converge"
                    if stage_name in STAGES:
                        last_done = stage_name
    except OSError:
        return None
    return last_done


def run_claude(worktree_path, prompt, logger, stage, dry_run=False):
    """Run `claude -p` in the worktree directory. Returns exit code."""
    full_prompt = HEADLESS_PREAMBLE + prompt

    # Close logger before subprocess to release file handle (P2)
    logger.flush()

    log(logger, stage, "START")
    logger.close()

    if dry_run:
        time.sleep(0.1)  # Simulate
        # Reopen logger
        logger_new = open_logger(logger.name)
        log(logger_new, stage, "DONE", exit=0, duration="0s", mode="dry-run")
        return 0, logger_new

    start = time.time()
    env = os.environ.copy()
    env["MSYS_NO_PATHCONV"] = "1"  # P1: Prevent Git Bash path expansion

    try:
        result = subprocess.run(
            ["claude", "-p", full_prompt, "--verbose"],
            cwd=worktree_path,
            env=env,
            capture_output=True,  # P6: Capture stdout/stderr
            text=True,
            timeout=3600,
        )
        duration = int(time.time() - start)

        # Reopen logger after subprocess (P2)
        logger_new = open_logger(logger.name)

        # P6: Write captured output to log
        if result.stdout:
            for line in result.stdout.strip().split("\n")[-20:]:  # Last 20 lines
                log(logger_new, stage, "STDOUT", line=line[:200])
        if result.stderr:
            for line in result.stderr.strip().split("\n")[-10:]:
                log(logger_new, stage, "STDERR", line=line[:200])

        log(logger_new, stage, "DONE", exit=result.returncode, duration=f"{duration}s")
        return result.returncode, logger_new
    except subprocess.TimeoutExpired:
        duration = int(time.time() - start)
        logger_new = open_logger(logger.name)
        log(logger_new, stage, "TIMEOUT", duration=f"{duration}s")
        print(f"\nERROR: '{stage}' stage timed out after 3600s.", file=sys.stderr)
        return 1, logger_new
    except FileNotFoundError:
        logger_new = open_logger(logger.name)
        log(logger_new, stage, "ERROR", reason="claude command not found")
        print("\nERROR: 'claude' command not found. Is Claude Code installed and on PATH?", file=sys.stderr)
        return 1, logger_new


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

    pipeline_start = time.time()
    pr_url = None

    try:
        for i, stage in enumerate(STAGES):
            if i < start_idx:
                continue

            if stage == "retro" and args.no_retro:
                log(logger, "retro", "SKIPPED", reason="--no-retro flag")
                continue

            if stage == "worktree":
                if worktree_path and os.path.exists(worktree_path):
                    log(logger, "worktree", "EXISTS", path=worktree_path)
                else:
                    worktree_path = create_worktree(repo_root, name, branch, logger)
                    if not worktree_path:
                        log(logger, "pipeline", "FAILED", stage="worktree")
                        sys.exit(1)

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
                    log(logger, "pipeline", "FAILED", stage="implement")
                    sys.exit(1)

                # P4: Outcome verification + retry
                if not verify_outcome(worktree_path, "implement", pre_commits) and not args.dry_run:
                    log(logger, "implement", "OUTCOME_MISS", reason="no new commits, retrying")
                    exit_code, logger = run_claude(worktree_path, prompt, logger, "implement-retry", args.dry_run)
                    if exit_code != 0 or not verify_outcome(worktree_path, "implement", pre_commits):
                        log(logger, "pipeline", "FAILED", stage="implement", reason="outcome verification failed")
                        sys.exit(1)

                # P8: Copy plan artifact back to main
                copy_plan_to_main(worktree_path, repo_root, logger)

            elif stage in ("gates", "verify", "review"):
                prompt = f"/{stage}"
                exit_code, logger = run_claude(worktree_path, prompt, logger, stage, args.dry_run)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage=stage)
                    sys.exit(1)

                # P4: Outcome verification + retry
                if not verify_outcome(worktree_path, stage, 0) and not args.dry_run:
                    log(logger, stage, "OUTCOME_MISS", reason="expected state not set, retrying")
                    exit_code, logger = run_claude(worktree_path, prompt, logger, f"{stage}-retry", args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage=stage)
                        sys.exit(1)

            elif stage == "converge":
                if check_review_passed(worktree_path):
                    log(logger, "converge", "SKIPPED", reason="review already passed")
                    continue

                for round_num in range(args.max_converge):
                    log(logger, "converge", "ROUND_START", round=round_num + 1, max=args.max_converge)

                    exit_code, logger = run_claude(worktree_path, "/converge", logger, f"converge-r{round_num + 1}", args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="converge")
                        sys.exit(1)

                    exit_code, logger = run_claude(worktree_path, "/gates", logger, f"gates-r{round_num + 1}", args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="gates-reconverge")
                        sys.exit(1)

                    exit_code, logger = run_claude(worktree_path, "/verify", logger, f"verify-r{round_num + 1}", args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="verify-reconverge")
                        sys.exit(1)

                    exit_code, logger = run_claude(worktree_path, "/review", logger, f"review-r{round_num + 1}", args.dry_run)
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="review-reconverge")
                        sys.exit(1)

                    if check_review_passed(worktree_path):
                        log(logger, "converge", "CONVERGED", rounds=round_num + 1)
                        break
                else:
                    log(logger, "converge", "FAILED_TO_CONVERGE", max_rounds=args.max_converge)
                    log(logger, "pipeline", "FAILED", stage="converge", reason="max rounds exceeded")
                    print(f"\nFAILED: Could not converge after {args.max_converge} rounds.", file=sys.stderr)
                    sys.exit(1)

            elif stage == "pr":
                exit_code, logger = run_claude(worktree_path, "/pr", logger, "pr", args.dry_run)
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage="pr")
                    sys.exit(1)
                pr_url = check_pr_created(worktree_path)

            elif stage == "retro":
                exit_code, logger = run_claude(worktree_path, "/retro", logger, "retro", args.dry_run)
                if exit_code != 0:
                    log(logger, "retro", "FAILED_NON_BLOCKING")
                else:
                    process_retro_findings(worktree_path, logger, repo_root)

        duration = int(time.time() - pipeline_start)
        log(logger, "pipeline", "COMPLETE", duration=f"{duration}s", pr=pr_url or "none")
        print(f"\nPipeline complete in {duration}s.")
        if pr_url:
            print(f"PR: {pr_url}")

    except KeyboardInterrupt:
        log(logger, "pipeline", "INTERRUPTED")
        print("\nPipeline interrupted by user.", file=sys.stderr)
        sys.exit(130)
    finally:
        logger.close()


if __name__ == "__main__":
    main()
