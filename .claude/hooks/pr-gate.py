#!/usr/bin/env python3
"""
PR gate hook: blocks gh pr create if workflow steps are incomplete.
Hard gate — exits code 2 to prevent PR creation.
"""
import json
import os
import subprocess
import sys


def main():
    # Read stdin (Claude Code sends JSON with tool info)
    # Parse command to check if this is actually a gh pr create command
    command = ""
    try:
        hook_input = json.load(sys.stdin)
        command = hook_input.get("tool_input", {}).get("command", "")
    except (json.JSONDecodeError, EOFError, AttributeError):
        pass

    # Only enforce on gh pr create — skip all other Bash commands
    if "gh pr create" not in command:
        sys.exit(0)

    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())

    # Resolve the working directory for git checks.
    # If the gh pr create command includes a -C or --repo-dir flag, use that.
    # Otherwise, try to detect the worktree directory from the command.
    # The session may be on main while the PR targets a worktree branch,
    # so we must NOT skip enforcement based on the session's branch.
    # Instead, we check workflow state which lives in the worktree.

    state_path = os.path.join(project_dir, ".workflow", "state.json")
    os.makedirs(os.path.dirname(state_path), exist_ok=True)

    # No state file = no evidence of any workflow steps
    if not os.path.exists(state_path):
        print(
            "PR blocked. No workflow state found.\n"
            "  Run /gates, /verify, /qa, and /review before creating a PR.",
            file=sys.stderr,
        )
        sys.exit(2)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        print(
            "PR blocked. Workflow state file is corrupted.\n"
            "  Delete .workflow/state.json and re-run workflow steps.",
            file=sys.stderr,
        )
        sys.exit(2)

    # Get current HEAD
    head_sha = None
    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if head.returncode == 0:
            head_sha = head.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        print(
            "PR blocked. Cannot resolve git HEAD.",
            file=sys.stderr,
        )
        sys.exit(2)

    # If HEAD could not be resolved, block PR creation
    if head_sha is None:
        print(
            "PR blocked. Cannot resolve git HEAD — cannot verify gates.",
            file=sys.stderr,
        )
        sys.exit(2)

    # Check each requirement
    missing = []

    # 1. Gates must match current HEAD
    gates = state.get("gates", {})
    gates_ref = gates.get("commit_ref")
    gates_passed = gates.get("passed")
    if not gates_passed or not gates_ref:
        missing.append("/gates not run")
    elif gates_ref != head_sha:
        missing.append(
            f"/gates not run against current HEAD "
            f"(last ran against {gates_ref[:8]}, HEAD is {head_sha[:8]})"
        )

    # 2. At least one surface verified
    verify = state.get("verify", {})
    verified_surfaces = [k for k, v in verify.items() if v]
    if not verified_surfaces:
        missing.append("/verify not completed for any surface")

    # 3. At least one surface QA'd
    qa = state.get("qa", {})
    qa_surfaces = [k for k, v in qa.items() if v]
    if not qa_surfaces:
        missing.append("/qa not completed for any surface")

    # 4. Review completed
    review = state.get("review", {})
    if not review.get("passed"):
        missing.append("/review not completed")

    if missing:
        msg = "PR blocked. Missing workflow steps:\n"
        for item in missing:
            msg += f"  ✗ {item}\n"
        msg += "Run these before creating a PR."
        print(msg, file=sys.stderr)
        sys.exit(2)

    # All checks passed
    sys.exit(0)


if __name__ == "__main__":
    main()
