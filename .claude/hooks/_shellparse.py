"""Read shell command text into statements of words, for the PreToolUse hooks.

Shared by ``shakedown-safety.py`` and ``command-text-guard.py``. It is a
reader for safety checks, not a shell: it never runs anything, and where it
cannot be sure it errs towards finding more commands, not fewer.

Two dialects:

* ``posix`` -- the Bash tool (Git Bash): ``'...'`` literal, ``"..."`` with
  backslash escapes, ``\\`` escapes and line continuations, ``$(...)``,
  backticks, ``<(...)``, here-documents.
* ``ps`` -- the PowerShell tool: ``'...'`` with ``''``, ``"..."`` with
  backtick escapes and ``""``, the backtick as escape and line continuation,
  ``$(...)``, ``@(...)``, ``(...)``, ``{...}`` script blocks, here-strings,
  the call operator ``&``, commas between arguments, ``--%``, ``<# #>``.
  Backslashes are ordinary characters (Windows paths).

Statements are split at ``;``, newlines, ``|``, ``&&``, ``||`` and ``&``.
Each word's text has quotes removed and escapes applied. Text only known
when the command runs (a variable, a substitution) is replaced by ``DYN``
and the word is marked ``dynamic``.

What makes this conservative:

* The inside of every substitution, script block and group is read as
  statements of its own (``code=True``).
* The inside of every quoted string, here-document, here-string and
  comment is also read as statements, in both dialects, because a string
  can be handed to another shell (``bash -c``, ``pwsh -Command``,
  ``Invoke-Expression``, ``cmd /c``) or to a program that runs it. Those
  statements are ``code=False`` unless the string is the argument of a
  known shell wrapper, in which case they are ``code=True``. Reading them
  is lenient: an unterminated quote there ends at the end of the text.
* ``complete`` is False when the top-level text (or a substitution in it)
  has an unterminated construct, or when a cap was reached. A caller must
  then assume the worst.
"""

from __future__ import annotations

import base64
import re

DYN = "\x00"

# Caps that keep the work bounded. Reaching one sets ``complete = False``.
MAX_TEXT_CHARS = 200_000
MAX_DEPTH = 8
MAX_WORK = 4_000_000

_SMART_SINGLE = "‘’‚‛"
_SMART_DOUBLE = "“”„"
_DASHES = "–—―"
_POSIX_NAME_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_POSIX_SPECIAL_VARS = set("@*#?$!-0123456789")
_PS_VAR_RE = re.compile(r"[A-Za-z0-9_?^$]+(?::[A-Za-z0-9_]+)?")
_ASSIGNMENT_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")

# Words after which the next argument is shell code.
_WRAPPER_CODE_FLAGS = {
    "bash": {"-c"}, "sh": {"-c"}, "zsh": {"-c"}, "dash": {"-c"}, "ksh": {"-c"},
    "su": {"-c"},
    "pwsh": {"-c", "-command", "-co", "-com", "-comm", "-comma", "-comman", "/c", "/command"},
    "powershell": {"-c", "-command", "-co", "-com", "-comm", "-comma", "-comman", "/c", "/command"},
    "cmd": {"/c", "/k", "/r", "-c"},
}
_WRAPPER_CODE_WORDS = {"eval", "iex", "invoke-expression", "invoke-command",
                       "start-job", "start-threadjob"}


class Word:
    """One word of a statement. ``text`` has quotes removed; ``DYN`` marks
    text only known at run time, in which case ``dynamic`` is True."""

    __slots__ = ("text", "dynamic", "quoted")

    def __init__(self, text: str, dynamic: bool, quoted: bool):
        self.text = text
        self.dynamic = dynamic
        self.quoted = quoted

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"Word({self.text!r}{', dyn' if self.dynamic else ''})"


class Statement:
    """The words of one simple command. ``code`` is False for a statement
    read out of a string, comment or here-document that is not known to be
    run as code."""

    __slots__ = ("words", "code", "dialect", "level")

    def __init__(self, words, code: bool, dialect: str, level: int = 0):
        self.words = words
        self.code = code
        self.dialect = dialect
        # Subshell level: a working-directory change at one level does not
        # reach statements at a lower level (a POSIX subshell or
        # substitution, or another process reading a string).
        self.level = level

    @property
    def texts(self):
        return [w.text for w in self.words]


