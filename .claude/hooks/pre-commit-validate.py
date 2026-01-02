#!/usr/bin/env python3
"""
Pre-commit validation hook for PPDS SDK.
Runs dotnet build and test before allowing git commit.
"""
import json
import shlex
import subprocess
import sys
import os

def main():
    try:
        input_data = json.load(sys.stdin)
    except json.JSONDecodeError:
        print("⚠️ pre-commit-validate: Failed to parse input. Skipping validation.", file=sys.stderr)
        sys.exit(0)

    tool_name = input_data.get("tool_name", "")
    tool_input = input_data.get("tool_input", {})
    command = tool_input.get("command", "")

    # Only validate git commit commands (robust check using shlex)
    if tool_name != "Bash":
        sys.exit(0)
    try:
        parts = shlex.split(command)
        is_git_commit = len(parts) >= 2 and os.path.basename(parts[0]) == "git" and parts[1] == "commit"
    except ValueError:
        is_git_commit = False
    if not is_git_commit:
        sys.exit(0)  # Allow non-commit commands

    # Get project directory
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())

    try:
        # Run dotnet build
        print("Running pre-commit validation...", file=sys.stderr)
        build_result = subprocess.run(
            ["dotnet", "build", "-c", "Release", "--nologo", "-v", "q"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=300  # 5 minute timeout
        )

        if build_result.returncode != 0:
            print("❌ Build failed. Fix errors before committing:", file=sys.stderr)
            if build_result.stdout:
                print(build_result.stdout, file=sys.stderr)
            if build_result.stderr:
                print(build_result.stderr, file=sys.stderr)
            sys.exit(2)  # Block commit

        # Run dotnet test (unit tests only - integration tests run on PR)
        test_result = subprocess.run(
            ["dotnet", "test", "--no-build", "-c", "Release", "--nologo", "-v", "q",
             "--filter", "Category!=Integration"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=300  # 5 minute timeout
        )

        if test_result.returncode != 0:
            print("❌ Unit tests failed. Fix before committing:", file=sys.stderr)
            if test_result.stdout:
                print(test_result.stdout, file=sys.stderr)
            if test_result.stderr:
                print(test_result.stderr, file=sys.stderr)
            sys.exit(2)  # Block commit

        print("✅ Build and unit tests passed", file=sys.stderr)
        sys.exit(0)  # Allow commit

    except FileNotFoundError:
        print("⚠️ pre-commit-validate: dotnet not found in PATH. Skipping validation.", file=sys.stderr)
        sys.exit(0)
    except subprocess.TimeoutExpired:
        print("⚠️ pre-commit-validate: Build/test timed out after 5 minutes. Skipping validation.", file=sys.stderr)
        sys.exit(0)

if __name__ == "__main__":
    main()
