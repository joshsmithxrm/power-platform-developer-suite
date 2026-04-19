#!/usr/bin/env python3
"""
Safely create a feature worktree rooted on origin/main.

Wraps `git worktree add` with the safety properties #799 demanded:
  - always `git fetch origin main` first, so the worktree starts from
    the current remote tip (not a stale local `main` ref)
  - detect a stranded target directory (on-disk but not a registered
    worktree) and refuse loudly rather than silently producing a broken
    worktree with stranded index residue
  - after creation, sanity-check that `git status` is clean and HEAD
    matches origin/main

Usage:
    python scripts/worktree-create.py --name feat-name [--branch feat/feat-name]
    python scripts/worktree-create.py --name feat-name --repo-root /abs/path

Exit codes:
    0 — worktree created and sanity-checked
    1 — stranded directory detected (user must clean up); nothing created
    2 — fetch or creation failed
    3 — sanity check failed after creation

Pure-Python helpers (`detect_stranded`, `parse_registered_worktrees`) are
exposed for unit tests; no subprocess calls happen at import time.
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
from typing import List, Optional, Tuple


def parse_registered_worktrees(porcelain: str) -> List[str]:
    """Return absolute paths of worktrees registered with git.

    `git worktree list --porcelain` emits blocks whose first line is
    `worktree <abs-path>`. We want those paths normalized with forward
    slashes so the caller can compare on any platform.
    """
    paths: List[str] = []
    for line in porcelain.splitlines():
        if line.startswith("worktree "):
            paths.append(line[len("worktree ") :].strip().replace("\\", "/"))
    return paths


def detect_stranded(target_abs: str, registered: List[str]) -> bool:
    """True iff `target_abs` exists on disk but is NOT registered.

    A stranded directory is the root cause of #799's "stranded index"
    failure mode: a previous worktree was rm -rf'd without
    `git worktree remove`, leaving `.git/worktrees/<name>` metadata
    and/or a leftover index. Adding on top of it silently produces a
    broken worktree.
    """
    if not os.path.exists(target_abs):
        return False
    normalized = os.path.abspath(target_abs).replace("\\", "/").rstrip("/")
    registered_norm = [p.rstrip("/") for p in registered]
    return normalized not in registered_norm


def _run(cmd: List[str], cwd: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(
        cmd,
        cwd=cwd,
        capture_output=True,
        text=True,
        timeout=60,
        stdin=subprocess.DEVNULL,
    )
    if check and result.returncode != 0:
        sys.stderr.write(
            f"command failed ({result.returncode}): {' '.join(cmd)}\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}\n"
        )
    return result


def create(
    repo_root: str,
    name: str,
    branch: Optional[str] = None,
) -> Tuple[int, str]:
    """Create `.worktrees/<name>` based on `origin/main`.

    Returns (exit_code, message). See module docstring for codes.
    """
    branch = branch or f"feat/{name}"
    target_rel = os.path.join(".worktrees", name)
    target_abs = os.path.abspath(os.path.join(repo_root, target_rel))

    # 1. Fetch origin/main so we can base the worktree on the current
    #    remote tip (fixes #799 stale-base pathology).
    fetch = _run(["git", "fetch", "origin", "main"], cwd=repo_root, check=False)
    if fetch.returncode != 0:
        return 2, f"git fetch origin main failed:\n{fetch.stderr}"

    # 2. Prune stale .git/worktrees/ metadata (cheap and safe even when
    #    no on-disk directory is stranded).
    _run(["git", "worktree", "prune"], cwd=repo_root, check=False)

    # 3. Stranded-directory check. `git worktree list --porcelain` is
    #    the source of truth for what's actually a worktree.
    list_result = _run(
        ["git", "worktree", "list", "--porcelain"], cwd=repo_root, check=False
    )
    registered = parse_registered_worktrees(list_result.stdout)
    if detect_stranded(target_abs, registered):
        return 1, (
            f"STRANDED: {target_rel} exists on disk but is not a registered worktree.\n"
            f"  Inspect the directory for in-progress work first.\n"
            f"  Then run: rm -rf {target_rel} && git worktree prune\n"
            f"  Then re-run /start."
        )

    # 4. Create from origin/main. Branch may or may not already exist.
    branch_exists = (
        _run(
            ["git", "branch", "--list", branch], cwd=repo_root, check=False
        ).stdout.strip()
        != ""
    )
    if branch_exists:
        # Reuse existing branch; don't reparent it onto origin/main —
        # that would rewrite user work.
        add_cmd = ["git", "worktree", "add", target_rel, branch]
    else:
        add_cmd = ["git", "worktree", "add", target_rel, "-b", branch, "origin/main"]

    add = _run(add_cmd, cwd=repo_root, check=False)
    if add.returncode != 0:
        return 2, f"git worktree add failed:\n{add.stderr}"

    # 5. Sanity-check. If HEAD is detached or index is dirty, refuse to
    #    hand the worktree back — the caller would commit on a broken
    #    base.
    status = _run(
        ["git", "status", "--porcelain"], cwd=target_abs, check=False
    )
    if status.stdout.strip():
        return 3, (
            f"SANITY: new worktree {target_rel} has dirty index:\n"
            f"{status.stdout}\n"
            f"This is the #799 stranded-index pathology. Remove the "
            f"worktree and clean up the directory before retrying."
        )

    if not branch_exists:
        head_sha = _run(
            ["git", "rev-parse", "HEAD"], cwd=target_abs, check=False
        ).stdout.strip()
        origin_sha = _run(
            ["git", "rev-parse", "origin/main"], cwd=repo_root, check=False
        ).stdout.strip()
        if head_sha and origin_sha and head_sha != origin_sha:
            return 3, (
                f"SANITY: new worktree HEAD ({head_sha[:8]}) does not match "
                f"origin/main ({origin_sha[:8]}). Refusing to hand back a "
                f"worktree on a stale base."
            )

    return 0, f"created {target_rel} on branch {branch}"


def _parse_args(argv: List[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--name", required=True, help="worktree short name (kebab-case)")
    p.add_argument("--branch", help="branch name (default: feat/<name>)")
    p.add_argument(
        "--repo-root",
        default=os.getcwd(),
        help="main repo root (default: cwd)",
    )
    return p.parse_args(argv)


def main(argv: Optional[List[str]] = None) -> int:
    args = _parse_args(argv if argv is not None else sys.argv[1:])
    code, msg = create(args.repo_root, args.name, args.branch)
    if code == 0:
        print(msg)
    else:
        sys.stderr.write(msg + "\n")
    return code


if __name__ == "__main__":
    sys.exit(main())