class Result:
    __slots__ = ("statements", "complete", "problems")

    def __init__(self):
        self.statements = []
        self.complete = True
        self.problems = []


class _Ctx:
    def __init__(self, result: Result):
        self.result = result
        self.work = 0
        self.seen = set()

    def fail(self, problem: str, lenient: bool) -> None:
        if not lenient:
            self.result.complete = False
            self.result.problems.append(problem)

    def cap(self, problem: str) -> None:
        self.result.complete = False
        self.result.problems.append(problem)

    def scan(self, text: str, depth: int, code: bool, dialects=("posix", "ps"), level: int = 0) -> None:
        """Read the inside of a string / comment / here-document leniently."""
        if not text or not text.strip():
            return
        if depth > MAX_DEPTH:
            self.cap("nesting deeper than the cap")
            return
        for dialect in dialects:
            key = (text, dialect, code)
            if key in self.seen:
                continue
            self.seen.add(key)
            _Reader(self, text, dialect, depth, lenient=True, code=code, level=level).parse(None)


class _Reader:
    def __init__(self, ctx: _Ctx, text: str, dialect: str, depth: int, lenient: bool, code: bool,
                 level: int = 0):
        self.level = level
        self.ctx = ctx
        self.s = text
        self.n = len(text)
        self.i = 0
        self.ps = dialect == "ps"
        self.dialect = dialect
        self.depth = depth
        self.lenient = lenient
        self.code = code
        self.words = []
        self.buf = []
        self.dyn = False
        self.quoted = False
        self.inword = False
        self.brace = False
        self.drop_next = False
        self.heredocs = []

    # -- helpers ---------------------------------------------------------

    def _scan(self, text: str, depth: int, code: bool, dialects=("posix", "ps")) -> None:
        """Read a string's inside as statements one subshell level down."""
        self.ctx.scan(text, depth, code, dialects, level=self.level + 1)

    def _tick(self, amount: int = 1) -> bool:
        self.ctx.work += amount
        if self.ctx.work > MAX_WORK:
            self.ctx.cap("command text larger than the work cap")
            self.i = self.n
            return False
        return True

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

    def _end_word(self) -> None:
        if not self.inword:
            return
        text = "".join(self.buf)
        dyn = self.dyn
        if not self.ps and self.brace and not self.quoted and re.search(r"\{[^{}]*(,|\.\.)[^{}]*\}", text):
            dyn = True  # brace expansion: the words are only known at run time
        quoted = self.quoted
        self.buf, self.dyn, self.quoted, self.inword, self.brace = [], False, False, False, False
        if not self.ps and not quoted and text in ("{", "}"):
            self._end_statement()
            return
        if self.ps and not quoted and text == "--%":
            self._stop_parsing()
            return
        if self.drop_next:
            self.drop_next = False
            return
        self._maybe_encoded(text, dyn)
        self.words.append(Word(text, dyn, quoted))

    def _maybe_encoded(self, text: str, dyn: bool) -> None:
        """``pwsh -EncodedCommand <base64>``: read the decoded script as code."""
        if not self.words:
            return
        flag = self.words[-1].text.lower()
        if not (flag == "-ec" or (len(flag) >= 2 and "-encodedcommand".startswith(flag))):
            return
        if not any(_basename(w.text.lower()) in ("pwsh", "powershell") for w in self.words[:-1]):
            return
        try:
            if dyn:
                raise ValueError("dynamic")
            decoded = base64.b64decode(text, validate=True).decode("utf-16-le")
        except (ValueError, UnicodeDecodeError):
            self.ctx.cap("an encoded PowerShell command could not be decoded")
            return
        self._scan(decoded, self.depth + 1, True, dialects=("ps",))

    def _end_statement(self) -> None:
        self._end_word()
        if self.words:
            self.ctx.result.statements.append(Statement(self.words, self.code, self.dialect, self.level))
        self.words = []
        self.drop_next = False

    def _wrapper_arg_is_code(self) -> bool:
        """True when the word being read is the code argument of a wrapper
        (``bash -c``, ``pwsh -Command``, ``cmd /c``, ``eval``, ``iex``)."""
        if self.inword or not self.words:
            return False
        prev = self.words[-1].text.lower()
        if _basename(prev) in _WRAPPER_CODE_WORDS:
            return True
        for w in self.words[:-1]:
            shell = _basename(w.text.lower())
            if shell not in _WRAPPER_CODE_FLAGS:
                continue
            if prev in _WRAPPER_CODE_FLAGS[shell]:
                return True
            if shell not in ("pwsh", "powershell", "cmd") and re.fullmatch(r"-[a-z]*c[a-z]*", prev):
                return True  # bash -lc, sh -ec
        return False

    def _sub(self, stop: str) -> None:
        """Read a nested region (substitution, block) up to ``stop``."""
        if self.depth + 1 > MAX_DEPTH:
            self.ctx.cap("nesting deeper than the cap")
            self.i = self.n
            return
        # A POSIX substitution or group runs in a subshell; PowerShell's
        # sub-expressions and script blocks share the caller's location.
        level = self.level if self.ps else self.level + 1
        child = _Reader(self.ctx, self.s, self.dialect, self.depth + 1, self.lenient, self.code, level)
        child.i = self.i
        closed = child.parse(stop)
        self.i = child.i
        if not closed:
            self.ctx.fail(f"unterminated {stop!r} region", self.lenient)

    def _stop_parsing(self) -> None:
        """PowerShell ``--%``: the rest of the line is passed as is."""
        end = self.s.find("\n", self.i)
        end = self.n if end < 0 else end
        rest = self.s[self.i:end]
        self.i = end
        for tok in rest.split():
            dyn = "%" in tok
            self.words.append(Word(re.sub(r"%[^%]*%", DYN, tok), dyn, False))

    # -- main loop -------------------------------------------------------

    def parse(self, stop):
        """Read statements until ``stop`` (or the end). True if ``stop`` was found."""
        s = self.s
        while self.i < self.n:
            if not self._tick():
                break
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
            if c in " \t\r\f\v":
                self._end_word()
                self.i += 1
                continue
            if self.ps:
                self._step_ps(c)
            else:
                self._step_posix(c)
        self._end_statement()
        if self.heredocs:
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
            code = self._wrapper_arg_is_code()
            end = s.find("'", self.i + 1)
            if end < 0:
                self.ctx.fail("unterminated single quote", self.lenient)
                end = self.n
            content = s[self.i + 1:end]
            self._add(content)
            self.quoted = True
            self.i = end + 1
            self._scan(content, self.depth + 1, code)
            return
        if c == '"':
            code = self._wrapper_arg_is_code()
            start = self.i + 1
            self._posix_double()
            self.quoted = True
            self._scan(s[start:self.i - 1] if self.i - 1 >= start else s[start:], self.depth + 1, code)
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
            self._scan(s[self.i + 1:end], self.depth + 1, False)
            self.i = end
            return
        if c in ";|&":
            # `&>` and `>&` are redirections, handled with `>` below.
            if c == "&" and self._peek() == ">":
                self._end_word()
                self.i += 1
                self._redirect()
                return
            self._end_statement()
            self.i += 1
            if self.i < self.n and s[self.i] in ";|&":
                self.i += 1  # &&, ||, ;;, |&
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
            if self.inword and all(ch.isdigit() for ch in "".join(self.buf)) and not self.quoted:
                self.buf, self.inword = [], False
            self._end_word()
            self._redirect()
            return
        if c == "{":
            self.brace = True
        self._add(c)
        self.i += 1

    def _redirect(self) -> None:
        s = self.s
        while self.i < self.n and s[self.i] in "<>&|":
            self.i += 1
        while self.i < self.n and s[self.i].isdigit() and s[self.i - 1] == "&":
            self.i += 1
        if self.i < self.n and s[self.i] == "-" and s[self.i - 1] == "&":
            self.i += 1
        while self.i < self.n and s[self.i] in " \t":
            self.i += 1
        self.drop_next = self.i < self.n and s[self.i] not in "\n;|&"

    def _posix_double(self) -> None:
        """Read ``"..."`` starting at the opening quote; leaves i after it."""
        s = self.s
        self.i += 1
        self.inword = True
        while self.i < self.n:
            if not self._tick():
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
        self.ctx.fail("unterminated double quote", self.lenient)

    def _posix_dollar(self, in_double: bool) -> None:
        s = self.s
        nxt = self._peek()
        if nxt == "(":
            self.i += 2
            self._add_dyn()
            self._sub(")")
            return
        if nxt == "{":
            end = _match_brace(s, self.i + 1)
            if end < 0:
                self.ctx.fail("unterminated ${", self.lenient)
                end = self.n
            self._scan(s[self.i + 2:end], self.depth + 1, self.code)
            self._add_dyn()
            self.i = end + 1
            return
        if nxt == "'" and not in_double:
            # ANSI-C quoting
            j = self.i + 2
            out = []
            while j < self.n and s[j] != "'":
                if s[j] == "\\" and j + 1 < self.n:
                    esc = s[j + 1]
                    out.append({"n": "\n", "t": "\t", "r": "\r", "'": "'", "\\": "\\", '"': '"'}.get(esc, "\\" + esc))
                    j += 2
                    continue
                out.append(s[j])
                j += 1
            if j >= self.n:
                self.ctx.fail("unterminated $' quote", self.lenient)
            content = "".join(out)
            self._add(content)
            self.quoted = True
            self.i = j + 1
            self._scan(content, self.depth + 1, self._wrapper_arg_is_code())
            return
        if nxt == '"' and not in_double:
            self.i += 1
            start = self.i + 1
            self._posix_double()
            self.quoted = True
            self._scan(s[start:self.i - 1], self.depth + 1, False)
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
        if delim:
            self.heredocs.append(("".join(delim), strip))

    def _read_heredocs(self) -> None:
        s = self.s
        for delim, strip in self.heredocs:
            body = []
            while self.i < self.n:
                end = s.find("\n", self.i)
                end = self.n if end < 0 else end
                line = s[self.i:end]
                self.i = min(end + 1, self.n)
                check = line.rstrip("\r")
                if strip:
                    check = check.lstrip("\t")
                if check == delim:
                    break
                body.append(line)
            self._scan("\n".join(body), self.depth + 1, False)
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
                self._add(nxt)
                self.i += 2
            else:
                self.i += 1
            return
        if c == "'" or c in _SMART_SINGLE:
            self._ps_single()
            return
        if c == '"' or c in _SMART_DOUBLE:
            code = self._wrapper_arg_is_code()
            start = self.i + 1
            self._ps_double()
            self.quoted = True
            self._scan(s[start:self.i - 1], self.depth + 1, code)
            return
        if c == "@" and not self.inword:
            nxt = self._peek()
            if nxt in ("'", '"') and self._here_string(nxt):
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
            self._scan(s[self.i + 1:end], self.depth + 1, False)
            self.i = end
            return
        if c == "<" and self._peek() == "#" and not self.inword:
            end = s.find("#>", self.i + 2)
            if end < 0:
                self.ctx.fail("unterminated <# comment", self.lenient)
                end = self.n
            self._scan(s[self.i + 2:end], self.depth + 1, False)
            self.i = end + 2
            return
        if c == ";" or c == "|":
            self._end_statement()
            self.i += 1
            if c == "|" and self.i < self.n and s[self.i] == "|":
                self.i += 1
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
                self.words.append(Word("&", False, False))  # call operator
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
            self.i += 1
            self._add_dyn()
            self._sub(")")
            return
        if c == "{" and not self.inword:
            self.i += 1
            self._add_dyn()
            self._sub("}")
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
        if c in _DASHES and not self.inword:
            c = "-"
        self._add(c)
        self.i += 1

    def _ps_single(self) -> None:
        s = self.s
        code = self._wrapper_arg_is_code()
        j = self.i + 1
        out = []
        closed = False
        while j < self.n:
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
            self.ctx.fail("unterminated single quote", self.lenient)
        content = "".join(out)
        self._add(content)
        self.quoted = True
        self.i = j + 1
        self._scan(content, self.depth + 1, code)

    def _ps_double(self) -> None:
        s = self.s
        self.i += 1
        self.inword = True
        while self.i < self.n:
            if not self._tick():
                return
            c = s[self.i]
            if c == '"' or c in _SMART_DOUBLE:
                if self._peek() == '"' or self._peek() in _SMART_DOUBLE and self._peek() != "":
                    self._add('"')
                    self.i += 2
                    continue
                self.i += 1
                return
            if c == "`":
                nxt = self._peek()
                if nxt:
                    self._add({"n": "\n", "t": "\t", "r": "\r", "0": ""}.get(nxt, nxt))
                    self.i += 2
                else:
                    self.i += 1
                continue
            if c == "$":
                self._ps_dollar()
                continue
            self._add(c)
            self.i += 1
        self.ctx.fail("unterminated double quote", self.lenient)

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
                self.ctx.fail("unterminated ${", self.lenient)
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
        j = self.i + 2
        while j < self.n and s[j] in " \t":
            j += 1
        if j < self.n and s[j] == "\r":
            j += 1
        if j >= self.n or s[j] != "\n":
            return False
        body_start = j + 1
        closer = "\n" + quote + "@"
        end = s.find(closer, j)
        if end < 0:
            self.ctx.fail("unterminated here-string", self.lenient)
            body = s[body_start:]
            self.i = self.n
        else:
            body = s[body_start:end]
            self.i = end + len(closer)
        body = body[:-1] if body.endswith("\r") else body
        if quote == '"' and "$" in body:
            self._add(body.replace("$", DYN))
            self.dyn = True
        else:
            self._add(body)
        self.quoted = True
        self._scan(body, self.depth + 1, self._wrapper_arg_is_code())
        return True


