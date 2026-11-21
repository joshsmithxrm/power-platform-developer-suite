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
import signal
import stat
import threading
import time
import subprocess
import uuid
from pathlib import Path

_FLOCK = threading.Lock()
SYSTEM_PATH = "/usr/bin:/bin:/usr/sbin"
# Names that get a stand-in. The upper-case and .exe spellings stand for Windows, where lookup is
# case-insensitive and the extension may be written.
GUARDED_NAMES = ("ppds", "ppds-mcp-server", "dotnet", "dnx")
EXTRA_SPELLINGS = ("ppds.exe", "ppds-mcp-server.exe", "dotnet.exe", "dnx.exe", "PPDS", "Ppds", "DotNet", "DOTNET",
                   "PPDS.exe", "PPDS-MCP-SERVER", "Ppds-Mcp-Server", "DNX", "Dnx", "PPDS.EXE", "DOTNET.EXE")

_STANDIN = """#!{python} -I
import json, os, sys
with open(os.environ.get("STANDIN_LOG", os.devnull), "a", encoding="utf-8", errors="surrogateescape") as fh:
    fh.write(json.dumps({{"prog": os.path.basename(sys.argv[0]), "argv": sys.argv[1:], "cwd": os.getcwd(),
                         "config_dir": os.environ.get("PPDS_CONFIG_DIR"), "profile": os.environ.get("PPDS_PROFILE"),
                         "home": os.environ.get("HOME"), "environment_url": os.environ.get("PPDS_ENVIRONMENT_URL")}},
                        ensure_ascii=False) + "\\n")
"""


# Approximating stand-ins for wrappers that are missing here or must not run for real (ssh: network;
# xvfb-run: starts an X server). Each applies the wrapper's own option rules and then runs the rest
# locally, so the guarded stand-ins see what the real wrapper would start. Texts that use one of them
# are reported with an ``-approx`` severity.
APPROX_WRAPPERS = ("ssh", "parallel", "busybox", "winpty", "xvfb-run", "sudo", "cmd", "cmd.exe")
_WRAPPER = """#!{python} -I
import os, sys
name = os.path.basename(sys.argv[0])
a = sys.argv[1:]
def run(argv):
    if argv:
        os.execvp(argv[0], argv)
    sys.exit(0)
if name == "ssh":
    # ssh [options] host [command...]: the remote shell runs the words joined by spaces.
    with_value = set("-b -c -D -E -e -F -I -i -J -L -l -m -O -o -p -Q -R -S -W -w".split())
    i = 0
    while i < len(a) and a[i].startswith("-"):
        i += 2 if a[i] in with_value else 1
    rest = a[i + 1:]
    run(["sh", "-c", " ".join(rest)] if rest else [])
elif name == "sudo":
    with_value = set("-u --user -g --group -C -h --host -p --prompt -U -r -t -T".split())
    i = 0
    while i < len(a) and a[i].startswith("-"):
        i += 2 if a[i] in with_value else 1
    run(a[i:])
elif name in ("cmd", "cmd.exe"):
    # cmd /c <command line>: the rest is one command line; run it the way a POSIX shell splits it.
    i = 0
    while i < len(a) and a[i].lower() not in ("/c", "/k"):
        i += 1
    rest = " ".join(a[i + 1:])
    run(["sh", "-c", rest] if rest else [])
elif name == "busybox":
    run(a if not a or a[0] not in ("sh", "ash") else ["sh"] + a[1:])
elif name == "winpty":
    while a and a[0].startswith("-"):
        a = a[1:]
    run(a)
elif name == "xvfb-run":
    with_value = set("-e --error-file -f --auth-file -n --server-num -p --xauth-protocol -s --server-args -w --wait".split())
    i = 0
    while i < len(a) and a[i].startswith("-"):
        i += 2 if a[i] in with_value else 1
    run(a[i:])
elif name == "parallel":
    # GNU parallel: [options] command [::: args]; without ::: each input line is one argument,
    # -n N puts N of them on one command line. Runs the command lines one after another.
    with_value = set("-j --jobs -n --max-args -N -L --max-lines -d --delimiter -S --sshlogin --joblog".split())
    i, n = 0, 1
    while i < len(a) and a[i].startswith("-"):
        if a[i] in ("-n", "--max-args", "-N"):
            n = int(a[i + 1])
        i += 2 if a[i] in with_value else 1
    cmd = a[i:]
    if ":::" in cmd:
        k = cmd.index(":::")
        cmd, items = cmd[:k], cmd[k + 1:]
    else:
        items = [l.rstrip("\\n") for l in sys.stdin if l.strip()]
    import subprocess
    for j in range(0, len(items), max(n, 1)):
        subprocess.run(cmd + items[j:j + max(n, 1)])
"""


