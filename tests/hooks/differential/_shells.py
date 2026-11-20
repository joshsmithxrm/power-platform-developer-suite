"""Ground truth from real shells: run a command text under real ``bash`` and real ``pwsh`` with a
harmless stand-in for every program the hook guards, and record which stand-ins started with what.

Safety of the runs:
* every stand-in only appends its argument vector to a log file and exits 0;
* each text runs with a working directory inside the sandbox, ``HOME`` and ``PPDS_CONFIG_DIR``
  redirected into it, and ``PATH`` = the stand-in folder plus ``/usr/bin:/bin`` only;
* ``assert_real_cli_unreachable`` runs before the first text: every guarded name must resolve to its
  stand-in, and no real ``ppds`` / ``ppds-mcp-server`` / ``dnx`` may exist on the system folders;
* a short timeout per text.
The generator (``gen_shell.py``) only composes texts from a fixed harmless vocabulary.
"""
from __future__ import annotations

import base64
import concurrent.futures as cf
import json
import os
import shutil
import stat
import subprocess
import uuid
from pathlib import Path

SYSTEM_PATH = "/usr/bin:/bin"
# Names that get a stand-in. The upper-case and .exe spellings stand for Windows, where lookup is
# case-insensitive and the extension may be written.
GUARDED_NAMES = ("ppds", "ppds-mcp-server", "dotnet", "dnx")
EXTRA_SPELLINGS = ("ppds.exe", "ppds-mcp-server.exe", "dotnet.exe", "dnx.exe", "PPDS", "Ppds", "DotNet", "DOTNET",
                   "PPDS.exe")

_STANDIN = """#!{python} -I
import json, os, sys
with open(os.environ.get("STANDIN_LOG", os.devnull), "a", encoding="utf-8", errors="surrogateescape") as fh:
    fh.write(json.dumps({{"prog": os.path.basename(sys.argv[0]), "argv": sys.argv[1:], "cwd": os.getcwd(),
                         "config_dir": os.environ.get("PPDS_CONFIG_DIR"), "profile": os.environ.get("PPDS_PROFILE")}},
                        ensure_ascii=False) + "\\n")
"""


