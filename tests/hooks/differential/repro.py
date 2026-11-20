"""Re-run one saved failing case against the hook.

    python tests/hooks/differential/repro.py <name>.case.json [<more>.case.json ...]

A case file (written by differential.py --cases) holds the tool name, the command text and the mode.
The hook runs as a subprocess in a fresh sandbox (made-up allowlist and profile store), exactly as in
the oracle run. Prints the hook's exit code; 0 means the hook still lets the harmful text through.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from _harness import Sandbox, run_hook  # noqa: E402


def main(paths: list) -> int:
    sandbox = Sandbox()
    still = 0
    try:
        for p in paths:
            case = json.loads(Path(p).read_text(encoding="utf-8"))
            res = run_hook(sandbox, case["tool"], case["command"], case["mode"])
            still += res.code != 2
            first = res.stderr.strip().splitlines()[:1]
            print(f"exit={res.code} {case['mode']}/{case['tool']} [{case.get('label', '')}] {case['command'][:120]!r} "
                  f"{first[0] if first else ''}")
    finally:
        sandbox.cleanup()
    return 1 if still else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
