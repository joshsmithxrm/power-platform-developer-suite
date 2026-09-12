#!/usr/bin/env python3
"""Map changed file paths to PPDS NuGet package names.

Given a list of file paths (one per line on stdin, or via --files args),
determines which PPDS packages are affected based on the ``src/PPDS.<Name>/``
directory prefix.

Usage:
    python scripts/ci/map_files_to_packages.py --files src/PPDS.Query/Foo.cs
    git diff --name-only HEAD~1 | python scripts/ci/map_files_to_packages.py

Output:
    JSON array of package names to stdout, e.g. ["PPDS.Cli", "PPDS.Query"]
    Returns ["unknown"] when no paths match a recognized src/PPDS.* prefix.

Exit codes:
    0 — always (detection of unknown packages is surfaced in the output, not
        via exit code — the caller reads the JSON and decides what to do)
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path, PurePosixPath
from typing import Optional

from release_model import ReleaseGraph


REPO_ROOT = Path(__file__).resolve().parents[2]


def map_files_to_packages(file_paths: list[str]) -> list[str]:
    """Map a list of changed file paths to affected PPDS package names.

    Parameters
    ----------
    file_paths:
        Relative file paths as reported by ``git diff --name-only`` or
        ``gh pr diff --name-only``.

    Returns
    -------
    Sorted, deduplicated list of ``PPDS.<Name>`` package names whose source
    directories appear in *file_paths*.  Returns ``["unknown"]`` if no path
    matches a recognized ``src/PPDS.<Name>/`` prefix.
    """
    graph = ReleaseGraph.discover(REPO_ROOT)
    roots = {
        surface.root.rstrip("/") + "/": name
        for name, surface in graph.surfaces.items()
    }
    packages: set[str] = set()
    for raw_path in file_paths:
        path = str(PurePosixPath(raw_path.replace("\\", "/")))
        for root, name in roots.items():
            if path.startswith(root):
                packages.add(name)
                break
    return sorted(packages) if packages else ["unknown"]


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Map changed file paths to PPDS package names.",
    )
    parser.add_argument(
        "--files",
        nargs="*",
        metavar="PATH",
        help="File paths to map (alternative to reading from stdin)",
    )
    args = parser.parse_args(argv)

    if args.files is not None:
        file_paths = args.files
    else:
        file_paths = [line.rstrip("\n") for line in sys.stdin if line.strip()]

    result = map_files_to_packages(file_paths)
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
