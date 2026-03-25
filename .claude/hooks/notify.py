"""Desktop toast notification hook for Claude Code.

Fires on idle_prompt (Claude waiting for input) and permission_prompt
(Claude needs permission). Reads .workflow/state.json to detect PR URLs
and makes the toast clickable to open the PR in the browser.

Hook event: Notification
Matcher: idle_prompt|permission_prompt
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

    notification_type = data.get("notification_type", "")
    message = data.get("message", "Claude Code")
    title = data.get("title", "Claude Code")
    cwd = data.get("cwd", ".")

    launch_url = None

    if notification_type == "idle_prompt":
        pr_url = get_pr_url(cwd)
        if pr_url:
            launch_url = pr_url
            title = "PR Ready"
            message = "Click to open pull request"
        else:
            title = "Claude Code"
            message = "Waiting for input"

    elif notification_type == "permission_prompt":
        title = "Claude Code"
        # message already contains the permission request details

    toast = Notification(
        app_id="Claude Code",
        title=title,
        msg=message,
        launch=launch_url or "",
    )
    toast.set_audio(audio.Default, loop=False)
    toast.show()


if __name__ == "__main__":
    main()
