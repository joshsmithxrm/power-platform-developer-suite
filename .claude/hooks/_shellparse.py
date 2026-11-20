r"""Read shell command text into statements of words, for the shakedown hook.

Used by ``shakedown-safety.py``. It is a reader for a safety check, not a
shell: it never runs anything, and where it cannot be sure it errs towards
finding more commands, not fewer. The hook does not rely on it alone: it
matches every mention of a guarded program in the raw text against the word
spans recorded here (``Unit``, ``Word.span``), and a mention the reader did
not place in a word it read to the end counts as an invocation.

Two dialects:

* ``posix`` -- the Bash tool (Git Bash): ``'...'`` literal, ``"..."`` with
  backslash escapes, ``$'...'`` with every ANSI-C escape, ``\`` escapes and
  line continuations, ``$(...)``, backticks, ``<(...)``, here-documents.
* ``ps`` -- the PowerShell tool: ``'...'`` with ``''``, ``"..."`` with
  backtick escapes (`` `u{...} `` included) and ``""``, the backtick as escape
  and line continuation, ``$(...)``, ``@(...)``, ``(...)``, ``{...}``
  script blocks (also glued to the word before them), here-strings, the
  call operator ``&``, assignments (``$r=ppds ...``), commas between
  arguments, ``--%``, ``<# #>``, typographic quotes, and every Unicode
  space as a separator. Backslashes are ordinary characters.

Statements are split at ``;``, newlines, ``|``, ``&&``, ``||`` and ``&``.
Each word's text has quotes removed and escapes applied. Text only known
when the command runs (a variable, a substitution, ``%VAR%``, a brace
expansion) is replaced by ``DYN`` and the word is marked ``dynamic``;
``split_safe`` says every such piece was inside double quotes. An unquoted
``?`` or ``*`` (and ``[`` in POSIX) marks the word ``glob``.

What makes this conservative:

* The inside of every substitution, script block and group is read as
  statements of its own (``code=True``).
* The inside of every quoted string, here-document, here-string and
  comment is also read as statements, in both dialects (``code=False``).
  The arguments handed to a shell wrapper (``bash -c``, ``pwsh -Command``,
  ``cmd /c``, ``eval``, ``Invoke-Expression``, ``pwsh -EncodedCommand`` in
  every spelling) are read again as one text, after their quoted pieces
  are joined, with ``code=True``; a here-document fed to a shell likewise.
  ``Statement.wrapper`` names the wrapper; ``Statement.code_arg`` says the
  reader found the code it runs; ``Statement.dynamic_code`` says that code
  is only known at run time.
* ``complete`` is False when the top-level text (or a substitution in it)
  has an unterminated construct, or when a cap or the deadline was reached.
  A caller must then assume the worst. ``Unit.ok`` is the same per read
  text, nested ones included.

Spans: every text read is a ``Unit``. A unit read from a slice of the
command text has ``base``, the offset of its first character in the
top-level text, so ``Word.span`` is an offset range in the top-level text.
Text produced by decoding (a joined wrapper argument, an encoded command)
has ``base`` None and no spans.

The work is bounded: every character read is counted against ``MAX_WORK``
and the clock is checked against the caller's deadline.
"""

from __future__ import annotations

import base64
import re
import time
import unicodedata

DYN = "\x00"

# Caps that keep the work bounded. Reaching one sets ``complete = False``.
MAX_TEXT_CHARS = 200_000
MAX_DEPTH = 8
MAX_WORK = 4_000_000
DEFAULT_BUDGET_SECONDS = 3.0

_SMART_SINGLE = "‘’‚‛"
_SMART_DOUBLE = "“”„"
_DASHES = "–—―"
_POSIX_BLANKS = " \t\r\f\v"
_POSIX_NAME_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_POSIX_SPECIAL_VARS = set("@*#?$!-0123456789")
_PS_VAR_RE = re.compile(r"[A-Za-z0-9_?^$]+(?::[A-Za-z0-9_]+)?")
_ASSIGNMENT_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
_CMD_VAR_RE = re.compile(r"%[A-Za-z0-9_#$*~:.\-]+%|![A-Za-z0-9_#$*~:.\-]+!|%~?[0-9a-z]")
_ASSIGN_OPS = "+-*/%?"

SHELLS = {"bash", "sh", "zsh", "dash", "ksh", "fish", "pwsh", "powershell", "cmd", "su", "wsl"}
# Words whose arguments are code.
CODE_WORDS = {"eval", "iex", "invoke-expression", "start-job", "sajb", "start-threadjob"}
# pwsh / powershell switches that take a value (the value is not code).
_PWSH_VALUE_FLAGS = ("executionpolicy", "workingdirectory", "configurationname", "outputformat",
                     "inputformat", "windowstyle", "settingsfile", "custompipename",
                     "configurationfile", "version", "psconsolefile", "apartmentstate")


def _is_ps_blank(c: str) -> bool:
    return c in " \t\r\f\v\u0085  " or unicodedata.category(c) == "Zs" or c == "﻿"


class Unit:
    """One text the reader read: the top-level command, a substitution or
    block in it (``sub``), a quoted string / comment / here-document read
    again as possible code (``string``), or code handed to a wrapper
    (``code``). ``base`` is the top-level offset of the text's first
    character, None when the text was produced by decoding; ``lo`` / ``hi``
    the top-level range the unit covers; ``outer`` the range of the whole
    construct (quotes included) for a ``string`` unit; ``ok`` False when the
    unit has an unterminated construct."""

    __slots__ = ("kind", "dialect", "code", "base", "lo", "hi", "outer", "parent", "ok", "words",
                 "children", "depth")

    def __init__(self, kind, dialect, code, base, lo, hi, outer, parent, depth):
        self.kind = kind
        self.dialect = dialect
        self.code = code
        self.base = base
        self.lo = lo
        self.hi = hi
        self.outer = outer
        self.parent = parent
        self.ok = True
        self.words = []
        self.children = []
        self.depth = depth
        if parent is not None:
            parent.children.append(self)