FIRST_TIMEOUT = 60.0
RETRY_TIMEOUT = 180.0


class HarnessError(RuntimeError):
    """The shells do not record what they run: ground truth would be empty."""


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
        for name in APPROX_WRAPPERS:
            p = self.bin / name
            p.write_text(_WRAPPER.format(python=python), encoding="utf-8")
            p.chmod(p.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        (sandbox.work / "docs").mkdir(exist_ok=True)
        (sandbox.work / "docs" / "notes.md").write_text("ppds env list\ndotnet run --project x\n", encoding="utf-8")
        (sandbox.work / "lock.txt").write_text("", encoding="utf-8")
        # Folders a ``cd`` in a text may name (so `cd src/PPDS.Cli && dotnet run` is meaningful).
        for sub in ("src/PPDS.Cli", "src/PPDS.Mcp"):
            (sandbox.work / sub).mkdir(parents=True, exist_ok=True)
        self.logs = sandbox.root / "standin-logs"
        self.logs.mkdir(exist_ok=True)
        self.path = f"{self.bin}:{SYSTEM_PATH}"
        # The configuration folder every shell run starts with. A stand-in that sees another one was
        # started with a configuration-folder override.
        # The lab sets no PPDS_CONFIG_DIR, so the store a program reaches is $HOME/.ppds unless the
        # text changes PPDS_CONFIG_DIR or HOME (ProfilePaths.cs, Linux). This is the default store.
        self.config_dir = None
        import collections
        self.outcomes = collections.Counter()   # "<shell>-finished" / "<shell>-timeout" per run
        self.timed_out = []
        self._count_lock = threading.Lock()
        self.default_store = os.path.normpath(str(sandbox.home / ".ppds"))
        self.bash = shutil.which("bash", path=SYSTEM_PATH)
        self.pwsh = shutil.which("pwsh", path=SYSTEM_PATH) or shutil.which("pwsh")
        self.canary_checks = 0
        self.canary_failures = []
        # Per-text timeouts and concurrency (README, "Integrity"). A first run past first_timeout is
        # retried once, retry_workers at a time, with retry_timeout: a slow start under load is not a
        # hang. Only a text that times out on the retry too counts as a timeout.
        self.first_timeout = FIRST_TIMEOUT
        self.retry_timeout = RETRY_TIMEOUT
        self.workers = max(4, min(16, os.cpu_count() or 4))
        self.retry_workers = 4
        self.assert_real_cli_unreachable()
        self.self_test()

    def assert_real_cli_unreachable(self) -> None:
        for name in GUARDED_NAMES + APPROX_WRAPPERS:
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
        return {"PATH": self.path, "HOME": str(self.sandbox.home),
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
        return {"config_dir_changed": self.reached_store(record) != self.default_store,
                "home_changed": not record.get("config_dir") and self.reached_store(record) != self.default_store,
                "profile": record.get("profile"), "environment_url": record.get("environment_url")}

    def reached_store(self, record: dict) -> str:
        """The profile-store folder the program would read (ProfilePaths: PPDS_CONFIG_DIR, else ~/.ppds)."""
        cwd = record.get("cwd") or str(self.sandbox.work)
        cfg = (record.get("config_dir") or "").strip()
        if cfg:
            return os.path.normpath(os.path.join(cwd, cfg))
        home = record.get("home") or ""
        return os.path.normpath(os.path.join(cwd, home, ".ppds"))

    def _run(self, argv: list, text: str, log: Path, timeout: float, count: bool = True) -> None:
        """Run one shell in its own process group; on timeout, kill the whole group, so that no child
        of a hanging text is left behind. Texts that take a file lock run one at a time."""
        lock = _FLOCK if "flock" in text else None
        if lock:
            lock.acquire()
        try:
            out_path = log.with_suffix(".out")
            out = open(out_path, "wb")  # the shell's own output, kept for diagnosis (a file, not a pipe)
            proc = subprocess.Popen(argv, env=self.env(log), cwd=str(self.sandbox.work), stdin=subprocess.DEVNULL,
                                    stdout=out, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                proc.wait(timeout=timeout)
                outcome = "finished"
            except subprocess.TimeoutExpired:
                outcome = "timeout"
            if "setsid" in text or "&" in text:
                time.sleep(0.5)  # a detached child may still be starting
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                pass
            proc.wait()
            out.close()
            with self._count_lock:
                if not count:
                    return outcome  # canaries are checked separately, not counted as corpus texts
                self.outcomes[f"{argv[0].rsplit('/', 1)[-1]}-{outcome}"] += 1
                if outcome == "timeout":
                    self.timed_out.append((argv[0].rsplit('/', 1)[-1], text[:200]))
            return outcome
        finally:
            if lock:
                lock.release()

    def integrity(self, submitted: int) -> dict:
        """Every submitted text must have a recorded outcome under both shells. ``*_timeouts`` counts the
        texts that timed out on the retry as well; ``*_retried`` the first runs that timed out."""
        done = {sh: self.outcomes[f"{sh}-finished"] + self.outcomes[f"{sh}-timeout"] for sh in ("bash", "pwsh")}
        return {"submitted": submitted, "bash_outcomes": done["bash"], "pwsh_outcomes": done["pwsh"],
                "canary_checks": self.canary_checks, "canary_failures": self.canary_failures[:10],
                "bash_timeouts": self.outcomes["bash-timeout"], "pwsh_timeouts": self.outcomes["pwsh-timeout"],
                "bash_retried": self.outcomes["bash-retried"], "pwsh_retried": self.outcomes["pwsh-retried"],
                "first_timeout_s": self.first_timeout, "retry_timeout_s": self.retry_timeout,
                "workers": self.workers, "retry_workers": self.retry_workers,
                "timed_out_examples": self.timed_out[:10]}

    def run_bash_ex(self, text: str, timeout: float = 60.0, count: bool = True) -> tuple:
        log = self.logs / f"b-{uuid.uuid4().hex}.log"
        outcome = self._run([self.bash, "--norc", "--noprofile", "-c", text], text, log, timeout, count)
        return self._read(log), outcome

    def run_pwsh_ex(self, text: str, timeout: float = 60.0, count: bool = True) -> tuple:
        log = self.logs / f"p-{uuid.uuid4().hex}.log"
        outcome = self._run([self.pwsh, "-NoProfile", "-NonInteractive", "-Command", text], text, log, timeout, count)
        return self._read(log), outcome

    def run_bash(self, text: str, timeout: float = 60.0) -> list:
        return self.run_bash_ex(text, timeout)[0]

    def run_pwsh(self, text: str, timeout: float = 60.0) -> list:
        return self.run_pwsh_ex(text, timeout)[0]

    # Known-good texts: each must record exactly this start. They run when the lab starts (after a
    # warm-up pwsh in the lab's fresh HOME, whose first start is cold and slow) and again inside every
    # run_all, so an oracle whose shells record nothing cannot pass (README, "Integrity").
    CANARIES = (("ppds env list", ("ppds", ["env", "list"])),
                ("& 'ppds' data delete --entity account", ("ppds", ["data", "delete", "--entity", "account"])),
                ("ppds-mcp-server --read-only", ("ppds-mcp-server", ["--read-only"])))

    def _canary_ok(self, records: list, want: tuple) -> bool:
        return any((r.get("prog"), r.get("argv")) == want for r in records)

    def self_test(self) -> None:
        """Warm pwsh up, then require every canary to record its start under pwsh (and the POSIX ones
        under bash). Raises HarnessError otherwise."""
        log = self.logs / "warmup.log"
        self._run([self.pwsh, "-NoProfile", "-NonInteractive", "-Command", "exit"], "exit", log, 300.0, False)
        for text, want in self.CANARIES:
            got, outcome = self.run_pwsh_ex(text, 180.0, count=False)
            if not self._canary_ok(got, want):
                raise HarnessError(f"pwsh records nothing for the known-good text {text!r} ({outcome}); "
                                   f"its output: {self._tail(text)}")
            if not text.startswith("&"):
                got, outcome = self.run_bash_ex(text, 60.0, count=False)
                if not self._canary_ok(got, want):
                    raise HarnessError(f"bash records nothing for the known-good text {text!r} ({outcome})")
        self.outcomes.clear()
        self.timed_out.clear()

    def _tail(self, text: str) -> str:
        outs = sorted(self.logs.glob("p-*.out"), key=lambda p: p.stat().st_mtime)
        return outs[-1].read_text("utf-8", "replace")[-300:] if outs else "(none)"

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
                f"$env:PPDS_CONFIG_DIR = $null; $env:HOME = '{self.sandbox.home}'; $env:PPDS_PROFILE = $null; "
                "$env:FOO = $null; $env:E = $null; "
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

    def _run_pass(self, go, name: str, batch: list, skip: set) -> list:
        """Run every text of ``batch`` under one shell; retry each run that timed out once, with a longer
        timeout and fewer runs at a time, and count the final outcome of every position not in ``skip``."""
        with cf.ThreadPoolExecutor(max_workers=self.workers) as pool:
            res = list(pool.map(lambda t: go(t, self.first_timeout, count=False), batch))
        again = [i for i, (_, outcome) in enumerate(res) if outcome == "timeout"]
        with cf.ThreadPoolExecutor(max_workers=self.retry_workers) as pool:
            for i, r in zip(again, pool.map(lambda i: go(batch[i], self.retry_timeout, count=False), again)):
                res[i] = r
        again = set(again)
        with self._count_lock:
            for i, (_, outcome) in enumerate(res):
                if i in skip:
                    continue
                self.outcomes[f"{name}-{outcome}"] += 1
                if i in again:
                    self.outcomes[f"{name}-retried"] += 1
                if outcome == "timeout":
                    self.timed_out.append((name, batch[i][:200]))
        return [records for records, _ in res]

    def run_all(self, texts: list) -> tuple:
        """(bash_records, pwsh_records) for every text, in order."""
        # One fresh process per text and shell, each with its own timeout (a batch would let one
        # hanging text stall all the others).
        canaries = [c for c, _ in self.CANARIES]
        # canaries first, in the middle and last, so a lab that stops recording midway is noticed
        mid = len(texts) // 2
        k = len(canaries)
        batch = canaries + list(texts[:mid]) + canaries + list(texts[mid:]) + canaries
        canary_pos = set(range(k)) | set(range(k + mid, 2 * k + mid)) | set(range(2 * k + len(texts), 3 * k + len(texts)))
        bash = self._run_pass(self.run_bash_ex, "bash", batch, canary_pos)
        pwsh = self._run_pass(self.run_pwsh_ex, "pwsh", batch, canary_pos)
        for shell, records in (("bash", bash), ("pwsh", pwsh)):
            for pos in (0, k + mid, 2 * k + len(texts)):
                for (text, want), got in zip(self.CANARIES, records[pos:pos + k]):
                    if shell == "bash" and text.startswith("&"):
                        continue
                    self.canary_checks += 1
                    if not self._canary_ok(got, want):
                        self.canary_failures.append((shell, text))
        strip = lambda rs: rs[k:k + mid] + rs[2 * k + mid:2 * k + len(texts)]
        return strip(bash), strip(pwsh)


# --------------------------------------------------------------------------------------------
# What a recorded stand-in start means: which PPDS program, with which argument vector
# --------------------------------------------------------------------------------------------

_DOTNET_RUN_VALUE = {"-c", "--configuration", "-f", "--framework", "-r", "--runtime", "-a", "--arch", "--os",
                     "-e", "--environment", "--launch-profile", "-lp", "-v", "--verbosity", "--property", "-p:",
                     "--artifacts-path", "--file"}
_DOTNET_RUN_FLAGS = {"--no-build", "--no-restore", "--no-launch-profile", "--interactive", "--force",
                     "--no-dependencies", "--disable-build-servers", "--no-cache", "--tl", "-q", "--quiet",
                     "--no-hot-reload", "--non-interactive"}
_HOST_VALUE_OPTIONS = {"--additionalprobingpath", "--additional-deps", "--depsfile", "--fx-version", "--roll-forward",
                       "--runtimeconfig", "--roll-forward-on-no-candidate-fx"}
_HOST_FLAGS = {"-d", "--diagnostics"}
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
    # host options before the verb or the .dll (dotnet [host-options] path.dll / exec)
    while argv and argv[0].startswith("-") and argv[0].lower() in _HOST_VALUE_OPTIONS | _HOST_FLAGS:
        argv = argv[2:] if argv[0].lower() in _HOST_VALUE_OPTIONS else argv[1:]
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
        cwd = record.get("cwd", "").replace("\\", "/")
        if project is None:
            project = cwd
        elif not project.startswith("/"):
            # a relative project (``--project .`` after ``cd src/PPDS.Cli``) is relative to the cwd
            project = os.path.normpath(os.path.join(cwd, project)).replace("\\", "/")
        program = _project_program(project) if project else None
        if program:
            return [(program, _app_args_after_run(rest), False)]
        return []
    if verb in ("exec",):
        rest = [w for w in rest]
        while rest and rest[0].startswith("-"):
            rest = rest[2:] if rest[0].lower() in _HOST_VALUE_OPTIONS else rest[1:]
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
