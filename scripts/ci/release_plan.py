#!/usr/bin/env python3
"""CLI for the read-only PPDS release scope advisory."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Optional

from release_model import (
    FileChange,
    ReleaseGraph,
    build_release_plan,
    collect_git_tags,
    render_markdown,
)


def _git(repo_root: Path, *args: str, allow_missing: bool = False) -> Optional[str]:
    result = subprocess.run(
        ["git", *args],
        cwd=repo_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode == 0:
        return result.stdout
    if allow_missing:
        return None
    raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")


def changes_from_git(repo_root: Path, base: str, head: str) -> list[FileChange]:
    output = _git(repo_root, "diff", "--name-only", "--no-renames", base, head) or ""
    changes: list[FileChange] = []
    for path in (line.strip() for line in output.splitlines() if line.strip()):
        changes.append(
            FileChange(
                path=path,
                before=_git(repo_root, "show", f"{base}:{path}", allow_missing=True),
                after=_git(repo_root, "show", f"{head}:{path}", allow_missing=True),
            )
        )
    return changes


def ensure_graph_revision(repo_root: Path, head: str) -> None:
    """Fail rather than analyze a diff with a graph from another revision."""
    checked_out = (_git(repo_root, "rev-parse", "HEAD") or "").strip()
    analyzed = (_git(repo_root, "rev-parse", head) or "").strip()
    if checked_out != analyzed:
        raise RuntimeError(
            "Release graph revision mismatch: "
            f"checkout is {checked_out or '(unknown)'} but --head resolves to "
            f"{analyzed or '(unknown)'}. Check out the analyzed head first."
        )


def _title(plan: dict) -> str:
    targets = plan["release_targets"]
    if not targets:
        return "No product release needed"
    return "Patch release review: " + ", ".join(targets)


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Explain PPDS release impact. This command is read-only and never "
            "creates tags, publishes packages, or dispatches workflows."
        )
    )
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument(
        "--delivery-manifest",
        type=Path,
        help=(
            "Optional release_surfaces.json supplied by the pinned automation "
            "revision when analyzing a historical commit"
        ),
    )
    parser.add_argument("--base", help="Base git revision for semantic diff analysis")
    parser.add_argument("--head", help="Head git revision for semantic diff analysis")
    parser.add_argument("--files-file", type=Path, help="Fallback list of changed paths, one per line")
    parser.add_argument("--release-kind", choices=["patch", "minor", "major"], default="patch")
    parser.add_argument("--channel", choices=["stable", "prerelease"], default="stable")
    parser.add_argument("--format", choices=["json", "markdown", "title"], default="json")
    args = parser.parse_args(argv)

    repo_root = args.repo_root.resolve()
    if bool(args.base) != bool(args.head):
        parser.error("--base and --head must be supplied together")
    if args.base and args.head:
        ensure_graph_revision(repo_root, args.head)
        changes = changes_from_git(repo_root, args.base, args.head)
    elif args.files_file:
        changes = [
            FileChange(path=line.strip())
            for line in args.files_file.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
    else:
        parser.error("provide --base/--head or --files-file")

    delivery_manifest = (
        args.delivery_manifest.resolve() if args.delivery_manifest else None
    )
    plan = build_release_plan(
        ReleaseGraph.discover(
            repo_root,
            delivery_manifest_path=delivery_manifest,
        ),
        changes,
        release_kind=args.release_kind,
        channel=args.channel,
        tags=collect_git_tags(repo_root),
    )
    if args.format == "markdown":
        print(render_markdown(plan), end="")
    elif args.format == "title":
        print(_title(plan))
    else:
        print(json.dumps(plan, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
