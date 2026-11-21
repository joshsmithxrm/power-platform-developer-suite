#!/usr/bin/env python3
"""CLI for the read-only PPDS release scope advisory.

``--breaking-prs-file`` names a JSON list of merged pull requests labelled
``breaking-change``, in the shape ``gh pr list --state merged --label
breaking-change --json number,title,mergeCommit`` prints (``files`` is
optional). With
``--base``/``--head`` only the pull requests whose merge commit is in that
range count, and a pull request without ``files`` is mapped from its merge
commit's diff. If the plan would release a surface such a pull request changes
as a patch or minor version, the plan lists the refusal and the command exits
1 (#1789). Without the file the command exits 0 as before.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Optional

from release_model import (
    BreakingChange,
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


def breaking_changes_from_file(
    path: Path,
    repo_root: Path,
    base: Optional[str],
    head: Optional[str],
) -> list[BreakingChange]:
    """Read merged ``breaking-change`` pull requests, keeping those in range."""
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError(f"{path}: expected a JSON list of pull requests")
    in_range: Optional[set[str]] = None
    if base and head:
        listed = _git(repo_root, "rev-list", f"{base}..{head}") or ""
        in_range = {line.strip() for line in listed.splitlines() if line.strip()}
    changes: list[BreakingChange] = []
    for item in data:
        number = int(item["number"])
        oid = str((item.get("mergeCommit") or {}).get("oid") or "")
        if in_range is not None and oid not in in_range:
            continue
        files = item.get("files")
        if files:
            paths = [str(entry["path"]) for entry in files]
        elif oid:
            diff = _git(repo_root, "diff", "--name-only", "--no-renames", f"{oid}^", oid) or ""
            paths = [line.strip() for line in diff.splitlines() if line.strip()]
        else:
            raise ValueError(
                f"#{number}: neither files nor a merge commit; cannot map it to a surface"
            )
        changes.append(BreakingChange(number=number, title=str(item.get("title") or ""), paths=tuple(paths)))
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
    if plan["refusals"]:
        return f"Release plan refused: breaking change in a {plan['release_kind']} release"
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
    manifest_group = parser.add_mutually_exclusive_group()
    manifest_group.add_argument(
        "--delivery-manifest",
        type=Path,
        help="Explicit release_surfaces.json to use instead of the repository copy",
    )
    manifest_group.add_argument(
        "--fallback-delivery-manifest",
        type=Path,
        help=(
            "Trusted current release_surfaces.json used only when the analyzed "
            "repository has no manifest; missing historical project paths are "
            "mapped conservatively"
        ),
    )
    parser.add_argument("--base", help="Base git revision for semantic diff analysis")
    parser.add_argument("--head", help="Head git revision for semantic diff analysis")
    parser.add_argument("--files-file", type=Path, help="Fallback list of changed paths, one per line")
    parser.add_argument("--release-kind", choices=["patch", "minor", "major"], default="patch")
    parser.add_argument("--channel", choices=["stable", "prerelease"], default="stable")
    parser.add_argument("--format", choices=["json", "markdown", "title"], default="json")
    parser.add_argument(
        "--breaking-prs-file",
        type=Path,
        help=(
            "JSON list of merged pull requests labelled breaking-change "
            "(gh pr list --json number,title,mergeCommit); a patch or "
            "minor plan that releases a surface they change is refused (exit 1)"
        ),
    )
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
    fallback_delivery_manifest = (
        args.fallback_delivery_manifest.resolve()
        if args.fallback_delivery_manifest
        else None
    )
    breaking = (
        breaking_changes_from_file(args.breaking_prs_file, repo_root, args.base, args.head)
        if args.breaking_prs_file
        else []
    )
    plan = build_release_plan(
        ReleaseGraph.discover(
            repo_root,
            delivery_manifest_path=delivery_manifest,
            fallback_delivery_manifest_path=fallback_delivery_manifest,
        ),
        changes,
        release_kind=args.release_kind,
        channel=args.channel,
        tags=collect_git_tags(repo_root),
        breaking_changes=breaking,
    )
    if args.format == "markdown":
        print(render_markdown(plan), end="")
    elif args.format == "title":
        print(_title(plan))
    else:
        print(json.dumps(plan, indent=2, sort_keys=True))
    for refusal in plan["refusals"]:
        print(f"::error title=Release plan refused::{refusal}", file=sys.stderr)
    return 1 if plan["refusals"] else 0


if __name__ == "__main__":
    sys.exit(main())
