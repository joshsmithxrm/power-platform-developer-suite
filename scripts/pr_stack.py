"""PR-stack envelope helper for /design plan decomposition.

See specs/feat-1070-pr-stack-alpha.md (ACs 01-14).

Public API:
    build_envelope(spec, entries, *, justification=None) -> dict
    validate_envelope(envelope) -> None
    write_envelope(envelope, path) -> None

CLI:
    python scripts/pr_stack.py validate <path>
        Exit 0 on valid envelope; exit 1 with stderr message on failure.
        Stdout is always empty (Constitution I1).
"""
from __future__ import annotations

import collections
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Union

SCHEMA_VERSION = "1.0"

_REQUIRED_ENTRY_FIELDS = (
    "id",
    "title",
    "branch_suffix",
    "plan",
    "files",
    "size_estimate",
    "depends_on",
    "ac_refs",
)


def build_envelope(
    spec: str,
    entries: list,
    *,
    justification: Union[str, None] = None,
) -> dict:
    """Construct and return a validated envelope dict.

    Raises ValueError when entries has exactly 1 item and justification is
    absent or empty after stripping whitespace.
    """
    envelope: dict = {
        "schema_version": SCHEMA_VERSION,
        "spec": spec,
        "created_at": datetime.now(timezone.utc).isoformat(),
        "stack": list(entries),
    }
    if justification is not None and justification.strip() != "":
        envelope["justification"] = justification
    validate_envelope(envelope)
    return envelope


def validate_envelope(envelope: dict) -> None:
    """Raise ValueError on any schema violation."""
    if not isinstance(envelope, dict):
        raise ValueError("envelope must be a dict")

    schema_version = envelope.get("schema_version")
    if not isinstance(schema_version, str) or not schema_version.startswith("1."):
        raise ValueError(
            f"expected schema_version major 1, got {schema_version!r}"
        )

    spec = envelope.get("spec")
    if not isinstance(spec, str) or spec == "":
        raise ValueError("spec must be a non-empty string")

    created_at = envelope.get("created_at")
    if not isinstance(created_at, str) or created_at == "":
        raise ValueError("created_at must be a non-empty string")

    stack = envelope.get("stack")
    if not isinstance(stack, list) or len(stack) == 0:
        raise ValueError("stack must be a non-empty list")

    if len(stack) == 1:
        just = envelope.get("justification")
        if not isinstance(just, str) or just.strip() == "":
            raise ValueError("justification required for single-entry stack")

    seen_ids: set = set()
    for entry in stack:
        if not isinstance(entry, dict):
            raise ValueError("stack entry must be a dict")
        for field_name in _REQUIRED_ENTRY_FIELDS:
            if field_name not in entry:
                entry_id = entry.get("id", "<unknown>")
                raise ValueError(
                    f"entry {entry_id!r} missing required field: {field_name}"
                )

        entry_id = entry["id"]
        if not isinstance(entry_id, str) or entry_id == "":
            raise ValueError("stack entry id must be a non-empty string")
        if entry_id in seen_ids:
            raise ValueError(f"stack entry id must be unique: {entry_id}")
        seen_ids.add(entry_id)

        title = entry["title"]
        if not isinstance(title, str) or title == "":
            raise ValueError(f"entry {entry_id!r} title must be non-empty")

        branch_suffix = entry["branch_suffix"]
        if not isinstance(branch_suffix, str) or branch_suffix == "":
            raise ValueError(
                f"entry {entry_id!r} branch_suffix must be a non-empty string"
            )
        if "/" in branch_suffix:
            raise ValueError(
                f"entry {entry_id!r} branch_suffix must not contain slashes"
            )

        plan = entry["plan"]
        if not isinstance(plan, str) or plan == "":
            raise ValueError(f"entry {entry_id!r} plan must be non-empty")

        files = entry["files"]
        if not isinstance(files, list) or len(files) == 0:
            raise ValueError(
                f"entry {entry_id!r} files must be a non-empty list"
            )
        for f in files:
            if not isinstance(f, str) or f == "":
                raise ValueError(
                    f"entry {entry_id!r} files entries must be non-empty strings"
                )

        size_estimate = entry["size_estimate"]
        if not isinstance(size_estimate, str) or size_estimate == "":
            raise ValueError(
                f"entry {entry_id!r} size_estimate must be non-empty"
            )

        depends_on = entry["depends_on"]
        if not isinstance(depends_on, list):
            raise ValueError(
                f"entry {entry_id!r} depends_on must be a list"
            )

        ac_refs = entry["ac_refs"]
        if not isinstance(ac_refs, list):
            raise ValueError(f"entry {entry_id!r} ac_refs must be a list")

        if "phase_label" in entry:
            phase_label = entry["phase_label"]
            if not isinstance(phase_label, str) or phase_label == "":
                raise ValueError(
                    f"entry {entry_id!r} phase_label must be non-empty if present"
                )

    valid_ids = set(seen_ids)
    for entry in stack:
        for dep in entry["depends_on"]:
            if dep not in valid_ids:
                raise ValueError(
                    f"entry {entry['id']!r} depends_on references unknown id: {dep}"
                )

    _detect_cycles(stack)


def _detect_cycles(stack: list) -> None:
    """Kahn's algorithm — raise ValueError if depends_on graph has a cycle."""
    in_degree: dict = {entry["id"]: 0 for entry in stack}
    adj: dict = {entry["id"]: [] for entry in stack}
    for entry in stack:
        for dep in entry["depends_on"]:
            adj[dep].append(entry["id"])
            in_degree[entry["id"]] += 1

    queue: collections.deque = collections.deque(
        node for node, deg in in_degree.items() if deg == 0
    )
    visited = 0
    while queue:
        node = queue.popleft()
        visited += 1
        for neighbour in adj[node]:
            in_degree[neighbour] -= 1
            if in_degree[neighbour] == 0:
                queue.append(neighbour)

    if visited != len(stack):
        remaining = sorted(node for node, deg in in_degree.items() if deg > 0)
        raise ValueError(
            f"circular dependency detected involving: {', '.join(remaining)}"
        )


def write_envelope(envelope: dict, path) -> None:
    """Validate then write JSON with 2-space indent and trailing newline.

    Validation happens before any file I/O — no partial file is written
    on validation failure.
    """
    validate_envelope(envelope)
    target = Path(path)
    payload = json.dumps(envelope, indent=2) + "\n"
    target.write_text(payload, encoding="utf-8")


def _cli_validate(path: str) -> int:
    try:
        envelope = json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        print(f"pr_stack: {exc}", file=sys.stderr)
        return 1
    except json.JSONDecodeError as exc:
        print(f"pr_stack: invalid JSON in {path}: {exc}", file=sys.stderr)
        return 1

    try:
        validate_envelope(envelope)
    except ValueError as exc:
        print(f"pr_stack: {exc}", file=sys.stderr)
        return 1
    return 0


def main(argv: list) -> int:
    if len(argv) >= 2 and argv[1] == "validate" and len(argv) == 3:
        return _cli_validate(argv[2])
    print(
        "usage: python scripts/pr_stack.py validate <path>",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
