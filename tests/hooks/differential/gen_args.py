"""Generator for the argument-level oracle (oracle 1).

Every command path of the CLI's real root command (from the parse oracle's ``tree``) is crossed with
the argument shapes that decide what runs and what binds: options before and after the subcommand,
each value delimiter (space, ``=``, ``:``), aliases, a value option followed by a flag (the flag is
swallowed as the value), flags with attached values, bundled short options, ``--`` in the middle,
response-file tokens, no arguments at all, help flags in every position; for ``query sql`` a corpus
of statement texts; for the switch command and target options, listed and unlisted names. The MCP
server gets the same treatment over its own options.

Nothing here encodes what the hook does. A case is only an argument vector and the words that start
the program; ground truth comes from the parse oracle, the verdict from the hook.
"""
from __future__ import annotations

import random
import re
from dataclasses import dataclass, field

from _harness import LISTED, RESPONSE_FILES, UNLISTED

HELP = ("--help", "-h", "-?", "/?", "/h")
DELIMS = (" ", "=", ":")
NAMES = LISTED + UNLISTED

# Words that start the program. The hook must read each the same way as plain ``ppds``.
CLI_PREFIXES = (
    ("ppds",),
    ("ppds",),
    ("ppds",),
    ("ppds.exe",),
    ("dotnet", "run", "--project", "src/PPDS.Cli", "--"),
    ("dotnet", "run", "--project", "src/PPDS.Cli/PPDS.Cli.csproj", "-c", "Release", "--"),
    ("dnx", "PPDS.Cli", "--yes", "--"),
    ("dotnet", "tool", "exec", "PPDS.Cli", "--"),
)
MCP_PREFIXES = (
    ("ppds-mcp-server",),
    ("ppds-mcp-server",),
    ("dotnet", "run", "--project", "src/PPDS.Mcp", "--"),
)

SQL_READS = [
    "SELECT name FROM account",
    "SELECT TOP 5 accountid, name FROM account WHERE name LIKE 'a%'",
    "SELECT name FROM account -- trailing comment",
    "SELECT 'delete' AS word FROM account",
    "SELECT name FROM account WHERE description = 'update me; drop table x'",
    "SELECT [delete] FROM account",
    "SELECT name AS deleted_on FROM account",
    "/* insert */ SELECT name FROM account",
    "DECLARE @n INT = 1; SELECT @n",
    "SELECT COUNT(*) FROM contact",
]
SQL_WRITES = [
    "DELETE FROM account WHERE name = 'x'",
    "DELETE account WHERE name = 'x'",
    "UPDATE account SET name = 'y' WHERE name = 'x'",
    "INSERT INTO account (name) VALUES ('x')",
    "INSERT account (name) VALUES ('x')",
    "MERGE account AS t USING (SELECT 1 AS id) AS s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (name) VALUES ('x');",
    "TRUNCATE TABLE account",
    "WITH x AS (SELECT accountid FROM account) DELETE FROM account WHERE accountid IN (SELECT accountid FROM x)",
    "SELECT name INTO #t FROM account",
    "EXEC ('DELETE FROM account')",
    "IF 1 = 1 DELETE FROM account",
    "BEGIN DELETE FROM account END",
]
# Ways to put text in front of, around or into a statement. Applied to reads and writes alike.
SQL_WRAPS = [
    lambda s: s,
    lambda s: "-- note\n" + s,
    lambda s: "--note\r\n" + s,
    lambda s: "-- a\n-- b\n" + s,
    lambda s: "/* note */ " + s,
    lambda s: "/*x*/" + s,
    lambda s: " \n\t" + s,
    lambda s: "SELECT 1; " + s,
    lambda s: "SELECT 1\n" + s,
    lambda s: "SELECT 1" + s,          # glued to a digit
    lambda s: "SELECT 1." + s,         # glued to a float
    lambda s: "SELECT 0x1" + s,        # glued to a binary literal
    lambda s: "SELECT 'a'" + s,        # glued to a string
    lambda s: "SELECT [a]" + s,        # glued to a bracketed name
    lambda s: "SELECT 1 AS a " + s,  # no-break space
    lambda s: s.lower(),
    lambda s: s.replace(" ", "\n"),
    lambda s: s.replace(" ", "/**/"),
    lambda s: "(" + s + ")" if s.startswith("SELECT") else s,
    lambda s: "-" + s,
    lambda s: ";" + s,
]


