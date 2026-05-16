r"""Regression test for #1130 and #1131 - Bash-tool portability.

Scans every .claude/skills/**/*.md for two patterns that fail at runtime
when Claude generates them via the Bash tool on Windows:

R-01 (#1130): PowerShell cmdlets (Test-Path, etc.) inside bash code
fences. The Bash tool's shell is Git Bash / MSYS; PowerShell cmdlets exit
127. Use POSIX ([ -e ... ], ls) or python -c "import os; ...".

R-02 (#1131): Windows backslash paths inside inline Python literals
(python -c "..."). The Bash tool passes the payload as Python source, so
'\u' and '\w' become escape sequences that produce SyntaxError or wrong
paths. Use forward slashes or raw strings (r'...').

Canonical guidance: .claude/WORKFLOW.md "Bash Tool Portability".
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SKILLS_DIR = REPO_ROOT / ".claude" / "skills"

# PowerShell cmdlets that the Bash tool's shell will not resolve.
_POWERSHELL_CMDLETS = (
    "Test-Path",
    "Get-ChildItem",
    "Remove-Item",
    "New-Item",
    "Copy-Item",
    "Move-Item",
    "Get-Content",
    "Set-Content",
)

# Stage 1: line invokes inline python (-c). Stage 2: line contains a
# non-raw string literal (opening quote NOT preceded by `r`) holding a
# backslash followed by a letter -- the pattern that becomes an unintended
# Python escape sequence (`\u`, `\w`, `\s`, etc.).
_INLINE_PY_INVOKE_RE = re.compile(r"python\s+-c\s+[\"']")
_NONRAW_BACKSLASH_LITERAL_RE = re.compile(
    r"(?<![rRbB])(['\"])"   # opening quote not prefixed with r/R/b/B
    r"[^'\"]*?"               # any chars except matching quotes
    r"\\[A-Za-z]"            # backslash + letter -- Python escape
    r"[^'\"]*?\1"             # to matching closing quote
)


def _has_inline_py_backslash_path(line: str) -> bool:
    if not _INLINE_PY_INVOKE_RE.search(line):
        return False
    return bool(_NONRAW_BACKSLASH_LITERAL_RE.search(line))


def _iter_skill_md_files():
    return sorted(SKILLS_DIR.rglob("*.md"))


def _code_fence_lines(text: str) -> list[tuple[int, str]]:
    """Return (line_number, line) pairs INSIDE fenced code blocks only.

    Markdown prose discussing forbidden patterns is fine; the runtime
    hazard is code blocks Claude copies and executes.
    """
    inside = False
    out: list[tuple[int, str]] = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        if line.lstrip().startswith("```"):
            inside = not inside
            continue
        if inside:
            out.append((lineno, line))
    return out


def test_no_powershell_cmdlets_in_skill_code_fences():
    """R-01 (#1130) regression - no PowerShell cmdlets inside skill code fences."""
    offenses: list[str] = []
    for path in _iter_skill_md_files():
        text = path.read_text(encoding="utf-8")
        for lineno, line in _code_fence_lines(text):
            for cmdlet in _POWERSHELL_CMDLETS:
                if re.search(rf"\b{re.escape(cmdlet)}\b", line):
                    offenses.append(
                        f"{path.relative_to(REPO_ROOT)}:{lineno}: {cmdlet} in code "
                        f"fence - use POSIX ([ -e ... ], ls) or python -c"
                    )
    assert not offenses, (
        "PowerShell cmdlets found in skill code fences (see "
        ".claude/WORKFLOW.md 'Bash Tool Portability'):\n  "
        + "\n  ".join(offenses)
    )


def test_no_windows_backslash_paths_in_inline_python():
    """R-02 (#1131) regression - no backslash paths in inline python -c strings."""
    offenses: list[str] = []
    for path in _iter_skill_md_files():
        text = path.read_text(encoding="utf-8")
        for lineno, line in _code_fence_lines(text):
            if _has_inline_py_backslash_path(line):
                offenses.append(
                    f"{path.relative_to(REPO_ROOT)}:{lineno}: backslash path in "
                    f"inline python -c - use forward slashes or r'...' raw string"
                )
    assert not offenses, (
        "Windows backslash paths in inline python -c literals (see "
        ".claude/WORKFLOW.md 'Bash Tool Portability'):\n  "
        + "\n  ".join(offenses)
    )


def test_detectors_catch_known_bad_samples():
    """Self-test: confirm the regex catches the patterns it claims to catch."""
    bad_powershell = [
        "Test-Path .workflow/state.json",
        "if (Test-Path .workflow) { ... }",
        "Get-ChildItem -Recurse .retros",
    ]
    for sample in bad_powershell:
        assert any(
            re.search(rf"\b{re.escape(c)}\b", sample) for c in _POWERSHELL_CMDLETS
        ), f"PowerShell detector missed: {sample!r}"

    bad_inline_py = [
        r'''python -c "open('.workflow\state.json')"''',
        r'''python -c "import os; print(os.path.exists('C:\Users\foo'))"''',
        r"""python -c 'open(".workflow\state.json")'""",
    ]
    for sample in bad_inline_py:
        assert _has_inline_py_backslash_path(sample), (
            f"Backslash-path detector missed: {sample!r}"
        )

    good_inline_py = [
        r'''python -c "open('.workflow/state.json')"''',
        r'''python -c "open(r'C:\Users\foo')"''',
        r'''python -c "import json; json.load(open('.claude/settings.json'))"''',
    ]
    for sample in good_inline_py:
        assert not _has_inline_py_backslash_path(sample), (
            f"Backslash-path detector false-positived: {sample!r}"
        )


def test_workflow_md_documents_portability_rules():
    """WORKFLOW.md must document the portability rules the tests enforce."""
    workflow = (REPO_ROOT / ".claude" / "WORKFLOW.md").read_text(encoding="utf-8")
    assert "Bash Tool Portability" in workflow, (
        ".claude/WORKFLOW.md must contain 'Bash Tool Portability' section"
    )
    assert "Test-Path" in workflow, (
        "Portability section must call out Test-Path as a forbidden cmdlet"
    )
    assert "raw string" in workflow.lower() or "r'" in workflow, (
        "Portability section must document raw-string fix for Windows paths"
    )
