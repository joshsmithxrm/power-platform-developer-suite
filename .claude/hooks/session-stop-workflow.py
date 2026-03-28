#!/usr/bin/env python3
"""
Stop hook: blocks session end when critical workflow steps are incomplete.
Returns exit code 2 with decision:block to prevent premature stopping.
Returns exit code 0 when workflow is complete or on main branch.
"""
import json
import os
import subprocess
import sys
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir


def main():
    # Pipeline/shakedown mode: orchestrator handles stage sequencing
    if os.environ.get("PPDS_PIPELINE") or os.environ.get("PPDS_SHAKEDOWN"):
        sys.exit(0)

    # Read stdin
    hook_input = {}
    try:
        hook_input = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass

    project_dir = get_project_dir()

    # Get current branch
    branch = "unknown"
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            branch = result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    # On main: always allow stop (no workflow enforcement)
    if branch in ("main", "master", "unknown"):
        sys.exit(0)

    state_path = os.path.join(project_dir, ".workflow", "state.json")
    os.makedirs(os.path.dirname(state_path), exist_ok=True)

    # No state file — no workflow tracked, allow stop
    if not os.path.exists(state_path):
        sys.exit(0)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        sys.exit(0)

    # If stop hook has already blocked 3+ times, allow stop to prevent infinite loop
    if state.get("stop_hook_count", 0) >= 3:
        sys.exit(0)

    # Phase-aware bypass: non-implementing phases don't need workflow enforcement
    phase = state.get("phase")
    if phase in ("starting", "investigating", "design", "reviewing", "qa", "shakedown", "retro", "pr"):
        sys.exit(0)

    # Get current HEAD for staleness check
    head_sha = None
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            head_sha = result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    # Code change detection: skip enforcement if no source code changes beyond main
    # (spec updates and docs don't owe gates/verify/review)
    try:
        result = subprocess.run(
            ["git", "diff", "--name-only", "origin/main...HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            changed_files = [
                f
                for f in result.stdout.strip().split("\n")
                if f.strip()
            ]
            # Prefixes that don't require workflow enforcement
            non_code_prefixes = (
                "specs/",
                ".plans/",
                "docs/",
                "README",
                "CLAUDE.md",
            )
            has_code_changes = any(
                not f.startswith(non_code_prefixes) for f in changed_files
            )
            if not has_code_changes:
                # Only spec/docs/config changes — no gates needed
                sys.exit(0)
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    # Check for uncommitted changes
    uncommitted = 0
    try:
        result = subprocess.run(
            ["git", "status", "--porcelain"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            uncommitted = len(
                [line for line in result.stdout.strip().split("\n") if line.strip()]
            )
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    # --- Determine what's missing ---
    missing = []

    # Gates
    gates = state.get("gates", {})
    gates_ref = gates.get("commit_ref")
    gates_passed = gates.get("passed")
    if head_sha is None:
        # git rev-parse failed — cannot determine staleness, skip stale check
        gates_current = gates_passed
    else:
        gates_current = gates_passed and gates_ref and gates_ref == head_sha
    if not gates_current:
        if gates_passed:
            missing.append("/gates (stale — code changed since last run)")
        else:
            missing.append("/gates")

    # Valid surface keys
    valid_surfaces = ("ext", "tui", "mcp", "cli", "workflow")

    # Verify (at least one valid surface)
    verify = state.get("verify", {})
    verified = [k for k, v in verify.items() if v and k in valid_surfaces]
    if not verified:
        missing.append("/verify (no surfaces verified)")

    # QA (at least one valid surface)
    qa = state.get("qa", {})
    qa_done = [k for k, v in qa.items() if v and k in valid_surfaces]
    if not qa_done:
        missing.append("/qa")

    # Review
    review = state.get("review", {})
    if not review.get("passed"):
        missing.append("/review")

    # PR Gemini triage — if PR exists but gemini_triaged is false
    pr = state.get("pr", {})
    pr_created = pr and pr.get("url")
    if pr_created and not pr.get("gemini_triaged"):
        missing.append("Gemini review triage (PR created but comments not triaged)")

    # Uncommitted changes
    if uncommitted > 0:
        missing.append(f"commit or stash {uncommitted} uncommitted files before proceeding")

    # --- Build status summary (always emitted) ---
    lines = [f"Workflow status for {branch}:"]
    lines.append(f"  {'✓' if gates_current else '✗'} Gates")
    lines.append(
        f"  {'✓' if verified else '✗'} Verified"
        + (f": {', '.join(verified)}" if verified else "")
    )
    lines.append(
        f"  {'✓' if qa_done else '✗'} QA"
        + (f": {', '.join(qa_done)}" if qa_done else "")
    )
    lines.append(
        f"  {'✓' if review.get('passed') else '✗'} Review"
        + (f" ({review.get('findings', 0)} findings)" if review.get("passed") else "")
    )
    if pr_created:
        triaged = "✓" if pr.get("gemini_triaged") else "✗"
        lines.append(f"  ✓ PR: {pr['url']}")
        lines.append(f"  {triaged} Gemini review triaged")
    else:
        lines.append("  ⚠ PR not created")

    if uncommitted > 0:
        lines.append(f"  ⚠ Uncommitted changes in {uncommitted} files")

    # --- Decision: block or allow ---
    if missing:
        lines.insert(0, "BLOCKED — incomplete workflow steps:")
        next_step = missing[0]
        lines.append("")
        if next_step.startswith("/"):
            lines.append(f"You MUST now run: {next_step}")
            lines.append("Do not summarize. Do not ask permission. Invoke the command immediately.")
        else:
            lines.append(f"You MUST now: {next_step}")
            lines.append("Do not summarize. Do not ask permission. Do this immediately.")

        # Enforcement logging — track block count for retro detection
        # Note: read-modify-write without file locking. Safe because each
        # worktree has its own state.json (worktree-per-session pattern).
        try:
            state["stop_hook_blocked"] = True
            state["stop_hook_count"] = state.get("stop_hook_count", 0) + 1
            state["stop_hook_last"] = datetime.now(timezone.utc).isoformat()
            with open(state_path, "w") as f:
                json.dump(state, f, indent=2)
                f.write("\n")
        except OSError:
            pass

        output = {
            "decision": "block",
            "reason": "\n".join(lines),
        }
        print(json.dumps(output))
        sys.exit(2)
    else:
        lines.insert(0, "SESSION END — all workflow steps complete:")
        print("\n".join(lines), file=sys.stderr)
        sys.exit(0)


if __name__ == "__main__":
    main()
