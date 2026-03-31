#!/usr/bin/env python3
"""
PR gate hook: blocks gh pr create if workflow steps are incomplete.
Hard gate -- exits code 2 to prevent PR creation.

Commit-aware validation (v8.0):
  - gates.commit_ref == HEAD (exact match)
  - review.commit_ref == HEAD (exact match)
  - verify.{surface}_commit_ref is ancestor-of-HEAD (per affected surface)
  - qa.{surface}_commit_ref is ancestor-of-HEAD (per non-workflow surface)
  - Workflow-only diffs skip QA requirement
  - Triage completeness when PR already exists in state
"""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import get_project_dir

# Valid surface keys
VALID_SURFACES = frozenset(("ext", "tui", "mcp", "cli", "workflow"))

# Path prefix -> surface mapping (order matters: more specific first)
_SURFACE_RULES = [
    ("src/PPDS.Extension/", "ext"),
    ("src/PPDS.Cli/Tui/", "tui"),
    ("src/PPDS.Mcp/", "mcp"),
    # Commands except Serve/ -> cli
    # (Serve/ is the MCP host, not a CLI surface)
    ("src/PPDS.Cli/Commands/Serve/", None),  # explicit skip
    ("src/PPDS.Cli/Commands/", "cli"),
    ("src/PPDS.Cli/Services/", "cli"),
    ("src/PPDS.Migration/", "cli"),
    (".claude/", "workflow"),
    ("scripts/", "workflow"),
    ("specs/", "workflow"),
    ("docs/", "workflow"),
    ("CLAUDE.md", "workflow"),
]


