"""Shared triage logic for pipeline.py and pr_monitor.py.

Extracted to satisfy Constitution A2 (single code path) — both callers
delegate to these pure-ish functions instead of duplicating logic.
"""
import json
import os
import subprocess


def get_repo_slug(worktree, shakedown=False):
    """Get owner/repo from gh CLI. Returns 'owner/repo' or None."""
    if shakedown:
        return "test-owner/test-repo"
    try:
        result = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner",
             "--jq", ".nameWithOwner"],
            cwd=worktree, capture_output=True, text=True, timeout=10,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    return None


def get_unreplied_comments(worktree, pr_number, shakedown=False):
    """Return Gemini + CodeQL inline comments with no threaded reply.

    Fetches all inline comments (pulls/comments endpoint) and identifies
    bot comments from gemini-code-assist[bot] and github-advanced-security[bot]
    that have no reply.  A reply is any comment whose in_reply_to_id matches
    the bot comment's id.

    Returns: list of unreplied comment dicts, empty list on error or no comments.
    """
    if shakedown:
        return []

    repo = get_repo_slug(worktree, shakedown=shakedown)
    if not repo:
        return []

    try:
        result = subprocess.run(
            ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
             "--paginate", "--slurp"],
            cwd=worktree, capture_output=True, text=True, timeout=60,
        )
        if result.returncode != 0:
            return []
        comments = json.loads(result.stdout)
    except (subprocess.TimeoutExpired, FileNotFoundError,
            json.JSONDecodeError, OSError):
        return []

    bot_usernames = {"gemini-code-assist[bot]", "github-advanced-security[bot]"}

    bot_comments = [
        c for c in comments
        if c.get("user", {}).get("login") in bot_usernames
        and c.get("in_reply_to_id") is None
    ]

    replied_to_ids = {
        c.get("in_reply_to_id")
        for c in comments
        if c.get("in_reply_to_id") is not None
    }

    unreplied = [
        {
            "id": c.get("id"),
            "user": c.get("user", {}).get("login"),
            "path": c.get("path"),
            "line": c.get("line"),
            "body": c.get("body"),
        }
        for c in bot_comments
        if c.get("id") not in replied_to_ids
    ]

    return unreplied


def is_triage_complete(worktree, pr_number, shakedown=False):
    """Return True when all bot comments have at least one reply."""
    return len(get_unreplied_comments(
        worktree, pr_number, shakedown=shakedown)) == 0


def detect_gemini_overload(worktree, pr_number, shakedown=False):
    """Detect if Gemini posted an overload message on the PR.

    Checks issue-level comments (issues/{pr}/comments endpoint, NOT
    pulls/comments) for a gemini-code-assist[bot] message containing
    "higher than usual traffic" or "unable to create".

    Returns: True if overload detected, False otherwise.
    """
    if shakedown:
        return False

    repo = get_repo_slug(worktree, shakedown=shakedown)
    if not repo:
        return False

    try:
        result = subprocess.run(
            ["gh", "api", f"repos/{repo}/issues/{pr_number}/comments",
             "--paginate", "--slurp"],
            cwd=worktree, capture_output=True, text=True, timeout=60,
        )
        if result.returncode != 0:
            return False
        comments = json.loads(result.stdout)
    except (subprocess.TimeoutExpired, FileNotFoundError,
            json.JSONDecodeError, OSError):
        return False

    for comment in comments:
        if comment.get("user", {}).get("login") != "gemini-code-assist[bot]":
            continue
        body = (comment.get("body") or "").lower()
        if "higher than usual traffic" in body or "unable to create" in body:
            return True

    return False


