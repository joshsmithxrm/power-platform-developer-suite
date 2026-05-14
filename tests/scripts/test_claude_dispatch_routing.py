"""CI regression guards for dispatch routing.

See specs/dispatch-routing.md ACs 17 (Phase 2E), 21-22 (Phase 4),
23 (Phase 5A). Each AC's test is in its own clearly-delimited section
so concurrent agents do not collide.
"""

# === Phase 4 (AC-21, AC-22) ===
from __future__ import annotations

import json
import re
from pathlib import Path

# Repo root is three levels above this file: tests/scripts/<this>.py -> repo root
_REPO_ROOT = Path(__file__).resolve().parents[2]

# Directory segments that should be skipped entirely during the walk.
_EXCLUDED_DIR_SEGMENTS = {
    ".git",
    "node_modules",
    "bin",
    "obj",
    ".worktrees",
    "__pycache__",
    "dist",
    "out",
}

# Multi-segment excludes (matched against the relative path's posix string).
_EXCLUDED_PATH_PREFIXES = (
    ".claude/jobs",
    ".claude/projects",
    ".claude/state",
)

_MAX_FILE_BYTES = 1 * 1024 * 1024  # 1 MB
_BINARY_SNIFF_BYTES = 8 * 1024  # 8 KB


def _rel_posix(path: Path) -> str:
    return path.relative_to(_REPO_ROOT).as_posix()


def _is_excluded_dir(rel_posix: str) -> bool:
    parts = rel_posix.split("/")
    if any(seg in _EXCLUDED_DIR_SEGMENTS for seg in parts):
        return True
    for prefix in _EXCLUDED_PATH_PREFIXES:
        if rel_posix == prefix or rel_posix.startswith(prefix + "/"):
            return True
    return False


def _iter_repo_files():
    """Yield (path, rel_posix) for every non-excluded file under the repo root."""
    for path in _REPO_ROOT.rglob("*"):
        if not path.is_file():
            continue
        try:
            rel = _rel_posix(path)
        except ValueError:
            continue
        if _is_excluded_dir(rel):
            continue
        yield path, rel


def _read_text_bytes(path: Path) -> bytes | None:
    """Return file bytes, or None if the file is too large or looks binary."""
    try:
        size = path.stat().st_size
    except OSError:
        return None
    if size > _MAX_FILE_BYTES:
        return None
    try:
        with path.open("rb") as fh:
            head = fh.read(_BINARY_SNIFF_BYTES)
            if b"\x00" in head:
                return None
            rest = fh.read()
    except OSError:
        return None
    return head + rest


# ---- AC-21 ----------------------------------------------------------------

_AC21_ALLOWLIST = {
    "scripts/claude_dispatch.py",
    "tests/scripts/test_claude_dispatch_routing.py",
    "specs/dispatch-routing.md",
    ".plans/2026-05-14-dispatch-routing.md",
}

_AC21_NEEDLES = (b'"claude", "-p"', b'"claude", "--bg"')


def test_no_claude_p_outside_dispatcher():
    """AC-21: literal argv fragments must only appear in the dispatcher + allowlist."""
    violations: list[str] = []
    for path, rel in _iter_repo_files():
        if rel in _AC21_ALLOWLIST:
            continue
        content = _read_text_bytes(path)
        if content is None:
            continue
        if any(needle in content for needle in _AC21_NEEDLES):
            violations.append(rel)

    assert not violations, (
        "AC-21: literal claude -p / claude --bg argv fragments found outside "
        "the dispatcher allowlist:\n  " + "\n  ".join(sorted(violations))
    )


# ---- AC-22 ----------------------------------------------------------------

_AC22_ALLOWLIST = {
    "tests/scripts/test_claude_dispatch_routing.py",
    ".plans/2026-05-14-dispatch-routing.md",
    "specs/dispatch-routing.md",
}

_PY_IMPORT_RE = re.compile(
    r"^\s*(import|from)\s+(claude_agent_sdk|anthropic)\b",
    re.M,
)
_JS_IMPORT_RE = re.compile(
    r"""(import|require)\s*\(?['"](anthropic|@anthropic-ai/claude-agent-sdk)\b""",
    re.M,
)
_REQTXT_RE = re.compile(r"^(anthropic|claude_agent_sdk)\s*[=<>~!]", re.M)

_DISALLOWED_PKG_NAMES = {"anthropic", "@anthropic-ai/claude-agent-sdk"}

_PY_SUFFIXES = {".py"}
_JS_SUFFIXES = {".ts", ".tsx", ".js", ".mjs"}


