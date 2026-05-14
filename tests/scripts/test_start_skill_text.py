"""Structural tests for .claude/skills/start/SKILL.md — AC-09, AC-10."""
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SKILL = (REPO_ROOT / ".claude" / "skills" / "start" / "SKILL.md").read_text(encoding="utf-8")


def _section(heading: str) -> str:
    """Return text from `### {heading}` to the next equal-or-greater rank.

    SKILL.md uses H3 for top-level steps. We stop at the next `### ` or
    `## ` so sub-steps are included in their parent step's section.
    """
    m = re.search(
        rf"(^|\n)### {re.escape(heading)}\b.*?(?=\n### |\n## |\Z)",
        SKILL,
        re.DOTALL,
    )
    assert m, f"section {heading!r} not found in SKILL.md"
    return m.group(0)


def test_step6_invokes_helper():
    """AC-09: Step 6 must contain a bash block invoking start-bg-spawn.py."""
    step6 = _section("Step 6")
    bash_blocks = re.findall(r"```bash\s+(.*?)```", step6, re.DOTALL)
    assert any("python scripts/start-bg-spawn.py" in b for b in bash_blocks), (
        "Step 6 must contain a bash block invoking start-bg-spawn.py"
    )


def test_step7_names_agent_view():
    """AC-09: Step 7 summary must reference Agent View."""
    step7 = _section("Step 7")
    assert "Agent View" in step7, "Step 7 summary must reference Agent View"


# AC-10 test (also in this file per plan)
SCAN_DIRS = [".claude", "scripts", "tests", "specs"]
EXCLUDED_FILES = {
    "specs/start-launch.md",
    ".plans/2026-05-13-start-launch.md",
    # This enforcer file itself contains the needle as a string literal.
    "tests/scripts/test_start_skill_text.py",
    # AC-156 row is a historical record (obsoleted by #1032); the script is deleted.
    "specs/workflow-enforcement.md",
}
NEEDLE = "launch-claude-session"


def test_launcher_purged():
    """AC-10: launch-claude-session.py must not exist; no live references remain."""
    assert not (REPO_ROOT / "scripts" / "launch-claude-session.py").exists(), (
        "launch-claude-session.py must be deleted"
    )
    offenders = []
    for d in SCAN_DIRS:
        scan_path = REPO_ROOT / d
        if not scan_path.exists():
            continue
        for path in scan_path.rglob("*"):
            if not path.is_file():
                continue
            if "__pycache__" in path.parts:
                continue
            rel = path.relative_to(REPO_ROOT).as_posix()
            if rel in EXCLUDED_FILES:
                continue
            try:
                text = path.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            if NEEDLE in text:
                offenders.append(rel)
    assert not offenders, (
        f"`{NEEDLE}` still referenced in: {offenders}. "
        "Remove or add to EXCLUDED_FILES with rationale."
    )
