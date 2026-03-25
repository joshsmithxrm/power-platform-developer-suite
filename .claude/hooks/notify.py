"""Desktop toast notification for Claude Code.

Two modes:
  1. Direct invocation (from /pr skill or ad-hoc):
       python notify.py --title "PR Ready" --msg "Click to open" --url "https://..."
  2. Hook mode (Notification event, idle_prompt matcher):
       Reads JSON from stdin, pulls PR URL from workflow state.
"""

import argparse
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
    except (json.JSONDecodeError, OSError):
        return None


def show_toast(title, msg, url):
    """Show a Windows desktop toast notification."""
    try:
        from winotify import Notification, audio
    except ImportError:
        # winotify not installed — skip silently
        return

    toast = Notification(
        app_id="Claude Code",
        title=title,
        msg=msg,
        launch=url,
    )
    toast.set_audio(audio.Default, loop=False)
    toast.show()


def main():
    parser = argparse.ArgumentParser(description="Desktop toast notification")
    parser.add_argument("--title", default="PR Ready")
    parser.add_argument("--msg", default="Click to open pull request")
    parser.add_argument("--url", default=None, help="URL to open on click")
    args = parser.parse_args()

    # Direct invocation — URL provided via CLI
    if args.url:
        show_toast(args.title, args.msg, args.url)
        return

    # Hook mode — read from stdin, get URL from workflow state
    try:
        data = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return

    cwd = data.get("cwd", ".")
    pr_url = get_pr_url(cwd)
    if pr_url:
        show_toast(args.title, args.msg, pr_url)


if __name__ == "__main__":
    main()