class Word:
    """One word of a statement. ``text`` has quotes removed; ``DYN`` marks
    text only known at run time, in which case ``dynamic`` is True.
    ``start`` / ``end`` index the word in its unit's text."""

    __slots__ = ("text", "dynamic", "quoted", "split_safe", "glob", "start", "end", "unit")

    def __init__(self, text: str, dynamic: bool, quoted: bool, split_safe: bool = False,
                 glob: bool = False, start=None, end=None, unit=None):
        self.text = text
        self.dynamic = dynamic
        self.quoted = quoted
        self.split_safe = split_safe
        self.glob = glob
        self.start = start
        self.end = end
        self.unit = unit

    @property
    def span(self):
        """The word's offset range in the top-level text, or None."""
        if self.unit is None or self.unit.base is None or self.start is None:
            return None
        return self.unit.base + self.start, self.unit.base + self.end

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"Word({self.text!r}{', dyn' if self.dynamic else ''})"


class Statement:
    """The words of one simple command. ``code`` is False for a statement
    read out of a string, comment or here-document that is not known to be
    run as code."""

    __slots__ = ("words", "code", "dialect", "level", "piped", "redirects", "dynamic_code", "wrapped",
                 "wrapper", "code_arg", "stdin_text")

    def __init__(self, words, code: bool, dialect: str, level: int = 0, piped: bool = False,
                 redirects=None, dynamic_code: bool = False, wrapped: bool = False,
                 wrapper=None, code_arg: bool = False, stdin_text: bool = False):
        self.words = words
        self.code = code
        self.dialect = dialect
        # Subshell level: a working-directory change at one level does not
        # reach statements at a lower level.
        self.level = level
        # The statement reads the previous statement's output (after `|`).
        self.piped = piped
        # Redirection targets (file names), in order.
        self.redirects = redirects or []
        # Code handed to a wrapper is only known at run time.
        self.dynamic_code = dynamic_code
        # Read from code handed to a shell wrapper (not the top-level text).
        self.wrapped = wrapped
        # The shell or code word this statement starts (bash, pwsh, eval, ...).
        self.wrapper = wrapper
        # The reader found the code the wrapper runs.
        self.code_arg = code_arg
        # The statement's input is a here-string (``<<<``).
        self.stdin_text = stdin_text

    @property
    def texts(self):
        return [w.text for w in self.words]


class Result:
    __slots__ = ("statements", "complete", "problems", "top", "capped")

    def __init__(self):
        self.statements = []
        self.complete = True
        self.problems = []
        self.top = None
        # A cap or the deadline was reached (not just an unterminated quote).
        self.capped = False


