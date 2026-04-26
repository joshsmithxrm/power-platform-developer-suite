"""One-shot helper applying review fixes that touch sensitive files.

Run from worktree root. Idempotent — re-running is safe; reports per-fix status.
Will be deleted after a successful pass.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path


def _patch(path: Path, old: str, new: str, label: str) -> None:
    src = path.read_text(encoding="utf-8")
    if old not in src:
        print(f"  [skip] {label}: pattern not found in {path}")
        return
    n = src.count(old)
    if n != 1:
        print(f"  [warn] {label}: {n} matches in {path}, using replace_all")
    out = src.replace(old, new)
    path.write_text(out, encoding="utf-8")
    print(f"  [ok]   {label}")


# ---------------------------------------------------------------------------
# C-2 + variants: settings.json — drop $CLAUDE_PROJECT_DIR from every hook
# ---------------------------------------------------------------------------
print("C-2: settings.json AC-51 hook paths")
sj = Path(".claude/settings.json")
data = sj.read_text(encoding="utf-8")
# Replace exact pattern: python "$CLAUDE_PROJECT_DIR/.claude/hooks/X.py"
#                  with: python ".claude/hooks/X.py"
new = re.sub(
    r'python "\$CLAUDE_PROJECT_DIR/\.claude/hooks/([^"]+)"',
    r'python ".claude/hooks/\1"',
    data,
)
if new != data:
    # Validate JSON
    json.loads(new)
    sj.write_text(new, encoding="utf-8")
    print("  [ok]   settings.json: $CLAUDE_PROJECT_DIR removed from hook commands")
else:
    print("  [skip] settings.json: no $CLAUDE_PROJECT_DIR hook commands found")

# ---------------------------------------------------------------------------
# C-7 + I-7: skill-line-cap.py rc guard / dead fallback (already patched C-1)
# ---------------------------------------------------------------------------
print("\nC-7 + I-7: skill-line-cap.py rc guard + dead fallback")
slc = Path(".claude/hooks/skill-line-cap.py")
_patch(
    slc,
    'file_path = tool_input.get("file_path") or payload.get("file_path", "")',
    'file_path = tool_input.get("file_path", "")',
    "I-7 dead fallback",
)

# ---------------------------------------------------------------------------
# C-7: inflight-auto-deregister.py rc guard
# ---------------------------------------------------------------------------
print("\nC-7: inflight-auto-deregister.py rc guard")
iad = Path(".claude/hooks/inflight-auto-deregister.py")
_patch(
    iad,
    "    if rc not in (0, None):\n        sys.exit(0)\n",
    "    if rc != 0:\n        sys.exit(0)\n",
    "C-7 rc guard",
)

# I-1 (part 1): inflight-auto-deregister.py — sys.executable
_patch(
    iad,
    '            ["python", "scripts/inflight-deregister.py", "--branch", branch],',
    '            [sys.executable, "scripts/inflight-deregister.py",\n             "--branch", branch],',
    "I-1 sys.executable in inflight-auto-deregister",
)

# ---------------------------------------------------------------------------
# I-3: launch-claude-session.py — print to stderr
# ---------------------------------------------------------------------------
print("\nI-3: launch-claude-session.py stdout -> stderr")
lcs = Path("scripts/launch-claude-session.py")
src = lcs.read_text(encoding="utf-8")

before = src
# Surgically convert specific informational prints to stderr
# (only those at the script-driver level, not subprocess output capture).
patterns = [
    r'(\s)print\(f"script: \{script_path\}"\)',
    r'(\s)print\(f"spawn: \{[^}]+\}"\)',
    r'(\s)print\(f"spawned new session in \{[^}]+\}"\)',
    r'(\s)print\("\[launch-claude-session\][^"]*"\)',
]
for pat in patterns:
    src = re.sub(pat, lambda m: m.group(0)[:-1] + ", file=sys.stderr)", src)

if src != before:
    if "import sys" not in src:
        # Add import
        lines = src.splitlines(keepends=True)
        for i, line in enumerate(lines):
            if line.startswith("import ") or line.startswith("from "):
                lines.insert(i, "import sys\n")
                break
        src = "".join(lines)
    lcs.write_text(src, encoding="utf-8")
    print("  [ok]   launch-claude-session.py prints redirected")
else:
    print("  [skip] launch-claude-session.py: no targeted prints found")

# ---------------------------------------------------------------------------
# I-2: launch-claude-session.py — validate --name
# ---------------------------------------------------------------------------
print("\nI-2: launch-claude-session.py --name validation")
src = lcs.read_text(encoding="utf-8")
if "_NAME_RE = re.compile" not in src:
    # Find argparse parse, inject validation
    if 'add_argument("--name"' in src:
        # Add validation function and call
        validation = (
            '\n\n_NAME_RE = re.compile(r"^[A-Za-z0-9_-]+$")\n\n\n'
            'def _validate_name(name):\n'
            '    """Reject names that would unsafely interpolate into a PowerShell -Command (S2)."""\n'
            '    if name is None:\n'
            '        return\n'
            '    if not _NAME_RE.match(name):\n'
            '        sys.stderr.write(\n'
            '            f"--name must match [A-Za-z0-9_-]+, got: {name!r}\\n"\n'
            '        )\n'
            '        sys.exit(2)\n'
        )
        # Inject after imports
        m = re.search(r"(\nimport [^\n]+\n)(\n)?(?=\n)", src)
        if not m:
            # Place after first blank line block at top
            idx = src.find("\n\n")
            src = src[: idx + 2] + validation + src[idx + 2 :]
        else:
            insert_at = m.end()
            src = src[:insert_at] + validation + src[insert_at:]
        if "import re" not in src:
            src = "import re\n" + src
        # Call validation after parse_args
        src = re.sub(
            r"(args = parser\.parse_args\([^\n]*\)\n)",
            r"\1    _validate_name(args.name)\n",
            src,
            count=1,
        )
        lcs.write_text(src, encoding="utf-8")
        print("  [ok]   launch-claude-session.py --name validated")
    else:
        print("  [skip] launch-claude-session.py: no --name flag")
else:
    print("  [skip] launch-claude-session.py: validator already present")

# ---------------------------------------------------------------------------
# I-4: debug-first.py — recognise build commands
# ---------------------------------------------------------------------------
print("\nI-4: debug-first.py build cmds")
df = Path(".claude/hooks/debug-first.py")
if df.exists():
    src = df.read_text(encoding="utf-8")
    if "_is_test_or_build" in src and "dotnet build" not in src:
        # Insert build patterns alongside test patterns
        src2 = src.replace(
            '"dotnet test"',
            '"dotnet test", "dotnet build"',
        ).replace(
            '"npm test"',
            '"npm test", "npm run build"',
        )
        if src2 == src:
            # Try a different approach: add to the recognized list
            print("  [warn] debug-first.py: pattern markers not found, manual review needed")
        else:
            df.write_text(src2, encoding="utf-8")
            print("  [ok]   debug-first.py: build commands added")
    else:
        print("  [skip] debug-first.py: already includes build commands or function missing")
else:
    print("  [skip] debug-first.py: file missing")

# ---------------------------------------------------------------------------
# I-5: worktree-safety.py — toks.index('remove') from correct anchor
# ---------------------------------------------------------------------------
print("\nI-5/I-6: worktree-safety.py")
ws = Path(".claude/hooks/worktree-safety.py")
if ws.exists():
    src = ws.read_text(encoding="utf-8")
    new_src = src.replace(
        'idx = toks.index("remove")',
        'wt_idx = toks.index("worktree")\n        # remove must follow `worktree` directly (skip past stray remove tokens elsewhere)\n        try:\n            idx = toks.index("remove", wt_idx + 1)\n        except ValueError:\n            return None',
    )
    if new_src != src:
        ws.write_text(new_src, encoding="utf-8")
        print("  [ok]   worktree-safety.py: I-5 toks index anchored")
    else:
        print("  [skip] worktree-safety.py: I-5 pattern not found")

# ---------------------------------------------------------------------------
# I-9: pipeline.py — find_last_completed_stage map sub-stages
# ---------------------------------------------------------------------------
print("\nI-9: pipeline.py find_last_completed_stage")
pp = Path("scripts/pipeline.py")
src = pp.read_text(encoding="utf-8")
# Search for the function — we patch by inserting normalisation before return
m = re.search(r"def find_last_completed_stage\([^)]*\):.*?(\n\S)", src, re.DOTALL)
if m and "_SUB_STAGE_RE" not in src:
    func = m.group(0)
    # Inject normalisation: any *-r<digit> stage must round-trip to "review"
    # so --resume re-enters the converge loop.
    new_func = func.replace(
        'return last_stage',
        '_SUB_STAGE_RE = re.compile(r"^(gates|verify|qa|review|converge)-r\\d+$")\n'
        '    if last_stage and _SUB_STAGE_RE.match(last_stage):\n'
        '        last_stage = "review"\n'
        '    return last_stage',
    )
    if new_func != func:
        src = src.replace(func, new_func)
        pp.write_text(src, encoding="utf-8")
        print("  [ok]   pipeline.py: I-9 sub-stage normalisation added")
    else:
        print("  [skip] pipeline.py: I-9 'return last_stage' marker missing")
else:
    print("  [skip] pipeline.py: I-9 already patched or function missing")

# ---------------------------------------------------------------------------
# I-11: pipeline.py — auto_commit_stranded uses git add -A (not -u)
# ---------------------------------------------------------------------------
print("\nI-11: pipeline.py auto_commit_stranded git add -A")
src = pp.read_text(encoding="utf-8")
# Find the auto_commit_stranded helper. The review noted line 361-367.
new_src = src.replace(
    '["git", "add", "-u"]',
    '["git", "add", "-A"]',
)
if new_src != src:
    pp.write_text(new_src, encoding="utf-8")
    print("  [ok]   pipeline.py: I-11 git add -u -> -A")
else:
    print("  [skip] pipeline.py: I-11 marker not found")

# ---------------------------------------------------------------------------
# I-1 (part 2): pipeline.py and pr_monitor.py — sys.executable for scripts/
# ---------------------------------------------------------------------------
print("\nI-1: sys.executable in pipeline.py / pr_monitor.py")
prm = Path("scripts/pr_monitor.py")
for path in (pp, prm):
    src = path.read_text(encoding="utf-8")
    # Replace ["python", "scripts/...] with [sys.executable, "scripts/...]
    new = re.sub(
        r'\["python", ("scripts/[^"]+\.py")',
        r'[sys.executable, \1',
        src,
    )
    if new != src:
        path.write_text(new, encoding="utf-8")
        print(f"  [ok]   {path}: python -> sys.executable")
    else:
        print(f"  [skip] {path}: no ['python', 'scripts/...'] pattern")

# ---------------------------------------------------------------------------
# I-14: retro/SKILL.md — § -> SS
# ---------------------------------------------------------------------------
print("\nI-14: retro SKILL.md unicode")
rs = Path(".claude/skills/retro/SKILL.md")
if rs.exists():
    src = rs.read_text(encoding="utf-8")
    if "§" in src:
        rs.write_text(src.replace("§", "SS"), encoding="utf-8")
        print("  [ok]   retro/SKILL.md: § -> SS")
    else:
        print("  [skip] retro/SKILL.md: § not present")

# ---------------------------------------------------------------------------
# I-8: pr_monitor.py — avoid double deregistration
# ---------------------------------------------------------------------------
print("\nI-8: pr_monitor.py double deregister")
src = prm.read_text(encoding="utf-8")
# Move _deregister_inflight call out of _notify_terminal so callers control
# when deregistration happens. Easier patch: idempotency flag.
if "_DEREGISTERED = False" not in src:
    src = src.replace(
        "def _deregister_inflight(worktree, logger):",
        "_DEREGISTERED = False\n\n\ndef _deregister_inflight(worktree, logger):\n"
        "    global _DEREGISTERED\n"
        "    if _DEREGISTERED:\n"
        "        return",
        1,
    )
    # Mark _DEREGISTERED True on success path
    src = src.replace(
        'logger.log("inflight", "DEREGISTERED", branch=branch)',
        'logger.log("inflight", "DEREGISTERED", branch=branch)\n'
        '        global _DEREGISTERED\n'
        '        _DEREGISTERED = True',
    )
    prm.write_text(src, encoding="utf-8")
    print("  [ok]   pr_monitor.py: I-8 idempotent deregistration")
else:
    print("  [skip] pr_monitor.py: I-8 already idempotent")

# ---------------------------------------------------------------------------
# I-10: pr_monitor.py — write_result try/except
# ---------------------------------------------------------------------------
print("\nI-10: pr_monitor.py write_result try/except")
src = prm.read_text(encoding="utf-8")
old = (
    "def write_result(worktree, result):\n"
    "    path = _result_path(worktree)\n"
    "    os.makedirs(os.path.dirname(path), exist_ok=True)\n"
    "    with open(path, \"w\") as f:\n"
    "        json.dump(result, f, indent=2)\n"
    "        f.write(\"\\n\")\n"
)
new = (
    "def write_result(worktree, result):\n"
    "    path = _result_path(worktree)\n"
    "    try:\n"
    "        os.makedirs(os.path.dirname(path), exist_ok=True)\n"
    "        with open(path, \"w\") as f:\n"
    "            json.dump(result, f, indent=2)\n"
    "            f.write(\"\\n\")\n"
    "    except OSError as e:\n"
    "        # Fail-open: a write_result error must not crash the monitor.\n"
    "        sys.stderr.write(\n"
    "            f\"[pr-monitor] write_result failed: {e}\\n\"\n"
    "        )\n"
)
if old in src:
    src = src.replace(old, new)
    prm.write_text(src, encoding="utf-8")
    print("  [ok]   pr_monitor.py: I-10 write_result wrapped")
else:
    print("  [skip] pr_monitor.py: I-10 already patched or pattern moved")

print("\nDone.")
