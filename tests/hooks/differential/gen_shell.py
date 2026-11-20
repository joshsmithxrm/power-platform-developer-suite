"""Generator for the statement-level oracle (oracle 2).

Composes command texts from a fixed, harmless vocabulary: the stand-in names, ``echo``, ``true``,
``cd`` into folders of the sandbox, variable assignment and expansion, quoting and escaping forms,
separators and control operators, subshells, command substitution, here-documents and here-strings,
``eval``, ``bash -c``, ``sh -c``, ``pwsh -c`` / ``-Command`` / ``-EncodedCommand``, call operators,
``env``, ``timeout``, ``nohup``, ``xargs``, ``command``, ``exec``, functions, splatting, line
continuations, comments and path spellings of the stand-in. No deletion, no network, nothing that
writes outside the sandbox (the only redirection target is a relative file in the sandbox folder).

A text is written in Bash style, PowerShell style or a mix; real shells decide what it means.
"""
from __future__ import annotations

import base64
import random
import re
from dataclasses import dataclass

# Cores: (label, program, argv). ``argv`` is what the program would receive in the plain form.
CORES = [
    ("write", "ppds", ["data", "delete", "--entity", "account", "--filter", "name eq 'x'"]),
    ("write", "ppds", ["solutions", "publish"]),
    ("write", "ppds", ["plugins", "deploy", "--config", "f.txt"]),
    ("write-sql", "ppds", ["query", "sql", "DELETE FROM account WHERE name = 'x'"]),
    ("switch", "ppds", ["env", "select", "--environment", "zz-prod-x"]),
    ("switch", "ppds", ["org", "select", "-env", "zz-prod-y"]),
    ("bare", "ppds", []),
    ("read", "ppds", ["solutions", "list"]),
    ("read", "ppds", ["query", "sql", "SELECT name FROM account"]),
    ("read-switch", "ppds", ["env", "select", "--environment", "zz-safe-b"]),
    ("mcp", "ppds-mcp-server", []),
    ("mcp-read", "ppds-mcp-server", ["--read-only"]),
    ("override", "ppds", ["solutions", "list", "--environment", "zz-prod-x"]),
    ("override", "ppds", ["solutions", "list", "--profile", "p-zz-prod-x"]),
    ("profile-switch", "ppds", ["auth", "select", "--name", "p-zz-prod-x"]),
    ("mcp-override", "ppds-mcp-server", ["--read-only", "--environment", "zz-prod-x"]),
]

# Process-environment overrides a text may put in front of a statement (configuration folder, profile).
OVERRIDE_WRAPS = [
    lambda s: f"PPDS_CONFIG_DIR=other {s}",
    lambda s: f"env PPDS_CONFIG_DIR=other {s}",
    lambda s: f"export PPDS_CONFIG_DIR=other; {s}",
    lambda s: f"$env:PPDS_CONFIG_DIR = 'other'; {s}",
    lambda s: f"PPDS_PROFILE=p-zz-prod-x {s}",
    lambda s: f"$env:PPDS_PROFILE = 'p-zz-prod-x'; {s}",
    lambda s: f"env PPDS_PROFILE=2 {s}",
]

# The class behind "env FOO=1 bash <<< '$(echo ppds) ...'": wrappers and assignment prefixes in front of
# every way of feeding code to a shell, with the program name literal, quoted, from a variable and from
# a substitution.
FEED_PREFIXES = ("", "env ", "env FOO=1 ", "FOO=1 ", "timeout 5 ", "nohup ", "command ", "exec ",
                 "env FOO=1 timeout 5 ", "PPDS_CONFIG_DIR=other ")
FEED_POSIX_FORMS = ("ppds", '"ppds"', "\\ppds", "P=ppds; $P", "$(echo ppds)", "`echo ppds`")
FEED_PWSH_FORMS = ("ppds", "& 'ppds'", '& "ppds"', "$P = 'ppds'; & $P", "& (echo ppds)")
FEED_CORES = (["data", "delete", "--entity", "account"], ["env", "select", "--environment", "zz-prod-x"])