def _match_brace(s: str, start: int) -> int:
    """Index of the ``}`` matching the ``{`` at ``start`` (or -1)."""
    depth = 0
    for j in range(start, len(s)):
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


def read(text: str, dialect: str) -> Result:
    """Read ``text`` as top-level code in ``dialect`` (``posix`` or ``ps``)."""
    result = Result()
    if not isinstance(text, str):
        result.complete = False
        result.problems.append("command is not text")
        return result
    if len(text) > MAX_TEXT_CHARS:
        result.complete = False
        result.problems.append("command longer than the length cap")
        text = text[:MAX_TEXT_CHARS]
    ctx = _Ctx(result)
    ctx.seen.add((text, dialect, True))
    _Reader(ctx, text, dialect, 0, lenient=False, code=True).parse(None)
    return result


def is_assignment(word: Word) -> bool:
    return bool(_ASSIGNMENT_RE.match(word.text)) and not word.quoted


# Words that run the word after them as a program.
COMMAND_PREFIXES = {
    "env", "command", "exec", "nohup", "time", "sudo", "nice", "xargs", "builtin",
    "then", "do", "else", "elif", "if", "while", "until", "!", "timeout", "stdbuf",
    "&", ".", "start-process", "saps", "start", "call", "invoke-item", "ii", "wsl",
    "iex", "invoke-expression", "invoke-command", "icm", "eval", "source",
}


def command_positions(stmt: Statement):
    """Indices of the words that are run as programs in ``stmt``: the first
    word after assignments, and the word after a prefix such as ``env``,
    ``sudo``, ``&`` or ``Start-Process`` (options of the prefix skipped)."""
    words = stmt.words
    out = []
    i = 0
    expect = True
    after_prefix = False
    while i < len(words):
        w = words[i]
        if expect:
            if is_assignment(w) and stmt.dialect == "posix":
                i += 1
                continue
            if stmt.dialect == "ps" and w.dynamic and not after_prefix:
                break  # `$x ...` / `(...)` at the start is an expression, not a call
            name = w.text.lower() if not w.dynamic else None
            if name in COMMAND_PREFIXES:
                after_prefix = True
                i += 1
                # skip options and assignments of the prefix (env -i X=1 cmd)
                while i < len(words) and not words[i].dynamic and (
                    words[i].text.startswith("-") or is_assignment(words[i])
                ) and words[i].text not in ("--",):
                    i += 1
                if i < len(words) and words[i].text == "--":
                    i += 1
                continue
            out.append(i)
            expect = False
        i += 1
    return out
