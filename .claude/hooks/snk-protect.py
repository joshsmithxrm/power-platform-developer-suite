"""PreToolUse hook: block Edit/Write on any *.snk file.

Strong-name keypairs (.snk) are decoded from CI secrets at publish time and
must NEVER be tracked in the repo. Regenerating one would change the assembly
PublicKeyToken and break consumers (PPDS.Plugins is the most visible victim,
but PPDS.Dataverse and PPDS.Migration are equally affected).

This hook is a deterministic alternative to the previous CLAUDE.md NEVER rule,
which only mentioned PPDS.Plugins.snk and could be ignored.

Triggers on Write/Edit where ``payload["tool_input"]["file_path"]`` ends in
``.snk`` (case-insensitive). Works on Windows native paths (``C:\\...``),
MSYS-style paths (``/c/...``), and POSIX paths.

Exit codes:
- 0: allow (path does not match)
- 2: block (path is a .snk; print message to stderr)
"""

from __future__ import annotations

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _pathfix import normalize_msys_path  # noqa: E402


def is_snk_path(file_path: str) -> bool:
    """Return True if file_path looks like a strong-name keypair file.

    Matches any path ending in ``.snk`` (case-insensitive). Normalizes MSYS
    drive paths so ``/c/foo.snk`` and ``C:\\foo.snk`` both match.
    """
    if not file_path:
        return False
    normalized = normalize_msys_path(file_path).replace("\\", "/").lower()
    return normalized.endswith(".snk")


def main() -> None:
    # Read the tool envelope. If stdin is empty/garbled, allow rather than
    # blocking unrelated tools with a traceback.
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)

    # Claude Code wraps tool params under tool_input; older format had file_path
    # at the top level. Use the wrapped form (matches the documented envelope)
    # and fall back to top-level for backwards compatibility.
    tool_input = payload.get("tool_input", {}) or {}
    file_path = tool_input.get("file_path") or payload.get("file_path", "")

    if not is_snk_path(file_path):
        sys.exit(0)

    print(
        "BLOCKED: refusing to write/edit a strong-name keypair (.snk).",
        file=sys.stderr,
    )
    print(
        "  .snk files are decoded from CI secrets at publish time and must "
        "never be tracked in the repo.",
        file=sys.stderr,
    )
    print(
        "  Regenerating a .snk changes the assembly PublicKeyToken and breaks "
        "every downstream consumer.",
        file=sys.stderr,
    )
    print(
        "  See docs/RELEASE.md > Strong-name rotation for the rare valid case.",
        file=sys.stderr,
    )
    sys.exit(2)


if __name__ == "__main__":
    main()