def feed_texts(rng: random.Random, limit=None) -> list:
    out = []
    for core in FEED_CORES:
        args = " ".join(core)
        for pre in FEED_PREFIXES:
            for sh in ("bash", "sh"):
                for form in FEED_POSIX_FORMS:
                    c = f"{form} {args}"
                    for name, text in (
                            ("here-string", f"{pre}{sh} <<< {bq(c)}"),
                            ("here-doc", f"{pre}{sh} <<'EOF'\n{c}\nEOF"),
                            ("here-doc-unquoted", f"{pre}{sh} <<EOF\n{c}\nEOF"),
                            ("dash-c", f"{pre}{sh} -c {bq(c)}"),
                            ("stdin-echo", f"echo {bq(c)} | {pre}{sh}"),
                            ("stdin-printf", f"printf '%s\\n' {bq(c)} | {pre}{sh}")):
                        out.append(Text(text, "feed-" + core[0], "ppds", core, f"feed:{name}:{sh}:{pre.strip() or '-'}:{form}"))
            for form in FEED_PWSH_FORMS:
                c = f"{form} {args}"
                for name, text in (
                        ("dash-c", f"{pre}pwsh -NoProfile -c {bq(c)}"),
                        ("dash-command", f"{pre}pwsh -NoProfile -Command {bq(c)}"),
                        ("stdin-echo", f"echo {bq(c)} | {pre}pwsh -NoProfile -Command -"),
                        ("here-doc", f"{pre}pwsh -NoProfile -Command - <<'EOF'\n{c}\nEOF")):
                    out.append(Text(text, "feed-" + core[0], "ppds", core, f"feed:{name}:pwsh:{pre.strip() or '-'}:{form}"))
    if limit is not None and limit < len(out):
        keep = set(rng.sample(range(len(out)), limit))
        out = [t for i, t in enumerate(out) if i in keep]
    return out

_SAFE = re.compile(r"^[A-Za-z0-9_./=-]+$")


def bq(s: str) -> str:
    return "'" + s.replace("'", "'\\''") + "'"