@dataclass
class Case:
    program: str          # "ppds" or "ppds-mcp-server"
    argv: list            # what the program receives
    shape: str
    prefix: tuple = ("ppds",)
    note: str = field(default="")

    def words(self) -> list:
        return list(self.prefix) + list(self.argv)


_REQ_OPT = re.compile(r"Option '([^']+)' is required")
_REQ_ARG = re.compile(r"Required argument missing")


def _value_for(opt: dict, rng: random.Random) -> list:
    t = opt.get("type", "String")
    if opt.get("max", 1) == 0 or t in ("Boolean", "Void"):
        return []
    if opt.get("enum"):
        return [opt["enum"][0]]
    if opt["name"] in ("--environment", "--env"):
        return [rng.choice(NAMES)]
    return {"Int32": ["1"], "Guid": ["00000000-0000-0000-0000-000000000001"], "FileInfo": ["f.txt"],
            "DirectoryInfo": ["d"], "DirectoryInfo[]": ["d"]}.get(t, ["x1"])


class Tree:
    def __init__(self, commands: list):
        self.commands = commands
        self.by_path = {tuple(c["path"]): c for c in commands}
        self.runnable = [c for c in commands if c["has_action"] and c["path"]]

    def options(self, path) -> list:
        return self.by_path[tuple(path)]["options"]

    def option(self, path, name):
        return next((o for o in self.options(path) if o["name"] == name), None)


def complete(oracle, path: list, extra: list, rng: random.Random, tree: Tree) -> list:
    """Add what the real parser says is required, so the vector actually runs (up to 6 rounds)."""
    argv = list(path) + list(extra)
    for _ in range(6):
        t = oracle.cli(argv)
        errs = t.get("errors", [])
        if not errs:
            return argv
        changed = False
        for e in errs:
            m = _REQ_OPT.search(e)
            if m:
                opt = tree.option(path, m.group(1)) or {"name": m.group(1), "type": "String", "max": 1}
                argv += [opt["name"]] + _value_for(opt, rng)
                changed = True
            elif _REQ_ARG.search(e):
                argv.insert(len(path), "x1")
                changed = True
        if not changed:
            return argv
    return argv


def _spell(opt_name: str, alias_pool: list, rng: random.Random) -> str:
    return rng.choice([opt_name] + [a for a in alias_pool if not a.startswith("/")])


def _with_value(name: str, value: str, delim: str) -> list:
    return [name, value] if delim == " " else [name + delim + value]


