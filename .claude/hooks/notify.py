"""Desktop toast notification hook for Claude Code.

Fires on idle_prompt only when a PR URL exists in workflow state.
Clicking the toast opens the PR in the default browser.

Hook event: Notification
Matcher: idle_prompt
Input: JSON on stdin with message, title, notification_type, cwd
"""

import json
import os
import sys


def get_pr_url(cwd):
    """Read PR URL from workflow state if available."""
    state_path = os.path.join(cwd, ".workflow", "state.json")
    if not os.path.exists(state_path):
        return None
    try:
        with open(state_path) as f:
            state = json.load(f)
        return (state.get("pr") or {}).get("url")
    except Exception:
        return None


def main():
    try:
        from winotify import Notification, audio
    except ImportError:
        # winotify not installed — skip silently
        return

    try:
        data = json.load(sys.stdin)
    except Exception:
        return

    cwd = data.get("cwd", ".")
    pr_url = get_pr_url(cwd)

    # Only notify when a PR is ready
    if not pr_url:
        return

    toast = Notification(
        app_id="Claude Code",
        title="PR Ready",
        msg="Click to open pull request",
        launch=pr_url,
    )
    toast.set_audio(audio.Default, loop=False)
    toast.show()


if __name__ == "__main__":
    main()
