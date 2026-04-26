#!/usr/bin/env python3
"""
Launch a new interactive `claude` session in a target directory.

This helper implements the `launch-session` pattern from the sibling
procode-toolkit repo. It exists because the `/start` skill's v1
dispatch AI deviated from the documented pattern and used:

    start pwsh -NoExit -Command "Set-Location X; claude 'prompt'"

which gives `claude` no TTY — claude exits immediately under -Command,
leaving a dead pwsh session. See #799 and the workflow-skills PR that
bundled bugs 3 and 4.

What this helper guarantees:
  - The prompt is embedded in a temporary `.ps1` script as a
    PowerShell single-quoted here-string (`@' ... '@`), so the prompt
    is not subject to CLI length or shell-escaping limits.
  - The new window is spawned via
        pwsh -Command "Start-Process pwsh -ArgumentList
            '-NoExit','-File','<script>'"
    which gives the child pwsh its own TTY. `claude` receives the
    prompt as a bare positional argument (NOT `-p`, which is
    non-interactive).
  - No `.plans/context.md` handoff file is written — the full prompt
    is delivered inline, which is the only handoff mechanism the
    receiving session reads reliably.

Usage:
    python scripts/launch-claude-session.py \
        --target 'C:\\path\\to\\worktree' \
        --name my-feature \
        --prompt-file path/to/prompt.txt \
        [--claude-path 'C:\\full\\path\\to\\claude.exe'] \
        [--launch-dir $LOCALAPPDATA/ppds] \
        [--dry-run]

`--dry-run` writes the `.ps1` and prints the spawn command without
executing it — used by regression tests.

Exit codes:
    0 — script written and (unless --dry-run) spawn command issued
    1 — usage / file / prompt error
    2 — spawn command failed
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from typing import List, Optional


PROMPT_TERMINATOR = "'@"
_TERMINATOR_RE = re.compile(r"^[ \t]*'@")

_NAME_RE = re.compile(r"^[A-Za-z0-9_-]+$")


def _validate_name(name: Optional[str]) -> None:
    """Reject --name values that would unsafely interpolate into PowerShell (S2)."""
    if name is None:
        return
    if not _NAME_RE.match(name):
        print("--name must match [A-Za-z0-9_-]+", file=sys.stderr)
        sys.exit(2)


def build_launch_script(
    target_windows_path: str,
    claude_path: str,
    prompt: str,
) -> str:
    """Return the full text of the `.ps1` launch script.

    The prompt is wrapped in a PowerShell single-quoted here-string
    (`@' ... '@`). The only content that breaks that here-string is a
    line whose first two characters are `'@` — we defensively check for
    it and raise, rather than silently corrupting the script.
    """
    for lineno, line in enumerate(prompt.splitlines(), start=1):
        if _TERMINATOR_RE.match(line):
            raise ValueError(
                f"prompt line {lineno} matches {PROMPT_TERMINATOR!r} (possibly "
                f"with leading whitespace), which would terminate the PowerShell "
                f"here-string. Reword or prefix with a non-whitespace character."
            )

    # Note: `claude $prompt` is a bare positional argument, which keeps
    # the session interactive. `claude -p $prompt` would be
    # non-interactive (claude exits after producing one response).
    return (
        f'Set-Location -Path "{target_windows_path}"\n'
        "\n"
        "$prompt = @'\n"
        f"{prompt}\n"
        "'@\n"
        "\n"
        f'& "{claude_path}" --model opus $prompt\n'
    )


def build_spawn_command(script_windows_path: str) -> List[str]:
    """Return the argv that spawns the new pwsh window.

    The inner PowerShell command list MUST use `-File`, not
    `-Command`. Under `-Command`, pwsh treats the argument as a script
    block with no TTY — `claude` sees no terminal and exits
    immediately (this was bug 3 in the v1 dispatch). Under `-File`
    with `-NoExit`, pwsh runs the script in an interactive shell and
    the child `claude` process inherits a real TTY.
    """
    # PowerShell single-quoted strings escape `'` by doubling it. Paths
    # containing apostrophes (e.g. user home `O'Brien`) would otherwise
    # break the ArgumentList.
    escaped = script_windows_path.replace("'", "''")
    inner = (
        f"Start-Process pwsh -ArgumentList "
        f"'-NoExit','-File','{escaped}'"
    )
    return ["pwsh", "-Command", inner]


def _to_windows_path(path: str) -> str:
    """Best-effort POSIX-to-Windows path conversion for mingw/cygwin.

    Tests don't need this (they pass native paths directly), so the
    failure mode is to return `path` unchanged.
    """
    try:
        result = subprocess.run(
            ["cygpath", "-w", path],
            capture_output=True,
            text=True,
            timeout=5,
            stdin=subprocess.DEVNULL,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip()
    except (FileNotFoundError, subprocess.TimeoutExpired, OSError):
        pass
    return path


def _resolve_claude_path() -> str:
    """Locate the claude binary and return a Windows-style path.

    Node version managers (fnm, nvm, volta) use session-scoped shim
    directories that may not survive into the spawned pwsh. Embedding
    the absolute path sidesteps that whole class of failure.
    """
    try:
        which = subprocess.run(
            ["which", "claude"],
            capture_output=True,
            text=True,
            timeout=5,
            stdin=subprocess.DEVNULL,
        )
        if which.returncode == 0 and which.stdout.strip():
            return _to_windows_path(which.stdout.strip())
    except (FileNotFoundError, subprocess.TimeoutExpired, OSError):
        pass
    # Fall back to bare name and hope PATH resolves it in the child.
    return "claude"


def launch(
    target: str,
    name: str,
    prompt: str,
    claude_path: Optional[str] = None,
    launch_dir: Optional[str] = None,
    dry_run: bool = False,
) -> int:
    """Write the launch script and (unless dry_run) spawn the window."""
    if not prompt.strip():
        sys.stderr.write("prompt is empty; refusing to launch\n")
        return 1

    launch_dir = launch_dir or os.path.join(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "ppds"
    )
    os.makedirs(launch_dir, exist_ok=True)
    script_path = os.path.join(launch_dir, f"launch-{name}.ps1")
    script_win = _to_windows_path(script_path)

    target_win = _to_windows_path(target)
    claude_win = claude_path or _resolve_claude_path()

    try:
        script_text = build_launch_script(target_win, claude_win, prompt)
    except ValueError as e:
        sys.stderr.write(f"prompt rejected: {e}\n")
        return 1

    with open(script_path, "w", encoding="utf-8") as f:
        f.write(script_text)

    cmd = build_spawn_command(script_win)

    if dry_run:
        print(f"script: {script_path}", file=sys.stderr)
        print(f"spawn:  {' '.join(cmd)}", file=sys.stderr)
        return 0

    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=15)
    except FileNotFoundError:
        sys.stderr.write("pwsh not found on PATH; falling through to manual\n")
        _print_manual_fallback(target_win, prompt, claude_win)
        return 2
    except subprocess.TimeoutExpired:
        sys.stderr.write("Start-Process timed out; window may still be opening\n")
        return 2

    if result.returncode != 0:
        sys.stderr.write(
            f"Start-Process failed ({result.returncode}):\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}\n"
        )
        _print_manual_fallback(target_win, prompt, claude_win)
        return 2

    print(f"spawned new session in {target}", file=sys.stderr)
    print(f"  script: {script_path}", file=sys.stderr)
    return 0


def _print_manual_fallback(target_win: str, prompt: str, claude_win: str) -> None:
    target_escaped = target_win.replace("'", "''")
    claude_escaped = claude_win.replace("'", "''")
    sys.stderr.write(
        "\nCould not open a new terminal automatically. Open PowerShell and run:\n\n"
        f"  cd '{target_escaped}'\n"
        "  $prompt = @'\n"
        f"{prompt}\n"
        "'@\n"
        f"  & '{claude_escaped}' --model opus $prompt\n"
    )


def _parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--target", required=True, help="target worktree directory")
    p.add_argument("--name", required=True, help="short name (for script filename)")
    prompt_group = p.add_mutually_exclusive_group(required=True)
    prompt_group.add_argument(
        "--prompt-file",
        help="path to a file containing the prompt verbatim",
    )
    prompt_group.add_argument(
        "--prompt-stdin",
        action="store_true",
        help="read prompt from stdin (avoids shell-quoting entirely)",
    )
    p.add_argument(
        "--claude-path",
        help="full path to claude executable (default: auto-resolved via `which`)",
    )
    p.add_argument(
        "--launch-dir",
        help=f"directory to write the .ps1 (default: $LOCALAPPDATA/ppds)",
    )
    p.add_argument(
        "--dry-run",
        action="store_true",
        help="write script and print spawn command without executing",
    )
    return p.parse_args(argv)


def main(argv: Optional[List[str]] = None) -> int:
    args = _parse_args(argv if argv is not None else sys.argv[1:])
    _validate_name(args.name)
    if args.prompt_stdin:
        prompt = sys.stdin.read()
    else:
        if not os.path.exists(args.prompt_file):
            sys.stderr.write(f"prompt file not found: {args.prompt_file}\n")
            return 1
        with open(args.prompt_file, "r", encoding="utf-8") as f:
            prompt = f.read()
    return launch(
        target=args.target,
        name=args.name,
        prompt=prompt,
        claude_path=args.claude_path,
        launch_dir=args.launch_dir,
        dry_run=args.dry_run,
    )


if __name__ == "__main__":
    sys.exit(main())