class ShellLab:
    def __init__(self, sandbox):
        self.sandbox = sandbox
        self.bin = sandbox.root / "standin-bin"
        self.bin.mkdir(exist_ok=True)
        python = shutil.which("python3", path=SYSTEM_PATH) or "/usr/bin/python3"
        for name in GUARDED_NAMES + EXTRA_SPELLINGS:
            p = self.bin / name
            p.write_text(_STANDIN.format(python=python), encoding="utf-8")
            p.chmod(p.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        # Folders a ``cd`` in a text may name (so `cd src/PPDS.Cli && dotnet run` is meaningful).
        for sub in ("src/PPDS.Cli", "src/PPDS.Mcp"):
            (sandbox.work / sub).mkdir(parents=True, exist_ok=True)
        self.logs = sandbox.root / "standin-logs"
        self.logs.mkdir(exist_ok=True)
        self.path = f"{self.bin}:{SYSTEM_PATH}"
        # The configuration folder every shell run starts with. A stand-in that sees another one was
        # started with a configuration-folder override.
        self.config_dir = str(sandbox.home / "cfg")
        self.bash = shutil.which("bash", path=SYSTEM_PATH)
        self.pwsh = shutil.which("pwsh", path=SYSTEM_PATH) or shutil.which("pwsh")
        self.assert_real_cli_unreachable()

    def assert_real_cli_unreachable(self) -> None:
        for name in GUARDED_NAMES:
            got = shutil.which(name, path=self.path)
            if got != str(self.bin / name):
                raise RuntimeError(f"refusing to run shells: {name!r} resolves to {got!r}, not the stand-in")
        # The real CLI and MCP server must not exist on the system folders at all. (``dotnet`` and the
        # SDK's ``dnx`` may; they are shadowed by the stand-ins, which come first on PATH, and the
        # vocabulary never names a system folder.)
        for name in ("ppds", "ppds-mcp-server") + EXTRA_SPELLINGS[:2]:
            got = shutil.which(name, path=SYSTEM_PATH)
            if got is not None:
                raise RuntimeError(f"refusing to run shells: a real {name!r} is on the system folders: {got!r}")

    def env(self, log: Path) -> dict:
        return {"PATH": self.path, "HOME": str(self.sandbox.home), "PPDS_CONFIG_DIR": self.config_dir,
                "STANDIN_LOG": str(log), "LANG": "C.UTF-8", "TERM": "dumb", "SB": str(self.sandbox.work),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "POWERSHELL_TELEMETRY_OPTOUT": "1",
                "POWERSHELL_UPDATECHECK": "Off", "NO_COLOR": "1"}

    @staticmethod
    def _read(log: Path) -> list:
        if not log.exists():
            return []
        out = []
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            try:
                out.append(json.loads(line))
            except ValueError:
                out.append({"prog": "?", "argv": ["<unreadable stand-in record>"], "cwd": ""})
        return out

    def overrides(self, record: dict) -> dict:
        return {"config_dir_changed": record.get("config_dir") != self.config_dir,
                "profile": record.get("profile")}

    def run_bash(self, text: str, timeout: float = 10.0) -> list:
        log = self.logs / f"b-{uuid.uuid4().hex}.log"
        try:
            subprocess.run([self.bash, "--norc", "--noprofile", "-c", text], env=self.env(log), cwd=str(self.sandbox.work),
                           stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            pass
        return self._read(log)

    def run_pwsh(self, text: str, timeout: float = 20.0) -> list:
        log = self.logs / f"p-{uuid.uuid4().hex}.log"
        try:
            subprocess.run([self.pwsh, "-NoProfile", "-NonInteractive", "-Command", text], env=self.env(log),
                           cwd=str(self.sandbox.work), stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            pass
        return self._read(log)

    def run_pwsh_batch(self, texts: list, timeout: float = 600.0) -> list:
        """Many texts in one pwsh process (start-up is slow). Each text is parsed and run as a new
        script block in a child scope, from the sandbox folder, with its own log. Failing cases are
        confirmed afterwards with ``run_pwsh`` (a fresh ``pwsh -Command``)."""
        batch = self.sandbox.root / f"batch-{uuid.uuid4().hex}"
        batch.mkdir()
        logs = []
        lines = ["$ErrorActionPreference = 'Continue'", "$ProgressPreference = 'SilentlyContinue'",
                 f"$__sb = '{self.sandbox.work}'"]
        for i, text in enumerate(texts):
            log = batch / f"{i}.log"
            logs.append(log)
            b64 = base64.b64encode(text.encode("utf-8")).decode("ascii")
            lines.append(
                "try { Set-Location -LiteralPath $__sb; "
                f"$env:PPDS_CONFIG_DIR = '{self.config_dir}'; $env:PPDS_PROFILE = $null; $env:FOO = $null; "
                f"$env:STANDIN_LOG = '{log}'; "
                f"$__t = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{b64}')); "
                "$null = & ([scriptblock]::Create($__t)) 2>&1 } catch { }")
        script = batch / "run.ps1"
        script.write_text("\n".join(lines) + "\n", encoding="utf-8")
        try:
            subprocess.run([self.pwsh, "-NoProfile", "-NonInteractive", "-File", str(script)],
                           env=self.env(batch / "none.log"), cwd=str(self.sandbox.work), stdin=subprocess.DEVNULL,
                           capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            pass
        return [self._read(log) for log in logs]

    def run_all(self, texts: list, workers: int = 8, chunk: int = 60) -> tuple:
        """(bash_records, pwsh_records) for every text, in order."""
        with cf.ThreadPoolExecutor(max_workers=max(4, (os.cpu_count() or 4))) as pool:
            bash = list(pool.map(self.run_bash, texts))
        chunks = [texts[i:i + chunk] for i in range(0, len(texts), chunk)]
        with cf.ThreadPoolExecutor(max_workers=workers) as pool:
            parts = list(pool.map(self.run_pwsh_batch, chunks))
        pwsh = [r for part in parts for r in part]
        return bash, pwsh


# --------------------------------------------------------------------------------------------
# What a recorded stand-in start means: which PPDS program, with which argument vector
# --------------------------------------------------------------------------------------------

_DOTNET_RUN_VALUE = {"-c", "--configuration", "-f", "--framework", "-r", "--runtime", "-a", "--arch", "--os",
                     "-e", "--environment", "--launch-profile", "-lp", "-v", "--verbosity", "--property", "-p:",
                     "--artifacts-path", "--file"}
_DOTNET_RUN_FLAGS = {"--no-build", "--no-restore", "--no-launch-profile", "--interactive", "--force",
                     "--no-dependencies", "--disable-build-servers", "--no-cache", "--tl", "-q", "--quiet",
                     "--no-hot-reload", "--non-interactive"}
_DNX_VALUE = {"--version", "--source", "--add-source", "--configfile", "-v", "--verbosity"}
_DNX_FLAGS = {"--yes", "-y", "--prerelease", "--allow-roll-forward", "--interactive", "--ignore-failed-sources",
              "--no-http-cache", "--disable-parallel"}


def _ppds_program(name: str):
    n = name.lower().replace("\\", "/").rsplit("/", 1)[-1]
    for ext in (".exe", ".dll", ".csproj"):
        if n.endswith(ext):
            n = n[: -len(ext)]
    if n in ("ppds", "ppds.cli"):
        return "ppds"
    if n in ("ppds-mcp-server", "ppds.mcp"):
        return "ppds-mcp-server"
    return None


def _project_program(value: str):
    v = value.replace("\\", "/").rstrip("/")
    for part in reversed(v.split("/")):
        p = _ppds_program(part)
        if p:
            return p
    return None


def _app_args_after_run(rest: list) -> list:
    """dotnet run: everything after ``--``, plus words dotnet run does not know (it forwards them)."""
    out, i = [], 0
    while i < len(rest):
        w = rest[i]
        if w == "--":
            return out + rest[i + 1:]
        key = w.split("=", 1)[0].split(":", 1)[0] if w.startswith("-") else w
        if key in ("--project", "-p") or key in _DOTNET_RUN_VALUE:
            i += 1 if ("=" in w or (":" in w and w.startswith("-p:"))) else 2
            continue
        if key in _DOTNET_RUN_FLAGS:
            i += 2 if key == "--tl" and i + 1 < len(rest) and rest[i + 1] in ("off", "on", "auto") else 1
            continue
        out.append(w)
        i += 1
    return out


def map_start(record: dict) -> list:
    """[(program, argv, exact)] for a stand-in start. ``exact`` is False where the mapping of a .NET
    host form is approximate (then the case is reported in its own group)."""
    prog = record["prog"]
    argv = record["argv"]
    p = _ppds_program(prog)
    if p:
        return [(p, argv, True)]
    low = prog.lower()
    if low.startswith("dnx"):
        return _dnx(argv)
    if not low.startswith("dotnet"):
        return []
    if not argv:
        return []
    verb = argv[0].lower()
    rest = argv[1:]
    if verb == "watch":
        if rest and rest[0].lower() == "run":
            rest = rest[1:]
        verb = "run"
    if verb == "run":
        project = None
        for i, w in enumerate(rest):
            if w == "--":
                break
            if w in ("--project", "-p") and i + 1 < len(rest):
                project = rest[i + 1]
            elif w.startswith(("--project=", "--project:")):
                project = w.split(w[9], 1)[1] if len(w) > 9 else ""
        if project is None:
            cwd = record.get("cwd", "").replace("\\", "/")
            project = cwd
        program = _project_program(project) if project else None
        if program:
            return [(program, _app_args_after_run(rest), False)]
        return []
    if verb in ("exec",):
        rest = [w for w in rest]
        while rest and rest[0].startswith("-"):
            rest = rest[2:] if rest[0] in ("--runtimeconfig", "--depsfile", "--additionalprobingpath") else rest[1:]
        verb = rest[0] if rest else ""
        rest = rest[1:]
    if verb.lower().endswith(".dll"):
        program = _ppds_program(verb)
        return [(program, rest, True)] if program else []
    if verb == "tool" and rest and rest[0].lower() in ("exec", "execute", "run"):
        return _dnx(rest[1:])
    if verb == "dnx":
        return _dnx(rest)
    if _ppds_program(verb):
        return [(_ppds_program(verb), rest, True)]
    return []


def _dnx(argv: list) -> list:
    i = 0
    while i < len(argv) and argv[i].startswith("-"):
        i += 2 if argv[i] in _DNX_VALUE else 1
    if i >= len(argv):
        return []
    program = _ppds_program(argv[i].split("@", 1)[0])
    if not program:
        return []
    rest = argv[i + 1:]
    out, j = [], 0
    while j < len(rest):
        w = rest[j]
        if w == "--":
            out += rest[j + 1:]
            break
        if w in _DNX_FLAGS:
            j += 1
            continue
        if w in _DNX_VALUE:
            j += 2
            continue
        out.append(w)
        j += 1
    return [(program, out, False)]
