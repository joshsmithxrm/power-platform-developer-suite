#!/usr/bin/env python3
"""Co-release guard — flag a Cli-v* release that shipped without an Extension refresh.

The VS Code Extension bundles the CLI at publish time (``extension-publish.yml``
runs ``npm run bundle:cli``), so marketplace users only pick up new CLI behavior
when a *new* ``Extension-v*`` tag is cut. If a ``Cli-v*`` release lands without a
matching ``Extension-v*`` refresh in the same train, marketplace users keep
running whatever CLI the last Extension release bundled — silently stale.

This helper encapsulates the decision so the GitHub Actions workflow
``post-merge-release-check.yml`` can call it and parse its JSON output, and so
unit tests can exercise the logic without touching git, GitHub Actions, or the
``gh`` CLI. It mirrors the shape of ``check_release_cadence.py``.

Comparison model
----------------
Cli and Extension version *numbers* are independent lines (Cli ``1.3.0`` vs
Extension ``1.4.1``), so "is the Extension stale?" cannot be answered by
comparing version numbers. We compare **tag dates** instead: if the newest
Extension tag was created *before* the newest stable Cli tag, the Extension has
not been refreshed to bundle that CLI, and the refresh is missing.

Which tags count
----------------
* Cli: only *stable* tags — anything with a ``-rc.`` or ``-beta.`` prerelease
  suffix is ignored (a prerelease CLI never ships to marketplace users).
* Extension: *all* ``Extension-v*`` tags count. Extension channels are encoded in
  the minor version (odd minor = pre-release, even minor = stable) rather than a
  suffix, and *either* channel bundles a fresh CLI — so we deliberately keep the
  comparison simple and treat any newer Extension tag as a satisfied refresh.

Public API
----------
is_stable_cli_tag(tag)      — False for -rc./-beta. suffixed Cli tags
select_latest_stable_cli(tags) — newest stable Cli (name, date) or None
select_latest_extension(tags)  — newest Extension (name, date) or None
evaluate_corelease(...)     — pure decision function, returns a decision dict
build_flag_message(...)     — the exact one-line flag string (also the issue title)
build_issue_body(...)       — issue body Markdown
main(argv=None)             — argparse entry point; reads git, prints JSON to stdout

Exit codes
----------
Always 0 — the caller reads the JSON ``should_open_issue`` field to decide
whether to file an issue, matching ``check_release_cadence.py``.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime
from typing import Optional


# ---------------------------------------------------------------------------
# Tag parsing / selection
# ---------------------------------------------------------------------------

# A (tag_name, tag_date) pair. Dates come from git ``creatordate`` and let us
# answer "which release is newer?" across the two independent version lines.
TagWithDate = tuple[str, datetime]


def is_stable_cli_tag(tag: str) -> bool:
    """Return True unless *tag* carries an -rc./-beta. prerelease suffix.

    Mirrors the stable/prerelease distinction the rest of the release tooling
    uses: ``Cli-v1.3.0`` is stable; ``Cli-v1.4.0-rc.1`` and ``Cli-v1.4.0-beta.2``
    are not.
    """
    return "-rc." not in tag and "-beta." not in tag


def _version_key(tag: str) -> tuple[int, ...]:
    """Sort key from the numeric ``X.Y.Z`` core of a ``<Prefix>-vX.Y.Z`` tag.

    The prerelease suffix (everything after the first ``-`` in the version) is
    dropped for sorting; only stable tags are ranked against each other here.
    Unparseable tags sort lowest so a malformed tag never wins selection.
    """
    _, _, version = tag.partition("-v")
    core = version.split("-", 1)[0]  # strip any -rc./-beta. suffix
    parts: list[int] = []
    for piece in core.split("."):
        if not piece.isdigit():
            return (-1,)
        parts.append(int(piece))
    return tuple(parts) if parts else (-1,)


def select_latest_stable_cli(tags: list[TagWithDate]) -> Optional[TagWithDate]:
    """Return the highest-versioned *stable* Cli tag as (name, date), or None.

    Prerelease Cli tags are excluded before ranking, so a newer ``-rc.`` tag can
    never mask a missing refresh for the last stable release.
    """
    stable = [t for t in tags if is_stable_cli_tag(t[0])]
    if not stable:
        return None
    return max(stable, key=lambda t: _version_key(t[0]))


def select_latest_extension(tags: list[TagWithDate]) -> Optional[TagWithDate]:
    """Return the highest-versioned Extension tag as (name, date), or None.

    All Extension tags count regardless of channel (odd/even minor); either
    channel bundles a fresh CLI.
    """
    if not tags:
        return None
    return max(tags, key=lambda t: _version_key(t[0]))


# ---------------------------------------------------------------------------
# Core decision logic
# ---------------------------------------------------------------------------

def evaluate_corelease(
    *,
    cli: Optional[TagWithDate],
    extension: Optional[TagWithDate],
    has_open_issue: bool,
) -> dict:
    """Decide whether an "Extension refresh missing" issue should be opened.

    Parameters
    ----------
    cli:
        The latest *stable* Cli tag as ``(name, date)``, or None if none exist.
    extension:
        The latest Extension tag as ``(name, date)``, or None if none exist.
    has_open_issue:
        True if an issue with the ``release:extension-corelease`` label is
        already open — prevents duplicates on every run (idempotency).

    Returns
    -------
    dict with keys:
        should_open_issue  bool
        reason             str  ("no stable cli tag" | "duplicate" |
                                 "no extension tag" | "extension current" |
                                 "extension stale")
        cli_tag            str | None
        extension_tag      str | None
    """
    cli_tag = cli[0] if cli else None
    ext_tag = extension[0] if extension else None

    if cli is None:
        # Nothing has shipped a stable CLI yet — nothing to guard.
        return {
            "should_open_issue": False,
            "reason": "no stable cli tag",
            "cli_tag": None,
            "extension_tag": ext_tag,
        }

    if has_open_issue:
        return {
            "should_open_issue": False,
            "reason": "duplicate",
            "cli_tag": cli_tag,
            "extension_tag": ext_tag,
        }

    if extension is None:
        # A stable CLI exists but the Extension has never been tagged — the
        # bundled CLI can only be stale. Flag it.
        return {
            "should_open_issue": True,
            "reason": "no extension tag",
            "cli_tag": cli_tag,
            "extension_tag": None,
        }

    # The refresh is satisfied when the newest Extension tag is at least as new
    # as the newest stable Cli tag. ``>=`` keeps a same-train Extension release
    # (tagged moments after the CLI) silent.
    if extension[1] >= cli[1]:
        return {
            "should_open_issue": False,
            "reason": "extension current",
            "cli_tag": cli_tag,
            "extension_tag": ext_tag,
        }

    return {
        "should_open_issue": True,
        "reason": "extension stale",
        "cli_tag": cli_tag,
        "extension_tag": ext_tag,
    }


# ---------------------------------------------------------------------------
# Issue formatting helpers
# ---------------------------------------------------------------------------

def build_flag_message(cli_tag: str, extension_tag: Optional[str]) -> str:
    """Return the exact one-line flag string (also used as the issue title)."""
    ext_display = extension_tag if extension_tag else "(none)"
    return (
        f"Extension bundled-CLI refresh missing for {cli_tag} "
        f"(latest {ext_display} predates it)"
    )


def build_issue_body(cli_tag: str, extension_tag: Optional[str]) -> str:
    """Return the GitHub issue body (Markdown) for a missing-refresh issue."""
    ext_display = f"`{extension_tag}`" if extension_tag else "_none tagged yet_"
    return f"""\
