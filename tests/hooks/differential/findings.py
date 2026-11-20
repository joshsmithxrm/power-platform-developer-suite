"""Name the failures an oracle run produced, so a run can say which known findings it rediscovered.

These predicates only label failures after the fact; the generators do not use them and no case is
generated from them. ``KNOWN`` lists the must-fix findings of the second review round of the hook
(pull request #1814) that the oracles are expected to rediscover on their own, and the further
findings the oracles themselves turned up. ``classify`` returns the label of a failure, or
``"unlabelled"`` -- an unlabelled failure is a finding nobody has named yet.
"""
from __future__ import annotations

import re

# Code handed to a shell: a here-string or here-document to bash/sh/pwsh, `-c` / `-Command`, or a pipe into one.
_FEED = re.compile(r"(?:(?i:-(?:e|ec|encodedcommand)\s+[A-Za-z0-9+/=]{8,}))|(?:\beval\b)|(?:\b(?:bash|sh|pwsh)\b[^\n|;&]*(?:<<|\s-c\b|\s-Command\b))|(?:\|\s*(?:[^|;&\n]*\s)?(?:bash|sh|pwsh)\b)")
_NEW_MODES = ("gate-unknown", "gate-profile-named", "gate-settings", "shakedown-sentinel", "shakedown-noal")
_EXTRA_FAMILY = {k: fam for fam, kinds in {
    "launcher": ("sudo", "sudo-opts", "time", "time-p", "find-exec", "find-exec-plus", "start-process",
                 "start-process-string", "cmd-c", "cmd-exe"),
    "shell-flag": ("pwsh-e", "pwsh-ec", "pwsh-enc", "bash-lc", "bash-ec", "sh-ec", "bash-xc"),
    "syntax": ("ansi-c-quote", "ansi-c-program", "process-substitution", "source-process-substitution",
               "dot-process-substitution"),
    "two-invocations": ("two-invocations", "two-and", "two-switch-first"),
    "dotnet-form": ("dotnet-tool-run", "dotnet-tool-run-dd", "host-option-dll", "exec-host-option",
                    "project-substitution", "project-pwd", "watch-without-run", "project-dot-after-cd"),
    "environment-url": ("environment-url", "environment-url-pwsh", "mcp-environment-url"),
    "mcp": ("mcp-upper", "mcp-mixed-case", "mcp-override"),
}.items() for k in kinds}
_EXP = re.compile(r"(?:\d|\.)e(?:[+-]?\d*)?(?:delete|insert|update|merge|truncate|with|exec|select)", re.I)
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
    if cause == "write-in-shakedown" and argv[:1] == ["version"]:
        out.append("r2-version-update")
    if cause == "mcp-writable-in-shakedown" and "--read-only" in argv:
        out.append("r2-read-only-swallowed")
    if cause == "switch-to-unlisted" and any(w.startswith("@") for w in argv):
        out.append("r2-response-file-in-env-gate")
    cmd = f.get("command", "")
    flat = re.sub(r"[\\`]\r?\n", " ", cmd)
    if f.get("recipe", "").startswith("feed:") or _FEED.search(flat):
        out.append("new-shell-feed")
    if cause == "runs-on-unlisted-active" and len(argv) >= 2 and argv[0] in ("env", "org") \
            and argv[1] in ("config", "type"):
        out.append("r2-env-config-write-on-unlisted")
    if f.get("recipe", "").startswith("args-elsewhere") and cause == "switch-to-unlisted":
        out.append("new-switch-args-from-elsewhere")
    if cause == "switch-to-unlisted" and re.search(r"(`echo |\$\(echo |\"\$1\"|\$P\b|& \$P)", cmd):
        out.append("new-switch-behind-runtime-program")
    if cause.endswith("-not-in-text"):
        out.append("unplaceable-without-switch-words")
    if cause == "config-folder-override":
        out.append("new-config-folder-override")
    if cause in ("environment-override", "mcp-environment-override", "mcp-allowed-env-unlisted"):
        out.append("r2-target-override")
    if cause in ("profile-override", "mcp-profile-override", "profile-switch"):
        out.append("r2-profile-override")
    recipe = f.get("recipe", "")
    if recipe.startswith("xargs:"):
        out.append("new-xargs-options")
    if recipe.startswith("hidden:"):
        out.append("new-hidden-words")
    if recipe.startswith("wrapper:"):
        out.append("new-unknown-wrapper")
    if cause == "home-override" or recipe.startswith("home:"):
        out.append("new-home-override")
    if cause == "runs-on-unlisted-active" and argv[:2] in (["env", "who"], ["org", "who"]):
        out.append("r5-env-who-on-unlisted")
    if cause == "environment-url-override":
        out.append("r5-environment-url")
    if cause == "switch-without-allowlist":
        out.append("r5-switch-without-allowlist")
    if recipe.startswith("extra:"):
        out.append("r5-extra-" + _EXTRA_FAMILY.get(recipe.split(":")[1], "other"))
    kinds = (((f.get("truth") or {}).get("sql") or {}).get("kinds")) or []
    if cause == "write-in-shakedown" and "ExecuteStatement" in kinds and f.get("shape") != "sql-implicit-exec":
        out.append("r5-sql-implicit-exec")
    if f.get("shape") in ("auth-select", "sql-implicit-exec"):
        out.append("r5-" + f["shape"])
    if f.get("mode") in _NEW_MODES:
        out.append("r5-mode-" + f["mode"])
    if cause == "write-in-shakedown" and _EXP.search(_sql_text(f) or ""):
        out.append("new-sql-exponent-glue")
    if f.get("severity", "").startswith("policy"):
        out.append("policy-" + cause)
    return out


def classify(f: dict) -> str:
    if f.get("label") and f.get("cause") in ("over-timeout", "over-budget", "not-refused", "cost-rule"):
        return f["label"]
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
