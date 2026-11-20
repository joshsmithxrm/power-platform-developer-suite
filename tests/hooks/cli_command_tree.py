"""Derive the ppds CLI command tree from its source (read only).

``src/PPDS.Cli/Program.cs`` adds each top-level command with
``rootCommand.Subcommands.Add(Type.Create(...))``; a command adds its
children the same way or through a method of its own class
(``Subcommands.Add(CreateX(...))``). The name of the command a method builds
is the first ``new Command(...)`` in it (a ``cond ? "a" : "b"`` name yields
both); ``Aliases.Add("x")`` in the same method adds another spelling.

``command_paths()`` returns every path as a tuple of names, the aliases
included; ``problems`` lists anything the walk could not follow, so a test
can fail instead of silently missing a command.
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLI = ROOT / "src" / "PPDS.Cli"

_CLASS_RE = re.compile(r"\b(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
_ADD_RE = re.compile(r"Subcommands\.Add\(\s*(?:([A-Za-z_][A-Za-z0-9_]*)\.)?([A-Za-z_][A-Za-z0-9_]*)\s*\(")
_NEW_COMMAND_RE = re.compile(r"new\s+Command\s*\(")
_ALIAS_RE = re.compile(r"Aliases\.Add\(\s*\"([^\"]+)\"\s*\)")
_STRING_RE = re.compile(r"\"([^\"\\]*)\"")


def _strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.DOTALL)
    return re.sub(r"(?m)//[^\n]*$", "", text)


class _Source:
    def __init__(self):
        # class name -> [(namespace, usings, text)]; partial classes give several
        self.classes = {}
        for path in sorted(CLI.rglob("*.cs")):
            if any(part in ("bin", "obj") for part in path.parts):
                continue
            text = _strip_comments(path.read_text(encoding="utf-8-sig"))
            ns = re.search(r"\bnamespace\s+([\w.]+)", text)
            usings = set(re.findall(r"(?m)^\s*using\s+([\w.]+)\s*;", text))
            for m in _CLASS_RE.finditer(text):
                self.classes.setdefault(m.group(1), []).append((ns.group(1) if ns else "", usings, text))
        self.problems = []
        self.actions = set()
        self.overloads = []

    def texts(self, cls: str, ns: str, usings=frozenset()):
        """Source texts of ``cls`` as seen from namespace ``ns``."""
        entries = self.classes.get(cls, [])
        for pick in (lambda e: e[0] == ns, lambda e: e[0] in usings, lambda e: True):
            chosen = [e for e in entries if pick(e)]
            if chosen:
                return chosen
        return []

    def method(self, cls: str, name: str, ns: str = "", usings=frozenset()):
        """``(body, namespace, usings)`` of ``cls.name(...)``, the first
        overload that builds a command when there is one."""
        bodies = []
        for entry_ns, entry_usings, text in self.texts(cls, ns, usings):
            for m in re.finditer(r"\b" + re.escape(name) + r"\s*\(", text):
                head = text[max(0, m.start() - 200):m.start()]
                if not re.search(r"(?:static|public|private|internal|protected)[\w<>\[\],\s?]*$", head):
                    continue  # a call, not a definition
                body = _body(text, m.end())
                if body is not None:
                    bodies.append((body, entry_ns, entry_usings))
        self.overloads = bodies
        for found in bodies:
            if _NEW_COMMAND_RE.search(found[0]):
                return found
        return bodies[0] if bodies else (None, ns, usings)


def _body(text: str, start: int):
    """The text of the method whose parameter list starts at ``start``."""
    depth, i = 1, start
    while i < len(text) and depth:
        depth += {"(": 1, ")": -1}.get(text[i], 0)
        i += 1
    rest = text[i:i + 400]
    arrow = re.match(r"\s*(?:where[^{=]*)?=>", rest)
    if arrow:
        end = text.find(";", i)
        return text[i:end]
    brace = text.find("{", i)
    if brace < 0:
        return None
    depth, j = 0, brace
    while j < len(text):
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                return text[brace:j + 1]
        j += 1
    return None


def _names(body: str) -> list:
    """Names of the command a method builds: the first ``new Command`` argument."""
    m = _NEW_COMMAND_RE.search(body)
    if not m:
        return []
    first, depth, in_str, i = [], 0, False, m.end()
    while i < len(body):
        ch = body[i]
        if in_str:
            if ch == "\\":
                first.append(body[i:i + 2])
                i += 2
                continue
            in_str = ch != '"'
        elif ch == '"':
            in_str = True
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                break
            depth -= 1
        elif ch == "," and depth == 0:
            break
        first.append(ch)
        i += 1
    names = _STRING_RE.findall("".join(first))
    return names + _ALIAS_RE.findall(body)


_CALL_RE = re.compile(r"(?:\b([A-Z][A-Za-z0-9_]*)\.)?\b(Create[A-Za-z0-9_]*)\s*\(")


def _call_literals(text: str, start: int) -> list:
    """String literals in the argument list of the call whose ``(`` ends at ``start``."""
    depth, i = 1, start
    while i < len(text) and depth:
        depth += {"(": 1, ")": -1}.get(text[i], 0)
        i += 1
    return _STRING_RE.findall(text[start:i])


def _resolve(src: _Source, cls: str, method: str, ns: str, usings, hops: int = 0, literals=None):
    """``(body, class, namespace, usings, literals)`` of the method that
    builds the command, following overloads that only delegate
    (``Create() => CreateCore(...)``, ``Create() => Other.Create(...)``,
    ``CreateQueueCommand() => CreateServiceBusCommand("queue", ...)``);
    ``literals`` are the string arguments of the last delegating call."""
    body, body_ns, body_usings = src.method(cls, method, ns, usings)
    if body is None or _NEW_COMMAND_RE.search(body) or hops > 6:
        return body, cls, body_ns, body_usings, literals or []
    for overload, o_ns, o_usings in list(src.overloads):
        for m in _CALL_RE.finditer(overload):
            target = (m.group(1) or cls, m.group(2))
            if target != (cls, method):
                found = _resolve(src, target[0], target[1], o_ns, o_usings, hops + 1,
                                 _call_literals(overload, m.end()))
                if found[0] is not None and _NEW_COMMAND_RE.search(found[0]):
                    return found
    return body, cls, body_ns, body_usings, literals or []


def _children(body: str):
    """``(class or None, method)`` for each child command a method adds:
    ``Subcommands.Add(X.Create(...))`` and the elements of a collection
    initializer after ``new Command(...)`` (``{ ListCommand.Create(), ... }``)."""
    out = [(m.group(1), m.group(2)) for m in _ADD_RE.finditer(body)]
    for m in _NEW_COMMAND_RE.finditer(body):
        depth, i = 1, m.end()
        while i < len(body) and depth:
            depth += {"(": 1, ")": -1}.get(body[i], 0)
            i += 1
        rest = body[i:]
        if not rest.lstrip().startswith("{"):
            continue
        start = i + rest.index("{")
        depth, j = 0, start
        while j < len(body):
            depth += {"{": 1, "}": -1}.get(body[j], 0)
            if depth == 0:
                break
            j += 1
        for c in re.finditer(r"[{,]\s*(?:([A-Z][A-Za-z0-9_]*)\.)?(Create[A-Za-z0-9_]*)\s*\(", body[start:j]):
            out.append((c.group(1), c.group(2)))
    return out


def _walk(src: _Source, cls: str, body: str, ns: str, usings, prefix: tuple, out: set, depth: int) -> None:
    if depth > 12:
        src.problems.append(f"too deep at {' '.join(prefix)}")
        return
    for child_of, method in _children(body):
        target_cls = child_of or cls
        child, child_cls, child_ns, child_usings, literals = _resolve(src, target_cls, method, ns, usings)
        if child is None:
            src.problems.append(f"{' '.join(prefix) or '<root>'}: cannot find {target_cls}.{method}")
            continue
        names = _names(child) or literals[:1]
        if not names:
            src.problems.append(f"{' '.join(prefix) or '<root>'}: {target_cls}.{method} builds no named command")
            continue
        for name in names:
            path = prefix + (name,)
            out.add(path)
            if ".SetAction(" in child:
                src.actions.add(path)
            _walk(src, child_cls, child, child_ns, child_usings, path, out, depth + 1)


def _derive():
    """``(paths, problems)``: every command path (aliases included)."""
    src = _Source()
    out = set()
    root = None
    for ns, usings, text in src.classes.get("Program", []):
        for m in re.finditer(r"\bCreateRootCommand\s*\(", text):
            body = _body(text, m.end())
            if body and _ADD_RE.search(body):
                root = (body, ns, usings)
    if root is None:
        return out, ["cannot find the Program.CreateRootCommand that adds the commands"], set()
    _walk(src, "Program", root[0], root[1], root[2], (), out, 0)
    return out, src.problems, src.actions


def command_paths():
    """``(paths, problems)``: every command path (aliases included)."""
    paths, problems, _ = _derive()
    return paths, problems


def commands_with_an_action():
    """Paths whose own method calls ``SetAction`` (the command runs by itself)."""
    return _derive()[2]


if __name__ == "__main__":  # pragma: no cover - a listing for people
    paths, problems = command_paths()
    for path in sorted(paths):
        print(" ".join(path))
    for problem in problems:
        print("PROBLEM:", problem)
