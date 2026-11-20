"""Unit tests for the shell command reader shared by the PreToolUse hooks."""
from __future__ import annotations

import base64
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / ".claude" / "hooks"))
import _shellparse as sp  # noqa: E402

D = sp.DYN


def code(text, dialect):
    return [s.texts for s in sp.read(text, dialect).statements if s.code and s.level == 0]


def every(text, dialect):
    return [s.texts for s in sp.read(text, dialect).statements]


@pytest.mark.parametrize("text,expected", [
    ("a 'b c' \"d e\"", [["a", "b c", "d e"]]),
    ("a 'it''s'", [["a", "it's"]]),
    ('a "say ""hi"""', [["a", 'say "hi"']]),
    ('a "x`"y"', [["a", 'x"y']]),
    ("p`pds x", [["ppds", "x"]]),
    ("a `\n  b", [["a", "b"]]),
    ("a; b && c || d | e", [["a"], ["b"], ["c"], ["d"], ["e"]]),
    ("& 'C:\\Program Files\\x.exe' y", [["&", "C:\\Program Files\\x.exe", "y"]]),
    ("C:\\tools\\ppds.exe a", [["C:\\tools\\ppds.exe", "a"]]),
    ("a b,c", [["a", "b", "c"]]),
    ("a $env:X b", [["a", D, "b"]]),
    ("a --% -f %X%", [["a", "-f", D]]),
    ("a \u2013f", [["a", "-f"]]),
    ("a @'\nline 'one'\n'@ b", [["a", "line 'one'", "b"]]),
])
def test_powershell_words(text, expected):
    assert code(text, "ps") == expected


@pytest.mark.parametrize("text,expected", [
    ("a 'b c' \"d e\"", [["a", "b c", "d e"]]),
    ("a 'it'\\''s'", [["a", "it's"]]),
    ('a "x\\"y"', [["a", 'x"y']]),
    ("p\\pds x", [["ppds", "x"]]),
    ("a \\\n  b", [["a", "b"]]),
    ("a; b && c || d | e & f", [["a"], ["b"], ["c"], ["d"], ["e"], ["f"]]),
    ("X=1 a $Y \"$Z\"", [["X=1", "a", D, D]]),
    ("a $'x\\ty'", [["a", "x\ty"]]),
    ("a > out.txt b", [["a", "b"]]),
    ("a 2>&1", [["a"]]),
    ("a {x,y}", [["a", "{x,y}"]]),
])
def test_posix_words(text, expected):
    assert code(text, "posix") == expected


def test_posix_brace_expansion_is_dynamic():
    word = sp.read("git push origin {main,v1}", "posix").statements[-1].words[-1]
    assert word.dynamic


@pytest.mark.parametrize("dialect", ["posix", "ps"])
def test_substitutions_are_read_as_code(dialect):
    statements = sp.read("echo $(git push -f)", dialect).statements
    assert any(s.code and s.texts == ["git", "push", "-f"] for s in statements)


def test_backticks_are_substitutions_in_posix():
    assert ["git", "push", "-f"] in every("echo `git push -f`", "posix")


def test_script_blocks_are_code_in_powershell():
    statements = sp.read("& { git push -f }", "ps").statements
    assert any(s.code and s.texts == ["git", "push", "-f"] for s in statements)


@pytest.mark.parametrize("dialect", ["posix", "ps"])
def test_strings_are_also_read_as_statements(dialect):
    assert ["git", "push", "-f"] in every("echo 'x; git push -f'", dialect)


@pytest.mark.parametrize("text,dialect", [
    ("bash -c 'git push -f'", "posix"),
    ("pwsh -NoProfile -Command \"git push -f\"", "ps"),
    ("cmd /c \"git push -f\"", "ps"),
    ("iex 'git push -f'", "ps"),
])
def test_wrapper_arguments_are_code(text, dialect):
    statements = sp.read(text, dialect).statements
    assert any(s.code and s.texts == ["git", "push", "-f"] for s in statements)


def test_heredoc_body_is_read_but_its_quotes_do_not_break_the_command():
    text = "git commit -m \"$(cat <<'EOF'\nIt's fine\nEOF\n)\"\ngit status"
    result = sp.read(text, "posix")
    assert result.complete
    assert ["git", "status"] in [s.texts for s in result.statements]


def test_encoded_command_is_decoded():
    enc = base64.b64encode("git push -f".encode("utf-16-le")).decode()
    statements = sp.read(f"pwsh -EncodedCommand {enc}", "ps").statements
    assert any(s.code and s.texts == ["git", "push", "-f"] for s in statements)


def test_undecodable_encoded_command_is_incomplete():
    result = sp.read("pwsh -enc !!", "ps")
    assert not result.complete


@pytest.mark.parametrize("text,dialect", [
    ("a 'unterminated", "posix"), ("a \"unterminated", "ps"), ("a $(b", "posix"), ("a <# b", "ps"),
])
def test_unterminated_text_is_incomplete(text, dialect):
    assert not sp.read(text, dialect).complete


def test_unterminated_quote_inside_a_string_is_lenient():
    assert sp.read("git commit -m \"don't\"", "posix").complete
    assert sp.read("git commit -m \"don't\"", "ps").complete


def test_caps():
    assert not sp.read("a" * (sp.MAX_TEXT_CHARS + 1), "posix").complete
    nested = "a " + "$(" * (sp.MAX_DEPTH + 2) + "b" + ")" * (sp.MAX_DEPTH + 2)
    assert not sp.read(nested, "posix").complete


def test_posix_subshell_raises_the_level_powershell_blocks_do_not():
    posix = {tuple(s.texts): s.level for s in sp.read("(cd x); y", "posix").statements}
    assert posix[("cd", "x")] == 1 and posix[("y",)] == 0
    ps = {tuple(s.texts): s.level for s in sp.read("& { Set-Location x }; y", "ps").statements}
    assert ps[("Set-Location", "x")] == 0


@pytest.mark.parametrize("text,dialect,expected", [
    ("env A=1 git push", "posix", [2]),
    ("X=1 $cmd a", "posix", [1]),
    ("& $exe a", "ps", [1]),
    ("$x = 1", "ps", []),
    ("Start-Process git -ArgumentList x", "ps", [1]),
])
def test_command_positions(text, dialect, expected):
    stmt = sp.read(text, dialect).statements[-1]
    assert sp.command_positions(stmt) == expected