## Extension Bundled-CLI Refresh Missing

A `{cli_tag}` release has shipped, but the latest `Extension-v*` tag predates it. \
The VS Code Extension bundles the CLI at publish time (`extension-publish.yml` → \
`npm run bundle:cli`), so **marketplace users keep running the CLI bundled by the \
last Extension release** until an `Extension-v*` refresh is cut.

| Field | Value |
|-------|-------|
| Latest stable Cli tag | `{cli_tag}` |
| Latest Extension tag | {ext_display} |

**Default rule:** a `Cli-v*` release includes an `Extension-v*` bundled-CLI \
refresh in the same train — opt-out only with a recorded reason.

## Options for the Maintainer

- **Cut the refresh** — run `/release` for an `Extension-v*` bundled-CLI refresh so \
  marketplace users pick up `{cli_tag}`
- **Opt out with a reason** — comment the reason on this issue and close it (e.g. \
  the CLI change does not affect bundled behavior)

## Checklist

- [ ] Cut an `Extension-v*` refresh bundling `{cli_tag}`, **or** record an opt-out reason
- [ ] Close this issue once actioned

> **Procedure:** Follow the co-release rule in the [`/release` skill](.claude/skills/release/SKILL.md).
"""


# ---------------------------------------------------------------------------
# git helpers (not exercised by unit tests — the pure functions above are)
# ---------------------------------------------------------------------------

def collect_tags(prefix: str) -> list[TagWithDate]:
    """Collect ``(tag_name, creatordate)`` pairs for tags matching ``<prefix>-v*``.

    Uses ``git for-each-ref`` so the creator date is available for the newness
    comparison. Tags with an unparseable date are skipped.
    """
    result = subprocess.run(
        [
            "git",
            "for-each-ref",
            "--format=%(refname:short)%09%(creatordate:iso-strict)",
            f"refs/tags/{prefix}-v*",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        # A git failure must not masquerade as "no tags" (which would silently
        # suppress the guard). Surface it on stderr and raise so the CI step
        # fails loudly rather than filing/skipping an issue on bad data.
        raise RuntimeError(
            f"git for-each-ref for {prefix}-v* failed "
            f"(exit {result.returncode}): {result.stderr.strip()}"
        )
    tags: list[TagWithDate] = []
    for line in result.stdout.splitlines():
        line = line.strip()
        if not line or "\t" not in line:
            continue
        name, _, date_str = line.partition("\t")
        try:
            when = datetime.fromisoformat(date_str.strip())
        except ValueError:
            continue
        tags.append((name.strip(), when))
    return tags


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Evaluate whether a Cli-v* release shipped without an Extension "
            "bundled-CLI refresh. Reads git tags; outputs a JSON object to "
            "stdout; always exits 0."
        ),
    )
    parser.add_argument(
        "--has-open-issue",
        action="store_true",
        default=False,
        help="Pass this flag if an open release:extension-corelease issue exists.",
    )
    parser.add_argument(
        "--format",
        choices=["json", "title", "body"],
        default="json",
        help=(
            "Output format. 'json' (default) emits the evaluate_corelease result. "
            "'title' emits the flag message. 'body' emits the issue body markdown."
        ),
    )
    args = parser.parse_args(argv)

    cli = select_latest_stable_cli(collect_tags("Cli"))
    extension = select_latest_extension(collect_tags("Extension"))

    result = evaluate_corelease(
        cli=cli,
        extension=extension,
        has_open_issue=args.has_open_issue,
    )

    if args.format in ("title", "body"):
        # title/body are only meaningful when a flag is warranted; the workflow
        # gates these on should_open_issue == true. Guard the standalone case
        # (no stable Cli tag) so we never emit a title/body naming a None tag.
        if result["cli_tag"] is None:
            print(
                "no stable Cli-v* tag; nothing to flag "
                f"(reason: {result['reason']})",
                file=sys.stderr,
            )
            return 0
        if args.format == "title":
            print(build_flag_message(result["cli_tag"], result["extension_tag"]))
        else:
            print(build_issue_body(result["cli_tag"], result["extension_tag"]))
    else:
        print(json.dumps(result))

    return 0


if __name__ == "__main__":
    sys.exit(main())
