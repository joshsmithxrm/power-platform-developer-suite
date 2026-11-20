"""Mutation check of the differential suite itself (not run by pytest).

    python tests/hooks/differential/mutate.py [--only NAME ...] [--unit] [--json OUT] [--per-group N]

Re-introduces each defect of ``mutants.M`` into a scratch copy of ``.claude/hooks`` (the checkout is
never changed) and reports which oracle catches it at the CI size:

1. Ground truth is computed once, at the CI size and seeds, independent of the hook: oracle 1's vectors
   and oracle 2's texts with their harms per mode, the timing texts.
2. The harmful jobs (requirement severity) are sampled, at most ``--per-group`` per (oracle, mode,
   cause, shape) group, and run against the unmutated hook; only jobs it refuses are kept.
3. Each mutant runs the kept jobs. It is caught by an oracle when at least one of that oracle's jobs
   now passes. The timing jobs run for every mutant whose name says cap, deadline or watchdog.
4. ``--unit``: for a mutant no oracle catches, the hook unit tests (``tests/hooks`` without this folder)
   are run against the mutated copy and the first failing test is named.

Prints a table and, with ``--json``, writes the details.
"""
from __future__ import annotations

import argparse
import collections
import json
import random
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import _truth  # noqa: E402
import gen_args  # noqa: E402
import gen_shell  # noqa: E402
import gen_timing  # noqa: E402
import mutants  # noqa: E402
from _harness import MODES, REPO, TOOLS, ParseOracle, Sandbox, run_hook, run_hooks  # noqa: E402
from _shells import ShellLab, map_start  # noqa: E402

TIMING_WORDS = ("cap", "deadline", "watchdog")


def _sample(jobs: list, per_group: int, rng: random.Random) -> list:
    groups = collections.defaultdict(list)
    for j in jobs:
        groups[j["group"]].append(j)
    out = []
    for key in sorted(groups):
        g = groups[key]
        out += g if len(g) <= per_group else rng.sample(g, per_group)
    return out


def prepare(sandbox, oracle, lab, per_group: int) -> dict:
    rng = random.Random(4242)
    jobs = []
    cases = gen_args.generate(oracle, 4500, 1814)
    for case in cases:
        for mode in MODES:
            harms = _truth.judge(oracle, case.program, case.argv, mode)[1]
            for tool in TOOLS:
                text = gen_args.render(case.words(), tool)
                req = [h for h in _truth.apply_text_rule(harms, text, mode) if h.severity == "requirement"]
                if req:
                    jobs.append({"oracle": "args", "tool": tool, "mode": mode, "command": text,
                                 "group": ("args", mode, req[0].cause, case.shape)})
    texts = gen_shell.generate(700, 1780, str(lab.bin), 500)
    bash, pwsh = lab.run_all([t.text for t in texts])
    for t, rb, rp in zip(texts, bash, pwsh):
        starts = [s + (lab.overrides(r),) for r in rb + rp for s in map_start(r)]
        if not starts:
            continue
        for mode in MODES:
            req = [h for program, argv, exact, ov in starts
                   for h in _truth.apply_text_rule(_truth.judge(oracle, program, argv, mode, ov)[1], t.text, mode)
                   if h.severity == "requirement"]
            if req:
                for tool in TOOLS:
                    jobs.append({"oracle": "shell", "tool": tool, "mode": mode, "command": t.text,
                                 "group": ("shell", mode, req[0].cause, t.recipe.split(":")[0])})
    sampled = _sample(jobs, per_group, rng)
    timing = [{"oracle": "timing", "tool": tool, "mode": "shakedown", "command": t["text"], "group": ("timing", t["shape"])}
              for t in gen_timing.generate((50_000,)) + gen_timing.raw_generate((200_000,)) for tool in TOOLS]
    return {"jobs": sampled, "all_harmful_jobs": len(jobs), "timing": timing,
            "integrity": lab.integrity(len(texts))}