def _check_package_json(path: Path, content: bytes) -> list[str]:
    """Return a list of offending dependency keys found in a package*.json file."""
    try:
        data = json.loads(content.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return []
    if not isinstance(data, dict):
        return []
    hits: list[str] = []
    for section in ("dependencies", "devDependencies", "peerDependencies"):
        deps = data.get(section)
        if not isinstance(deps, dict):
            continue
        for key in deps:
            if key in _DISALLOWED_PKG_NAMES:
                hits.append(f"{section}.{key}")
    return hits


def test_no_sdk_dependencies():
    """AC-22: no SDK imports or dependency entries anywhere in the repo."""
    violations: list[str] = []

    for path, rel in _iter_repo_files():
        if rel in _AC22_ALLOWLIST:
            continue

        name = path.name
        suffix = path.suffix
        is_py = suffix in _PY_SUFFIXES
        is_js = suffix in _JS_SUFFIXES
        is_pkg_json = name == "package.json" or (
            name.startswith("package-") and name.endswith(".json")
        )
        is_reqs_txt = name.startswith("requirements") and name.endswith(".txt")

        if not (is_py or is_js or is_pkg_json or is_reqs_txt):
            continue

        content = _read_text_bytes(path)
        if content is None:
            continue

        if is_pkg_json:
            hits = _check_package_json(path, content)
            for h in hits:
                violations.append(f"{rel}: {h}")
            continue

        try:
            text = content.decode("utf-8")
        except UnicodeDecodeError:
            continue

        if is_py:
            m = _PY_IMPORT_RE.search(text)
            if m:
                violations.append(f"{rel}: {m.group(0).strip()}")
        elif is_js:
            m = _JS_IMPORT_RE.search(text)
            if m:
                violations.append(f"{rel}: {m.group(0).strip()}")
        elif is_reqs_txt:
            m = _REQTXT_RE.search(text)
            if m:
                violations.append(f"{rel}: {m.group(0).strip()}")

    assert not violations, (
        "AC-22: SDK imports / dependencies found:\n  "
        + "\n  ".join(sorted(violations))
    )


# === end Phase 4 ===


# === Phase 5A (AC-23) ===

_AC23_ALLOWLIST = {
    "tests/scripts/test_claude_dispatch_routing.py",
    "specs/dispatch-routing.md",
    ".plans/2026-05-14-dispatch-routing.md",
}

_AC23_NEEDLE = b"CLAUDE_CODE_OAUTH_TOKEN"


def test_no_github_action_or_token_references():
    """AC-23: claude.yml is deleted and no file references the secret."""
    claude_yml = _REPO_ROOT / ".github" / "workflows" / "claude.yml"
    assert not claude_yml.exists(), (
        "AC-23: .github/workflows/claude.yml must be deleted; found it on disk."
    )

    violations: list[str] = []
    for path, rel in _iter_repo_files():
        if rel in _AC23_ALLOWLIST:
            continue
        content = _read_text_bytes(path)
        if content is None:
            continue
        if _AC23_NEEDLE in content:
            violations.append(rel)

    assert not violations, (
        "AC-23: CLAUDE_CODE_OAUTH_TOKEN references found outside allowlist:\n  "
        + "\n  ".join(sorted(violations))
    )


# === end Phase 5A ===


# === Phase 5B (AC-24) ===

_AC24_FORBIDDEN_HEADINGS = (
    "### `type:`",
    "### `area:`",
    "### `epic:`",
    "### `priority:`",
    "### `status:`",
)


def test_backlog_tables_removed():
    """AC-24: docs/BACKLOG.md no longer contains the six label-reference tables."""
    backlog_path = _REPO_ROOT / "docs" / "BACKLOG.md"
    assert backlog_path.exists(), "docs/BACKLOG.md must exist"

    text = backlog_path.read_text(encoding="utf-8")
    lines = text.splitlines()

    # Forbidden literal headings: must not appear anywhere.
    for heading in _AC24_FORBIDDEN_HEADINGS:
        offenders = [i for i, line in enumerate(lines) if line.strip() == heading]
        assert not offenders, (
            f"AC-24: forbidden heading {heading!r} still present at line(s) "
            f"{[i + 1 for i in offenders]}"
        )

    # `### Other` is only forbidden inside the Labels section (between
    # `## Labels` heading and the next `## ` heading).
    labels_start = None
    for i, line in enumerate(lines):
        if line.strip() == "## Labels":
            labels_start = i
            break
    if labels_start is not None:
        labels_end = len(lines)
        for j in range(labels_start + 1, len(lines)):
            if lines[j].startswith("## ") and not lines[j].startswith("## Labels"):
                labels_end = j
                break
        for k in range(labels_start, labels_end):
            assert lines[k].strip() != "### Other", (
                f"AC-24: `### Other` heading still present inside Labels "
                f"section at line {k + 1}"
            )

    # The prose replacement must reference `gh label list`.
    assert "gh label list" in text, (
        "AC-24: docs/BACKLOG.md must reference `gh label list` as the canonical "
        "label inspection command."
    )


# === end Phase 5B ===
