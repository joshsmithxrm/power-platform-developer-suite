#!/usr/bin/env python3
"""
SessionStart hook: injects workflow state into AI context.
Outputs current workflow status so the AI knows what's been done and what's pending.

TODO: Wire up as post-compaction context re-injection hook.
  Attempted SessionStart(compact), PreCompact, and PostCompact matchers in
  settings.json — none fired on /compact in Claude Code v2.1.83. The script
  works standalone (writes to stdout for compact, stderr for normal). When
  Claude Code adds compaction hook support, add a compact matcher to
  SessionStart in settings.json and use source=="compact" from stdin JSON
  to switch output to stdout (context injection requires stdout, not stderr).
  See also: Windows encoding issue — stdout needs io.TextIOWrapper with
  encoding="utf-8" to handle unicode chars (arrows, checkmarks) on cp1252.
"""
import json
import os
import subprocess
import sys


def main():
    # Read stdin
    try:
        json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        pass

    project_dir = os.environ.get("CLAUDE_PROJECT_DIR", os.getcwd())

    # Get current branch
    branch = "unknown"
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            branch = result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    # On main: show active worktrees and /design guidance
    if branch in ("main", "master"):
        _show_main_guidance(project_dir)
        sys.exit(0)

    state_path = os.path.join(project_dir, ".workflow", "state.json")

    if not os.path.exists(state_path):
        # No state file — inject workflow reminder
        print(
            f"WORKFLOW ENFORCEMENT ACTIVE on branch {branch}:\n"
            "  No workflow state tracked yet.\n"
            "  For new features: /spec → /implement → /gates → /verify → /qa → /review → /pr\n"
            "  For bug fixes: /gates → /verify (if UI changed) → /pr\n"
            + _behavioral_rules(),
            file=sys.stderr,
        )
        sys.exit(0)

    try:
        with open(state_path, "r") as f:
            state = json.load(f)
    except (json.JSONDecodeError, OSError):
        print(
            f"WORKFLOW STATE for branch {branch}:\n"
            "  ⚠ State file is corrupted. Delete .workflow/state.json and re-run steps.",
            file=sys.stderr,
        )
        sys.exit(0)

    # Get current HEAD for staleness check
    head_sha = None
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            head_sha = result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    # Build status lines
    lines = [f"WORKFLOW ENFORCEMENT ACTIVE on branch {branch}:"]

    # Gates
    gates = state.get("gates", {})
    gates_ref = gates.get("commit_ref")
    gates_passed = gates.get("passed")
    if gates_passed and gates_ref:
        if head_sha and gates_ref == head_sha:
            lines.append(f"  ✓ Gates passed (commit {gates_ref[:8]}, current)")
        else:
            lines.append(
                f"  ⚠ Gates STALE (ran against {gates_ref[:8]}, "
                f"HEAD is {head_sha[:8] if head_sha else 'unknown'})"
            )
    else:
        lines.append("  ✗ Gates not run")

    # Verify surfaces
    verify = state.get("verify", {})
    for surface in ("ext", "tui", "mcp", "cli"):
        ts = verify.get(surface)
        label = {"ext": "Extension", "tui": "TUI", "mcp": "MCP", "cli": "CLI"}[surface]
        if ts:
            lines.append(f"  ✓ {label} verified")
        else:
            lines.append(f"  ✗ {label} not verified")

    # QA
    qa = state.get("qa", {})
    qa_surfaces = [k for k, v in qa.items() if v]
    if qa_surfaces:
        lines.append(f"  ✓ QA completed ({', '.join(qa_surfaces)})")
    else:
        lines.append("  ✗ QA not completed")

    # Review
    review = state.get("review", {})
    if review.get("passed"):
        findings = review.get("findings", 0)
        lines.append(f"  ✓ Review passed ({findings} findings)")
    else:
        lines.append("  ✗ Review not completed")

    # PR
    pr = state.get("pr", {})
    if pr and pr.get("url"):
        lines.append(f"  ✓ PR created: {pr['url']}")
    else:
        lines.append("  ⚠ PR not created")

    # Required steps
    missing = []
    if not gates_passed or (head_sha and gates_ref != head_sha):
        missing.append("/gates")
    if not any(verify.get(s) for s in ("ext", "tui", "mcp", "cli")):
        missing.append("/verify")
    if not qa_surfaces:
        missing.append("/qa")
    if not review.get("passed"):
        missing.append("/review")

    if missing:
        lines.append(f"  Required before PR: {', '.join(missing)}")

    lines.append(_behavioral_rules())

    print("\n".join(lines), file=sys.stderr)
    sys.exit(0)


def _show_main_guidance(project_dir):
    """Show active worktrees and /design guidance when on main."""
    lines = ["You are on main."]

    # List active worktrees (exclude the main worktree itself)
    try:
        result = subprocess.run(
            ["git", "worktree", "list", "--porcelain"],
            cwd=project_dir,
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode == 0:
            worktrees = []
            current_path = None
            current_branch = None

            def _collect(path, branch):
                if path and branch and branch not in ("main", "master"):
                    rel = os.path.relpath(path, project_dir)
                    worktrees.append(f"  {rel}  [{branch}]")

            for line in result.stdout.strip().split("\n"):
                if line.startswith("worktree "):
                    current_path = line[len("worktree "):]
                elif line.startswith("branch refs/heads/"):
                    current_branch = line[len("branch refs/heads/"):]
                elif line == "":
                    _collect(current_path, current_branch)
                    current_path = None
                    current_branch = None
            # Handle last entry (no trailing blank line)
            _collect(current_path, current_branch)

            if worktrees:
                lines.append("")
                lines.append("Active worktrees:")
                lines.extend(worktrees)
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass

    lines.append("")
    lines.append("To start new work: /design (creates spec branch) or python scripts/pipeline.py <plan-path>")
    lines.append("Brainstorming is fine on main. All file changes (specs included) require a branch.")

    print("\n".join(lines), file=sys.stderr)


def _behavioral_rules():
    """Key behavioral rules injected into every session."""
    return (
        "\n"
        "  RULES (enforced by hooks — read CLAUDE.md for full details):\n"
        "  • Commit after EACH issue fixed or plan task completed. One commit per fix.\n"
        "  • You MUST visually verify affected surfaces before declaring done.\n"
        "    A passing test suite is NOT verification. Use the product yourself.\n"
        "  • Do NOT declare work complete without running /gates → /verify → /qa → /review.\n"
        "  • PR creation WILL BE BLOCKED if these steps are incomplete.\n"
        "  • The stop hook WILL BLOCK session end if workflow steps are incomplete.\n"
        "  • After each workflow step, proceed to the next WITHOUT asking permission.\n"
        "    gates → verify → qa → review → pr is a pipeline. Execute end-to-end."
    )


if __name__ == "__main__":
    main()
