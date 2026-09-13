#!/usr/bin/env python3
"""Create or reconcile workflow-owned release labels from one manifest."""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Optional, Sequence


_COLOR_RE = re.compile(r"^[0-9A-Fa-f]{6}$")
_REPOSITORY_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


@dataclass(frozen=True)
class ReleaseLabel:
    name: str
    color: str
    description: str


class LabelSyncError(RuntimeError):
    """A label could not be created or reconciled."""


CommandRunner = Callable[[Sequence[str]], subprocess.CompletedProcess[str]]


def load_manifest(path: Path) -> tuple[ReleaseLabel, ...]:
    """Load a strict, duplicate-free label manifest."""
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"could not read release-label manifest {path}: {exc}") from exc
    if not isinstance(payload, dict) or not isinstance(payload.get("labels"), list):
        raise ValueError("release-label manifest must contain a labels list")

    labels: list[ReleaseLabel] = []
    seen: set[str] = set()
    for raw in payload["labels"]:
        if not isinstance(raw, dict):
            raise ValueError("every release-label manifest entry must be an object")
        name = raw.get("name")
        color = raw.get("color")
        description = raw.get("description")
        if not isinstance(name, str) or not name or "\n" in name:
            raise ValueError("release label name must be a non-empty single line")
        if name in seen:
            raise ValueError(f"duplicate release label in manifest: {name}")
        if not isinstance(color, str) or not _COLOR_RE.fullmatch(color):
            raise ValueError(f"release label {name!r} must have a six-digit hex color")
        if not isinstance(description, str) or not description or "\n" in description:
            raise ValueError(
                f"release label {name!r} description must be a non-empty single line"
            )
        seen.add(name)
        labels.append(ReleaseLabel(name, color.upper(), description))

    if not labels:
        raise ValueError("release-label manifest must not be empty")
    return tuple(labels)


def _default_runner(command: Sequence[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        list(command),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )


def sync_labels(
    repository: str,
    labels: Sequence[ReleaseLabel],
    *,
    runner: CommandRunner = _default_runner,
) -> None:
    """Idempotently create/update every manifest label with ``gh --force``."""
    if not _REPOSITORY_RE.fullmatch(repository):
        raise ValueError("repository must use the owner/name form")
    for label in labels:
        command = [
            "gh",
            "label",
            "create",
            label.name,
            "--repo",
            repository,
            "--color",
            label.color,
            "--description",
            label.description,
            "--force",
        ]
        result = runner(command)
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "unknown gh error").strip()
            raise LabelSyncError(
                f"could not ensure release label {label.name!r} in "
                f"{repository}: {detail}"
            )


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Create or reconcile workflow-owned release labels.",
    )
    parser.add_argument(
        "--repo",
        default=os.environ.get("GITHUB_REPOSITORY"),
        help="GitHub repository in owner/name form (defaults to GITHUB_REPOSITORY)",
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path(__file__).with_name("release_labels.json"),
    )
    args = parser.parse_args(argv)
    if not args.repo:
        parser.error("--repo or GITHUB_REPOSITORY is required")

    try:
        sync_labels(args.repo, load_manifest(args.manifest))
    except (LabelSyncError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
