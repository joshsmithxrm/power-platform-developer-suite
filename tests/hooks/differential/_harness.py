"""Shared plumbing for the differential tests of ``.claude/hooks/shakedown-safety.py``.

* ``Sandbox``: a throwaway folder with two made-up profile stores (active environment listed or not),
  a project folder with an empty ``.claude/settings.json``, and response files. Nothing here reads or
  writes the real profile store, the real home folder or the real project settings.
* ``run_hook`` / ``run_hooks``: the hook as a subprocess with a payload on stdin, in one of three modes.
* ``ParseOracle``: a client for the parse-only .NET helper in ``ParseOracle/`` (ground truth from the
  CLI's own parser, the MCP server's own option parser and the product's SQL parser).
"""
from __future__ import annotations

import concurrent.futures as cf
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
HOOK = Path(os.environ.get("PPDS_DIFF_HOOK", str(REPO / ".claude" / "hooks" / "shakedown-safety.py")))

TOOLS = ("Bash", "PowerShell")
MODES = ("gate-unlisted", "gate-listed", "shakedown")

# Made-up environment names. LISTED are on the allowlist, UNLISTED are not.
LISTED = ("zz-safe-a", "zz-safe-b")
UNLISTED = ("zz-prod-x", "zz-prod-y")
ALLOWLIST = ",".join(LISTED)
ACTIVE = {"gate-unlisted": "zz-prod-x", "gate-listed": "zz-safe-a", "shakedown": "zz-safe-a"}

# Profiles in each mode's made-up store: index 1 is the active one, index 2 the other.
PROFILES = {"p-zz-safe-a": "zz-safe-a", "p-zz-prod-x": "zz-prod-x"}


def store_profiles(mode: str) -> list:
    active = "p-" + ACTIVE[mode]
    return [(active, PROFILES[active])] + [(n, e) for n, e in PROFILES.items() if n != active]


def profile_environment(value, mode: str):
    """The environment a ``--profile`` / ``PPDS_PROFILE`` value selects in a mode's store, or None.
    Mirrors ProfileCollection.GetByNameOrIndex: "[N]", "[N] Name", a plain index, or a name (any case)."""
    if not isinstance(value, str) or not value.strip():
        return None
    profiles = store_profiles(mode)
    v = value.strip()
    if v.startswith("[") and "]" in v[2:]:
        inner = v[1:v.index("]")]
        if inner.isdigit():
            i = int(inner)
            return profiles[i - 1][1] if 1 <= i <= len(profiles) else None
    if v.lstrip("+-").isdigit():
        i = int(v)
        return profiles[i - 1][1] if 1 <= i <= len(profiles) else None
    return next((e for n, e in profiles if n.lower() == v.lower()), None)


# Response files the generators may name with an ``@file`` token (created in the sandbox folder
# that is the working directory of every hook run, shell run and parse).
RESPONSE_FILES = {
    "sel.rsp": "--environment\nzz-prod-x\n",
    "cmd.rsp": "env\nselect\n--environment\nzz-prod-x\n",
    "del.rsp": "data\ndelete\n--entity\naccount\n--filter\nname eq 'x'\n",
    "dry.rsp": "--dry-run\n",
}

_SCRUB_PREFIXES = ("PPDS_", "CLAUDE_", "AZURE_", "DATAVERSE_", "MSAL_", "ARM_")


def _scrubbed_env() -> dict:
    return {k: v for k, v in os.environ.items() if not k.startswith(_SCRUB_PREFIXES)}