def scratch_copy(root: Path, name: str) -> Path:
    dest = root / name
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(REPO / ".claude" / "hooks", dest / ".claude" / "hooks", ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copy(REPO / ".claude" / "settings.json", dest / ".claude" / "settings.json")
    for fname, old, new in mutants.M.get(name, []):
        path = dest / ".claude" / "hooks" / fname
        text = path.read_text(encoding="utf-8")
        if text.count(old) != 1:
            raise LookupError(f"{fname}: the old text occurs {text.count(old)} times")
        path.write_text(text.replace(old, new), encoding="utf-8")
    return dest


def run_unit_tests(dest: Path) -> str:
    """Run tests/hooks (without this folder) against the mutated copy; name the first failing test."""
    (dest / "tests").mkdir(exist_ok=True)
    shutil.copy(REPO / "tests" / "conftest.py", dest / "tests" / "conftest.py")
    if not (dest / "tests" / "hooks").exists():
        shutil.copytree(REPO / "tests" / "hooks", dest / "tests" / "hooks",
                        ignore=shutil.ignore_patterns("__pycache__", "differential"))
    for link in ("src", "scripts", "docs", "specs"):
        if (REPO / link).exists() and not (dest / link).exists():
            (dest / link).symlink_to(REPO / link)
    for f in (REPO / "tests").glob("test_settings*.py"):
        shutil.copy(f, dest / "tests" / f.name)
    r = subprocess.run([sys.executable, "-m", "pytest", "tests/hooks", "-q", "-x", "-p", "no:cacheprovider",
                        "--no-header", "-rf"], cwd=str(dest), capture_output=True, text=True, timeout=3600)
    failed = [ln.split(" ", 1)[1].split(" - ")[0] for ln in r.stdout.splitlines() if ln.startswith("FAILED ")]
    return failed[0] if failed else ("none failed" if r.returncode == 0 else f"exit {r.returncode}")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", nargs="*", help="mutant names (default: all)")
    ap.add_argument("--unit", action="store_true", help="name the unit test that catches what no oracle catches")
    ap.add_argument("--per-group", type=int, default=25)
    ap.add_argument("--json")
    a = ap.parse_args(argv)
    t0 = time.monotonic()
    sandbox = Sandbox()
    oracle = ParseOracle(sandbox)
    lab = ShellLab(sandbox)
    scratch = Path(tempfile.mkdtemp(prefix="ppds-mutants-"))
    try:
        prep = prepare(sandbox, oracle, lab, a.per_group)
        jobs = prep["jobs"]
        base = run_hooks(sandbox, [(j["tool"], j["command"], j["mode"]) for j in jobs])
        kept = [j for j, r in zip(jobs, base) if r.code == 2]
        baseline_passes = len(jobs) - len(kept)
        budget, timeout = gen_timing.documented_budget(), gen_timing.registered_timeout()

        def timing_bad(hook):
            res = [run_hook(sandbox, j["tool"], j["command"], j["mode"], timeout=timeout + 5, hook=hook)
                   for j in prep["timing"]]
            return [r.code == "TIMEOUT" or r.seconds > timeout for r in res]

        base_timing = timing_bad(None)
        rows = []
        for name in (a.only or sorted(mutants.M)):
            row = {"mutant": name}
            try:
                dest = scratch_copy(scratch, name)
            except LookupError as exc:
                row.update(applied=False, note=str(exc))
                rows.append(row)
                print(f"{name}: not applicable ({exc})", flush=True)
                continue
            hook = dest / ".claude" / "hooks" / "shakedown-safety.py"
            res = run_hooks(sandbox, [(j["tool"], j["command"], j["mode"]) for j in kept], hook=hook)
            caught = collections.Counter(j["oracle"] for j, r in zip(kept, res) if r.code != 2)
            examples = {}
            for j, r in zip(kept, res):
                if r.code != 2 and j["oracle"] not in examples:
                    examples[j["oracle"]] = f"{j['mode']}/{j['tool']}: {j['command'][:100]}"
            row.update(applied=True, args=caught.get("args", 0), shell=caught.get("shell", 0), examples=examples)
            if any(w in name for w in TIMING_WORDS):
                bad = timing_bad(hook)
                row["timing"] = sum(1 for b, b0 in zip(bad, base_timing) if b and not b0)
            row["caught"] = bool(row["args"] or row["shell"] or row.get("timing"))
            if a.unit and not row["caught"]:
                row["unit_test"] = run_unit_tests(dest)
            rows.append(row)
            print(f"{name}: args={row['args']} shell={row['shell']} timing={row.get('timing', '-')} "
                  f"{'CAUGHT' if row['caught'] else 'NOT CAUGHT'} {row.get('unit_test', '')}", flush=True)
            shutil.rmtree(dest, ignore_errors=True)
        report = {"jobs_harmful": prep["all_harmful_jobs"], "jobs_sampled": len(jobs), "jobs_kept": len(kept),
                  "baseline_passes": baseline_passes, "baseline_timing_failures": sum(base_timing),
                  "shell_integrity": prep["integrity"], "seconds": round(time.monotonic() - t0, 1), "rows": rows}
        applied = [r for r in rows if r.get("applied")]
        print(f"\nmutants: {len(rows)}, applied: {len(applied)}, caught: {sum(r['caught'] for r in applied)}, "
              f"not caught: {[r['mutant'] for r in applied if not r['caught']]}, "
              f"not applicable: {[r['mutant'] for r in rows if not r.get('applied')]}")
        if a.json:
            Path(a.json).write_text(json.dumps(report, indent=1), encoding="utf-8")
        return 0
    finally:
        oracle.close()
        sandbox.cleanup()
        shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
