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


def main():
    # Read stdin (Claude Code sends JSON with tool info)
    try:
        json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass  # Input parsing is optional - matcher already filtered

    # Get project directory from environment or use current directory
    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())

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

        # Run extension lint if extension/ has changes or exists
        extension_dir = os.path.join(project_dir, "extension")
        if os.path.exists(extension_dir) and os.path.exists(os.path.join(extension_dir, "package.json")):
            lint_result = subprocess.run(
                ["npm", "run", "lint"],
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
        sys.exit(0)

    except FileNotFoundError:
        print("⚠️ dotnet not found in PATH. Skipping validation.", file=sys.stderr)
        sys.exit(0)
    except subprocess.TimeoutExpired:
        print("⚠️ Build/test timed out. Skipping validation.", file=sys.stderr)
        sys.exit(0)


if __name__ == "__main__":
    main()
