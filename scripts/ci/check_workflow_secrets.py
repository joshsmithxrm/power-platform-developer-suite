#!/usr/bin/env python3
"""Rule 2 of the pre-merge gate: workflow secret-ref drift detection.

Catches the failure mode from PR #797 / ppds-docs#15, where a workflow
referenced ``AUDIT_REPO_TOKEN`` — a secret that didn't exist in the repo.
The workflow merged green (because the missing-secret check is runtime, not
parse-time), then failed at first execution.

For every PR that touches ``.github/workflows/*.yml`` or ``*.yaml``:

1. Parse each changed workflow file.
2. Extract every ``${{ secrets.X }}`` and ``${{ vars.X }}`` reference.
3. Compare against the actual repo's secret/variable inventory
   (``gh secret list`` and ``gh variable list``).
4. Block merge if any referenced name is missing.

Bypass marker: ``[secret-ref-allow: <name>]`` in PR title or body, repeated
for each missing name. Intended for legitimate cases — for example, secrets
defined on a reusable workflow caller, or environment-scoped secrets that
``gh secret list`` doesn't enumerate without ``--env``.

Note: ``GITHUB_TOKEN`` is built-in and always considered present.

Usage:
    python -m scripts.ci.check_workflow_secrets --pr 123
    python -m scripts.ci.check_workflow_secrets --files <a.yml> <b.yml> --title "..." --body "..."

Exit codes:
    0 — all referenced secrets/vars exist OR are bypassed
    1 — at least one missing reference and not bypassed
    2 — invocation / data error
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from typing import Iterable, Optional

# Match ``${{ secrets.NAME }}`` and ``${{ vars.NAME }}`` allowing arbitrary
# whitespace inside the ``${{ }}`` and around the dot. Names are
# letters/digits/underscores per GitHub Actions spec.
SECRET_REF_RE = re.compile(r"\$\{\{\s*secrets\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")
VAR_REF_RE = re.compile(r"\$\{\{\s*vars\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")
ALLOW_MARKER_RE = re.compile(r"\[secret-ref-allow:\s*([A-Za-z_][A-Za-z0-9_]*)\s*\]")

# Built-in / always-present secrets.
BUILTIN_SECRETS = frozenset({"GITHUB_TOKEN"})


def _run_gh(args: list[str]) -> str:
    """Run a gh command, return stdout. Raises RuntimeError on failure."""
    try:
        out = subprocess.run(
            ["gh", *args], capture_output=True, text=True, check=True,
        )
    except FileNotFoundError as e:
        raise RuntimeError(f"gh CLI not available: {e}") from e
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"gh {' '.join(args)} failed: {e.stderr.strip()}") from e
    return out.stdout


def fetch_pr_metadata(pr_number: int) -> dict:
    """Return PR title, body, and changed workflow files."""
    raw = _run_gh([
        "pr", "view", str(pr_number),
        "--json", "title,body,files",
    ])
    data = json.loads(raw)
    files = [f.get("path", "") for f in data.get("files", [])]
    workflow_files = [
        f for f in files
        if f.startswith(".github/workflows/")
        and (f.endswith(".yml") or f.endswith(".yaml"))
    ]
    return {
        "title": data.get("title", "") or "",
        "body": data.get("body", "") or "",
        "workflow_files": workflow_files,
    }


def fetch_repo_secrets() -> set[str]:
    """List repo-level secret names via `gh secret list`."""
    raw = _run_gh(["secret", "list", "--json", "name"])
    data = json.loads(raw) if raw.strip() else []
    return {item["name"] for item in data if "name" in item}


def fetch_repo_variables() -> set[str]:
    """List repo-level variable names via `gh variable list`."""
    raw = _run_gh(["variable", "list", "--json", "name"])
    data = json.loads(raw) if raw.strip() else []
    return {item["name"] for item in data if "name" in item}


def extract_refs(workflow_text: str) -> tuple[set[str], set[str]]:
    """Extract (secret_names, var_names) referenced in a workflow file.

    Pure regex — ``yaml`` parsing isn't required because the references are
    string-substituted before YAML semantics; regex catches them in any
    YAML position (key, value, multi-line scalar).
    """
    secrets = set(SECRET_REF_RE.findall(workflow_text))
    variables = set(VAR_REF_RE.findall(workflow_text))
    return secrets, variables


def find_allow_markers(title: str, body: str) -> set[str]:
    """Return the set of names allowed via ``[secret-ref-allow: NAME]`` markers."""
    allowed: set[str] = set()
    for blob in (title or "", body or ""):
        for m in ALLOW_MARKER_RE.finditer(blob):
            allowed.add(m.group(1))
    return allowed


def _read_workflow(path: str) -> str:
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def check_workflow_secrets(
    workflow_files: Iterable[str],
    repo_secrets: set[str],
    repo_variables: set[str],
    title: str,
    body: str,
    *,
    reader=_read_workflow,
) -> tuple[bool, str]:
    """Apply the secret-ref drift rule.

    Returns (passed, message).

    `reader` is injectable for tests so they don't need real files on disk.
    """
    files = list(workflow_files)
    if not files:
        return True, "No workflow files changed — rule not applicable."

    referenced_secrets: dict[str, set[str]] = {}  # name -> set(file)
    referenced_vars: dict[str, set[str]] = {}

    for f in files:
        try:
            text = reader(f)
        except OSError as e:
            return False, f"could not read workflow file {f}: {e}"
        s_refs, v_refs = extract_refs(text)
        for name in s_refs:
            referenced_secrets.setdefault(name, set()).add(f)
        for name in v_refs:
            referenced_vars.setdefault(name, set()).add(f)

    allowed = find_allow_markers(title, body)

    missing_secrets = sorted(
        name for name in referenced_secrets
        if name not in repo_secrets
        and name not in BUILTIN_SECRETS
        and name not in allowed
    )
    missing_vars = sorted(
        name for name in referenced_vars
        if name not in repo_variables and name not in allowed
    )

    if not missing_secrets and not missing_vars:
        bits = []
        if referenced_secrets:
            bits.append(f"{len(referenced_secrets)} secret refs")
        if referenced_vars:
            bits.append(f"{len(referenced_vars)} var refs")
        if allowed:
            bits.append(f"{len(allowed)} bypassed")
        summary = ", ".join(bits) if bits else "no refs"
        return True, (
            f"All workflow secret/var refs resolved ({summary}) "
            f"across {len(files)} changed workflow file(s)."
        )

    lines = ["Missing workflow secret/var references:"]
    for name in missing_secrets:
        where = ", ".join(sorted(referenced_secrets[name]))
        lines.append(f"  - secret '{name}' (in {where})")
    for name in missing_vars:
        where = ", ".join(sorted(referenced_vars[name]))
        lines.append(f"  - var '{name}' (in {where})")
    lines.append(
        "Bypass: add `[secret-ref-allow: <name>]` to the PR title or body "
        "for each legitimate case (e.g., reusable workflow secret defined "
        "elsewhere)."
    )
    return False, "\n".join(lines)


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Pre-merge gate Rule 2: workflow secret-ref drift detection.",
    )
    src = parser.add_mutually_exclusive_group(required=True)
    src.add_argument("--pr", type=int, help="PR number (uses gh CLI for everything)")
    src.add_argument(
        "--files",
        nargs="*",
        help="Explicit workflow file paths (for testing); pair with --title/--body",
    )
    parser.add_argument("--title", default="", help="PR title (with --files)")
    parser.add_argument("--body", default="", help="PR body (with --files)")
    args = parser.parse_args(argv)

    try:
        if args.pr is not None:
            meta = fetch_pr_metadata(args.pr)
            workflow_files = meta["workflow_files"]
            title = meta["title"]
            body = meta["body"]
        else:
            workflow_files = args.files or []
            title = args.title
            body = args.body

        if not workflow_files:
            print("No workflow files changed — rule not applicable.")
            return 0

        repo_secrets = fetch_repo_secrets()
        repo_variables = fetch_repo_variables()
    except (RuntimeError, json.JSONDecodeError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2

    passed, message = check_workflow_secrets(
        workflow_files, repo_secrets, repo_variables, title, body,
    )
    print(message)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
