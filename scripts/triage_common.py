"""Shared triage logic for pipeline.py and pr_monitor.py.

Extracted to satisfy Constitution A2 (single code path) — both callers
delegate to these pure-ish functions instead of duplicating logic.
"""
import json
import os
import subprocess
import time
from itertools import chain


# Bot logins used by review-detection and triage code paths.
GEMINI_BOT_LOGIN = "gemini-code-assist[bot]"
CODEQL_BOT_LOGIN = "github-advanced-security[bot]"


def get_repo_slug(worktree, shakedown=False):
    """Get owner/repo from gh CLI. Returns 'owner/repo' or None."""
    if shakedown:
        return "test-owner/test-repo"
    try:
        result = subprocess.run(
            ["gh", "repo", "view", "--json", "nameWithOwner",
             "--jq", ".nameWithOwner"],
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        pass
    return None


def _flatten_paginate_slurp(payload):
    """Flatten the result of ``gh api ... --paginate --slurp``.

    v1-prelaunch retro item #5: ``--paginate --slurp`` returns a list of
    pages where each page is itself a list of items. Iterating directly
    crashes with ``AttributeError: 'list' object has no attribute 'get'``
    when callers do ``comment.get(...)``. Flatten one level when we detect
    the page-of-pages shape; otherwise return *payload* unchanged.
    """
    if not payload:
        return payload or []
    if isinstance(payload, list) and payload and isinstance(payload[0], list):
        return list(chain.from_iterable(payload))
    return payload


def poll_gemini_review(worktree, pr_number, pr_created_at,
                       max_wait=300, poll_interval=30,
                       min_wait=0, shakedown=False, log_fn=None):
    """Poll for a Gemini review across all three GitHub endpoints.

    v1-prelaunch retro item #3: the previous implementation only polled
    ``/pulls/{n}/comments`` (inline review comments). Gemini posts its
    summary review via ``/pulls/{n}/reviews`` (a top-level review object),
    so the old code never saw the review and timed out at 5 minutes.

    Polls in parallel-ish (sequentially per tick — fast enough):
      - GET /repos/{owner}/{repo}/pulls/{n}/reviews   (top-level reviews)
      - GET /repos/{owner}/{repo}/pulls/{n}/comments  (inline review comments)
      - GET /repos/{owner}/{repo}/issues/{n}/comments (PR-level discussion)

    Termination: any submission whose ``user.login`` matches
    ``GEMINI_BOT_LOGIN`` AND whose ``submitted_at`` (reviews) /
    ``created_at`` (comments) is at or after *pr_created_at*.

    Args:
        worktree: directory to run gh from
        pr_number: PR number
        pr_created_at: ISO timestamp string of the PR's created_at; reviews
            older than this are ignored (prevents matching stale reviews
            from previous force-pushes).
        max_wait: total seconds to poll before giving up
        poll_interval: seconds between polls
        min_wait: minimum seconds before considering termination
        shakedown: if True, return ([], "shakedown") immediately
        log_fn: optional callable(event, **kwargs) for status logging

    Returns:
        Tuple ``(comments, status)`` where:
          - ``comments`` is a list of inline review comment dicts (may be
            empty even on success — Gemini sometimes posts a top-level
            review with no inline comments).
          - ``status`` is one of: "review_received", "timeout", "error",
            "shakedown".
    """
    def _log(event, **kwargs):
        if log_fn:
            log_fn(event, **kwargs)

    if shakedown:
        _log("SHAKEDOWN_SKIPPED")
        return [], "shakedown"

    repo = get_repo_slug(worktree, shakedown=shakedown)
    if not repo:
        _log("ERROR", reason="Cannot determine repo slug")
        return [], "error"

    start = time.time()

    while time.time() - start < max_wait:
        elapsed = time.time() - start
        if elapsed < min_wait:
            time.sleep(min(poll_interval, max(1, min_wait - elapsed)))
            continue

        endpoints = {
            "reviews": f"repos/{repo}/pulls/{pr_number}/reviews",
            "pulls_comments": f"repos/{repo}/pulls/{pr_number}/comments",
            "issues_comments": f"repos/{repo}/issues/{pr_number}/comments",
        }
        gemini_seen = False
        endpoint_results = {}
        for key, path in endpoints.items():
            try:
                proc = subprocess.run(
                    ["gh", "api", path, "--paginate", "--slurp"],
                    cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
                )
            except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
                _log("POLL_ERROR", endpoint=key, error=str(e))
                continue
            if proc.returncode != 0:
                _log("POLL_ERROR", endpoint=key,
                     stderr=proc.stderr.strip()[:120])
                continue
            try:
                items = _flatten_paginate_slurp(json.loads(proc.stdout or "[]"))
            except (json.JSONDecodeError, ValueError):
                _log("PARSE_ERROR", endpoint=key)
                continue
            endpoint_results[key] = items
            for item in items:
                if (item.get("user") or {}).get("login") != GEMINI_BOT_LOGIN:
                    continue
                ts = item.get("submitted_at") or item.get("created_at") or ""
                if pr_created_at and ts < pr_created_at:
                    continue
                gemini_seen = True
                break

        _log("POLL", elapsed=f"{int(elapsed)}s",
             gemini_seen=gemini_seen,
             reviews=len(endpoint_results.get("reviews", [])),
             pull_comments=len(endpoint_results.get("pulls_comments", [])),
             issue_comments=len(endpoint_results.get("issues_comments", [])))

        if gemini_seen:
            _log("REVIEW_RECEIVED")
            inline = []
            for c in endpoint_results.get("pulls_comments", []):
                # Filter to gemini-authored inline comments only — the
                # pulls/comments endpoint also returns human reviewer
                # comments and stale force-push comments, which would
                # otherwise be triaged as if Gemini had posted them.
                if (c.get("user") or {}).get("login") != GEMINI_BOT_LOGIN:
                    continue
                inline.append({
                    "id": c.get("id"),
                    "user": (c.get("user") or {}).get("login"),
                    "path": c.get("path"),
                    "line": c.get("line"),
                    "body": c.get("body"),
                })
            return inline, "review_received"

        time.sleep(poll_interval)

    _log("TIMEOUT", elapsed=f"{int(time.time() - start)}s")
    return [], "timeout"


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
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
        )
        if result.returncode != 0:
            return []
        comments = _flatten_paginate_slurp(json.loads(result.stdout))
    except (subprocess.TimeoutExpired, FileNotFoundError,
            json.JSONDecodeError, OSError):
        return []

    bot_usernames = {GEMINI_BOT_LOGIN, CODEQL_BOT_LOGIN}

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
            cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
        )
        if result.returncode != 0:
            return False
        comments = _flatten_paginate_slurp(json.loads(result.stdout))
    except (subprocess.TimeoutExpired, FileNotFoundError,
            json.JSONDecodeError, OSError):
        return False

    for comment in comments:
        if comment.get("user", {}).get("login") != GEMINI_BOT_LOGIN:
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
        with open(state_path, "r", encoding="utf-8") as f:
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
    """Find and parse a triage JSON array from raw text content.

    Scans right-to-left for '[' and returns the last valid JSON array found.
    For CI-fix agent output (which is a JSON object), use parse_triage_json_obj.
    Returns list or None.
    """
    decoder = json.JSONDecoder()
    search_from = len(content)
    while search_from > 0:
        pos = content.rfind("[", 0, search_from)
        if pos == -1:
            return None
        try:
            obj, _ = decoder.raw_decode(content[pos:])
            if isinstance(obj, list):
                return obj
        except (json.JSONDecodeError, ValueError):
            pass
        search_from = pos
    return None


