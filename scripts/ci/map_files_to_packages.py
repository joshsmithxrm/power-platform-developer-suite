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
import re
import sys
from typing import Optional

# The 8 NuGet packages that make up the PPDS platform.
KNOWN_PACKAGES = frozenset({
    "Auth",
    "Cli",
    "Dataverse",
    "Extension",
    "Mcp",
    "Migration",
    "Plugins",
    "Query",
})

# Match src/PPDS.<Name>/ at the start of a path.  The name must be at least
# one character and consist only of word characters (letters, digits,
# underscores) — this rejects bare ``src/PPDS./foo.cs`` entries.
_PREFIX_RE = re.compile(r"^src/PPDS\.(\w+)/")


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
    packages: set[str] = set()
    for path in file_paths:
        m = _PREFIX_RE.match(path)
        if m:
            name = m.group(1)
            if name in KNOWN_PACKAGES:
                packages.add(f"PPDS.{name}")
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
