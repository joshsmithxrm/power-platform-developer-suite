#!/usr/bin/env python3
"""
Deterministic pipeline orchestrator for PPDS development workflow.

Runs /implement → /gates → /verify → /review → /converge → /pr → /retro
as sequential `claude -p` sessions. Each step gets a fresh context window.
The script — not the AI — decides what runs next.

Usage:
    python scripts/pipeline.py <plan-path> [options]

Options:
    --from <step>       Resume from a specific step
    --name <name>       Override worktree/branch name
    --no-retro          Skip the post-PR retro step
    --max-converge <n>  Max converge rounds (default: 3)
    --worktree <path>   Use existing worktree instead of creating one
    --issue <N>         GitHub issue number(s) this work closes (repeatable)
"""
import argparse
import json
import os
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
    # Also print to console with local time for readability
    console_parts = [f"[{local_time()}] {stage}: {event}"]
    for k, v in extra.items():
        console_parts.append(f"{k}={v}")
    print(" ".join(console_parts))


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


def run_claude(worktree_path, prompt, logger, stage):
    """Run `claude -p` in the worktree directory. Returns exit code."""
    log(logger, stage, "START")
    start = time.time()

    try:
        result = subprocess.run(
            ["claude", "-p", prompt, "--verbose"],
            cwd=worktree_path,
            timeout=1800,  # 30 minute timeout per stage
        )
        duration = int(time.time() - start)
        log(logger, stage, "DONE", exit=result.returncode, duration=f"{duration}s")
        return result.returncode
    except subprocess.TimeoutExpired:
        duration = int(time.time() - start)
        log(logger, stage, "TIMEOUT", duration=f"{duration}s")
        return 1
    except FileNotFoundError:
        log(logger, stage, "ERROR", reason="claude command not found")
        print(
            "\nERROR: 'claude' command not found. Is Claude Code installed and on PATH?",
            file=sys.stderr,
        )
        return 1


def derive_name(plan_path):
    """Derive worktree/branch name from plan filename."""
    stem = Path(plan_path).stem
    # Strip date prefix if present (e.g., 2026-03-24-pipeline-orchestrator → pipeline-orchestrator)
    parts = stem.split("-")
    # Check if first 3 parts look like a date (YYYY-MM-DD)
    if len(parts) >= 4:
        try:
            int(parts[0])
            int(parts[1])
            int(parts[2])
            return "-".join(parts[3:])
        except ValueError:
            pass
    return stem


def create_worktree(repo_root, name, logger):
    """Create a git worktree and initialize workflow state."""
    worktree_path = os.path.join(repo_root, ".worktrees", name)
    branch = f"feature/{name}"

    # Check if worktree already exists
    if os.path.exists(worktree_path):
        log(logger, "worktree", "EXISTS", path=worktree_path, branch=branch)
        return worktree_path

    # Check if branch already exists
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
        log(
            logger,
            "worktree",
            "FAILED",
            error=result.stderr.strip(),
        )
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
        logger,
        "retro",
        "FINDINGS_SUMMARY",
        auto_fix=len(auto_fixes),
        draft_fix=len(draft_fixes),
        issue_only=len(issues),
    )

    # Create GitHub issues for issue-only findings
    for finding in issues:
        desc = finding.get("description", "No description")
        fix = finding.get("fix_description", "")
        finding_id = finding.get("id", "R-??")

        body = f"## Retro Finding {finding_id}\n\n{desc}\n\n**Recommended fix:** {fix}"
        if finding.get("root_cause_chain"):
            body += "\n\n**Root cause chain:**\n"
            for i, cause in enumerate(finding["root_cause_chain"]):
                body += f"{'  ' * i}→ {cause}\n"
        body += "\n\n---\n*Filed automatically by pipeline retro. Needs triage: assign `type:`, `area:`, and milestone or `status:` label.*"

        try:
            subprocess.run(
                [
                    "gh",
                    "issue",
                    "create",
                    "--title",
                    f"retro: {desc[:70]}",
                    "--body",
                    body,
                ],
                cwd=repo_root,
                capture_output=True,
                text=True,
                timeout=30,
                check=True,
            )
            log(logger, "retro", "ISSUE_CREATED", finding=finding_id)
        except (subprocess.TimeoutExpired, FileNotFoundError, subprocess.CalledProcessError):
            log(logger, "retro", "ISSUE_FAILED", finding=finding_id)

    # For auto-fix and draft-fix, spawn pipeline recursively
    fixable = auto_fixes + draft_fixes
    if fixable:
        log(
            logger,
            "retro",
            "AUTO_HEAL_AVAILABLE",
            count=len(fixable),
            note="Auto-heal not yet implemented — findings logged for manual review",
        )
        # TODO: Spawn pipeline.py --no-retro on a new branch for fixable findings
        # This requires generating a plan file from the findings, which is a future enhancement


