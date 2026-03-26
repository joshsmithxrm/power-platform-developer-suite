"""PreToolUse hook: block gh issue create during review/converge cycles.

Forces fix-don't-file — when in a review or converge workflow stage,
findings must be fixed, not filed as issues.
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir


def main() -> None:
    try:
        tool_input = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    command = tool_input.get("command", "")

    # Only enforce on gh issue create
    if "gh issue create" not in command:
        sys.exit(0)

    project_dir = get_project_dir()
    state_path = os.path.join(project_dir, ".workflow", "state.json")

    if not os.path.exists(state_path):
        sys.exit(0)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        sys.exit(0)

    # Check if we're in a review or converge stage
    review = state.get("review", {})
    # If review has findings but hasn't passed, we're in review/converge
    if review.get("findings") and not review.get("passed"):
        print(
            "BLOCKED: You are in a review cycle. Fix the finding, don't file an issue.\n"
            "  Use /converge to fix review findings.",
            file=sys.stderr,
        )
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
