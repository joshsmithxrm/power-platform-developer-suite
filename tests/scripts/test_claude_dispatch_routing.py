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
    # The dispatcher itself.
    "scripts/claude_dispatch.py",
    # The /start foreground helper builds its own claude --bg argv
    # locally (no --dangerously-skip-permissions, foreground UX).
    "scripts/start-bg-spawn.py",
    # Test files that assert on the literal argv shape produced by spawn().
    "tests/scripts/test_claude_dispatch_routing.py",
    "tests/scripts/test_claude_dispatch.py",
    "tests/scripts/test_start_bg_spawn.py",
    "tests/test_pipeline.py",
    # Specs and plans that describe the dispatcher contract.
    "specs/dispatch-routing.md",
    "specs/workflow-enforcement.md",
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


# === Phase 5C (AC-25) ===

_AC25_REQUIRED_HEADINGS = (
    "### When interactive",
    "### When headless",
    "### Overrides",
    "### Spend journal",
)


def test_workflow_md_has_dispatch_section_headings():
    """AC-25: .claude/WORKFLOW.md has the four dispatch-routing subsection headings."""
    workflow_path = _REPO_ROOT / ".claude" / "WORKFLOW.md"
    assert workflow_path.exists(), ".claude/WORKFLOW.md must exist"

    lines = workflow_path.read_text(encoding="utf-8").splitlines()
    missing = [h for h in _AC25_REQUIRED_HEADINGS if h not in lines]
    assert not missing, (
        "AC-25: required Dispatch routing subsection heading(s) missing from "
        f".claude/WORKFLOW.md: {missing}"
    )


# === end Phase 5C ===


# === Phase 5D (AC-26a, AC-26b) ===

_AC26_NEEDLE_REFERENCE = "claude_dispatch.py:spawn()"
_AC26_NEEDLE_MARKER = "<!-- enforcement: T2 hook:sdk-spend-warn -->"
_AC26_LINE_CAP = 100


def test_claudemd_never_line_present():
    """AC-26a: CLAUDE.md contains the dispatcher NEVER line with the T2 marker."""
    claudemd_path = _REPO_ROOT / "CLAUDE.md"
    assert claudemd_path.exists(), "CLAUDE.md must exist at repo root"

    lines = claudemd_path.read_text(encoding="utf-8").splitlines()
    matches = [
        line
        for line in lines
        if _AC26_NEEDLE_REFERENCE in line and _AC26_NEEDLE_MARKER in line
    ]
    assert matches, (
        "AC-26a: CLAUDE.md must contain one line referencing "
        f"{_AC26_NEEDLE_REFERENCE!r} together with {_AC26_NEEDLE_MARKER!r}; "
        "none found."
    )


def test_claudemd_line_cap():
    """AC-26b: CLAUDE.md remains at or under the 100-line cap."""
    claudemd_path = _REPO_ROOT / "CLAUDE.md"
    line_count = len(claudemd_path.read_text(encoding="utf-8").splitlines())
    assert line_count <= _AC26_LINE_CAP, (
        f"AC-26b: CLAUDE.md at {line_count} lines exceeds cap of {_AC26_LINE_CAP}."
    )


# === end Phase 5D ===


# === Phase 2E (AC-17 cross-cutting smoke test) ===

def test_dangerous_flag_unattended_only():
    """AC-17: All pipeline.py and pr_monitor.py spawn() invocations include
    dangerous=True (unattended daemons); start-bg-spawn.py does NOT (foreground
    /start helper has a human attached).

    Token-scan (not AST) because the dispatcher's spawn() is the only entry
    point and both pipeline/pr_monitor call it with kwargs; a literal
    'dangerous=True' must appear in each spawn(...) call.
    """
    import re as _re
    # 1) pipeline.py must contain dangerous=True somewhere inside its spawn() call.
    pipeline_src = (_REPO_ROOT / "scripts" / "pipeline.py").read_text(encoding="utf-8")
    assert _re.search(r"claude_dispatch\.spawn\(.*?dangerous=True", pipeline_src, _re.S), \
        "AC-17: pipeline.run_claude must pass dangerous=True to claude_dispatch.spawn"

    # 2) pr_monitor.py must contain dangerous=True in both run_triage and run_retro spawns.
    pr_src = (_REPO_ROOT / "scripts" / "pr_monitor.py").read_text(encoding="utf-8")
    spawn_calls = _balanced_call_bodies(pr_src, "claude_dispatch.spawn(")
    assert len(spawn_calls) >= 2, \
        "AC-17: pr_monitor must have at least 2 claude_dispatch.spawn() calls (triage + retro)"
    for call_body in spawn_calls:
        assert "dangerous=True" in call_body, \
            "AC-17: every pr_monitor.spawn() invocation must include dangerous=True"

    # 3) start-bg-spawn.py must NOT import or use claude_dispatch.spawn or pass
    # --dangerously-skip-permissions. It builds its own --bg argv locally for
    # the foreground UX.
    start_src = (_REPO_ROOT / "scripts" / "start-bg-spawn.py").read_text(encoding="utf-8")
    assert "claude_dispatch.spawn" not in start_src, \
        "AC-17: start-bg-spawn.py must NOT call claude_dispatch.spawn " \
        "(foreground UX, no unattended-daemon flag)"
    assert "--dangerously-skip-permissions" not in start_src, \
        "AC-17: start-bg-spawn.py must NOT pass --dangerously-skip-permissions " \
        "(human-attached foreground session)"


# === end Phase 2E ===



def _balanced_call_bodies(src, prefix):
    """Extract the body of every f(...) call matching prefix."""
    bodies = []
    i = 0
    while True:
        idx = src.find(prefix, i)
        if idx < 0:
            return bodies
        start = idx + len(prefix)
        depth = 1
        j = start
        while j < len(src) and depth > 0:
            c = src[j]
            if c == '(':
                depth += 1
            elif c == ')':
                depth -= 1
                if depth == 0:
                    bodies.append(src[start:j])
                    break
            j += 1
        i = j + 1