def main():
    parser = argparse.ArgumentParser(
        description="PPDS Deterministic Pipeline Orchestrator"
    )
    parser.add_argument("plan", help="Path to the implementation plan file")
    parser.add_argument(
        "--from",
        dest="from_stage",
        choices=STAGES,
        help="Resume from a specific stage",
    )
    parser.add_argument(
        "--name", help="Override worktree/branch name (default: derived from plan)"
    )
    parser.add_argument(
        "--no-retro", action="store_true", help="Skip the post-PR retro step"
    )
    parser.add_argument(
        "--max-converge",
        type=int,
        default=3,
        help="Max converge rounds (default: 3)",
    )
    parser.add_argument(
        "--worktree", help="Use existing worktree instead of creating one"
    )
    parser.add_argument(
        "--issue",
        type=int,
        action="append",
        default=[],
        help="GitHub issue number(s) this work closes (repeatable)",
    )
    args = parser.parse_args()

    # Resolve paths
    repo_root = os.getcwd()
    plan_path = args.plan
    if not os.path.isabs(plan_path):
        plan_path = os.path.join(repo_root, plan_path)

    if not os.path.exists(plan_path):
        print(f"ERROR: Plan file not found: {plan_path}", file=sys.stderr)
        sys.exit(1)

    # Make plan path relative to repo root for portability
    try:
        plan_rel = os.path.relpath(plan_path, repo_root)
    except ValueError:
        plan_rel = plan_path

    name = args.name or derive_name(plan_path)

    # Determine which stages to run
    start_idx = 0
    if args.from_stage:
        start_idx = STAGES.index(args.from_stage)

    # Set up worktree
    if args.worktree:
        worktree_path = os.path.abspath(args.worktree)
        if not os.path.exists(worktree_path):
            print(
                f"ERROR: Worktree not found: {worktree_path}",
                file=sys.stderr,
            )
            sys.exit(1)
    elif start_idx > 0:
        # Resuming — worktree should already exist
        worktree_path = os.path.join(repo_root, ".worktrees", name)
        if not os.path.exists(worktree_path):
            print(
                f"ERROR: Worktree not found at {worktree_path}. "
                f"Use --worktree to specify the path.",
                file=sys.stderr,
            )
            sys.exit(1)
    else:
        worktree_path = None  # Will be created in the worktree stage

    # Open log file
    if worktree_path:
        log_dir = os.path.join(worktree_path, ".workflow")
    else:
        log_dir = os.path.join(repo_root, ".worktrees", name, ".workflow")
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.join(log_dir, "pipeline.log")

    # Append mode for resume
    mode = "a" if args.from_stage else "w"
    logger = open(log_path, mode)

    log(
        logger,
        "pipeline",
        "START" if not args.from_stage else "RESUME",
        plan=plan_rel,
        name=name,
        from_stage=args.from_stage or "worktree",
    )

    pipeline_start = time.time()
    pr_url = None

    try:
        for i, stage in enumerate(STAGES):
            if i < start_idx:
                continue

            # Skip retro if --no-retro
            if stage == "retro" and args.no_retro:
                log(logger, "retro", "SKIPPED", reason="--no-retro flag")
                continue

            if stage == "worktree":
                if worktree_path and os.path.exists(worktree_path):
                    log(logger, "worktree", "EXISTS", path=worktree_path)
                else:
                    worktree_path = create_worktree(repo_root, name, logger)
                    if not worktree_path:
                        log(logger, "pipeline", "FAILED", stage="worktree")
                        sys.exit(1)

                # Update log path now that we know the worktree
                new_log_dir = os.path.join(worktree_path, ".workflow")
                os.makedirs(new_log_dir, exist_ok=True)
                new_log_path = os.path.join(new_log_dir, "pipeline.log")
                if new_log_path != log_path:
                    # Copy what we have so far
                    logger.close()
                    if os.path.exists(log_path):
                        import shutil

                        shutil.copy2(log_path, new_log_path)
                    log_path = new_log_path
                    logger = open(log_path, "a")

                # Set plan path and started timestamp in workflow state
                for state_args in [
                    ["set", "plan", plan_rel],
                    ["set", "started", "now"],
                ]:
                    result = subprocess.run(
                        ["python", "scripts/workflow-state.py"] + state_args,
                        cwd=worktree_path,
                        capture_output=True,
                        text=True,
                    )
                    if result.returncode != 0:
                        log(logger, "worktree", "STATE_SET_FAILED", error=result.stderr.strip())

                # Store linked issue numbers in workflow state
                for issue_num in args.issue:
                    subprocess.run(
                        ["python", "scripts/workflow-state.py", "append", "issues", str(issue_num)],
                        cwd=worktree_path,
                        capture_output=True,
                        text=True,
                    )
                if args.issue:
                    log(logger, "worktree", "ISSUES_LINKED", issues=args.issue)

            elif stage == "implement":
                exit_code = run_claude(
                    worktree_path,
                    f"/implement {plan_rel}",
                    logger,
                    "implement",
                )
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage="implement")
                    sys.exit(1)

            elif stage == "gates":
                exit_code = run_claude(worktree_path, "/gates", logger, "gates")
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage="gates")
                    sys.exit(1)

            elif stage == "verify":
                exit_code = run_claude(worktree_path, "/verify", logger, "verify")
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage="verify")
                    sys.exit(1)

            elif stage == "review":
                exit_code = run_claude(worktree_path, "/review", logger, "review")
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage="review")
                    sys.exit(1)

            elif stage == "converge":
                # Converge loop: review already ran above, check if it passed
                if check_review_passed(worktree_path):
                    log(logger, "converge", "SKIPPED", reason="review already passed")
                    continue

                for round_num in range(args.max_converge):
                    log(
                        logger,
                        "converge",
                        "ROUND_START",
                        round=round_num + 1,
                        max=args.max_converge,
                    )

                    # Fix findings
                    exit_code = run_claude(
                        worktree_path, "/converge", logger, f"converge-r{round_num + 1}"
                    )
                    if exit_code != 0:
                        log(
                            logger,
                            "converge",
                            "FIX_FAILED",
                            round=round_num + 1,
                        )
                        log(logger, "pipeline", "FAILED", stage="converge")
                        sys.exit(1)

                    # Re-gate
                    exit_code = run_claude(
                        worktree_path, "/gates", logger, f"gates-r{round_num + 1}"
                    )
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="gates-reconverge")
                        sys.exit(1)

                    # Re-verify
                    exit_code = run_claude(
                        worktree_path, "/verify", logger, f"verify-r{round_num + 1}"
                    )
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="verify-reconverge")
                        sys.exit(1)

                    # Re-review
                    exit_code = run_claude(
                        worktree_path, "/review", logger, f"review-r{round_num + 1}"
                    )
                    if exit_code != 0:
                        log(logger, "pipeline", "FAILED", stage="review-reconverge")
                        sys.exit(1)

                    if check_review_passed(worktree_path):
                        log(
                            logger,
                            "converge",
                            "CONVERGED",
                            rounds=round_num + 1,
                        )
                        break
                else:
                    # Max rounds exceeded
                    log(
                        logger,
                        "converge",
                        "FAILED_TO_CONVERGE",
                        max_rounds=args.max_converge,
                    )
                    log(logger, "pipeline", "FAILED", stage="converge", reason="max rounds exceeded")
                    print(
                        f"\nFAILED: Could not converge after {args.max_converge} rounds.",
                        file=sys.stderr,
                    )
                    sys.exit(1)

            elif stage == "pr":
                exit_code = run_claude(worktree_path, "/pr", logger, "pr")
                if exit_code != 0:
                    log(logger, "pipeline", "FAILED", stage="pr")
                    sys.exit(1)
                pr_url = check_pr_created(worktree_path)

            elif stage == "retro":
                exit_code = run_claude(worktree_path, "/retro", logger, "retro")
                # Retro failure is non-blocking
                if exit_code != 0:
                    log(logger, "retro", "FAILED_NON_BLOCKING")
                else:
                    process_retro_findings(worktree_path, logger, repo_root)

        # Pipeline complete
        duration = int(time.time() - pipeline_start)
        log(
            logger,
            "pipeline",
            "COMPLETE",
            duration=f"{duration}s",
            pr=pr_url or "none",
        )
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
