"""Name the failures an oracle run produced, so a run can say which known findings it rediscovered.

These predicates only label failures after the fact; the generators do not use them and no case is
generated from them. ``KNOWN`` lists the must-fix findings of the second review round of the hook
(pull request #1814) that the oracles are expected to rediscover on their own, and the further
findings the oracles themselves turned up. ``classify`` returns the label of a failure, or
``"unlabelled"`` -- an unlabelled failure is a finding nobody has named yet.
"""
from __future__ import annotations

import re

_GLUED = re.compile(r"(?:\d|0x[0-9a-f]+|'|\]|\.)(?:delete|insert|update|merge|truncate)", re.I)


def _sql_text(f: dict) -> str:
    argv = f.get("argv", [])
    if argv[:2] == ["query", "sql"] or "sql" in argv[:3]:
        # the bound text, if the argument oracle reported it; otherwise the longest word
        return max((w for w in argv if " " in w or "\n" in w), key=len, default="")
    return ""


def _labels(f: dict) -> list:
    argv = [str(w) for w in f.get("argv", [])]
    cause = f["cause"]
    out = []
    if f.get("program") == "ppds" and argv == [] and cause in ("runs-on-unlisted-active", "write-in-shakedown"):
        out.append("r2-bare-start")
    if cause in ("switch-to-unlisted", "runs-on-unlisted-active") and any(
            re.match(r"^(--environment|-env):", w) for w in argv):
        out.append("r2-switch-colon")
    if cause in ("switch-to-unlisted", "runs-on-unlisted-active") and any(
            re.fullmatch(r"(--environment|-env)[=:]", w) for w in argv):
        out.append("new-switch-delimiter-then-space")
    if cause == "write-in-shakedown":
        sql = _sql_text(f)
        if sql and sql.lstrip().startswith("-"):
            out.append("r2-sql-leading-comment")
        if sql and _GLUED.search(sql):
            out.append("r2-sql-glued-write-word")
        if argv[:2] == ["plugins", "deploy"] and "--dry-run" in argv and (f.get("truth") or {}).get(
                "explicit_options", {}).get("--dry-run") is not True:
            out.append("r2-dry-run-swallowed")
    if cause == "mcp-writable-in-shakedown" and "--read-only" in argv:
        out.append("r2-read-only-swallowed")
    if cause == "switch-to-unlisted" and any(w.startswith("@") for w in argv):
        out.append("r2-response-file-in-env-gate")
    cmd = f.get("command", "")
    if "<<<" in cmd and re.search(r"\benv\b[^|;&\n]*\b(bash|sh)\b", cmd):
        out.append("new-env-wrapper-here-string")
    if f.get("recipe", "").startswith("args-elsewhere") and cause == "switch-to-unlisted":
        out.append("new-switch-args-from-elsewhere")
    if cause == "switch-to-unlisted" and re.search(r"(`echo |\$\(echo |\"\$1\"|\$P\b|& \$P)", cmd):
        out.append("new-switch-behind-runtime-program")
    if f.get("severity", "").startswith("policy"):
        out.append("policy-" + cause)
    return out


def classify(f: dict) -> str:
    labels = _labels(f)
    return "+".join(labels) if labels else "unlabelled"


# The six findings the brief requires the oracles to rediscover (round 2 must-fix).
REQUIRED = ("r2-switch-colon", "r2-bare-start", "r2-sql-leading-comment", "r2-sql-glued-write-word",
            "r2-dry-run-swallowed", "r2-read-only-swallowed")


def rediscovered(failures: list) -> dict:
    seen = {}
    for f in failures:
        for label in _labels(f):
            seen[label] = seen.get(label, 0) + 1
    return {"required": {r: seen.get(r, 0) for r in REQUIRED},
            "all_labels": dict(sorted(seen.items(), key=lambda kv: -kv[1])),
            "unlabelled": sum(1 for f in failures if not _labels(f))}
