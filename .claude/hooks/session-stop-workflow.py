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


def main():
    # Read stdin
    try:
        json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass

    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())

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

    state_path = os.path.join(project_dir, ".claude", "workflow-state.json")

    # No state file — no workflow tracked, allow stop
    if not os.path.exists(state_path):
        sys.exit(0)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
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
    gates_current = gates_passed and gates_ref and head_sha and gates_ref == head_sha
    if not gates_current:
        if gates_passed:
            missing.append("/gates (stale — code changed since last run)")
        else:
            missing.append("/gates")

    # Verify (at least one surface)
    verify = state.get("verify", {})
    verified = [k for k, v in verify.items() if v]
    if not verified:
        missing.append("/verify (no surfaces verified)")

    # QA (at least one surface)
    qa = state.get("qa", {})
    qa_done = [k for k, v in qa.items() if v]
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
        missing.append(f"uncommitted changes ({uncommitted} files)")

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
        lines.append("")
        lines.append("Remaining steps:")
        for step in missing:
            lines.append(f"  → {step}")
        lines.append("")
        lines.append(
            "Complete these steps before stopping. "
            "Run them in order: /gates → /verify → /qa → /review → /pr"
        )

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
