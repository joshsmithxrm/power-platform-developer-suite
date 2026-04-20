#!/usr/bin/env python3
"""
Pre-commit validation hook for PPDS.
Runs dotnet build and test before allowing git commit.

Note: This hook is only triggered for 'git commit' commands via the
matcher in .claude/settings.json - no need to filter here.
"""
import subprocess
import sys
import os
import json

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir


def main():
    # Read stdin (Claude Code sends JSON with tool info)
    try:
        json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass  # Input parsing is optional - matcher already filtered

    project_dir = get_project_dir()

    # Block commits on main
    try:
        branch_result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if branch_result.returncode == 0 and branch_result.stdout.strip() in ("main", "master"):
            print(
                "❌ Cannot commit to main. Use /start to create a feature worktree.",
                file=sys.stderr,
            )
            sys.exit(2)
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass  # If git isn't available, don't block

    # Dedup: if /gates already validated the current HEAD, skip build+test.
    # /gates writes gates.commit_ref = HEAD after passing. If it still matches
    # HEAD at commit time, the build+test we're about to run is redundant.
    # Post-commit-state clears gates.commit_ref, so this only skips the FIRST
    # commit after /gates — exactly the duplicate case.
    if _gates_fresh(project_dir):
        print(
            "✅ Skipping build+test — /gates validated HEAD already (dedup).",
            file=sys.stderr,
        )
        sys.exit(0)

    try:
        print("🔨 Running pre-commit validation...", file=sys.stderr)

        # Run dotnet build
        build_result = subprocess.run(
            ["dotnet", "build", "-c", "Release", "--nologo", "-v", "q"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=300
        )

        if build_result.returncode != 0:
            print("❌ Build failed. Fix errors before committing:", file=sys.stderr)
            if build_result.stdout:
                print(build_result.stdout, file=sys.stderr)
            if build_result.stderr:
                print(build_result.stderr, file=sys.stderr)
            sys.exit(2)

        # Run unit tests only (integration tests run on PR)
        test_result = subprocess.run(
            ["dotnet", "test", "--no-build", "-c", "Release", "--nologo", "-v", "q",
             "--filter", "Category!=Integration"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=300
        )

        if test_result.returncode != 0:
            print("❌ Unit tests failed. Fix before committing:", file=sys.stderr)
            if test_result.stdout:
                print(test_result.stdout, file=sys.stderr)
            if test_result.stderr:
                print(test_result.stderr, file=sys.stderr)
            sys.exit(2)

        # Run extension lint only if staged files include extension changes
        extension_dir = os.path.join(project_dir, "src", "PPDS.Extension")
        has_ext_changes = False
        try:
            staged = subprocess.run(
                ["git", "diff", "--cached", "--name-only"],
                cwd=project_dir,
                capture_output=True,
                text=True,
                timeout=10,
            )
            if staged.returncode == 0:
                has_ext_changes = any(
                    line.startswith("src/PPDS.Extension/")
                    for line in staged.stdout.strip().split("\n") if line
                )
        except (subprocess.TimeoutExpired, FileNotFoundError):
            has_ext_changes = True  # If git check fails, run lint to be safe

        if has_ext_changes and os.path.exists(extension_dir) and os.path.exists(os.path.join(extension_dir, "package.json")):
            # shell=True required on Windows where npm is a .cmd batch file
            lint_result = subprocess.run(
                "npm run lint",
                cwd=extension_dir,
                capture_output=True,
                text=True,
                timeout=60,
                shell=True
            )

            if lint_result.returncode != 0:
                print("❌ Extension lint failed. Fix before committing:", file=sys.stderr)
                if lint_result.stdout:
                    print(lint_result.stdout, file=sys.stderr)
                if lint_result.stderr:
                    print(lint_result.stderr, file=sys.stderr)
                sys.exit(2)

            print("✅ Extension lint passed", file=sys.stderr)

        print("✅ All validations passed", file=sys.stderr)

        # Workflow state check (informational only — does not block)
        _check_workflow_state(project_dir)

        sys.exit(0)

    except FileNotFoundError:
        print("⚠️ dotnet not found in PATH. Skipping validation.", file=sys.stderr)
        sys.exit(0)
    except subprocess.TimeoutExpired:
        print("⚠️ Build/test timed out. Skipping validation.", file=sys.stderr)
        sys.exit(0)


def _gates_fresh(project_dir):
    """Return True if .workflow/state.json shows gates.commit_ref == HEAD.

    Used to skip redundant build+test in the pre-commit hook when /gates
    already validated the current HEAD. Returns False on any error (state
    missing, JSON corrupt, git unavailable) so the hook falls back to the
    full validation path.
    """
    state_path = os.path.join(project_dir, ".workflow", "state.json")
    if not os.path.exists(state_path):
        return False

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        return False

    gates = state.get("gates") or {}
    if not gates.get("passed"):
        return False
    gates_ref = gates.get("commit_ref")
    if not gates_ref:
        return False

    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return False

    if head.returncode != 0:
        return False

    return head.stdout.strip() == gates_ref


def _check_workflow_state(project_dir):
    """Check if gates are stale and warn (does not block)."""
    state_path = os.path.join(project_dir, ".workflow", "state.json")
    if not os.path.exists(state_path):
        return

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        return

    gates = state.get("gates", {})
    gates_ref = gates.get("commit_ref")
    if not gates_ref:
        return

    # Check if src/ files are staged
    try:
        staged = subprocess.run(
            ["git", "diff", "--cached", "--name-only"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if staged.returncode != 0:
            return

        has_src = any(line.startswith("src/") for line in staged.stdout.strip().split("\n") if line)
        if not has_src:
            return
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return

    # Compare gates ref to HEAD
    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if head.returncode == 0 and head.stdout.strip() != gates_ref:
            print(
                "⚠ Warning: /gates has not been run since your last changes. "
                "Run /gates before creating a PR.",
                file=sys.stderr,
            )
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass


if __name__ == "__main__":
    main()