class _Ctx:
    def __init__(self, result: Result, deadline: float):
        self.result = result
        self.work = 0
        self.seen = set()
        self.deadline = deadline
        self.stopped = False

    def fail(self, problem: str, lenient: bool) -> None:
        if not lenient:
            self.result.complete = False
            self.result.problems.append(problem)

    def cap(self, problem: str) -> None:
        self.result.complete = False
        self.result.capped = True
        if problem not in self.result.problems:
            self.result.problems.append(problem)

    def tick(self, amount: int = 1) -> bool:
        if self.stopped:
            return False
        self.work += amount
        if self.work > MAX_WORK:
            self.cap("command text larger than the work cap")
            self.stopped = True
            return False
        if (self.work & 0x3FF) == 0 and time.monotonic() > self.deadline:
            self.cap("reading the command took longer than the time cap")
            self.stopped = True
            return False
        return True

    def scan(self, text: str, depth: int, code: bool, dialects=("posix", "ps"), level: int = 0,
             wrapped: bool = False, base=None, outer=None, parent=None, kind: str = "string") -> None:
        """Read a string / comment / here-document / wrapper argument leniently."""
        if self.stopped or not text or not text.strip():
            return
        if depth > MAX_DEPTH:
            self.cap("nesting deeper than the cap")
            return
        for dialect in dialects:
            key = (text, dialect, code, base)
            if key in self.seen:
                continue
            self.seen.add(key)
            if not self.tick(len(text) // 8 + 1):
                return
            unit = Unit(kind, dialect, code, base, base,
                        None if base is None else base + len(text), outer, parent, depth)
            _Reader(self, text, dialect, depth, lenient=True, code=code, level=level,
                    wrapped=wrapped, unit=unit).parse(None)


def _flag_name(text: str) -> str:
    """``-Command`` / ``--command`` / ``/Command`` -> ``command``."""
    t = text.lower()
    for prefix in ("--", "-", "/"):
        if t.startswith(prefix):
            return t[len(prefix):]
    return ""


def _is_encoded_flag(text: str) -> bool:
    name = _flag_name(text)
    return bool(name) and (name == "ec" or "encodedcommand".startswith(name))


def _wrapper_code_flag(shell: str, text: str) -> bool:
    """True when ``text`` is the flag after which ``shell`` runs code."""
    t = text.lower()
    if shell in ("pwsh", "powershell"):
        name = _flag_name(text)
        return bool(name) and name[0] == "c" and (
            "command".startswith(name) or "commandwithargs".startswith(name) or name == "cwa")
    if shell == "cmd":
        return t in ("/c", "/k", "/r") or (t.startswith("/") and t[-2:] in ("/c", "/k"))
    if shell == "wsl":
        return t in ("-e", "--exec", "--")
    return bool(re.fullmatch(r"-[a-z]*c[a-z]*", t))


class _Reader:
    def __init__(self, ctx: _Ctx, text: str, dialect: str, depth: int, lenient: bool, code: bool,
                 level: int = 0, wrapped: bool = False, unit: Unit = None):
        self.ctx = ctx
        self.wrapped = wrapped
        self.unit = unit
        self.s = text
        self.n = len(text)
        self.i = 0
        self.ps = dialect == "ps"
        self.dialect = dialect
        self.depth = depth
        self.lenient = lenient
        self.code = code
        self.level = level
        self.words = []
        self.buf = []
        self.dyn = False
        self.unsafe_dyn = False
        self.in_dq = False
        self.quoted = False
        self.inword = False
        self.brace = False
        self.glob = False
        self.wstart = 0
        self.wend = 0
        self.redirect_next = False
        self.redirects = []
        self.heredocs = []
        self.heredoc_shell = None
        self.piped = False
        self.dynamic_code = False
        self.stdin_text = False
        # Wrapper state for the statement being read.
        self.shell = None
        self.wrapper = None
        self.code_arg = False
        self.code_next = False
        self.cmd_rest = None
        self.rest_dialects = ("posix", "ps")
        self.file_seen = False

    # -- helpers ---------------------------------------------------------

    def _abs(self, i):
        return None if self.unit.base is None else self.unit.base + i

    def _fail(self, problem: str) -> None:
        self.unit.ok = False
        self.ctx.fail(problem, self.lenient)

    def _scan(self, text: str, depth: int, code: bool, dialects=("posix", "ps"), wrapped: bool = None,
              start=None, outer=None, kind: str = "string") -> None:
        """Read a string's inside as statements one subshell level down.
        ``start`` is the index of ``text`` in this unit's text, None when
        ``text`` was decoded; ``outer`` the construct's range there."""
        if wrapped is None:
            wrapped = self.wrapped and code
        base = None if start is None else self._abs(start)
        out = None
        if outer is not None and base is not None:
            out = (self._abs(outer[0]), self._abs(outer[1]))
        self.ctx.scan(text, depth, code, dialects, level=self.level + 1, wrapped=wrapped,
                      base=base, outer=out, parent=self.unit, kind=kind)

    def _peek(self, k: int = 1) -> str:
        j = self.i + k
        return self.s[j] if j < self.n else ""

    def _add(self, text: str) -> None:
        self.buf.append(text)
        self.inword = True

    def _add_dyn(self) -> None:
        self.buf.append(DYN)
        self.dyn = True
        self.inword = True
        if not self.in_dq:
            self.unsafe_dyn = True

    def _push(self, word: Word) -> None:
        word.unit = self.unit
        self.unit.words.append(word)
        self.words.append(word)

    def _end_word(self) -> None:
        if not self.inword:
            return
        text = "".join(self.buf)
        dyn = self.dyn
        if not self.ps and self.brace and not self.quoted and "{" in text and "}" in text and (
                "," in text or ".." in text):
            dyn = True  # brace expansion: the words are only known at run time
        if _CMD_VAR_RE.search(text):
            dyn = True  # cmd.exe %VAR% / !VAR! / %i expansion
        split_safe = dyn and not self.unsafe_dyn and not (dyn and not self.dyn)
        quoted, glob = self.quoted, self.glob
        start, end = self.wstart, max(self.wend, self.wstart)
        self.buf, self.dyn, self.unsafe_dyn, self.quoted, self.inword, self.brace, self.glob = \
            [], False, False, False, False, False, False
        if not self.ps and not quoted and not self.words and text in ("{", "}"):
            self._end_statement()  # a group opens or closes only where a command starts
            return
        if self.ps and not quoted and text == "--%":
            self._stop_parsing()
            return
        word = Word(text, dyn, quoted, split_safe, glob, start, end, self.unit)
        if self.redirect_next:
            self.redirect_next = False
            self.unit.words.append(word)
            self.redirects.append(word)
            return
        self._wrapper_step(word)
        self.unit.words.append(word)
        self.words.append(word)

    def _wrapper_step(self, word: Word) -> None:
        """Track wrappers: the words after ``pwsh -Command`` / ``cmd /c`` /
        ``eval`` are code, the word after ``bash -c`` is code, an encoded
        command is decoded."""
        if self.cmd_rest is not None:
            self.cmd_rest.append(word)
            return
        if self.code_next:
            self.code_next = False
            self._code_word(word)
            return
        low = word.text.lower()
        if self.shell in ("pwsh", "powershell") and self.words and _is_encoded_flag(self.words[-1].text):
            self._encoded(word)
            return
        base = _basename(low) if not word.dynamic else None
        if not self.words or self.shell is None:
            if base in SHELLS and self._at_command_position():
                self.shell = base
                self.wrapper = base
                return
        if base in CODE_WORDS and self._at_command_position():
            self.wrapper = base
            self.cmd_rest = []
            self.rest_dialects = ("posix",) if base == "eval" else ("ps",)
            return
        if self.shell is not None:
            if _wrapper_code_flag(self.shell, word.text):
                if self.shell in ("cmd", "pwsh", "powershell", "wsl"):
                    self.cmd_rest = []
                    self.rest_dialects = ("ps",) if self.shell in ("pwsh", "powershell") else \
                        ("posix",) if self.shell == "wsl" else ("posix", "ps")
                else:
                    self.code_next = True
                return
            if self.shell in ("pwsh", "powershell"):
                name = _flag_name(word.text)
                prev = _flag_name(self.words[-1].text) if self.words else ""
                if name and low.startswith(("-", "/")):
                    if "file".startswith(name):
                        self.file_seen = True
                elif not self.file_seen and not any(
                        prev and len(prev) >= 2 and f.startswith(prev) for f in _PWSH_VALUE_FLAGS):
                    self.file_seen = True
                    if self.shell == "powershell":
                        # `powershell <code...>` runs its positional arguments
                        self.cmd_rest = [word]
                        self.rest_dialects = ("ps",)
                    else:
                        # `pwsh <file>`: a script file; read it as code too
                        self._scan(word.text, self.depth + 1, True, ("ps",), wrapped=True, kind="code")

    def _at_command_position(self) -> bool:
        """True when the next word may be run as a program: nothing before
        it, a prefix just before it, or only assignments and a prefix with
        its options and values before it (``X=1 bash``, ``env X=1 bash``,
        ``timeout 5 bash``, ``nice -n 5 sh``, ``sudo -u u pwsh``). Generous
        on purpose: a shell taken for a wrapper here only adds readings."""
        if not self.words:
            return True
        if self.words[-1].text.lower() in COMMAND_PREFIXES:
            return True
        lead = next((w for w in self.words if not is_assignment(w)), None)
        if lead is None:
            return True
        name = lead.text.lower() if not lead.dynamic else None
        return name in COMMAND_PREFIXES or name in OPAQUE_PREFIXES

    def _code_dialects(self):
        """The dialect a wrapper's code is read in: the wrapper's own."""
        if self.shell in ("pwsh", "powershell"):
            return ("ps",)
        if self.shell == "cmd":
            return ("posix", "ps")
        return ("posix",)

    def _code_word(self, word: Word) -> None:
        if word.dynamic:
            self.dynamic_code = True
        if word.text.strip() and word.text.strip() != "-":
            self.code_arg = True
        self._scan(word.text, self.depth + 1, True, self._code_dialects(), wrapped=True, kind="code")

    def _encoded(self, word: Word) -> None:
        try:
            if word.dynamic:
                raise ValueError("dynamic")
            decoded = base64.b64decode(word.text, validate=True).decode("utf-16-le")
        except (ValueError, UnicodeDecodeError):
            self.ctx.cap("an encoded PowerShell command could not be decoded")
            return
        self.code_arg = True
        self._scan(decoded, self.depth + 1, True, dialects=("ps",), wrapped=True, kind="code")

    def _end_statement(self, piped_next: bool = False) -> None:
        self._end_word()
        if self.cmd_rest is not None:
            joined = " ".join(w.text for w in self.cmd_rest)
            if any(w.dynamic for w in self.cmd_rest):
                self.dynamic_code = True
            if joined.strip() and joined.strip() != "-":
                self.code_arg = True
            self._scan(joined, self.depth + 1, True, self.rest_dialects, wrapped=True, kind="code")
        if self.words or self.redirects:
            self.ctx.result.statements.append(Statement(
                self.words, self.code, self.dialect, self.level, self.piped, self.redirects,
                self.dynamic_code, self.wrapped, self.wrapper, self.code_arg, self.stdin_text))
        self.words, self.redirects = [], []
        self.redirect_next = False
        self.piped = piped_next
        self.dynamic_code = False
        self.stdin_text = False
        self.shell, self.wrapper, self.code_arg = None, None, False
        self.code_next, self.cmd_rest, self.file_seen = False, None, False

    def _sub(self, stop: str) -> None:
        """Read a nested region (substitution, block) up to ``stop``."""
        if self.depth + 1 > MAX_DEPTH:
            self.ctx.cap("nesting deeper than the cap")
            self.i = self.n
            return
        # A POSIX substitution or group runs in a subshell; PowerShell's
        # sub-expressions and script blocks share the caller's location.
        level = self.level if self.ps else self.level + 1
        unit = Unit("sub", self.dialect, self.code, self.unit.base, self._abs(self.i), None, None,
                    self.unit, self.depth + 1)
        child = _Reader(self.ctx, self.s, self.dialect, self.depth + 1, self.lenient, self.code, level,
                        self.wrapped, unit)
        child.i = self.i
        closed = child.parse(stop)
        unit.hi = self._abs(child.i)
        self.i = child.i
        if not closed:
            unit.ok = False
            self._fail(f"unterminated {stop!r} region")

    def _stop_parsing(self) -> None:
        """PowerShell ``--%``: the rest of the line is passed as is."""
        end = self.s.find("\n", self.i)
        end = self.n if end < 0 else end
        start = self.i
        rest = self.s[start:end]
        self.i = end
        for m in re.finditer(r"\S+", rest):
            tok = m.group(0)
            dyn = bool(_CMD_VAR_RE.search(tok))
            word = Word(_CMD_VAR_RE.sub(DYN, tok), dyn, False, False, False,
                        start + m.start(), start + m.end(), self.unit)
            self._wrapper_step(word)
            self.unit.words.append(word)
            self.words.append(word)

    # -- main loop -------------------------------------------------------

    def parse(self, stop):
        """Read statements until ``stop`` (or the end). True if ``stop`` was found."""
        s = self.s
        ctx = self.ctx
        while self.i < self.n:
            if not ctx.tick():
                break
            if self.inword:
                self.wend = self.i
            else:
                self.wstart = self.i
            c = s[self.i]
            if stop is not None and c == stop:
                self._end_statement()
                self.i += 1
                return True
            if c == "\n":
                self._end_statement()
                self.i += 1
                if self.heredocs:
                    self._read_heredocs()
                continue
            if (c in _POSIX_BLANKS) if not self.ps else _is_ps_blank(c):
                self._end_word()
                self.i += 1
                continue
            if self.ps:
                self._step_ps(c)
            else:
                self._step_posix(c)
        if self.inword:
            self.wend = self.i
        self._end_statement()
        if self.heredocs:
            self._fail("unterminated here-document")
        self.heredocs = []
        return stop is None

    # -- POSIX -----------------------------------------------------------

    def _step_posix(self, c: str) -> None:
        s = self.s
        if c == "\\":
            nxt = self._peek()
            if nxt == "\n":
                self.i += 2
            elif nxt == "\r" and self._peek(2) == "\n":
                self.i += 3
            elif nxt:
                self._add(nxt)
                self.i += 2
            else:
                self._add("\\")
                self.i += 1
            return
        if c == "'":
            end = s.find("'", self.i + 1)
            if end < 0:
                self._fail("unterminated single quote")
                end = self.n
            content = s[self.i + 1:end]
            self.ctx.tick(len(content) // 8 + 1)
            self._add(content)
            self.quoted = True
            start = self.i
            self.i = end + 1
            self._scan(content, self.depth + 1, False, start=start + 1, outer=(start, min(end + 1, self.n)))
            return
        if c == '"':
            start = self.i + 1
            self._posix_double()
            self.quoted = True
            close = self.i - 1 if self.i - 1 >= start else self.n
            self._scan(s[start:close], self.depth + 1, False, start=start, outer=(start - 1, self.i))
            return
        if c == "$":
            self._posix_dollar(in_double=False)
            return
        if c == "`":
            self.i += 1
            self._add_dyn()
            self._sub("`")
            return
        if c == "#" and not self.inword:
            end = s.find("\n", self.i)
            end = self.n if end < 0 else end
            self._scan(s[self.i + 1:end], self.depth + 1, False, start=self.i + 1)
            self.i = end
            return
        if c in ";|&":
            if c == "&" and self._peek() == ">":
                self._end_word()
                self.i += 1
                self._redirect()
                return
            piped = c == "|" and self._peek() not in ("|",)
            self.i += 1
            if self.i < self.n and s[self.i] in ";|&":
                self.i += 1  # &&, ||, ;;, |&
            self._end_statement(piped_next=piped)
            return
        if c == "(":
            self._end_statement()
            self.i += 1
            self._sub(")")
            return
        if c == ")":
            self._end_statement()
            self.i += 1
            return
        if c in "<>":
            if c == "<" and s.startswith("<<<", self.i):
                self._end_word()
                self.i += 3
                self.stdin_text = True
                return
            if c == "<" and s.startswith("<<", self.i):
                self._end_word()
                self.i += 2
                self._heredoc_start()
                return
            if self._peek() == "(":
                # process substitution
                self.i += 2
                self._add_dyn()
                self._sub(")")
                return
            if self.inword and not self.quoted and all(ch.isdigit() for ch in "".join(self.buf)):
                self.buf, self.inword = [], False
            self._end_word()
            self._redirect()
            return
        if c == "{":
            self.brace = True
        if c in "?*[":
            self.glob = True
        self._add(c)
        self.i += 1

    def _redirect(self) -> None:
        """Consume a redirection operator. ``>&2``, ``2>&1``, ``<&0`` and
        ``>&-`` name a descriptor, not a file: nothing more is taken. Any
        other operator's target is recorded as a redirect, not a word."""
        s = self.s
        while self.i < self.n and s[self.i] in "<>|":
            self.i += 1
        if self.i < self.n and s[self.i] == "&":
            self.i += 1
            j = self.i
            while j < self.n and (s[j].isdigit() or s[j] == "-"):
                j += 1
            if j > self.i:
                self.i = j
                return
        while self.i < self.n and s[self.i] in " \t":
            self.i += 1
        self.redirect_next = self.i < self.n and s[self.i] not in "\n;|&"

    def _posix_double(self) -> None:
        """Read ``"..."`` starting at the opening quote; leaves i after it."""
        s = self.s
        self.i += 1
        self.inword = True
        self.in_dq = True
        try:
            while self.i < self.n:
                if not self.ctx.tick():
                    return
                c = s[self.i]
                if c == '"':
                    self.i += 1
                    return
                if c == "\\":
                    nxt = self._peek()
                    if nxt in ('$', '`', '"', '\\'):
                        self._add(nxt)
                        self.i += 2
                    elif nxt == "\n":
                        self.i += 2
                    else:
                        self._add("\\")
                        self.i += 1
                    continue
                if c == "$":
                    self._posix_dollar(in_double=True)
                    continue
                if c == "`":
                    self.i += 1
                    self._add_dyn()
                    self._sub("`")
                    continue
                self._add(c)
                self.i += 1
            self._fail("unterminated double quote")
        finally:
            self.in_dq = False

    def _ansi_c(self, j: int):
        """Decode ``$'...'`` from index ``j`` (after the quote). Returns
        (text, index after the closing quote, closed)."""
        s = self.s
        out = []
        simple = {"n": "\n", "t": "\t", "r": "\r", "a": "\a", "b": "\b", "e": "\x1b", "E": "\x1b",
                  "f": "\f", "v": "\v", "'": "'", "\\": "\\", '"': '"', "?": "?"}
        while j < self.n and s[j] != "'":
            if not self.ctx.tick():
                break
            if s[j] == "\\" and j + 1 < self.n:
                esc = s[j + 1]
                if esc in simple:
                    out.append(simple[esc])
                    j += 2
                    continue
                if esc in "01234567":
                    m = re.match(r"[0-7]{1,3}", s[j + 1:j + 4])
                    out.append(chr(int(m.group(0), 8) & 0xFF))
                    j += 1 + len(m.group(0))
                    continue
                if esc in "xuU":
                    width = {"x": 2, "u": 4, "U": 8}[esc]
                    m = re.match(r"[0-9A-Fa-f]{1,%d}" % width, s[j + 2:j + 2 + width])
                    if m:
                        try:
                            out.append(chr(int(m.group(0), 16)))
                        except (ValueError, OverflowError):
                            out.append("�")
                        j += 2 + len(m.group(0))
                        continue
                if esc == "c" and j + 2 < self.n:
                    out.append(chr(ord(s[j + 2]) & 0x1F))
                    j += 3
                    continue
                out.append("\\" + esc)
                j += 2
                continue
            out.append(s[j])
            j += 1
        return "".join(out), j + 1, j < self.n

    def _posix_dollar(self, in_double: bool) -> None:
        s = self.s
        nxt = self._peek()
        if nxt == "(":
            self.i += 2
            self._add_dyn()
            self._sub(")")
            return
        if nxt == "{":
            end = _match_brace(s, self.i + 1, self.ctx)
            if end < 0:
                self._fail("unterminated ${")
                end = self.n
            self._scan(s[self.i + 2:end], self.depth + 1, self.code, start=self.i + 2,
                       kind="string" if not self.code else "code")
            self._add_dyn()
            self.i = end + 1
            return
        if nxt == "'" and not in_double:
            start = self.i
            content, self.i, closed = self._ansi_c(self.i + 2)
            if not closed:
                self._fail("unterminated $' quote")
            self._add(content)
            self.quoted = True
            raw_end = min(self.i - 1, self.n)
            self._scan(s[start + 2:raw_end], self.depth + 1, False, start=start + 2,
                       outer=(start, min(self.i, self.n)))
            self._scan(content, self.depth + 1, False)
            return
        if nxt == '"' and not in_double:
            outer_start = self.i
            self.i += 1
            start = self.i + 1
            self._posix_double()
            self.quoted = True
            close = self.i - 1 if self.i - 1 >= start else self.n
            self._scan(s[start:close], self.depth + 1, False, start=start, outer=(outer_start, self.i))
            return
        m = _POSIX_NAME_RE.match(s, self.i + 1)
        if m:
            self._add_dyn()
            self.i = m.end()
            return
        if nxt and nxt in _POSIX_SPECIAL_VARS:
            self._add_dyn()
            self.i += 2
            return
        self._add("$")
        self.i += 1

    def _heredoc_start(self) -> None:
        s = self.s
        strip = False
        if self.i < self.n and s[self.i] == "-":
            strip = True
            self.i += 1
        while self.i < self.n and s[self.i] in " \t":
            self.i += 1
        delim = []
        while self.i < self.n and s[self.i] not in " \t\n;|&<>()":
            ch = s[self.i]
            if ch in "'\"":
                end = s.find(ch, self.i + 1)
                end = self.n if end < 0 else end
                delim.append(s[self.i + 1:end])
                self.i = end + 1
                continue
            if ch == "\\":
                self.i += 1
                continue
            delim.append(ch)
            self.i += 1
        # A here-document fed to a shell or code word is code, also when the
        # shell follows assignments or a prefix (`env X=1 bash <<EOF`).
        first = self.words[0] if self.words else None
        first_code = first is not None and not first.dynamic and \
            _basename(first.text.lower()) in SHELLS | CODE_WORDS
        code = first_code or self.shell is not None
        if code:
            self.code_arg = True
        else:
            # Fed to a program the reader does not know as a shell (`$SH`,
            # `$(which bash)`): the text may still be run as code.
            self.stdin_text = True
        if delim:
            self.heredoc_shell = (self.shell or _basename(first.text.lower())) if code else None
            self.heredocs.append(("".join(delim), strip, code))
        else:
            self._fail("here-document without a delimiter")

    def _read_heredocs(self) -> None:
        s = self.s
        for delim, strip, code in self.heredocs:
            body_start = self.i
            body_end = self.i
            closed = False
            while self.i < self.n:
                end = s.find("\n", self.i)
                end = self.n if end < 0 else end
                line = s[self.i:end]
                if not self.ctx.tick(len(line) // 8 + 1):
                    break
                check = line.rstrip("\r")
                if strip:
                    check = check.lstrip("\t")
                if check == delim:
                    self.i = min(end + 1, self.n)
                    closed = True
                    break
                body_end = end
                self.i = min(end + 1, self.n)
            if not closed:
                self._fail("unterminated here-document")
            dialects = ("ps",) if code and self.heredoc_shell in ("pwsh", "powershell") else \
                ("posix",) if code else ("posix", "ps")
            self._scan(s[body_start:body_end], self.depth + 1, code, dialects, wrapped=code,
                       start=body_start, kind="code" if code else "string")
        self.heredocs = []

    # -- PowerShell ------------------------------------------------------

    def _step_ps(self, c: str) -> None:
        s = self.s
        if c == "`":
            nxt = self._peek()
            if nxt == "\n":
                self.i += 2
            elif nxt == "\r" and self._peek(2) == "\n":
                self.i += 3
            elif nxt:
                self.i += 1 + self._ps_escape(nxt)
            else:
                self.i += 1
            return
        if c == "'" or c in _SMART_SINGLE:
            self._ps_single()
            return
        if c == '"' or c in _SMART_DOUBLE:
            start = self.i + 1
            self._ps_double()
            self.quoted = True
            close = self.i - 1 if self.i - 1 >= start else self.n
            self._scan(s[start:close], self.depth + 1, False, start=start, outer=(start - 1, self.i))
            return
        if c == "@" and not self.inword:
            nxt = self._peek()
            if (nxt in ("'", '"') or nxt in _SMART_SINGLE or nxt in _SMART_DOUBLE) and self._here_string(nxt):
                return
            if nxt == "(":
                self.i += 2
                self._add_dyn()
                self._sub(")")
                return
            if nxt == "{":
                self.i += 2
                self._add_dyn()
                self._sub("}")
                return
            m = _PS_VAR_RE.match(s, self.i + 1)
            if m:
                self._add_dyn()  # splatting
                self.i = m.end()
                return
        if c == "$":
            self._ps_dollar()
            return
        if c == "#" and not self.inword:
            end = s.find("\n", self.i)
            end = self.n if end < 0 else end
            self._scan(s[self.i + 1:end], self.depth + 1, False, start=self.i + 1)
            self.i = end
            return
        if c == "<" and self._peek() == "#" and not self.inword:
            end = s.find("#>", self.i + 2)
            if end < 0:
                self._fail("unterminated <# comment")
                end = self.n
            self._scan(s[self.i + 2:end], self.depth + 1, False, start=self.i + 2)
            self.i = end + 2
            return
        if c == ";" or c == "|":
            self.i += 1
            double = c == "|" and self.i < self.n and s[self.i] == "|"
            if double:
                self.i += 1
            self._end_statement(piped_next=(c == "|" and not double))
            return
        if c == "&":
            if self._peek() == "&":
                self._end_statement()
                self.i += 2
                return
            if self._peek() == ">":
                # not PowerShell syntax; read as a redirection
                self._end_word()
                self.i += 1
                self._redirect()
                return
            if not self.inword and not self.words:
                self._push(Word("&", False, False, start=self.i, end=self.i + 1))  # call operator
                self.i += 1
                return
            self._end_statement()
            self.i += 1
            return
        if c == ",":
            self._end_word()
            self.i += 1
            return
        if c == "(":
            text = "".join(self.buf)
            if self.inword and not self.dyn and not self.quoted and not text.startswith(("[", "$", "@", "-")):
                # `ppds.exe('data','delete')`, `Write-Output(...)`: the command
                # name ends where the parenthesis starts
                self.wend = self.i
                self._end_word()
                self.wstart = self.i
            self.i += 1
            self._add_dyn()
            self._sub(")")
            return
        if c == "{":
            # A script block, also when glued to the word before it
            # (`if($x){...}`, `%{...}`, `ForEach-Object{...}`).
            self.wend = self.i
            self._end_word()
            self.wstart = self.i
            self.i += 1
            self._add_dyn()
            self._sub("}")
            self.wend = self.i
            self._end_word()
            return
        if c in ")}":
            self._end_statement()
            self.i += 1
            return
        if c in "<>":
            if self.inword and not self.quoted and "".join(self.buf) in ("1", "2", "3", "4", "5", "6", "*"):
                self.buf, self.inword = [], False
            self._end_word()
            self._redirect()
            return
        if c == "=" and self._ps_assignment():
            return
        if c in _DASHES and not self.inword:
            c = "-"
        if c in "?*" and not self.in_dq:
            self.glob = True
        self._add(c)
        self.i += 1

    def _ps_assignment(self) -> bool:
        """``$r=<command>`` / ``$r += <command>``: split off the operator so
        the command after it is a command position."""
        text = "".join(self.buf) if self.inword else ""
        k = len(text)
        while k > 0 and text[k - 1] in _ASSIGN_OPS:
            k -= 1
        head, ops = text[:k], text[k:]
        if head:
            target = DYN in head and not self.words and not head.startswith("-") and not self.quoted
        else:
            target = len(self.words) == 1 and self.words[0].dynamic
        if not target:
            return False
        if head:
            self.buf = [head]
            self.wend = self.i - len(ops)
            self._end_word()
        else:
            self.buf, self.inword, self.glob = [], False, False
        self._push(Word(ops + "=", False, False, start=self.i - len(ops), end=self.i + 1))
        self.i += 1
        return True

    def _ps_single(self) -> None:
        s = self.s
        start = self.i
        j = self.i + 1
        out = []
        closed = False
        while j < self.n:
            if not self.ctx.tick():
                break
            ch = s[j]
            if ch == "'" or ch in _SMART_SINGLE:
                if j + 1 < self.n and (s[j + 1] == "'" or s[j + 1] in _SMART_SINGLE):
                    out.append("'")
                    j += 2
                    continue
                closed = True
                break
            out.append(ch)
            j += 1
        if not closed:
            self._fail("unterminated single quote")
        content = "".join(out)
        self._add(content)
        self.quoted = True
        self.i = j + 1
        raw = s[start + 1:min(j, self.n)]
        self._scan(raw, self.depth + 1, False, start=start + 1, outer=(start, min(j + 1, self.n)))
        if content != raw:
            self._scan(content, self.depth + 1, False)

    def _ps_escape(self, nxt: str) -> int:
        """Apply the backtick escape whose letter is ``nxt``; returns how many
        characters after the backtick it used."""
        if nxt == "u" and self._peek(2) == "{":
            end = self.s.find("}", self.i + 3)
            if 0 < end <= self.i + 12:
                try:
                    self._add(chr(int(self.s[self.i + 3:end], 16)))
                except (ValueError, OverflowError):
                    self._add("�")
                return end - self.i
        table = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "a": "\a", "b": "\b", "e": "\x1b",
                 "f": "\f", "v": "\v"}
        self._add(table.get(nxt, nxt))
        return 1

    def _ps_double(self) -> None:
        s = self.s
        self.i += 1
        self.inword = True
        self.in_dq = True
        try:
            while self.i < self.n:
                if not self.ctx.tick():
                    return
                c = s[self.i]
                if c == '"' or c in _SMART_DOUBLE:
                    nxt = self._peek()
                    if nxt and (nxt == '"' or nxt in _SMART_DOUBLE):
                        self._add('"')
                        self.i += 2
                        continue
                    self.i += 1
                    return
                if c == "`":
                    nxt = self._peek()
                    if nxt:
                        self.i += 1 + self._ps_escape(nxt)
                    else:
                        self.i += 1
                    continue
                if c == "$":
                    self._ps_dollar()
                    continue
                self._add(c)
                self.i += 1
            self._fail("unterminated double quote")
        finally:
            self.in_dq = False

    def _ps_dollar(self) -> None:
        s = self.s
        nxt = self._peek()
        if nxt == "(":
            self.i += 2
            self._add_dyn()
            self._sub(")")
            return
        if nxt == "{":
            end = s.find("}", self.i + 2)
            if end < 0:
                self._fail("unterminated ${")
                end = self.n
            self._add_dyn()
            self.i = end + 1
            return
        m = _PS_VAR_RE.match(s, self.i + 1)
        if m:
            self._add_dyn()
            self.i = m.end()
            return
        self._add("$")
        self.i += 1

    def _here_string(self, quote: str) -> bool:
        """``@'`` / ``@"`` followed by a line break opens a here-string."""
        s = self.s
        start = self.i
        j = self.i + 2
        while j < self.n and s[j] in " \t":
            j += 1
        if j < self.n and s[j] == "\r":
            j += 1
        if j >= self.n or s[j] != "\n":
            return False
        single = quote == "'" or quote in _SMART_SINGLE
        closers = ["\n" + q + "@" for q in ("'" + _SMART_SINGLE if single else '"' + _SMART_DOUBLE)]
        found = [(s.find(cl, j), cl) for cl in closers]
        found = [(k, cl) for k, cl in found if k >= 0]
        body_start = j + 1
        if not found:
            self._fail("unterminated here-string")
            body = s[body_start:]
            self.i = self.n
        else:
            end, closer = min(found)
            body = s[body_start:end]
            self.i = end + len(closer)
        self.ctx.tick(len(body) // 8 + 1)
        body = body[:-1] if body.endswith("\r") else body
        if not single and "$" in body:
            self._add(body.replace("$", DYN))
            self.dyn = True  # inside quotes: one argument
        else:
            self._add(body)
        self.quoted = True
        self._scan(body, self.depth + 1, False, start=body_start, outer=(start, self.i))
        return True


def _match_brace(s: str, start: int, ctx: _Ctx) -> int:
    """Index of the ``}`` matching the ``{`` at ``start`` (or -1)."""
    depth = 0
    for j in range(start, len(s)):
        if not ctx.tick():
            return -1
        if s[j] == "{":
            depth += 1
        elif s[j] == "}":
            depth -= 1
            if depth == 0:
                return j
    return -1


def _basename(text: str) -> str:
    base = re.split(r"[\\/]", text)[-1].lower()
    for ext in (".exe", ".cmd", ".bat", ".ps1", ".com"):
        if base.endswith(ext):
            return base[: -len(ext)]
    return base


def program_name(word: Word):
    """The program a word names, lower case, without path or extension.
    None when the last path component is only known at run time."""
    base = _basename(word.text)
    if DYN in base:
        return None
    return base


def read(text: str, dialect: str, deadline: float = None) -> Result:
    """Read ``text`` as top-level code in ``dialect`` (``posix`` or ``ps``).
    ``deadline`` is a ``time.monotonic()`` value; past it the reading stops
    and is reported incomplete."""
    result = Result()
    if not isinstance(text, str):
        result.complete = False
        result.problems.append("command is not text")
        return result
    if len(text) > MAX_TEXT_CHARS:
        result.complete = False
        result.capped = True
        result.problems.append("command longer than the length cap")
        text = text[:MAX_TEXT_CHARS]
    if deadline is None:
        deadline = time.monotonic() + DEFAULT_BUDGET_SECONDS
    ctx = _Ctx(result, deadline)
    ctx.seen.add((text, dialect, True, 0))
    top = Unit("top", dialect, True, 0, 0, len(text), None, None, 0)
    result.top = top
    _Reader(ctx, text, dialect, 0, lenient=False, code=True, unit=top).parse(None)
    if not top.ok:
        result.complete = False
    return result


def is_assignment(word: Word) -> bool:
    return bool(_ASSIGNMENT_RE.match(word.text)) and not word.quoted


# Words that run the word after them as a program, passing it the words
# that follow ("transparent" prefixes).
COMMAND_PREFIXES = {
    "env", "command", "exec", "nohup", "time", "sudo", "nice", "builtin",
    "then", "do", "else", "elif", "if", "while", "until", "!", "timeout", "stdbuf",
    "&", ".", "wsl", "source", "ionice", "chrt", "taskset", "strace", "ltrace", "doas",
}
# Words that run a program whose arguments come from elsewhere, or decide
# them at run time ("opaque" prefixes).
OPAQUE_PREFIXES = {
    "xargs", "parallel", "start-process", "saps", "start", "call", "invoke-item", "ii",
    "iex", "invoke-expression", "invoke-command", "icm", "eval", "foreach-object", "%",
    "watch", "find", "fd", "runas", "gsudo", "start-job", "sajb", "start-threadjob",
}


def command_positions(stmt: Statement):
    """Indices of the words that are run as programs in ``stmt``, each with
    whether its arguments are only known at run time: the first word after
    assignments, and the word after a prefix such as ``env``, ``sudo``,
    ``&``, ``xargs`` or ``Start-Process`` (options of the prefix skipped).
    Returns a list of ``(index, opaque)``."""
    words = stmt.words
    out = []
    i = 0
    expect = True
    after_prefix = False
    opaque = False
    while i < len(words):
        w = words[i]
        if expect:
            if is_assignment(w) and stmt.dialect == "posix":
                i += 1
                continue
            if stmt.dialect == "ps" and w.dynamic and not after_prefix:
                # `$x ...` / `(...)` at the start is an expression, not a call;
                # in `$x = <command>` the command after `=` runs.
                if i + 1 < len(words) and re.fullmatch(r"[-+*/%?]?\??=", words[i + 1].text):
                    i += 2
                    after_prefix = True
                    continue
                break
            name = w.text.lower() if not w.dynamic else None
            if name in COMMAND_PREFIXES or name in OPAQUE_PREFIXES:
                after_prefix = True
                opaque = opaque or name in OPAQUE_PREFIXES
                i += 1
                # skip options and assignments of the prefix (env -i X=1 cmd)
                while i < len(words) and not words[i].dynamic and (
                    words[i].text.startswith("-") or is_assignment(words[i])
                ) and words[i].text not in ("--",):
                    i += 1
                if i < len(words) and words[i].text == "--":
                    i += 1
                continue
            out.append((i, opaque))
            expect = False
        i += 1
    return out
