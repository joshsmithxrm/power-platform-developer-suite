"""
AC-44: Guard test ensuring no non-deprecation in-repo references to legacy metadata nouns
(table, column, choice) remain in skills, scripts, docs, tests, and changelog.

Legacy nouns are only acceptable in:
- Files that explicitly define the deprecation shim (Table/ColumnCommandGroup, ChoiceCommandGroup)
- The deprecation test file itself
- CHANGELOG entries describing the deprecation
- This test file
"""
import re
import os
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).parent.parent

# Files/dirs to scan
SCAN_DIRS = [
    REPO_ROOT / ".claude" / "skills",
    REPO_ROOT / "scripts",
    REPO_ROOT / "docs",
    REPO_ROOT / "tests",
    REPO_ROOT / "src" / "PPDS.Cli" / "CHANGELOG.md",
]

# Patterns that indicate a legacy-noun *command invocation* (not just prose/docs references)
# These are CLI command strings like "ppds metadata table", "ppds metadata column", "ppds metadata choice"
LEGACY_COMMAND_PATTERNS = [
    re.compile(r'ppds\s+metadata\s+table\b', re.IGNORECASE),
    re.compile(r'ppds\s+metadata\s+column\b', re.IGNORECASE),
    re.compile(r'ppds\s+metadata\s+choice\b', re.IGNORECASE),
]

# Files that are allowed to reference legacy nouns (deprecation shims + this test)
ALLOWED_FILES = {
    "TableCommandGroup.cs",
    "ColumnCommandGroup.cs",
    "ChoiceCommandGroup.cs",
    "MetadataTableCommandTests.cs",
    "MetadataColumnCommandTests.cs",
    "MetadataChoiceCommandTests.cs",
    "MetadataDeprecationTests.cs",
    "test_metadata_canonical_nouns.py",
    "CHANGELOG.md",
    # Historical QA records preserve the commands that were tested at the time
    "shakedown-v1-2026-04-20.md",
}


def scan_file(path: Path) -> list[tuple[int, str, str]]:
    """Return list of (line_number, matched_pattern, line) for violations."""
    violations = []
    if path.name in ALLOWED_FILES:
        return violations

    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except Exception:
        return violations

    for lineno, line in enumerate(text.splitlines(), 1):
        for pattern in LEGACY_COMMAND_PATTERNS:
            if pattern.search(line):
                violations.append((lineno, pattern.pattern, line.strip()))
                break  # one violation per line is enough

    return violations


def collect_files():
    """Yield all files to scan."""
    for entry in SCAN_DIRS:
        p = Path(entry)
        if not p.exists():
            continue
        if p.is_file():
            yield p
        else:
            for ext in (".cs", ".py", ".ts", ".js", ".md", ".sh", ".ps1", ".yaml", ".yml"):
                yield from p.rglob(f"*{ext}")


def test_no_legacy_noun_commands():
    all_violations = []

    for file_path in collect_files():
        violations = scan_file(file_path)
        for lineno, pattern, line in violations:
            rel = file_path.relative_to(REPO_ROOT)
            all_violations.append(f"{rel}:{lineno}: [{pattern}] {line}")

    if all_violations:
        msg = (
            "Found references to deprecated metadata nouns (table/column/choice) "
            "outside of allowed deprecation-shim files.\n"
            "Update these to use canonical nouns: entity, attribute, optionset.\n\n"
        ) + "\n".join(all_violations)
        assert False, msg


if __name__ == "__main__":
    test_no_legacy_noun_commands()
    print("All checks passed — no legacy metadata noun commands found outside of shim files.")