def generate(oracle, size: int, seed: int) -> list:
    """Deterministic list of Cases. ``size`` scales the random part; the systematic part is fixed."""
    rng = random.Random(seed)
    tree = Tree(oracle.tree())
    cases: list = []

    def add(argv, shape, program="ppds", note=""):
        prefixes = CLI_PREFIXES if program == "ppds" else MCP_PREFIXES
        cases.append(Case(program, list(argv), shape, rng.choice(prefixes), note))

    # -- no arguments at all, and only a help flag
    for prefix in CLI_PREFIXES:
        cases.append(Case("ppds", [], "bare", prefix))
    for prefix in MCP_PREFIXES:
        cases.append(Case("ppds-mcp-server", [], "bare", prefix))
    for h in HELP + ("--version",):
        add([h], "root-flag")

    # -- every runnable path, completed until the real parser accepts it
    base = {}
    for c in tree.runnable:
        path = c["path"]
        argv = complete(oracle, path, [], rng, tree)
        base[tuple(path)] = argv
        add(argv, "path")
        # help flag at every position (and as a token glued to the last one)
        for i in range(1, len(argv) + 1):
            if rng.random() < 0.35 or i == len(argv):
                add(argv[:i] + [rng.choice(HELP)] + argv[i:], "help-position")
        # aliases of the group
        for depth in range(len(path)):
            node = tree.by_path.get(tuple(path[: depth + 1]))
            for alias in (node or {}).get("aliases", []):
                add(path[:depth] + [alias] + argv[depth + 1:], "command-alias")

    paths = list(base)

    # -- the target options with every delimiter and alias, before and after the subcommand
    for path in paths:
        opt = tree.option(path, "--environment")
        prof = tree.option(path, "--profile")
        for o in (opt, prof):
            if not o:
                continue
            spellings = [o["name"]] + [a for a in o["aliases"] if not a.startswith("/")]
            for spelling in spellings:
                for delim in DELIMS:
                    if rng.random() > (0.9 if path[-1] in ("select",) else 0.25):
                        continue
                    name = rng.choice(NAMES)
                    argv = [a for a in base[path]]
                    # drop a generated value of the same option, so there is only one
                    argv = _drop_option(argv, o)
                    add(argv + _with_value(spelling, name, delim), f"target{delim.strip() or 'space'}", note=spelling)
                    if len(path) > 1:
                        # before the last word of the command path (a parent position)
                        add(list(path[:-1]) + _with_value(spelling, name, delim) + argv[len(path) - 1:],
                            "target-before-subcommand", note=spelling)

    # -- the switch command specifically: every delimiter, alias, repeated, empty, odd spacing
    for group in ("env", "org"):
        for spelling in ("--environment", "-env"):
            for delim in DELIMS + ("= ", ": "):
                for name in NAMES:
                    if delim.endswith(" ") and delim != " ":
                        argv = [group, "select", spelling + delim.strip(), name]
                    else:
                        argv = [group, "select"] + _with_value(spelling, name, delim)
                    add(argv, "switch")
            add([group, "select", spelling, rng.choice(UNLISTED), spelling, rng.choice(LISTED)], "switch-repeated")
            add([group, "select", spelling + "=" + rng.choice(UNLISTED) + "," + rng.choice(LISTED)], "switch-comma")
        add([group, "select"], "switch-no-target")
        add([group, "select", "--environment", ""], "switch-empty")
        add([group, "select", "--", "--environment", rng.choice(UNLISTED)], "switch-after-dashdash")

    # -- a value option followed by a flag: the flag becomes the value
    flags_of_interest = ("--dry-run", "--help", "--environment", "--yes", "--confirm")
    for path in paths:
        values = [o for o in tree.options(path) if o["max"] >= 1 and o["type"] in ("String", "String[]")
                  and o["name"] not in ("--environment", "--profile")]
        flags = [o for o in tree.options(path) if o["name"] in flags_of_interest]
        if not values or not flags:
            continue
        for _ in range(2 if path != ("plugins", "deploy") else 12):
            v = rng.choice(values)
            f = rng.choice(flags)
            add(_drop_option(_drop_option(list(base[path]), v), f) + [_spell(v["name"], v["aliases"], rng), f["name"]],
                "value-swallows-flag", note=f"{v['name']} {f['name']}")

    # -- flags with attached or separate values
    bool_values = ("true", "false", "False", " false", "false ", "0", "1", "no", "")
    for path in paths:
        for o in tree.options(path):
            if o["type"] != "Boolean" or o["name"] not in ("--dry-run", "--update", "--yes", "--confirm", "--list", "--show"):
                continue
            for _ in range(6 if o["name"] == "--dry-run" else 2):
                val = rng.choice(bool_values)
                delim = rng.choice(DELIMS)
                add(_drop_option(list(base[path]), o) + _with_value(o["name"], val, delim), "flag-value",
                    note=o["name"])
            add(_drop_option(list(base[path]), o) + [o["name"], o["name"] + "=false"], "flag-repeated", note=o["name"])

    # -- bundled short options
    for path in paths:
        shorts = [a for o in tree.options(path) if o["max"] == 0 or o["type"] == "Boolean"
                  for a in o["aliases"] if re.fullmatch(r"-[A-Za-z]", a)]
        if len(shorts) >= 2 and rng.random() < 0.5:
            a, b = rng.sample(shorts, 2)
            add(list(base[path]) + ["-" + a[1] + b[1]], "bundled")
            add(list(base[path]) + [a + "true"], "short-attached")

    # -- ``--`` in the middle, a child named after a parent's argument, unknown words
    for path in paths:
        argv = base[path]
        if rng.random() < 0.3:
            i = rng.randrange(1, len(argv) + 1)
            add(argv[:i] + ["--"] + argv[i:], "dashdash")
        if len(path) > 1 and rng.random() < 0.3:
            add(list(path[:-1]) + ["--verbose"] + argv[len(path) - 1:], "parent-option")
            add(list(path[:-1]) + ["x1"] + argv[len(path) - 1:], "parent-argument")
        if rng.random() < 0.1:
            add([w.upper() if i < len(path) else w for i, w in enumerate(argv)], "case")

    # -- response files at every level
    for name in RESPONSE_FILES:
        add(["@" + name], "response-file")
        add(["env", "select", "@" + name], "response-file")
        add(["plugins", "deploy", "@" + name] + base.get(("plugins", "deploy"), [])[2:], "response-file")
        add(["data", "@" + name], "response-file")
        add(["query", "sql", "SELECT 1", "@" + name], "response-file")

    # -- query sql: statement corpus x wraps x option shapes
    sql_opts = [[], ["--output-format", "json"], ["-f", "json"], ["--top", "5"], ["--confirm"], ["--dry-run"],
                ["--explain"], ["--environment", LISTED[0]], ["--profile", "p-zz-safe-a"]]
    corpus = [(w(s), kind) for s, kind in [(s, "read") for s in SQL_READS] + [(s, "write") for s in SQL_WRITES]
              for w in SQL_WRAPS]
    rng.shuffle(corpus)
    for text, kind in corpus:
        opts = rng.choice(sql_opts)
        order = rng.random()
        if order < 0.5:
            argv = ["query", "sql", text] + opts
        elif order < 0.8:
            argv = ["query", "sql"] + opts + [text]
        else:
            argv = ["query"] + opts + ["sql", text]
        add(argv, f"sql-{kind}")
    for opt in ("--file", "--stdin"):
        add(["query", "sql", opt] + (["q.sql"] if opt == "--file" else []), "sql-elsewhere")

    # -- the MCP server's own options
    mcp_vals = ("--profile", "--environment", "--allowed-env", "--log-level")
    mcp_tail = ["--read-only", "--read-only=true", "--read-only:true", "--Read-Only", "--readonly", "--read-only ",
                "--version", "--", "-ro"]
    for _ in range(max(40, size // 10)):
        argv = []
        for _ in range(rng.randint(0, 4)):
            r = rng.random()
            if r < 0.4:
                argv += [rng.choice(mcp_vals)]
                if rng.random() < 0.7:
                    argv += [rng.choice(NAMES + ("p-zz-safe-a", "Debug"))]
            else:
                argv += [rng.choice(mcp_tail)]
        if rng.random() < 0.5:
            argv.insert(rng.randrange(len(argv) + 1), "--read-only")
        add(argv, "mcp", program="ppds-mcp-server")

    # -- random compositions over the real tree, to fill the requested size
    while len(cases) < size:
        path = rng.choice(paths)
        argv = list(base[path])
        for _ in range(rng.randint(1, 3)):
            op = rng.random()
            opts = tree.options(path)
            o = rng.choice(opts)
            if op < 0.35:
                ins = [o["name"]] + _value_for(o, rng) if o["max"] else [rng.choice([o["name"]] + o["aliases"])]
                at = rng.randrange(1, len(argv) + 1)
                argv[at:at] = ins
            elif op < 0.55 and o["max"]:
                f = rng.choice([x for x in opts if not x["max"]] or [o])
                argv += [o["name"], f["name"]]
            elif op < 0.7:
                argv.insert(rng.randrange(1, len(argv) + 1), rng.choice(HELP + ("--", "@sel.rsp")))
            elif op < 0.85 and o["max"]:
                argv += [o["name"] + rng.choice("=:") + _value_for(o, rng)[0] if _value_for(o, rng) else o["name"]]
            else:
                argv += [rng.choice(("--environment", "-env", "--profile")) + rng.choice((" ", "=", ":")).strip(),
                         rng.choice(NAMES)]
        add(argv, "random")
    return cases


def _drop_option(argv: list, opt: dict) -> list:
    names = {opt["name"], *opt.get("aliases", [])}
    out, skip = [], False
    for i, w in enumerate(argv):
        if skip:
            skip = False
            continue
        if w in names:
            skip = opt.get("max", 0) >= 1
            continue
        if any(w.startswith(n + d) for n in names for d in "=:"):
            continue
        out.append(w)
    return out


# --------------------------------------------------------------------------------------------
# Rendering an argument vector as command text for each tool
# --------------------------------------------------------------------------------------------

_BASH_SAFE = re.compile(r"^[A-Za-z0-9_./=:,+@%-]+$")
_PWSH_SAFE = re.compile(r"^[A-Za-z0-9_./=+%-]+$")


def render(words: list, tool: str) -> str:
    """Quote each word so that the tool's shell passes exactly this vector to the program."""
    out = []
    for i, w in enumerate(words):
        if tool == "Bash":
            out.append(w if w and _BASH_SAFE.match(w) and not w.startswith("@") else "'" + w.replace("'", "'\\''") + "'")
        else:
            # PowerShell: a single-quoted word is literal; '' inside stands for one quote.
            # Words starting with '-' or containing ':' or '@' are always quoted: PowerShell would
            # otherwise treat some of them as parameters or splats.
            plain = w and _PWSH_SAFE.match(w) and not w.startswith("-") and w != "--"
            out.append(w if (plain or i == 0) else "'" + w.replace("'", "''") + "'")
    return " ".join(out)
