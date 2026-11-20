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
]

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


def generate(size: int, seed: int, bindir: str) -> list:
    rng = random.Random(seed)
    wrappers = _wrappers(bindir)
    out = []
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
        if rng.random() < 0.15:
            text = _continuation(text, rng)
            steps.append("continuation")
        out.append(Text(text, label, program, argv, "+".join(steps)))
    return out
