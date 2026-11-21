"""Generator for the cost rule (the other direction): ordinary commands that only mention a program
name next to option-looking words must not be refused with a listed environment and no shakedown.

Every text here is a search, a listing or a process query. Ground truth: under real bash and real
pwsh no stand-in starts. The property (mode ``gate-listed``): if no stand-in starts under either
shell and the text names no switch and no target (``_truth.text_shows_target``), the hook passes it.
Nothing here changes anything: ``Stop-Process`` always carries ``-WhatIf``.
"""
from __future__ import annotations

import random

NAMES = ("ppds", "dotnet", "dnx", "ppds-mcp-server", "PPDS.Cli", "ppds.exe", "PPDS")

BASH = (
    "grep {n} -rE docs", "grep -rn {n} docs", "grep -rniE '{n}' docs/", "grep -c {n} docs/notes.md",
    "grep -rl -- {n} docs", "rg -n {n} docs", "rg -n --hidden {n} docs", "rg '{n} run' -g '*.md' docs",
    "rg -F '{n} --help' docs", "find docs -name '*{n}*' -print", "ls -la docs | grep -i {n}",
    "command -v {n} || true", "which {n} || true", "type -a {n} || true", "pgrep -a {n} || true",
    "ps aux | grep -v grep | grep {n} || true", "echo {n} --help", "echo '{n} -rE'",
    "sed -n '/{n}/p' docs/notes.md", "awk '/{n}/ {{print}}' docs/notes.md", "cat docs/notes.md | grep -E '^{n}'",
    "grep -rn '{n} --project' docs", "git grep -n {n} -- docs || true", "history | grep {n} || true",
)
PWSH = (
    "Select-String -Pattern '{n}' -Path docs/*", "Select-String -Path docs/notes.md -Pattern {n} -SimpleMatch",
    "Get-Process {n} -ErrorAction SilentlyContinue", "Get-Process -Name {n} -ErrorAction SilentlyContinue | Select-Object Id",
    "Stop-Process -Name {n} -Force -WhatIf -ErrorAction SilentlyContinue",
    "Get-ChildItem -Recurse -Filter '*{n}*' docs", "Get-Command {n} -ErrorAction SilentlyContinue",
    "Get-Content docs/notes.md | Select-String {n}", "Get-ChildItem docs | Where-Object {{ $_.Name -match '{n}' }}",
    "(Get-Content docs/notes.md) -match '{n}'", "Write-Output '{n} -ErrorAction'", "Test-Path ./{n}",
)
# A few that do name a switch or a target: a refusal of these is allowed (counted, not failed).
NAMED = ("grep -rn '{n} env select' docs", "rg -n -- '--environment' docs | grep {n}",
         "Select-String -Pattern '{n} --profile' -Path docs/*")


def generate(seed: int = 7, limit=None) -> list:
    out = [(t.format(n=n), f"ordinary:{'bash' if t in BASH else 'pwsh' if t in PWSH else 'named'}:{t}")
           for t in BASH + PWSH + NAMED for n in NAMES]
    if limit is not None and limit < len(out):
        out = random.Random(seed).sample(out, limit)
    return out
