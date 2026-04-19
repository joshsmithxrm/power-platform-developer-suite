"""Desktop toast notification for Claude Code.

Two modes:
  1. Direct invocation (from /pr skill or ad-hoc):
       python notify.py --title "PR Ready" --msg "Click to open" --url "https://..."
  2. Hook mode (Notification event, idle_prompt matcher):
       Reads JSON from stdin, pulls PR URL from workflow state, then asks
       GitHub for the actual draft/state before deciding whether to fire.

Hook-mode state-awareness (v1-prelaunch retro item #6): the previous
implementation fired a "PR Ready" toast on every idle_prompt event
whenever the workflow state had a ``pr.url`` set, regardless of whether
the PR was still a draft, open, merged, or closed. The spurious
notifications eroded user trust. We now query
``gh pr view --json isDraft,state`` (with a 30s in-process cache to avoid
GitHub rate-limit pressure) and only fire when the PR is OPEN and not a
draft. A separate "PR merged" toast fires when the state is MERGED.
"""

import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import normalize_msys_path


# In-process cache for the gh-pr-view result.
# {pr_number: (timestamp, {"isDraft": bool, "state": str})}
_PR_STATE_CACHE = {}
_PR_STATE_CACHE_TTL = 30.0  # seconds


def _is_worktree(cwd):
    """Return True if cwd is a git worktree (not the main repo)."""
    git_path = os.path.join(cwd, ".git")
    # In a worktree, .git is a file (not a directory) containing "gitdir: ..."
    return os.path.isfile(git_path)


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


def _pr_number_from_url(pr_url):
    """Extract the trailing PR number from a github.com/.../pull/N URL."""
    if not pr_url:
        return None
    candidate = pr_url.rstrip("/").split("/")[-1]
    return candidate if candidate.isdigit() else None


def fetch_pr_state(cwd, pr_number, now=None):
    """Return dict with ``isDraft`` and ``state`` for *pr_number*.

    Cached for ``_PR_STATE_CACHE_TTL`` seconds to avoid hammering the
    GitHub API on rapid idle_prompt events. Returns ``None`` on failure
    so callers can choose to skip notification rather than emit a wrong
    one.
    """
    if not pr_number:
        return None
    now = now if now is not None else time.time()
    cached = _PR_STATE_CACHE.get(pr_number)
    if cached and (now - cached[0]) < _PR_STATE_CACHE_TTL:
        return cached[1]
    try:
        result = subprocess.run(
            ["gh", "pr", "view", str(pr_number),
             "--json", "isDraft,state"],
            cwd=cwd, capture_output=True, text=True, timeout=10,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return None
    if result.returncode != 0 or not result.stdout.strip():
        return None
    try:
        data = json.loads(result.stdout)
    except (json.JSONDecodeError, ValueError):
        return None
    parsed = {
        "isDraft": bool(data.get("isDraft", False)),
        "state": str(data.get("state", "")).upper(),
    }
    _PR_STATE_CACHE[pr_number] = (now, parsed)
    return parsed


def show_toast(title, msg, url):
    """Show a Windows desktop toast notification."""
    try:
        from winotify import Notification, audio
    except ImportError:
        # winotify not installed - skip silently
        return

    try:
        toast = Notification(
            app_id="Claude Code",
            title=title,
            msg=msg,
            launch=url,
        )
        toast.set_audio(audio.Default, loop=False)
        toast.show()
    except Exception:
        # OS-level toast failure should not break hook lifecycle
        pass


def main():
    if os.environ.get("PPDS_PIPELINE") or os.environ.get("PPDS_SHAKEDOWN"):
        sys.exit(0)

    parser = argparse.ArgumentParser(description="Desktop toast notification")
    parser.add_argument("--title", default="PR Ready")
    parser.add_argument("--msg", default="Click to open pull request")
    parser.add_argument("--url", default=None, help="URL to open on click")
    args = parser.parse_args()

    # Direct invocation - URL provided via CLI; trust the caller and fire.
    if args.url:
        show_toast(args.title, args.msg, args.url)
        return

    # Hook mode - only fires inside a worktree (never on main repo root)
    try:
        data = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return

    cwd = normalize_msys_path(data.get("cwd", "."))

    # Worktree check: .workflow/state.json should only exist in .worktrees/<name>/
    if not _is_worktree(cwd):
        return

    pr_url = get_pr_url(cwd)
    if not pr_url:
        return

    # v1-prelaunch retro item #6: don't fire blindly. Ask GitHub for the
    # actual state before deciding what (if anything) to show.
    pr_number = _pr_number_from_url(pr_url)
    pr_state = fetch_pr_state(cwd, pr_number)
    if pr_state is None:
        # Couldn't determine state (no gh CLI, network failure, rate limit).
        # Suppress the notification rather than emit a possibly-stale one.
        return

    state = pr_state["state"]
    is_draft = pr_state["isDraft"]

    if state == "MERGED":
        show_toast(
            f"PR merged: #{pr_number}",
            f"PR #{pr_number} has been merged.",
            pr_url,
        )
        return
    if state == "OPEN" and not is_draft:
        show_toast(args.title, args.msg, pr_url)
        return

    # Draft, closed, or anything unknown - stay silent. The previous code
    # always toasted, which produced spurious "PR Ready" alerts for drafts
    # the orchestrator hadn't yet flipped to ready.


if __name__ == "__main__":
    main()
