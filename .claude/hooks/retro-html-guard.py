#!/usr/bin/env python3
"""PreToolUse hook: block .retros/*.html writes in interactive mode.

In pipeline mode (PPDS_PIPELINE=1), HTML retro artifacts are expected.
In interactive mode, the conversation IS the analysis - HTML is noise.

Triggers on Write where file_path matches .retros/*.html.
Exit 0: allow. Exit 2: block.
"""
import json
import os
import sys


def main():
    if os.environ.get("PPDS_PIPELINE"):
        sys.exit(0)
    try:
        payload = json.loads(sys.stdin.read())
    except (json.JSONDecodeError, ValueError):
        sys.exit(0)
    tool_input = payload.get("tool_input", {}) or {}
    file_path = (tool_input.get("file_path") or "").replace("\\", "/")
    # Match .retros/<anything>.html
    if "/.retros/" in file_path or file_path.startswith(".retros/") or file_path.startswith("/.retros/"):
        if file_path.endswith(".html"):
            print(
                "BLOCKED: HTML artifacts are not written in interactive retro mode.\n"
                "  The conversation IS the analysis. See retro SKILL.md Step 8b.\n"
                "  (To override: re-run with PPDS_PIPELINE=1.)",
                file=sys.stderr,
            )
            sys.exit(2)
    sys.exit(0)


if __name__ == "__main__":
    main()
