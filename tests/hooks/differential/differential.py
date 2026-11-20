"""Run the two differential oracles against ``.claude/hooks/shakedown-safety.py``.

    python tests/hooks/differential/differential.py args  [--size N] [--seed S] [--cases DIR] [--json OUT]
    python tests/hooks/differential/differential.py shell [--size N] [--seed S] [--cases DIR] [--json OUT]

``args``  oracle 1, argument level: generated argument vectors, ground truth from the CLI's own parsers.
``shell`` oracle 2, statement level: generated command texts, ground truth from real bash and pwsh with
          stand-ins, then the argument level for what the stand-ins received.

The property, in every mode (see README.md): if the hook passes while a gate applies, ground truth must
say the invocation is harmless. Exit code 1 when a requirement-severity failure is found.
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from _harness import MODES, TOOLS, OracleUnavailable, ParseOracle, Sandbox, run_hooks, write_case  # noqa: E402
import _truth  # noqa: E402
import findings  # noqa: E402

CI_SIZE_ARGS = 4500
CI_SIZE_SHELL = 700
LONG_SIZE_ARGS = 40000
LONG_SIZE_SHELL = 8000


def _blocked(result) -> bool:
    return result.code == 2


def _summarise(failures: list, strict: bool) -> dict:
    groups = collections.Counter()
    for f in failures:
        groups[(f["mode"], f["severity"], f["cause"], f.get("shape", f.get("recipe", "")))] += 1
    return {" | ".join(k): v for k, v in sorted(groups.items(), key=lambda kv: (-kv[1], kv[0]))}


def _case_name(prefix: str, f: dict) -> str:
    h = hashlib.sha1((f["tool"] + f["mode"] + f["command"]).encode("utf-8", "surrogatepass")).hexdigest()[:10]
    return f"{prefix}-{f['mode']}-{f['cause']}-{h}"


def _save_cases(failures: list, cases_dir, prefix: str, sandbox, per_group: int = 6) -> int:
    if not cases_dir:
        return 0
    cases_dir = Path(cases_dir)
    seen = collections.Counter()
    n = 0
    for f in failures:
        key = (f["mode"], f["cause"], f.get("shape", f.get("recipe", "")))
        if seen[key] >= per_group:
            continue
        seen[key] += 1
        write_case(cases_dir, _case_name(prefix, f), f["tool"], f["command"], f["mode"], sandbox,
                   {k: v for k, v in f.items() if k not in ("tool", "command", "mode")})
        n += 1
    return n


# --------------------------------------------------------------------------------------------
# Oracle 1
# --------------------------------------------------------------------------------------------

def run_argument_oracle(size: int = CI_SIZE_ARGS, seed: int = 1814, cases_dir=None, strict: bool = False,
                        confirm: bool = True) -> dict:
    import gen_args
    t0 = time.monotonic()
    sandbox = Sandbox()
    oracle = ParseOracle(sandbox)
    try:
        cases = gen_args.generate(oracle, size, seed)
        jobs, meta = [], []
        for ci, case in enumerate(cases):
            for mode in MODES:
                truth, harms = _truth.judge(oracle, case.program, case.argv, mode)
                for tool in TOOLS:
                    jobs.append((tool, gen_args.render(case.words(), tool), mode))
                    meta.append((ci, mode, harms, truth))
        t_truth = time.monotonic() - t0
        results = run_hooks(sandbox, jobs)
        t_hook = time.monotonic() - t0 - t_truth
        stats = collections.defaultdict(collections.Counter)
        failures, false_blocks = [], collections.Counter()
        for (tool, text, mode), (ci, _, harms, truth), res in zip(jobs, meta, results):
            case = cases[ci]
            harms = _truth.apply_text_rule(harms, text)
            s = stats[f"{mode}/{tool}"]
            s["runs"] += 1
            s["hook_blocked" if _blocked(res) else "hook_passed"] += 1
            if res.code not in (0, 2):
                s[f"hook_exit_{res.code}"] += 1
            if res.seconds > 3.5:
                s["slower_than_budget"] += 1
            req = [h for h in harms if h.severity == "requirement"]
            pol = [h for h in harms if h.severity == "policy"]
            if harms:
                s["harmful"] += 1
            if not harms and _blocked(res):
                s["false_block"] += 1
                false_blocks[(mode, case.shape)] += 1
            if harms and not _blocked(res):
                for h in (req or pol):
                    failures.append({"tool": tool, "mode": mode, "command": text, "program": case.program,
                                     "argv": case.argv, "prefix": list(case.prefix), "shape": case.shape,
                                     "cause": h.cause, "severity": h.severity, "detail": h.detail,
                                     "hook_exit": res.code, "truth": _slim(truth)})
                    s["fail_" + h.severity] += 1
        confirmed = _confirm_rendering(sandbox, oracle, failures) if confirm else None
        if confirmed is not None:
            failures = confirmed["kept"]
        for f in failures:
            f["label"] = findings.classify(f)
        req_fail = [f for f in failures if f["severity"] == "requirement"]
        pol_fail = [f for f in failures if f["severity"] == "policy"]
        saved = _save_cases(req_fail + pol_fail, cases_dir, "args", sandbox)
        return {
            "oracle": "args", "seed": seed, "size": size, "cases": len(cases), "hook_runs": len(jobs),
            "seconds": {"truth": round(t_truth, 1), "hook": round(t_hook, 1), "total": round(time.monotonic() - t0, 1)},
            "shapes": dict(collections.Counter(c.shape for c in cases)),
            "stats": {k: dict(v) for k, v in sorted(stats.items())},
            "requirement_failures": len(req_fail), "policy_failures": len(pol_fail),
            "groups": _summarise(failures, strict),
            "findings": findings.rediscovered(failures),
            "false_blocks_by_mode_shape": {" | ".join(k): v for k, v in sorted(false_blocks.items(), key=lambda kv: -kv[1])},
            "render_check": None if confirmed is None else {k: v for k, v in confirmed.items() if k != "kept"},
            "saved_cases": saved,
            "failures": failures,
        }
    finally:
        oracle.close()
        sandbox.cleanup()


def _slim(truth: dict) -> dict:
    if not isinstance(truth, dict):
        return truth
    out = {k: truth[k] for k in ("path", "errors", "runs", "tui", "read_only", "environment", "profile", "allowed_env",
                                 "sql") if k in truth}
    if "options" in truth:
        out["explicit_options"] = {k: v.get("value") for k, v in truth["options"].items() if not v.get("implicit")}
    return out


def _confirm_rendering(sandbox, oracle, failures: list) -> dict:
    """Run each failing text in the real shell of its tool with stand-ins and check that the program
    received exactly the generated vector. A failure whose rendering does not hold is re-judged with
    what the program really received; it is dropped only if that is harmless."""
    try:
        from _shells import ShellLab, map_start
        lab = ShellLab(sandbox)
    except Exception as exc:  # no bash or pwsh: keep everything, say so
        return {"kept": failures, "skipped": str(exc)}
    by_text = {}
    for f in failures:
        by_text.setdefault((f["tool"], f["command"]), []).append(f)
    pwsh_texts = [t for (tool, t) in by_text if tool == "PowerShell"]
    pwsh_records = dict(zip(pwsh_texts, lab.run_all(pwsh_texts)[1])) if pwsh_texts else {}
    kept, mismatched, dropped = [], 0, 0
    for (tool, text), fs in by_text.items():
        records = lab.run_bash(text) if tool == "Bash" else pwsh_records.get(text, [])
        starts = [s for r in records for s in map_start(r)]
        want = (fs[0]["program"], fs[0]["argv"])  # oracle 1 texts carry no environment overrides
        if any((p, a) == want for p, a, _ in starts):
            kept += fs
            continue
        mismatched += 1
        for f in fs:
            really = [h for p, a, _ in starts
                      for h in _truth.apply_text_rule(_truth.judge(oracle, p, a, f["mode"])[1], text)]
            if really:
                f = dict(f, detail=f["detail"] + f" (shell gave {starts!r})")
                kept.append(f)
            else:
                dropped += 1
    return {"kept": kept, "texts_checked": len(by_text), "rendering_differed": mismatched, "dropped": dropped}


# --------------------------------------------------------------------------------------------
# Oracle 2
# --------------------------------------------------------------------------------------------

CI_FEED_LIMIT = 500


def run_statement_oracle(size: int = CI_SIZE_SHELL, seed: int = 1780, cases_dir=None, strict: bool = False,
                         feed_limit=CI_FEED_LIMIT) -> dict:
    import gen_shell
    from _shells import ShellLab, map_start
    t0 = time.monotonic()
    sandbox = Sandbox()
    lab = ShellLab(sandbox)
    try:
        oracle = ParseOracle(sandbox)
    except OracleUnavailable as exc:
        oracle = None
        oracle_note = str(exc)
    else:
        oracle_note = ""
    try:
        texts = gen_shell.generate(size, seed, str(lab.bin), feed_limit)
        bash, pwsh = lab.run_all([t.text for t in texts])
        t_shell = time.monotonic() - t0
        verdicts = []
        for t, rb, rp in zip(texts, bash, pwsh):
            starts = {"bash": [s + (lab.overrides(r),) for r in rb for s in map_start(r)],
                      "pwsh": [s + (lab.overrides(r),) for r in rp for s in map_start(r)]}
            per_mode = {}
            for mode in MODES:
                harms = []
                for shell, ss in starts.items():
                    for program, argv, exact, ov in ss:
                        if oracle is None:
                            hs = [_truth.Harm("starts-on-unlisted-active", "requirement")] if mode == "gate-unlisted" else []
                        else:
                            hs = _truth.apply_text_rule(_truth.judge(oracle, program, argv, mode, ov)[1], t.text)
                        for h in hs:
                            harms.append((h, shell, program, argv, exact))
                per_mode[mode] = harms
            verdicts.append((starts, per_mode))
        jobs, meta = [], []
        for i, t in enumerate(texts):
            for mode in MODES:
                if oracle is None and mode != "gate-unlisted":
                    continue
                for tool in TOOLS:
                    jobs.append((tool, t.text, mode))
                    meta.append((i, mode))
        results = run_hooks(sandbox, jobs)
        t_hook = time.monotonic() - t0 - t_shell
        by_case = collections.defaultdict(dict)
        for (tool, _, mode), (i, _), res in zip(jobs, meta, results):
            by_case[(i, mode)][tool] = res
        stats = collections.defaultdict(collections.Counter)
        failures = []
        started = collections.Counter()
        for (i, mode), res in by_case.items():
            t = texts[i]
            starts, per_mode = verdicts[i]
            harms = per_mode[mode]
            s = stats[mode]
            s["texts"] += 1
            if starts["bash"]:
                started["bash"] += 1 if mode == MODES[0] else 0
            if starts["pwsh"]:
                started["pwsh"] += 1 if mode == MODES[0] else 0
            passed = [tool for tool, r in res.items() if not _blocked(r)]
            if any(r.seconds > 3.5 for r in res.values()):
                s["slower_than_budget"] += 1
            if not harms:
                if not starts["bash"] and not starts["pwsh"]:
                    s["nothing_started"] += 1
                s["false_block_any_tool"] += 1 if len(passed) < len(res) else 0
                continue
            s["harmful"] += 1
            if not passed:
                continue
            req = [h for h in harms if h[0].severity == "requirement"]
            h, shell, program, argv, exact = (req or harms)[0]
            sev = h.severity if exact else h.severity + "-approx"
            for tool in passed:
                failures.append({"tool": tool, "mode": mode, "command": t.text, "recipe": t.recipe, "core": t.core,
                                 "cause": h.cause, "severity": sev, "detail": h.detail, "started_under": shell,
                                 "program": program, "argv": argv, "hook_exit": res[tool].code,
                                 "starts": {k: [[p, a] for p, a, _, _ in v] for k, v in starts.items()}})
                s["fail_" + sev] += 1
        failures = _confirm_shell(lab, failures)
        for f in failures:
            f["label"] = findings.classify(f)
        req_fail = [f for f in failures if f["severity"] == "requirement"]
        saved = _save_cases(failures, cases_dir, "shell", sandbox, per_group=4)
        return {
            "oracle": "shell", "seed": seed, "size": size, "texts": len(texts), "hook_runs": len(jobs),
            "argument_truth": "parse oracle" if oracle else f"unavailable ({oracle_note}); gate-unlisted mode only",
            "seconds": {"shells": round(t_shell, 1), "hook": round(t_hook, 1), "total": round(time.monotonic() - t0, 1)},
            "texts_starting_a_standin": dict(started),
            "stats": {k: dict(v) for k, v in sorted(stats.items())},
            "requirement_failures": len(req_fail),
            "other_failures": len(failures) - len(req_fail),
            "groups": _summarise(failures, strict),
            "findings": findings.rediscovered(failures),
            "saved_cases": saved,
            "failures": failures,
        }
    finally:
        if oracle:
            oracle.close()
        sandbox.cleanup()


def _confirm_shell(lab, failures: list) -> list:
    """A failure seen only under the batched pwsh run is re-run in a fresh ``pwsh -Command``."""
    out = []
    cache = {}
    for f in failures:
        if f["started_under"] != "pwsh" or f["starts"]["bash"]:
            out.append(f)
            continue
        if f["command"] not in cache:
            cache[f["command"]] = lab.run_pwsh(f["command"])
        if cache[f["command"]]:
            out.append(f)
    return out


# --------------------------------------------------------------------------------------------
# Timing
# --------------------------------------------------------------------------------------------

CI_TIMING_LENGTHS = (50_000, 110_000)
LONG_TIMING_LENGTHS = (20_000, 50_000, 80_000, 110_000, 150_000, 200_000)


def run_timing_oracle(lengths=CI_TIMING_LENGTHS, modes=("shakedown",), seed: int = 110, cases_dir=None,
                      workers: int = 2) -> dict:
    """Long inputs valid in both shells: the hook must refuse inside its documented budget (plus a
    start-up allowance) and never after its registered timeout. Runs few at a time so that the
    measured time is the hook's, not the machine's load."""
    import gen_timing
    t0 = time.monotonic()
    budget = gen_timing.documented_budget()
    timeout = gen_timing.registered_timeout()
    limit = budget + gen_timing.STARTUP_ALLOWANCE
    sandbox = Sandbox()
    try:
        texts = gen_timing.generate(lengths, seed)
        validity = [gen_timing.valid_in_both_shells(t["text"]) for t in texts]
        jobs, meta = [], []
        for i, t in enumerate(texts):
            for mode in modes:
                for tool in TOOLS:
                    jobs.append((tool, t["text"], mode))
                    meta.append(i)
        from _harness import run_hook
        import concurrent.futures as cf
        with cf.ThreadPoolExecutor(max_workers=workers) as pool:
            # A run is stopped 5 s after the registered timeout: by then it is late either way.
            results = list(pool.map(lambda j: run_hook(sandbox, *j, timeout=timeout + 5), jobs))
        rows, failures = [], []
        for (tool, text, mode), i, res in zip(jobs, meta, results):
            t = texts[i]
            secs = round(res.seconds, 2)
            row = {"shape": t["shape"], "length": len(text), "tool": tool, "mode": mode, "exit": res.code,
                   "seconds": secs, "valid_bash": validity[i][0], "valid_pwsh": validity[i][1]}
            rows.append(row)
            base = {"tool": tool, "mode": mode, "command": text, "shape": t["shape"], "length": len(text),
                    "seconds": secs, "hook_exit": res.code, "severity": "requirement", "program": "ppds", "argv": []}
            if res.code == "TIMEOUT" or res.seconds > timeout:
                failures.append(dict(base, cause="over-timeout", label="r2-late-answer",
                                     detail=f"{secs} s > registered timeout {timeout} s"))
            elif res.seconds > limit:
                failures.append(dict(base, cause="over-budget", label="r2-over-budget",
                                     detail=f"{secs} s > budget {budget} s + {gen_timing.STARTUP_ALLOWANCE} s"))
            if res.code != 2 and res.code != "TIMEOUT":
                failures.append(dict(base, cause="not-refused", label="timing-not-refused",
                                     detail=f"exit {res.code}; the text starts with a write"))
        saved = _save_cases(failures, cases_dir, "timing", sandbox, per_group=3)
        return {"oracle": "timing", "seed": seed, "lengths": list(lengths), "texts": len(texts), "hook_runs": len(jobs),
                "budget": budget, "startup_allowance": gen_timing.STARTUP_ALLOWANCE, "registered_timeout": timeout,
                "seconds": {"total": round(time.monotonic() - t0, 1)},
                "max_seconds": max((r["seconds"] for r in rows), default=0),
                "rows": rows, "requirement_failures": len(failures),
                "groups": _summarise(failures, False), "saved_cases": saved, "failures": failures}
    finally:
        sandbox.cleanup()


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("oracle", choices=("args", "shell", "timing"))
    ap.add_argument("--size", type=int)
    ap.add_argument("--long", action="store_true", help="the larger corpus for a long run")
    ap.add_argument("--seed", type=int)
    ap.add_argument("--cases", help="folder to write failing cases into (payload + case file)")
    ap.add_argument("--json", help="write the full report as JSON")
    ap.add_argument("--strict", action="store_true", help="policy-severity failures also fail the run")
    a = ap.parse_args(argv)
    if a.oracle == "args":
        size = a.size or (LONG_SIZE_ARGS if a.long else CI_SIZE_ARGS)
        rep = run_argument_oracle(size, a.seed if a.seed is not None else 1814, a.cases, a.strict)
    elif a.oracle == "timing":
        rep = run_timing_oracle(LONG_TIMING_LENGTHS if a.long else CI_TIMING_LENGTHS,
                                ("shakedown", "gate-unlisted") if a.long else ("shakedown",),
                                a.seed if a.seed is not None else 110, a.cases)
    else:
        size = a.size or (LONG_SIZE_SHELL if a.long else CI_SIZE_SHELL)
        rep = run_statement_oracle(size, a.seed if a.seed is not None else 1780, a.cases, a.strict,
                                   None if a.long else CI_FEED_LIMIT)
    if a.json:
        Path(a.json).write_text(json.dumps(rep, indent=1, ensure_ascii=False), encoding="utf-8")
    shown = {k: v for k, v in rep.items() if k not in ("failures", "rows")}
    print(json.dumps(shown, indent=1, ensure_ascii=False))
    bad = rep["requirement_failures"] + (rep.get("policy_failures", 0) + rep.get("other_failures", 0) if a.strict else 0)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