def detect_affected_surfaces(project_dir):
    """Detect which surfaces are affected by the diff against origin/main.

    Runs ``git diff --name-only origin/main...HEAD`` and maps each changed
    path to a surface key.  Returns a set of surface names.
    """
    try:
        result = subprocess.run(
            ["git", "diff", "--name-only", "origin/main...HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=30,
        )
        if result.returncode != 0:
            return set()
        changed_files = [
            line.strip() for line in result.stdout.splitlines() if line.strip()
        ]
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return set()

    surfaces = set()
    for filepath in changed_files:
        # Normalize to forward slashes for matching
        normalized = filepath.replace("\\", "/")
        for prefix, surface in _SURFACE_RULES:
            if normalized.startswith(prefix):
                if surface is not None:
                    surfaces.add(surface)
                break  # first match wins (most specific prefix)
    return surfaces


def is_ancestor(commit_ref, head, project_dir):
    """Check if *commit_ref* is an ancestor of (or equal to) *head*.

    Uses ``git merge-base --is-ancestor``.  Returns True when the ref is
    reachable from head, False otherwise.
    """
    try:
        result = subprocess.run(
            ["git", "merge-base", "--is-ancestor", commit_ref, head],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        return result.returncode == 0
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return False


def _check_triage_completeness(project_dir, state):
    """Check triage completeness if a PR already exists.

    Returns a list of failure messages (empty if OK or not applicable).
    Only runs when state contains a PR number -- for ``gh pr create`` on a
    brand-new PR there is nothing to triage yet.
    """
    pr_info = state.get("pr", {})
    pr_number = pr_info.get("number")
    if not pr_number:
        # Try to extract from URL (pipeline writes pr.url but not pr.number)
        pr_url = pr_info.get("url", "")
        candidate = pr_url.rstrip("/").split("/")[-1] if pr_url else ""
        if candidate.isdigit():
            pr_number = int(candidate)
    if not pr_number:
        return []  # No existing PR -- skip triage check

    # Import triage_common from scripts/
    scripts_dir = os.path.join(project_dir, "scripts")
    if not os.path.isdir(scripts_dir):
        return []
    sys.path.insert(0, scripts_dir)
    try:
        import triage_common  # noqa: F811
    except ImportError:
        return []

    # get_unreplied_comments may not exist yet -- gracefully skip
    get_unreplied = getattr(triage_common, "get_unreplied_comments", None)
    if get_unreplied is None:
        return []

    try:
        unreplied = get_unreplied(project_dir, pr_number)
        if unreplied and len(unreplied) > 0:
            return [
                f"Triage incomplete: {len(unreplied)} unreplied review "
                f"comment(s) on PR #{pr_number}. "
                f"Run /triage to address them before creating a new PR."
            ]
    except Exception:
        # Triage check is best-effort -- do not block on errors
        pass
    return []


def main():
    # Read stdin (Claude Code sends JSON with tool info)
    # Parse command to check if this is actually a gh pr create command
    command = ""
    try:
        hook_input = json.load(sys.stdin)
        command = hook_input.get("tool_input", {}).get("command", "")
    except (json.JSONDecodeError, EOFError, AttributeError):
        pass

    # Only enforce on gh pr create -- skip all other Bash commands
    if "gh pr create" not in command:
        sys.exit(0)

    project_dir = get_project_dir()

    state_path = os.path.join(project_dir, ".workflow", "state.json")
    os.makedirs(os.path.dirname(state_path), exist_ok=True)

    # No state file = no evidence of any workflow steps
    if not os.path.exists(state_path):
        print(
            "PR blocked. No workflow state found.\n"
            "  Run /gates, /verify, /qa, and /review before creating a PR.",
            file=sys.stderr,
        )
        sys.exit(2)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        print(
            "PR blocked. Workflow state file is corrupted.\n"
            "  Delete .workflow/state.json and re-run workflow steps.",
            file=sys.stderr,
        )
        sys.exit(2)

    # Get current HEAD
    head_sha = None
    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if head.returncode == 0:
            head_sha = head.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        print(
            "PR blocked. Cannot resolve git HEAD.",
            file=sys.stderr,
        )
        sys.exit(2)

    if head_sha is None:
        print(
            "PR blocked. Cannot resolve git HEAD -- cannot verify workflow.",
            file=sys.stderr,
        )
        sys.exit(2)

    # Detect affected surfaces from the diff
    affected = detect_affected_surfaces(project_dir)

    # Check each requirement
    missing = []

    # -----------------------------------------------------------------
    # 1. Gates must match current HEAD exactly
    # -----------------------------------------------------------------
    gates = state.get("gates", {})
    gates_ref = gates.get("commit_ref")
    gates_passed = gates.get("passed")
    if not gates_passed or not gates_ref:
        missing.append("/gates not run")
    elif gates_ref != head_sha:
        missing.append(
            f"/gates not run against current HEAD "
            f"(last ran against {gates_ref[:8]}, HEAD is {head_sha[:8]})"
        )

    # -----------------------------------------------------------------
    # 2. Review must match current HEAD exactly
    # -----------------------------------------------------------------
    review = state.get("review", {})
    review_ref = review.get("commit_ref")
    review_passed = review.get("passed")
    if not review_passed or not review_ref:
        missing.append("/review not completed")
    elif review_ref != head_sha:
        missing.append(
            f"/review not run against current HEAD "
            f"(last ran against {review_ref[:8]}, HEAD is {head_sha[:8]})"
        )

    # -----------------------------------------------------------------
    # 3. Verify: each affected surface needs an ancestor-of-HEAD commit ref
    # -----------------------------------------------------------------
    verify = state.get("verify", {})
    if affected:
        for surface in sorted(affected):
            ref_key = f"{surface}_commit_ref"
            surface_ref = verify.get(ref_key)
            if not surface_ref:
                missing.append(
                    f"/verify not completed for surface '{surface}'"
                )
            elif not is_ancestor(surface_ref, head_sha, project_dir):
                missing.append(
                    f"/verify for '{surface}' is not ancestor of HEAD "
                    f"(verify ref {surface_ref[:8]}, HEAD is {head_sha[:8]})"
                )
    else:
        # No affected surfaces detected -- require at least one verify
        verified_any = any(
            verify.get(f"{s}_commit_ref") for s in VALID_SURFACES
        )
        if not verified_any:
            missing.append("/verify not completed for any surface")

    # -----------------------------------------------------------------
    # 4. QA: each affected non-workflow surface needs ancestor-of-HEAD ref
    #    Workflow-only diffs skip QA entirely.
    # -----------------------------------------------------------------
    qa = state.get("qa", {})
    non_workflow_affected = affected - {"workflow"}
    if non_workflow_affected:
        for surface in sorted(non_workflow_affected):
            ref_key = f"{surface}_commit_ref"
            surface_ref = qa.get(ref_key)
            if not surface_ref:
                missing.append(
                    f"/qa not completed for surface '{surface}'"
                )
            elif not is_ancestor(surface_ref, head_sha, project_dir):
                missing.append(
                    f"/qa for '{surface}' is not ancestor of HEAD "
                    f"(qa ref {surface_ref[:8]}, HEAD is {head_sha[:8]})"
                )
    elif not affected:
        # No affected surfaces detected -- require at least one QA
        qa_any = any(qa.get(f"{s}_commit_ref") for s in VALID_SURFACES)
        if not qa_any:
            missing.append("/qa not completed for any surface")
    # else: all affected surfaces are workflow-only -> QA not required

    # -----------------------------------------------------------------
    # 5. Triage completeness (only when a PR already exists in state)
    # -----------------------------------------------------------------
    triage_failures = _check_triage_completeness(project_dir, state)
    missing.extend(triage_failures)

    # -----------------------------------------------------------------
    # Report
    # -----------------------------------------------------------------
    if missing:
        msg = "PR blocked. Missing workflow steps:\n"
        for item in missing:
            msg += f"  \u2717 {item}\n"
        msg += "Run these before creating a PR."
        print(msg, file=sys.stderr)
        sys.exit(2)

    # All checks passed
    sys.exit(0)


if __name__ == "__main__":
    main()
