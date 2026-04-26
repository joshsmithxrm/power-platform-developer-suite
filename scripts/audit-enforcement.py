#!/usr/bin/env python3
"""Audit enforcement harness for workflow-enforcement v9.0.

Three modes:

  --strict     Exit 0 if every `<!-- enforcement: T1 hook:<name> ... -->`
               marker references a hook file that exists on disk AND is
               wired in `.claude/settings.json`. Exit 1 (with reasons on
               stderr) on any missing reference.

  --discover   Scan all SKILL.md + CLAUDE.md for MANDATORY/MUST/NEVER/
               ALWAYS/DO NOT/FORBIDDEN directives that lack an
               `enforcement:` marker. Print findings to stdout. Exit 0
               if zero unmarked, 1 otherwise.

  --report     Generate `.workflow/audit-snapshot.md` with:
                 - total directive count + date
                 - T1 / T2 / T3 breakdown with percentages
                 - per-skill marker counts
                 - hook coverage percentage
                 - per-file unmarked count
                 - top 5 longest SKILL.md files

ACs: 165, 166, 167, 168, 172, 177.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
from collections import Counter
from datetime import date
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SKILL_GLOB = ".claude/skills/*/SKILL.md"
CLAUDE_MD = "CLAUDE.md"
SETTINGS_PATH = ".claude/settings.json"
SNAPSHOT_PATH = ".workflow/audit-snapshot.md"

# T1 marker syntax: <!-- enforcement: T1 hook:<name> [extra] -->
T1_PATTERN = re.compile(
    r"<!--\s*enforcement:\s*T1\s+hook:([A-Za-z0-9_-]+)"
)
TIER_PATTERN = re.compile(r"<!--\s*enforcement:\s*(T[123])")
DIRECTIVE_KEYWORDS = re.compile(
    r"\b(MANDATORY|MUST|NEVER|ALWAYS|DO NOT|FORBIDDEN)\b"
)


def _audited_files(repo_root: Path) -> list[Path]:
    paths = sorted(repo_root.glob(SKILL_GLOB))
    claude_md = repo_root / CLAUDE_MD
    if claude_md.exists():
        paths.append(claude_md)
    return paths


def _read(p: Path) -> str:
    try:
        return p.read_text(encoding="utf-8")
    except OSError:
        return ""


def _strip_code_fences(text: str) -> str:
    """Remove fenced code blocks so directive scans don't match prose
    inside ```...``` blocks (those are documentation, not rules)."""
    out = []
    in_fence = False
    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        out.append(line)
    return "\n".join(out)


def _all_t1_hooks(repo_root: Path) -> list[tuple[Path, str, int]]:
    """Return [(file, hook_name, line_number)] for every T1 marker."""
    results = []
    for p in _audited_files(repo_root):
        text = _read(p)
        for lineno, line in enumerate(text.splitlines(), start=1):
            for m in T1_PATTERN.finditer(line):
                results.append((p, m.group(1), lineno))
    return results


def _hook_file_exists(repo_root: Path, hook_name: str) -> bool:
    return (repo_root / ".claude" / "hooks" / f"{hook_name}.py").exists()


def _wired_hook_names(repo_root: Path) -> set[str]:
    """Return the set of hook script basenames referenced from settings.json."""
    settings = repo_root / SETTINGS_PATH
    try:
        cfg = json.loads(settings.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return set()
    names: set[str] = set()
    hooks_section = cfg.get("hooks", {})
    for event_name, entries in hooks_section.items():
        for entry in entries or []:
            for hook in (entry.get("hooks") or []):
                cmd = hook.get("command", "") or ""
                # extract /.claude/hooks/<name>.py
                for m in re.finditer(r"\.claude/hooks/([A-Za-z0-9_-]+)\.py", cmd):
                    names.add(m.group(1))
    return names


def cmd_strict(repo_root: Path) -> int:
    """AC-165, AC-166. Exit 0 if all T1 hooks exist + are wired; 1 otherwise."""
    t1_hooks = _all_t1_hooks(repo_root)
    wired = _wired_hook_names(repo_root)
    missing_files: list[tuple[Path, str, int]] = []
    missing_wiring: list[tuple[Path, str, int]] = []
    for fp, hook, lineno in t1_hooks:
        if not _hook_file_exists(repo_root, hook):
            missing_files.append((fp, hook, lineno))
        elif hook not in wired:
            missing_wiring.append((fp, hook, lineno))

    if not missing_files and not missing_wiring:
        print(f"OK: {len(t1_hooks)} T1 markers, all hooks present and wired.")
        return 0

    if missing_files:
        print("MISSING HOOK FILES:", file=sys.stderr)
        for fp, hook, lineno in missing_files:
            print(f"  {fp.relative_to(repo_root)}:{lineno} → "
                  f".claude/hooks/{hook}.py (file does not exist)",
                  file=sys.stderr)
    if missing_wiring:
        print("HOOK FILES EXIST BUT NOT WIRED IN settings.json:", file=sys.stderr)
        for fp, hook, lineno in missing_wiring:
            print(f"  {fp.relative_to(repo_root)}:{lineno} → "
                  f"{hook} (file exists, no settings entry)",
                  file=sys.stderr)
    return 1


def _scan_unmarked(repo_root: Path) -> dict[Path, list[tuple[int, str]]]:
    """Return {file: [(lineno, line)]} for directives without enforcement markers."""
    result: dict[Path, list[tuple[int, str]]] = {}
    for p in _audited_files(repo_root):
        text = _read(p)
        no_fences = _strip_code_fences(text)
        # Build line-number-aware view: re-iterate original to keep numbers
        in_fence = False
        unmarked: list[tuple[int, str]] = []
        for lineno, line in enumerate(text.splitlines(), start=1):
            stripped = line.lstrip()
            if stripped.startswith("```"):
                in_fence = not in_fence
                continue
            if in_fence:
                continue
            if not DIRECTIVE_KEYWORDS.search(line):
                continue
            if "enforcement:" in line:
                continue
            unmarked.append((lineno, line.rstrip()))
        if unmarked:
            result[p] = unmarked
    return result


def cmd_discover(repo_root: Path) -> int:
    """AC-167. Report unmarked directives. Exit 0 if zero, 1 otherwise."""
    unmarked = _scan_unmarked(repo_root)
    total = sum(len(v) for v in unmarked.values())
    if total == 0:
        print("OK: zero unmarked directives across SKILL.md + CLAUDE.md.")
        return 0
    print(f"FOUND {total} unmarked directives:")
    for fp, lines in unmarked.items():
        rel = fp.relative_to(repo_root)
        for lineno, line in lines:
            print(f"  {rel}:{lineno}: {line}")
    return 1


def _tier_breakdown(repo_root: Path) -> tuple[Counter, list[tuple[Path, int, int, int]]]:
    counts: Counter = Counter()
    per_file: list[tuple[Path, int, int, int]] = []
    for p in _audited_files(repo_root):
        text = _read(p)
        t1 = len(re.findall(r"enforcement:\s*T1", text))
        t2 = len(re.findall(r"enforcement:\s*T2", text))
        t3 = len(re.findall(r"enforcement:\s*T3", text))
        if t1 + t2 + t3:
            per_file.append((p, t1, t2, t3))
        counts["T1"] += t1
        counts["T2"] += t2
        counts["T3"] += t3
    return counts, per_file


def cmd_report(repo_root: Path) -> int:
    """AC-177, AC-168, AC-172. Generate audit-snapshot.md."""
    counts, per_file = _tier_breakdown(repo_root)
    total = sum(counts.values())
    unmarked = _scan_unmarked(repo_root)
    unmarked_total = sum(len(v) for v in unmarked.values())

    line_counts: list[tuple[Path, int]] = []
    for p in _audited_files(repo_root):
        text = _read(p)
        line_counts.append((p, text.count("\n") + (0 if text.endswith("\n") else 1)))
    top5 = sorted(line_counts, key=lambda x: -x[1])[:5]

    out = []
    out.append("# Audit Enforcement Snapshot")
    out.append("")
    out.append(f"**Date:** {date.today().isoformat()}")
    out.append(f"**Total directives marked:** {total}")
    out.append("")
    out.append("## Tier breakdown")
    out.append("")
    out.append("| Tier | Count | Percent |")
    out.append("|------|-------|---------|")

    def _pct(n: int) -> str:
        return f"{(100 * n // total) if total else 0}%"

    out.append(f"| T1 (hook-enforced) | {counts['T1']} | {_pct(counts['T1'])} |")
    out.append(f"| T2 (hook candidate, deferred) | {counts['T2']} | {_pct(counts['T2'])} |")
    out.append(f"| T3 (soft directive) | {counts['T3']} | {_pct(counts['T3'])} |")
    out.append("")
    out.append("## Per-file marker counts")
    out.append("")
    out.append("| File | T1 | T2 | T3 |")
    out.append("|------|----|----|----|")
    for fp, t1, t2, t3 in per_file:
        rel = fp.relative_to(repo_root).as_posix()
        out.append(f"| `{rel}` | {t1} | {t2} | {t3} |")
    out.append("")
    out.append("## Hook coverage")
    out.append("")
    hook_coverage = (100 * counts["T1"] // total) if total else 0
    out.append(f"- T1 markers (deterministic hook coverage): {hook_coverage}%")
    out.append(f"- T2 + T3 (deferred / soft directives): {100 - hook_coverage}%")
    out.append("")
    out.append("## Per-file unmarked directives")
    out.append("")
    if unmarked:
        out.append("| File | Unmarked |")
        out.append("|------|----------|")
        for fp, lines in unmarked.items():
            rel = fp.relative_to(repo_root).as_posix()
            out.append(f"| `{rel}` | {len(lines)} |")
    else:
        out.append("**Zero unmarked directives across all SKILL.md files and CLAUDE.md.**")
    out.append("")
    out.append("## Top 5 longest files")
    out.append("")
    out.append("| File | Lines |")
    out.append("|------|-------|")
    for fp, n in top5:
        rel = fp.relative_to(repo_root).as_posix()
        out.append(f"| `{rel}` | {n} |")
    out.append("")
    out.append(f"**Unmarked total:** {unmarked_total}")

    snapshot = repo_root / SNAPSHOT_PATH
    snapshot.parent.mkdir(parents=True, exist_ok=True)
    snapshot.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"Wrote {snapshot.relative_to(repo_root)} "
          f"(total={total}, unmarked={unmarked_total})")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--strict", action="store_true",
                        help="Exit 1 if any T1 marker references a missing or unwired hook.")
    parser.add_argument("--discover", action="store_true",
                        help="Report unmarked directives.")
    parser.add_argument("--report", action="store_true",
                        help="Write .workflow/audit-snapshot.md.")
    parser.add_argument("--repo-root", default=str(REPO_ROOT),
                        help="Override repo root (testing).")
    args = parser.parse_args(argv)

    modes = sum([args.strict, args.discover, args.report])
    if modes != 1:
        parser.error("specify exactly one of --strict, --discover, --report")

    repo_root = Path(args.repo_root).resolve()
    if args.strict:
        return cmd_strict(repo_root)
    if args.discover:
        return cmd_discover(repo_root)
    if args.report:
        return cmd_report(repo_root)
    return 1


if __name__ == "__main__":
    sys.exit(main())
