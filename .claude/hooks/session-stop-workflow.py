#!/usr/bin/env python3
"""
Stop hook: emits workflow completion summary when session ends.
Cannot block session end — always exits 0.
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
    state_path = os.path.join(project_dir, ".claude", "workflow-state.json")

    # No state file — nothing to report
    if not os.path.exists(state_path):
        sys.exit(0)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        sys.exit(0)

    # Get branch
    branch = state.get("branch", "unknown")

    # Get current HEAD
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
            uncommitted = len([line for line in result.stdout.strip().split("\n") if line.strip()])
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    lines = [f"SESSION END — Workflow status for {branch}:"]

    # Gates
    gates = state.get("gates", {})
    gates_ref = gates.get("commit_ref")
    gates_passed = gates.get("passed")
    if gates_passed and gates_ref and head_sha and gates_ref == head_sha:
        lines.append("  ✓ Gates passed")
    elif gates_passed:
        lines.append("  ⚠ Gates stale (code changed since last run)")
    else:
        lines.append("  ✗ Gates not run")

    # Verify
    verify = state.get("verify", {})
    verified = [k for k, v in verify.items() if v]
    if verified:
        lines.append(f"  ✓ Verified: {', '.join(verified)}")
    else:
        lines.append("  ✗ No surfaces verified")

    # QA
    qa = state.get("qa", {})
    qa_done = [k for k, v in qa.items() if v]
    if qa_done:
        lines.append(f"  ✓ QA completed: {', '.join(qa_done)}")
    else:
        lines.append("  ✗ QA not completed — /qa was never run")

    # Review
    review = state.get("review", {})
    if review.get("passed"):
        lines.append(f"  ✓ Review passed ({review.get('findings', 0)} findings)")
    else:
        lines.append("  ✗ Review not completed — /review was never run")

    # PR
    pr = state.get("pr", {})
    if pr and pr.get("url"):
        lines.append(f"  ✓ PR: {pr['url']}")
    else:
        lines.append("  ⚠ PR not created")

    # Uncommitted changes
    if uncommitted > 0:
        lines.append(f"  ⚠ Uncommitted changes in {uncommitted} files")

    print("\n".join(lines), file=sys.stderr)
    sys.exit(0)


if __name__ == "__main__":
    main()