def pq(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def _quote_word(w: str, rng: random.Random) -> str:
    r = rng.random()
    if _SAFE.match(w) and r < 0.45:
        return w
    if r < 0.6:
        return bq(w) if "'" not in w or rng.random() < 0.5 else pq(w)
    if r < 0.7:
        return '"' + re.sub(r'(["$`\\])', r"\\\1", w) + '"'          # bash double quotes
    if r < 0.78:
        return '"' + re.sub(r'(["$`])', r"`\1", w) + '"'             # PowerShell double quotes
    if r < 0.86 and _SAFE.match(w) and len(w) > 2:
        i = rng.randrange(1, len(w))
        return w[:i] + rng.choice(['""', "''"]) + w[i:]              # empty quotes inside the word
    if r < 0.93 and _SAFE.match(w) and len(w) > 2:
        i = rng.randrange(1, len(w))
        return w[:i] + rng.choice(["\\", "`"]) + w[i:]               # an escape inside the word
    return bq(w)


def _args_text(argv: list, rng: random.Random) -> str:
    return " ".join(_quote_word(w, rng) for w in argv)


def _program_word(program: str, bindir: str, rng: random.Random) -> tuple:
    """(prefix statements, program word)."""
    r = rng.random()
    p = program
    choices = [
        ("", p), ("", p), ("", p),
        ("", bq(p)), ("", '"' + p + '"'),
        ("", p[:2] + "''" + p[2:]), ("", p[:2] + '""' + p[2:]),
        ("", "\\" + p), ("", p[:2] + "\\" + p[2:]), ("", p[:2] + "`" + p[2:]),
        ("", p + ".exe"), ("", p.upper()),
        ("", f"{bindir}/{p}"), ("", f'"{bindir}/{p}"'),
        (f"cd {bindir}; ", f"./{p}"),
        ("", f'"$SB/../standin-bin/{p}"'), ("", f'"$env:SB/../standin-bin/{p}"'),
        (f"P={p}; ", "$P"), (f"P={p[:2]}; ", "${P}" + p[2:]),
        (f"$P = '{p}'; ", "& $P"), ("", f"& '{p}'"), ("", f"& \"{p}\""), ("", f". {p}"),
        ("", f"$(echo {p})"), ("", f"`echo {p}`"), ("", f"& (echo {p})"),
        ("", "${P:-" + p + "}"), (f"set -- {p}; ", '"$1"'),
        ("", f"pp{{d,x}}s".replace("x", "q") if p == "ppds" else p),   # brace expansion: ppds ppqs
        ("", "pp?s" if p == "ppds" else p),                             # glob, matches only in the bin folder
    ]
    return rng.choice(choices)


# Wrappers take an inner statement text and return a larger one.
def _wrappers(bindir: str):
    def enc(s):
        return base64.b64encode(s.encode("utf-16-le")).decode("ascii")
    return [
        lambda s, r: f"true; {s}",
        lambda s, r: f"true && {s}",
        lambda s, r: f"false || {s}",
        lambda s, r: f"echo start\n{s}",
        lambda s, r: f"echo x | {s}",
        lambda s, r: f"{s} | cat",
        lambda s, r: f"{s} 2>&1",
        lambda s, r: f"{s} > out.txt",
        lambda s, r: f"{s} &\nwait",
        lambda s, r: f"( {s} )",
        lambda s, r: f"{{ {s}; }}",
        lambda s, r: f'echo "$( {s} )"',
        lambda s, r: f"echo `{s}`",
        lambda s, r: f"eval {bq(s)}",
        lambda s, r: f'eval "{s}"' if '"' not in s and "$" not in s else f"eval {bq(s)}",
        lambda s, r: f"bash -c {bq(s)}",
        lambda s, r: f"sh -c {bq(s)}",
        lambda s, r: f"bash <<'EOF'\n{s}\nEOF",
        lambda s, r: f"cat <<'EOF' | bash\n{s}\nEOF",
        lambda s, r: f"bash <<< {bq(s)}",
        lambda s, r: f"env {s}" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"env FOO=1 {s}" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"timeout 5 {s}" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"nohup {s} > nohup.txt 2>&1" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"command {s}" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"exec {s}" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"FOO=1 {s}" if not s.startswith(("&", "$", "(", "{", ".", "P=", "cd ", "set ")) else s,
        lambda s, r: f"f() {{ {s}; }}; f",
        lambda s, r: f"# note\n{s}",
        lambda s, r: f"{s} # note",
        lambda s, r: f"pwsh -NoProfile -c {pq(s)}",
        lambda s, r: f"pwsh -NoProfile -Command {pq(s)}",
        lambda s, r: f"pwsh -NoProfile -EncodedCommand {enc(s)}",
        lambda s, r: f"& {{ {s} }}",
        lambda s, r: f". {{ {s} }}",
        lambda s, r: f"$null = {s}",
        lambda s, r: f"Invoke-Expression {pq(s)}",
        lambda s, r: f"iex {pq(s)}",
        lambda s, r: f"{pq(s)} | iex",
        lambda s, r: f"$c = {pq(s)}; iex $c",
        lambda s, r: f"iex @'\n{s}\n'@",
        lambda s, r: f"if ($true) {{ {s} }}",
        lambda s, r: f"foreach ($i in 1) {{ {s} }}",
        lambda s, r: f"function f {{ {s} }}; f",
        lambda s, r: f"{s} | Out-Null",
        lambda s, r: f"$x = $({s})",
        lambda s, r: f"Set-Location {bindir}/..; {s}",
        lambda s, r: f"cd .; {s}",
    ]


def _continuation(s: str, rng: random.Random) -> str:
    spaces = [m.start() for m in re.finditer(" ", s)]
    if not spaces:
        return s
    i = rng.choice(spaces)
    return s[:i] + rng.choice([" \\\n", " `\n", " \\\r\n", " `\r\n"]) + s[i + 1:]


@dataclass
class Text:
    text: str
    core: str
    program: str
    argv: list
    recipe: str


def _core_statement(label, program, argv, bindir, rng) -> tuple:
    """A statement that starts the program, in one of many forms. Returns (text, recipe)."""
    r = rng.random()
    args = _args_text(argv, rng)
    if program == "ppds" and r < 0.08:
        form = rng.choice([
            f"dotnet run --project src/PPDS.Cli -- {args}",
            f"dotnet run --project src/PPDS.Cli {args}",
            f"dotnet run --project=src/PPDS.Cli -- {args}",
            f"dotnet watch run --project src/PPDS.Cli -- {args}",
            f"dotnet run -p src/PPDS.Cli --tl off -- {args}",
            f"cd src/PPDS.Cli && dotnet run -- {args}",
            f"cd src/PPDS.Cli; dotnet run -- {args}",
            f"Set-Location src/PPDS.Cli; dotnet run -- {args}",
            f"pushd src/PPDS.Cli && dotnet run -- {args}",
            f"dotnet src/PPDS.Cli/bin/ppds.dll {args}",
            f"dotnet exec out/ppds.dll {args}",
            f"dotnet ppds {args}",
            f"dotnet tool exec PPDS.Cli -- {args}",
            f"dnx PPDS.Cli --yes -- {args}",
            f"dnx PPDS.Cli@1.0.0 -- {args}",
            f"dotnet dnx PPDS.Cli -- {args}",
        ])
        return form, "host"
    if program == "ppds-mcp-server" and r < 0.08:
        return rng.choice([f"dotnet run --project src/PPDS.Mcp -- {args}", f"dnx PPDS.Mcp -- {args}",
                           f"cd src/PPDS.Mcp && dotnet run -- {args}"]), "host"
    if argv and r < 0.14:
        return rng.choice([
            f"echo {args} | xargs {program}",
            f"printf '%s\\n' {args} | xargs {program}",
            f"a=({args}); {program} \"${{a[@]}}\"",
            f"$a = @({', '.join(pq(w) for w in argv)}); {program} @a",
            f"{program} $(echo {argv[0]}) {_args_text(argv[1:], rng)}",
            f"{program} (echo {argv[0]}) {_args_text(argv[1:], rng)}",
            f"set -- {args}; {program} \"$@\"",
            f"X={bq(argv[-1])}; {program} {_args_text(argv[:-1], rng)} \"$X\"",
            f"$X = {pq(argv[-1])}; {program} {_args_text(argv[:-1], rng)} $X",
        ]), "args-elsewhere"
    pre, word = _program_word(program, bindir, rng)
    return f"{pre}{word} {args}".rstrip(), "program-word"


def generate(size: int, seed: int, bindir: str, feed_limit=None) -> list:
    """``size`` random compositions, after the plain cores and the shell-feed block (all of it, or a
    seeded sample of ``feed_limit`` texts)."""
    rng = random.Random(seed)
    wrappers = _wrappers(bindir)
    out = feed_texts(random.Random(seed + 1), feed_limit)
    out += round4_texts(random.Random(seed + 2), None if feed_limit is None else ROUND4_CI_LIMIT)
    size += len(out)
    # Every core in its plain form first, in both quoting styles.
    for label, program, argv in CORES:
        out.append(Text(f"{program} {_args_text(argv, random.Random(0))}".rstrip(), label, program, argv, "plain"))
    while len(out) < size:
        label, program, argv = rng.choice(CORES)
        text, recipe = _core_statement(label, program, argv, bindir, rng)
        steps = [recipe]
        for _ in range(rng.choice((0, 1, 1, 1, 2, 2, 3))):
            w = rng.randrange(len(wrappers))
            text = wrappers[w](text, rng)
            steps.append(f"w{w}")
        if rng.random() < 0.12:
            text = rng.choice(OVERRIDE_WRAPS)(text)
            steps.append("override")
        if rng.random() < 0.15:
            text = _continuation(text, rng)
            steps.append("continuation")
        out.append(Text(text, label, program, argv, "+".join(steps)))
    return out


# --------------------------------------------------------------------------------------------
# Round 4: classes a review by hand found (each text's recipe starts with its class name)
# --------------------------------------------------------------------------------------------

R4_CORES = (
    ("write", ["data", "truncate", "--entity", "account", "--force"]),
    ("write", ["data", "delete", "--entity", "account", "--filter", "name eq 'x'"]),
    ("switch", ["env", "select", "--environment", "zz-prod-x"]),
    ("target", ["solutions", "list", "--environment", "zz-prod-x"]),
    ("version", ["version", "--update"]),
    ("profile", ["solutions", "list", "--profile", "p-zz-prod-x"]),
)
XARGS_OPTS = ("", "-n 4", "-n 9", "-n 1", "-L 1", "-P 2 -n 4", "-r", "-t", "--max-args=9", "-n9", "-L1 -P 2",
              "-s 4096", "-x -n 9")
APPROX_WRAPPER_WORDS = ("ssh", "parallel", "busybox", "winpty", "xvfb-run")


def _stdin_feeds(words: list) -> list:
    joined = " ".join(words)
    nl = "\\n".join(words)
    return [f"echo {joined}", f"printf '%s\\n' {joined}", f"printf '{nl}\\n'", f"echo -e '{nl}'"]


def xargs_texts() -> list:
    out = []
    for label, argv in R4_CORES:
        for k in range(1, len(argv) + 1):
            head, tail = argv[:k - 1], argv[k - 1:]
            if any(" " in w for w in tail):
                tail = [bq(w) if " " in w else w for w in tail]
            prog = " ".join(["ppds"] + head)
            for opts in XARGS_OPTS:
                x = f"xargs {opts} ".replace("  ", " ")
                for i, feed in enumerate(_stdin_feeds(tail)):
                    out.append(Text(f"{feed} | {x}{prog}", "xargs-" + label, "ppds", argv,
                                    f"xargs:{opts or '-'}:k{k}:feed{i}"))
                out.append(Text(f"{x}{prog} <<< {bq(' '.join(tail))}", "xargs-" + label, "ppds", argv,
                                f"xargs:{opts or '-'}:k{k}:here-string"))
            # -I: the last word arrives on stdin and replaces {}
            out.append(Text(f"echo {argv[-1]} | xargs -I{{}} {' '.join(['ppds'] + [bq(w) if ' ' in w else w for w in argv[:-1]])} {{}}",
                            "xargs-" + label, "ppds", argv, f"xargs:-I:last"))
            # GNU parallel (approximated stand-in): input lines as arguments, -n groups them
            n = len(tail)
            out.append(Text(f"printf '%s\\n' {' '.join(tail)} | parallel -j 4 -n {n} {prog}", "xargs-" + label,
                            "ppds", argv, f"xargs:parallel-n:k{k}:approx"))
            out.append(Text(f"parallel -j 4 {prog} {' '.join(tail[:-1])} ::: {tail[-1]}", "xargs-" + label,
                            "ppds", argv, f"xargs:parallel-:::k{k}:approx"))
    return out


def hidden_texts() -> list:
    """Switch, target or write words that are in the text but not as whole words."""
    t = []
    for label, argv in R4_CORES:
        nl = "\\n".join(argv)
        tab = "\\t".join(argv)
        hx = "\\x0a".join(argv)
        t += [(f"printf '{nl}\\n' | xargs ppds", "printf-nl"), (f"printf '{tab}' | xargs ppds", "printf-tab"),
              (f"printf '{hx}' | xargs ppds", "printf-hex"), (f"echo -e '{nl}' | xargs ppds", "echo-e"),
              (f"printf '{nl}' | xargs -n {len(argv)} ppds", "printf-nl-n")]
        words = [bq(w) if " " in w else w for w in argv]
        for i, w in enumerate(argv):
            if w.startswith("-") or w in ("select", "truncate", "delete", "version", "env"):
                rest = words[:i] + ["$E"] + words[i + 1:]
                t.append((f"E={w}; ppds {' '.join(rest)}", "assign"))
                t.append((f"E={w} && ppds {' '.join(rest)}", "assign-and"))
                t.append((f"export E={w}; bash -c {bq('ppds ' + ' '.join(rest))}", "assign-export"))
                prest = [pq(x) if " " in x else x for x in argv[:i]] + ["$E"] + [pq(x) if " " in x else x for x in argv[i + 1:]]
                t.append((f"$E = '{w}'; ppds {' '.join(prest)}", "assign-pwsh"))
                if len(w) > 3:
                    j = len(w) // 2
                    t.append((f"ppds {' '.join(words[:i] + [w[:j] + chr(92) + chr(10) + w[j:]] + words[i + 1:])}", "continuation"))
                    t.append((f"ppds {' '.join(words[:i] + ['{' + w + ',}'] + words[i + 1:])}", "brace"))
                    t.append((f"ppds {' '.join(words[:i] + [w[:j] + '{' + w[j:] + ',}'] + words[i + 1:])}", "brace-inner"))
        t.append((f"W={bq(' '.join(words))}; ppds $W", "split-variable"))
        t.append((f"$w = {', '.join(pq(x) for x in argv)}; ppds @w", "splat"))
        t.append((f"{{ppds,}} {' '.join(words)}", "brace-program"))
    return [Text(text, "hidden-" + core, "ppds", [], f"hidden:{kind}") for (text, kind), core in
            zip(t, [c for c in _cycle_labels(t)])]


def _cycle_labels(t):
    # labels are informational; keep them aligned with the core that produced each text
    out, per = [], []
    for label, argv in R4_CORES:
        per.append(label)
    n = len(t) // len(R4_CORES) or 1
    for i in range(len(t)):
        out.append(per[min(i // n, len(per) - 1)])
    return out


WRAPPERS = ("setsid", "setsid -f", "flock lock.txt", "chroot /", "busybox", "xvfb-run -a", "winpty", "stdbuf -o0",
            "stdbuf -oL -eL", "nice", "nice -n 5", "ionice -c 3", "ssh host", "ssh -o BatchMode=yes host")


def wrapper_texts() -> list:
    out = []
    for label, argv in R4_CORES[:3] + R4_CORES[4:5]:
        args = " ".join(bq(w) if " " in w else w for w in argv)
        for form in ("ppds", "$(echo ppds)", "P=ppds; $P"):
            code = f"{form} {args}"
            for w in WRAPPERS:
                approx = w.split()[0] in APPROX_WRAPPER_WORDS
                sh = "sh" if w == "busybox" else "bash"
                cases = [(f"{w} {sh} -c {bq(code)}", "dash-c"), (f"{w} {sh} <<< {bq(code)}", "here-string"),
                         (f"echo {bq(code)} | {w} {sh}", "stdin"), (f"{w} pwsh -NoProfile -c {bq(code)}", "pwsh-c")]
                if form == "ppds":
                    cases.append((f"{w} {code}", "direct"))
                if w.startswith("ssh"):
                    cases.append((f"{w} {bq(code)}", "ssh-remote"))
                for text, kind in cases:
                    out.append(Text(text, "wrapper-" + label, "ppds", argv,
                                    f"wrapper:{w}:{kind}:{form}" + (":approx" if approx else "")))
            out.append(Text(f"script -qc {bq(code)} /dev/null", "wrapper-" + label, "ppds", argv, f"wrapper:script:dash-c:{form}"))
    return out


def home_texts() -> list:
    out = []
    for label, argv in (("read", ["solutions", "list"]), ("write", ["data", "delete", "--entity", "account"]),
                        ("switch", ["env", "select", "--environment", "zz-safe-b"])):
        args = " ".join(argv)
        for text, kind in ((f"HOME=other ppds {args}", "prefix"), (f"env HOME=other ppds {args}", "env"),
                           (f"export HOME=other; ppds {args}", "export"), (f"$env:HOME = 'other'; ppds {args}", "pwsh"),
                           (f"HOME=other bash -c {bq('ppds ' + args)}", "shell"), (f"cd d && HOME=. ppds {args}", "relative"),
                           (f'HOME="$HOME" ppds {args}', "same-home"), (f"HOME=$PWD ppds {args}", "pwd"),
                           (f"USERPROFILE=other ppds {args}", "userprofile"), (f"XDG_CONFIG_HOME=other ppds {args}", "xdg")):
            out.append(Text(text, "home-" + label, "ppds", argv, f"home:{kind}"))
    return out


ROUND4_CI_LIMIT = 400


def round4_texts(rng: random.Random, limit=None) -> list:
    out = xargs_texts() + hidden_texts() + wrapper_texts() + home_texts()
    if limit is not None and limit < len(out):
        # keep every hidden and home text (small), sample the large xargs and wrapper blocks
        small = [t for t in out if t.recipe.startswith(("hidden:", "home:"))]
        big = [t for t in out if not t.recipe.startswith(("hidden:", "home:"))]
        out = small + rng.sample(big, max(0, limit - len(small)))
    return out