def parse_triage_json_obj(content):
    """Find and parse a triage JSON object from raw text content.

    Scans right-to-left for '{', returns the first valid dict decoded.
    Optional fields with explicit null values are coalesced to safe defaults
    by the caller; this function returns the raw decoded dict or None.
    """
    decoder = json.JSONDecoder()
    search_from = len(content)
    while search_from > 0:
        pos = content.rfind("{", 0, search_from)
        if pos == -1:
            return None
        try:
            obj, _ = decoder.raw_decode(content[pos:])
            if isinstance(obj, dict):
                return obj
        except (json.JSONDecodeError, ValueError):
            pass
        search_from = pos
    return None


def parse_triage_jsonl(jsonl_path):
    """Parse triage results from JSONL stream output (pr_monitor style).

    Extracts text from 'result' and 'assistant' events, then finds
    the JSON array. Returns list or None.
    """
    text_parts = []
    try:
        with open(jsonl_path, "r", encoding="utf-8", errors="replace") as f:
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
        with open(stage_log_path, "r", encoding="utf-8", errors="replace") as f:
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
                cwd=worktree, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15,
            )
            if result.returncode != 0:
                log_fn("FAILED", comment_id=comment_id,
                       error=result.stderr.strip()[:100])
            else:
                log_fn("POSTED", comment_id=comment_id, action=action)
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
            log_fn("FAILED", comment_id=comment_id)


def dispatch_subagent(profile_name, payload, model="sonnet", worktree=".", timeout=1800):
    """Invoke ``claude -p`` with an agent profile, passing payload as the -p prompt argument.

    Centralises the Windows-quoting / UTF-8 / shell=False (Constitution S2)
    invocation pattern so CI-fix and Gemini-triage dispatchers share one code path.

    Args:
        profile_name: agent profile name (matches .claude/agents/<name>.md)
        payload: dict serialized as JSON and passed as the -p prompt argument
        model: model slug (floating name, no version pin)
        worktree: working directory for the subprocess
        timeout: seconds before the process is killed (default 30 min)

    Returns:
        dict with keys: ``stdout`` (str), ``stderr`` (str), ``exit_code`` (int)
    """
    prompt = json.dumps(payload)
    cmd = [
        "claude", "-p", prompt,
        "--verbose", "--output-format", "stream-json",
        "--model", model,
        "--agent", profile_name,
    ]

    try:
        proc = subprocess.Popen(
            cmd,
            cwd=worktree,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        try:
            stdout_bytes, stderr_bytes = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
            return {"stdout": "", "stderr": "timeout", "exit_code": -1}
        return {
            "stdout": stdout_bytes.decode("utf-8", errors="replace"),
            "stderr": stderr_bytes.decode("utf-8", errors="replace"),
            "exit_code": proc.returncode,
        }
    except FileNotFoundError:
        return {"stdout": "", "stderr": "claude command not found", "exit_code": -1}
    except OSError as e:
        return {"stdout": "", "stderr": str(e), "exit_code": -1}
