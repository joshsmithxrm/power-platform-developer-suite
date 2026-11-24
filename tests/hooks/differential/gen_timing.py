"""Generator and check for the hook's answer time on long inputs.

Texts of 50,000 to 200,000 characters that are valid in both shells (checked with ``bash -n`` and the
PowerShell parser), in several shapes: a write followed by many words naming the programs and many short
words, or many statements. The property: the hook answers inside its documented budget
(``_BUDGET_SECONDS`` in the hook, plus a fixed allowance for process start-up) and never later than the
timeout registered for it in ``.claude/settings.json``, and it refuses (every text starts with a write).
Both limits are read from the repository.
"""
from __future__ import annotations

import ast
import json
import random
import shutil
import subprocess
import tempfile
from pathlib import Path

from _harness import HOOK, REPO

# Allowance for starting the interpreter and reading the payload, on top of the documented budget.
STARTUP_ALLOWANCE = 0.5

LEAD = "ppds data delete --entity account ; echo "
NAME_WORDS = ("ppds", "dotnet", "dnx", "ppds-mcp-server", "PPDS.Cli", "src/PPDS.Cli", "ppds.exe")


def documented_budget(hook: Path = HOOK) -> float:
    tree = ast.parse(Path(hook).read_text(encoding="utf-8"))
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(getattr(t, "id", None) == "_BUDGET_SECONDS" for t in node.targets):
            return float(ast.literal_eval(node.value))
    raise AssertionError("_BUDGET_SECONDS not found in the hook")


def registered_timeout(settings: Path = REPO / ".claude" / "settings.json") -> float:
    data = json.loads(Path(settings).read_text(encoding="utf-8"))
    found = []
    for entries in (data.get("hooks") or {}).values():
        for entry in entries:
            for h in entry.get("hooks", []):
                if "shakedown-safety" in h.get("command", ""):
                    found.append(float(h.get("timeout", 60)))  # Claude Code's default hook timeout is 60 s
    if not found:
        raise AssertionError("the hook is not registered in .claude/settings.json")
    return min(found)


def _fill(prefix: str, length: int, words: callable) -> str:
    parts = [prefix]
    n = len(prefix)
    while n < length:
        w = words()
        parts.append(w + " ")
        n += len(w) + 1
    return "".join(parts).rstrip()


def shapes(rng: random.Random) -> dict:
    """name -> function(length) -> text."""
    def names_short(length):
        return _fill(LEAD, length, lambda: "ppds" if rng.random() < 0.25 else "a")   # ~40% / 60% of characters

    def names_only(length):
        return _fill(LEAD, length, lambda: "ppds")

    def mixed(length):
        return _fill(LEAD, length, lambda: rng.choice(NAME_WORDS) if rng.random() < 0.4 else rng.choice("abcxyz"))

    def quoted(length):
        return _fill(LEAD, length, lambda: rng.choice(("'ppds'", '"ppds"', "'a'", '"b"', "pp''ds")))

    def statements(length):
        return _fill("ppds data delete --entity account ; ", length, lambda: rng.choice(("ppds env list ;", "echo a ;")))

    def dotnet_run(length):
        return _fill("dotnet run --project src/PPDS.Cli -- data delete --entity account ; echo ", length,
                     lambda: rng.choice(("dotnet run", "--project", "src/PPDS.Cli", "a", "b")))

    return {"names-short": names_short, "names-only": names_only, "mixed-programs": mixed,
            "quoted-names": quoted, "statements": statements, "dotnet-run": dotnet_run}


def generate(lengths, seed: int = 110) -> list:
    rng = random.Random(seed)
    out = []
    for name, make in shapes(rng).items():
        for length in lengths:
            out.append({"shape": name, "length": length, "text": make(length)})
    return out


def valid_in_both_shells(text: str) -> tuple:
    """(bash ok, pwsh ok); None for a shell that is not installed."""
    with tempfile.TemporaryDirectory() as tmp:
        f = Path(tmp) / "t.txt"
        f.write_text(text, encoding="utf-8")
        bash = shutil.which("bash")
        pwsh = shutil.which("pwsh")
        b = None if not bash else subprocess.run([bash, "-n", str(f)], capture_output=True, timeout=60).returncode == 0
        p = None
        if pwsh:
            script = ("$e = $null; $t = [IO.File]::ReadAllText($args[0]); "
                      "$null = [System.Management.Automation.Language.Parser]::ParseInput($t, [ref]$null, [ref]$e); "
                      "exit $e.Count")
            ps1 = Path(tmp) / "parse.ps1"
            ps1.write_text(script, encoding="utf-8")
            p = subprocess.run([pwsh, "-NoProfile", "-NonInteractive", "-File", str(ps1), str(f)],
                               capture_output=True, timeout=120).returncode == 0
        return b, p


# Raw text far above the hook's caps: tens of millions of one character (or a short unit), with and
# without a mention in front. Property: an answer inside the registered timeout.
RAW_UNITS = ("'", '"', "(", "{", "a", "\\", "$", "`", ";", " ", "a ", "ppds ", "\"a\" ", "$(", "dotnet ", "dotnet run ",
             "dnx ", "ppds-mcp-server ")


def raw_generate(lengths) -> list:
    out = []
    for unit in RAW_UNITS:
        for length in lengths:
            body = unit * max(1, length // len(unit))
            out.append({"shape": f"raw{unit!r}", "length": length, "text": body})
            out.append({"shape": f"raw{unit!r}+mention", "length": length,
                        "text": "ppds data delete --entity account ; " + body})
    return out