class Sandbox:
    """A temporary folder holding everything a hook run, a shell run or a parse may touch."""

    def __init__(self, root: Optional[Path] = None):
        self.root = Path(root or tempfile.mkdtemp(prefix="ppds-diff-"))
        self.work = self.root / "work"
        self.work.mkdir(parents=True, exist_ok=True)
        for name, text in RESPONSE_FILES.items():
            (self.work / name).write_text(text, encoding="utf-8")
        # Files and folders that file-valued options name (the CLI's parser checks they exist).
        for name in ("f.txt", "q.sql", "x1"):
            (self.work / name).write_text("", encoding="utf-8")
        (self.work / "d").mkdir(exist_ok=True)
        self.stores = {}
        # The hook finds each mode's store the way the product does on Linux when PPDS_CONFIG_DIR is
        # not set: under $HOME/.ppds. (PPDS_CONFIG_DIR is left unset so that a HOME change in a text
        # is a real change of store; see ProfilePaths.cs.)
        self.mode_homes = {}
        for mode, env_name in ACTIVE.items():
            self.mode_homes[mode] = self.root / f"home-{mode}"
            cfg = self.mode_homes[mode] / ".ppds"
            cfg.mkdir(parents=True, exist_ok=True)
            (cfg / "profiles.json").write_text(json.dumps({
                "version": 2, "activeProfileIndex": 1,
                "profiles": [{"index": i, "name": name, "environment": {
                    "displayName": env, "uniqueName": env, "url": f"https://{env}.invalid/"}}
                    for i, (name, env) in enumerate(store_profiles(mode), start=1)],
            }), encoding="utf-8")
            self.stores[mode] = cfg
        self.project = self.root / "project"
        (self.project / ".claude" / "state").mkdir(parents=True, exist_ok=True)
        (self.project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")
        self.home = self.root / "home"
        self.home.mkdir(exist_ok=True)

    def hook_env(self, mode: str) -> dict:
        env = _scrubbed_env()
        env.update({
            "CLAUDE_PROJECT_DIR": str(self.project),
            "HOME": str(self.mode_homes[mode]),
            "PPDS_SAFE_ENVS": ALLOWLIST,
        })
        if mode == "shakedown":
            env["PPDS_SHAKEDOWN"] = "1"
        return env

    def cleanup(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)


def payload(tool: str, command: str, cwd: str) -> dict:
    return {"session_id": "diff", "transcript_path": "t.jsonl", "cwd": cwd,
            "hook_event_name": "PreToolUse", "tool_name": tool,
            "tool_input": {"command": command, "description": ""}, "tool_use_id": "diff"}


@dataclass
class HookResult:
    code: object  # int, or "TIMEOUT"
    seconds: float
    stderr: str


def run_hook(sandbox: Sandbox, tool: str, command: str, mode: str, timeout: float = 30.0) -> HookResult:
    data = json.dumps(payload(tool, command, str(sandbox.work)), ensure_ascii=False).encode("utf-8", "surrogatepass")
    t0 = time.monotonic()
    try:
        r = subprocess.run([sys.executable, str(HOOK)], input=data, capture_output=True,
                           timeout=timeout, env=sandbox.hook_env(mode), cwd=str(sandbox.work))
        return HookResult(r.returncode, time.monotonic() - t0, r.stderr.decode("utf-8", "replace"))
    except subprocess.TimeoutExpired:
        return HookResult("TIMEOUT", time.monotonic() - t0, "")


def run_hooks(sandbox: Sandbox, jobs: Iterable[tuple], workers: Optional[int] = None) -> list:
    """Run (tool, command, mode) jobs in parallel; results in job order."""
    jobs = list(jobs)
    workers = workers or max(2, min(32, (os.cpu_count() or 4)))
    with cf.ThreadPoolExecutor(max_workers=workers) as pool:
        return list(pool.map(lambda j: run_hook(sandbox, *j), jobs))


def write_case(folder: Path, name: str, tool: str, command: str, mode: str, sandbox: Sandbox, info: dict) -> Path:
    """Save a failing case as a payload file plus the mode it fails in (reproducible with repro.py)."""
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{name}.payload.json"
    body = payload(tool, command, "<sandbox work folder>")
    path.write_text(json.dumps(body, ensure_ascii=False, indent=1), encoding="utf-8")
    (folder / f"{name}.case.json").write_text(json.dumps(
        {"mode": mode, "tool": tool, "command": command, "allowlist": list(LISTED),
         "active_environment": ACTIVE[mode], **info}, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


# --------------------------------------------------------------------------------------------
# Parse oracle client
# --------------------------------------------------------------------------------------------

ORACLE_DIR = HERE / "ParseOracle"
ORACLE_DLL = ORACLE_DIR / "bin" / "Debug" / "net10.0" / "ParseOracle.dll"


class OracleUnavailable(RuntimeError):
    pass


def _oracle_sources_newer() -> bool:
    if not ORACLE_DLL.exists():
        return True
    built = ORACLE_DLL.stat().st_mtime
    return any(p.stat().st_mtime > built for p in ORACLE_DIR.glob("*.cs*"))


def build_oracle() -> None:
    """Build the helper (and so src/PPDS.Cli) if needed. Raises OracleUnavailable when it cannot."""
    if not _oracle_sources_newer():
        return
    if shutil.which("dotnet") is None:
        raise OracleUnavailable("dotnet is not on PATH")
    r = subprocess.run(["dotnet", "build", str(ORACLE_DIR / "ParseOracle.csproj"), "-v", "q", "-nologo"],
                       capture_output=True, text=True, timeout=1800, cwd=str(REPO))
    if r.returncode != 0 or not ORACLE_DLL.exists():
        tail = "\n".join((r.stdout + r.stderr).splitlines()[-15:])
        raise OracleUnavailable("building ParseOracle failed:\n" + tail)


class ParseOracle:
    """One long-lived helper process; answers are cached by request."""

    def __init__(self, sandbox: Sandbox):
        build_oracle()
        empty_cfg = sandbox.root / "cfg-oracle-empty"
        empty_cfg.mkdir(exist_ok=True)
        env = _scrubbed_env()
        env.update({"HOME": str(sandbox.home), "PPDS_CONFIG_DIR": str(empty_cfg),
                    "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
        self._proc = subprocess.Popen(["dotnet", str(ORACLE_DLL)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                      stderr=subprocess.DEVNULL, cwd=str(sandbox.work), env=env,
                                      text=True, encoding="utf-8", bufsize=1)
        self._lock = threading.Lock()
        self._cache: dict = {}

    def ask(self, request: dict) -> dict:
        key = json.dumps(request, sort_keys=True, ensure_ascii=False)
        if key in self._cache:
            return self._cache[key]
        with self._lock:
            self._proc.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
            self._proc.stdin.flush()
            line = self._proc.stdout.readline()
        if not line:
            raise OracleUnavailable("ParseOracle ended unexpectedly")
        answer = json.loads(line)
        self._cache[key] = answer
        return answer

    def cli(self, argv: list) -> dict:
        return self.ask({"op": "cli", "argv": list(argv)})

    def mcp(self, argv: list) -> dict:
        return self.ask({"op": "mcp", "argv": list(argv)})

    def sql(self, text: str) -> dict:
        return self.ask({"op": "sql", "text": text})

    def tree(self) -> list:
        return self.ask({"op": "tree"})["commands"]

    def close(self) -> None:
        try:
            self._proc.stdin.close()
            self._proc.wait(timeout=10)
        except Exception:
            self._proc.kill()