def build_triage_prompt(worktree, pr_number, comments):
    """Build the triage prompt. Pure function -- no I/O except reading state.json."""
    state_path = os.path.join(worktree, ".workflow", "state.json")
    spec_path = ""
    try:
        with open(state_path, "r") as f:
            state = json.load(f)
        spec_path = state.get("spec", "")
    except (json.JSONDecodeError, OSError):
        pass

    return (
        f"Triage these Gemini review comments on PR #{pr_number}.\n\n"
        f"Spec (read for design rationale): {spec_path}\n\n"
        f"Comments:\n{json.dumps(comments, indent=2)}\n\n"
        "For each comment:\n"
        "1. Read the referenced file at the specified line\n"
        "2. Evaluate: is this a valid finding?\n"
        "3. If valid: fix the code and commit\n"
        "4. If invalid: compose a brief dismissal rationale\n\n"
        "After all comments, push fixes and output this JSON:\n"
        '[{"id": <id>, "action": "fixed"|"dismissed", '
        '"description": "...", "commit": "<sha>"|null}]'
    )


def parse_triage_json(content):
    """Find and parse triage JSON array from raw text content.

    Works for both direct stage log content (pipeline) and
    pre-extracted text (pr_monitor). Returns list or None.
    """
    last_bracket = content.rfind("[")
    if last_bracket != -1:
        try:
            decoder = json.JSONDecoder()
            obj, _ = decoder.raw_decode(content[last_bracket:])
            if isinstance(obj, list):
                return obj
        except (json.JSONDecodeError, ValueError):
            pass
    return None


def parse_triage_jsonl(jsonl_path):
    """Parse triage results from JSONL stream output (pr_monitor style).

    Extracts text from 'result' and 'assistant' events, then finds
    the JSON array. Returns list or None.
    """
    text_parts = []
    try:
        with open(jsonl_path, "r", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if event.get("type") == "result":
                    result_text = event.get("result", "")
                    if result_text:
                        text_parts.append(result_text)
                elif event.get("type") == "assistant":
                    content = event.get("message", {}).get("content", [])
                    for block in content:
                        if block.get("type") == "text":
                            text = block.get("text", "")
                            if text:
                                text_parts.append(text)
    except OSError:
        return None

    return parse_triage_json("\n".join(text_parts))


def parse_triage_stage_log(stage_log_path):
    """Parse triage results from a stage log file (pipeline style).

    Reads the file and finds the JSON array via raw_decode.
    Returns list or None.
    """
    try:
        with open(stage_log_path, "r", errors="replace") as f:
            content = f.read()
    except OSError:
        return None
    return parse_triage_json(content)


def format_reply_body(item):
    """Format a single triage result into a reply body string."""
    action = item.get("action", "unknown")
    description = item.get("description", "")
    commit_sha = item.get("commit")

    if action == "fixed" and commit_sha:
        return f"Fixed in {commit_sha} \u2014 {description}"
    elif action == "dismissed":
        return f"Not applicable \u2014 {description}"
    else:
        return description or "Reviewed."


def post_replies(worktree, pr_number, triage_results, log_fn, shakedown=False):
    """Post threaded replies to Gemini comments.

    Args:
        worktree: path to the git worktree
        pr_number: PR number (int or str)
        triage_results: list of triage result dicts
        log_fn: callable(event, **kwargs) for logging
        shakedown: if True, skip actual posting
    """
    if shakedown:
        log_fn("SHAKEDOWN_SKIPPED")
        return

    repo = get_repo_slug(worktree, shakedown=shakedown)
    if not repo:
        log_fn("ERROR", reason="Cannot determine repo slug")
        return

    for item in triage_results:
        comment_id = item.get("id")
        if not comment_id:
            log_fn("SKIPPED", reason="missing comment id")
            continue

        body = format_reply_body(item)
        action = item.get("action", "unknown")

        try:
            result = subprocess.run(
                ["gh", "api", f"repos/{repo}/pulls/{pr_number}/comments",
                 "-F", f"in_reply_to={comment_id}", "-f", f"body={body}"],
                cwd=worktree, capture_output=True, text=True, timeout=15,
            )
            if result.returncode != 0:
                log_fn("FAILED", comment_id=comment_id,
                       error=result.stderr.strip()[:100])
            else:
                log_fn("POSTED", comment_id=comment_id, action=action)
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            log_fn("FAILED", comment_id=comment_id)
