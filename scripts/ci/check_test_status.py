"""Fail-closed evaluator for the always-reporting Test aggregate."""
from __future__ import annotations

import json
import os
import sys

SUITES = {"code": "test", "extraction": "single-file-extraction", "python": "python-tests"}


def failures(needs: dict) -> list[str]:
    changes = needs.get("changes", {})
    if changes.get("result") != "success":
        return [f"Changes detection did not succeed: {changes.get('result', 'missing')}"]
    outputs = changes.get("outputs", {})
    errors = []
    for flag, job in SUITES.items():
        selected = outputs.get(flag)
        result = needs.get(job, {}).get("result", "missing")
        if selected not in ("true", "false"):
            errors.append(f"{job}: missing or invalid classification ({selected!r})")
        elif selected == "true" and result != "success":
            errors.append(f"{job}: required suite did not succeed ({result})")
        elif selected == "false" and result not in ("skipped", "success"):
            errors.append(f"{job}: unexpected result for an unneeded suite ({result})")
    return errors


def main() -> int:
    try:
        needs = json.loads(os.environ["TEST_NEEDS_JSON"])
        if not isinstance(needs, dict):
            raise ValueError("expected needs object")
        errors = failures(needs)
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        print(f"Invalid test status input: {error}", file=sys.stderr)
        return 1
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("All required test suites passed; unneeded suites were intentionally skipped.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
