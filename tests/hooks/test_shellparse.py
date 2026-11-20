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
    ("env A=1 git push", "posix", [(2, False)]),
    ("X=1 $cmd a", "posix", [(1, False)]),
    ("& $exe a", "ps", [(1, False)]),
    ("$x.Count", "ps", []),
    ("$r = git rev-parse HEAD", "ps", [(2, False)]),
    ("Start-Process git -ArgumentList x", "ps", [(1, True)]),
    ("xargs -0 git push", "posix", [(2, True)]),
    ("sudo git push", "posix", [(1, False)]),
])
def test_command_positions(text, dialect, expected):
    """(index, arguments only known at run time) for each program run."""
    stmt = sp.read(text, dialect).statements[-1]
    assert sp.command_positions(stmt) == expected


# -- review round 1 of #1815 (reader findings) ---------------------------------


@pytest.mark.parametrize("dialect", ["posix", "ps"])
@pytest.mark.parametrize("text,expected", [
    ("git push origin main 2>&1 --force", ["git", "push", "origin", "main", "--force"]),
    ("git tag 2>&1 v1", ["git", "tag", "v1"]),
    ("git reset >&2 --hard", ["git", "reset", "--hard"]),
    ("git push 2>&- --tags", ["git", "push", "--tags"]),
    ("git push <&0 --tags", ["git", "push", "--tags"]),
    ("git push > out.txt --force", ["git", "push", "--force"]),
])
def test_descriptor_redirects_keep_the_next_word(dialect, text, expected):
    """M1: `2>&1`, `>&2`, `2>&-`, `<&0` name a descriptor, not a file."""
    assert code(text, dialect)[-1] == expected


def test_file_redirect_target_is_recorded_not_a_word():
    stmt = sp.read("git rev-parse HEAD > .git/refs/tags/v1", "posix").statements[-1]
    assert stmt.texts == ["git", "rev-parse", "HEAD"]
    assert [w.text for w in stmt.redirects] == [".git/refs/tags/v1"]


@pytest.mark.parametrize("text,expected", [
    ("git commit -m { -n", ["git", "commit", "-m", "{", "-n"]),
    ("git commit -m x -m } --no-verify", ["git", "commit", "-m", "x", "-m", "}", "--no-verify"]),
])
def test_a_lone_brace_argument_does_not_end_the_statement(text, expected):
    """M3: only a `{` or `}` where a command starts is a group."""
    assert code(text, "posix") == [expected]


def test_brace_group_still_groups():
    assert code("{ git push -f; }", "posix") == [["git", "push", "-f"]]


@pytest.mark.parametrize("text,expected", [
    ("$'\\x67it' push", ["git", "push"]), ("$'\\147it' push", ["git", "push"]),
    ("$'\\u0067it' push", ["git", "push"]), ("$'\\U00000067it' push", ["git", "push"]),
    ("git push $'--f\\x6frce'", ["git", "push", "--force"]), ("x $'\\e\\a\\cA'", ["x", "\x1b\a\x01"]),
])
def test_ansi_c_escapes(text, expected):
    """M4: every ANSI-C escape bash decodes."""
    assert code(text, "posix") == [expected]


@pytest.mark.parametrize("text,expected", [
    ('git push "--`u{66}orce"', ["git", "push", "--force"]), ('& "`u{67}it" push', ["&", "git", "push"]),
    ('x "`e`a`b`f`v"', ["x", "\x1b\a\b\f\v"]),
])
def test_powershell_escapes(text, expected):
    """M4: PowerShell `u{...} and the other backtick escapes."""
    assert code(text, "ps") == [expected]


@pytest.mark.parametrize("blank", [" ", " ", " ", "　", " "])
def test_powershell_splits_on_unicode_spaces(blank):
    """M5: pwsh splits on every Unicode space; bash does not."""
    assert code(f"git{blank}push{blank}--force", "ps") == [["git", "push", "--force"]]
    assert code(f"git{blank}push", "posix") == [[f"git{blank}push"]]


@pytest.mark.parametrize("text,dialect", [
    ("eval 'git pu''sh --force'", "posix"), ("bash -c 'git pu''sh --force'", "posix"),
    ("bash -c git\\ push\\ --force", "posix"), ("eval git\\ push\\ --force", "posix"),
    ("cmd /c git push --force", "ps"), ("cmd /v:on /c git push --force", "ps"),
    ("powershell \"git push --force\"", "ps"), ("pwsh -NoProfile 'git push --force'", "ps"),
])
def test_wrapper_code_is_read_as_one_joined_text(text, dialect):
    """M8: the joined argument, not each quoted piece, is the code."""
    statements = sp.read(text, dialect).statements
    assert any(s.code and s.wrapped and s.texts == ["git", "push", "--force"] for s in statements), \
        [(s.texts, s.code, s.wrapped) for s in statements]


@pytest.mark.parametrize("flag", ["-EncodedCommand", "--EncodedCommand", "/EncodedCommand", "/enc", "/e",
                                  "-e", "-ec", "-enc", "-encodedc"])
def test_every_encoded_command_spelling_is_decoded(flag):
    enc = base64.b64encode("git push -f".encode("utf-16-le")).decode()
    statements = sp.read(f"pwsh {flag} {enc}", "ps").statements
    assert any(s.code and s.texts == ["git", "push", "-f"] for s in statements)


@pytest.mark.parametrize("text,dialect", [("eval \"$x\"", "posix"), ("iex $x", "ps"),
                                          ("bash -c \"$CMD\"", "posix"), ("cmd /c %X%", "ps")])
def test_run_time_code_for_a_wrapper_is_flagged(text, dialect):
    assert any(s.dynamic_code for s in sp.read(text, dialect).statements)


def test_pipe_into_a_shell_is_flagged():
    statements = sp.read("echo x | bash", "posix").statements
    assert [s.piped for s in statements] == [False, True]


def test_a_here_document_fed_to_a_shell_is_code():
    statements = sp.read("bash <<'EOF'\ngit push -f\nEOF", "posix").statements
    assert any(s.code and s.texts == ["git", "push", "-f"] for s in statements)


def test_cmd_variables_are_run_time_text():
    word = sp.read("git push origin main %F%", "ps").statements[-1].words[-1]
    assert word.dynamic


def test_a_quoted_substitution_is_one_argument():
    stmt = sp.read("git commit -m \"$(cat <<'EOF'\nmsg\nEOF\n)\"", "posix").statements[-1]
    assert stmt.words[-1].dynamic and stmt.words[-1].split_safe
    unquoted = sp.read("git commit -m $(echo x)", "posix").statements[-1]
    assert unquoted.words[-1].dynamic and not unquoted.words[-1].split_safe


@pytest.mark.parametrize("dialect", ["posix", "ps"])
def test_reading_is_linear_and_bounded(dialect):
    import time

    for text in ("echo " + "'a' " * 20000, "echo {" + "," * 190000, "a " * 90000):
        start = time.monotonic()
        sp.read(text, dialect)
        assert time.monotonic() - start < 3


def test_the_deadline_stops_the_reading():
    import time

    result = sp.read("echo " + "'a' " * 40000, "posix", deadline=time.monotonic() - 1)
    assert not result.complete
    assert any("time cap" in p for p in result.problems)


# -- spans: where each word sits in the top-level text ---------------------------


@pytest.mark.parametrize("dialect", ["posix", "ps"])
def test_word_spans_index_the_top_level_text(dialect):
    text = "git commit -m 'fix ppds' && echo \"a $(ppds env list) b\""
    result = sp.read(text, dialect)
    for stmt in result.statements:
        for word in stmt.words:
            if word.span is not None:
                a, b = word.span
                assert 0 <= a <= b <= len(text)
    spans = {text[w.span[0]:w.span[1]] for s in result.statements for w in s.words if w.span}
    assert "ppds" in spans and "git" in spans and "'fix ppds'" in spans


def test_decoded_text_has_no_spans():
    stmts = sp.read("bash -c 'ppds env list'", "posix").statements
    wrapped = [s for s in stmts if s.wrapped]
    assert wrapped and all(w.span is None for s in wrapped for w in s.words)


def test_a_failure_inside_a_string_marks_only_that_unit():
    result = sp.read("git commit -m \"don't\" && ppds env list", "posix")
    assert result.complete and result.top.ok
    assert any(not unit.ok for unit in result.top.children)


def test_string_units_record_the_whole_construct():
    text = "x --m=\"ppds env\""
    unit = next(u for u in sp.read(text, "posix").top.children if u.kind == "string")
    assert text[unit.outer[0]:unit.outer[1]] == "\"ppds env\""


# -- PowerShell forms the reader splits ------------------------------------------


@pytest.mark.parametrize("text,expected", [
    ("if($true){ppds data delete}", ["ppds", "data", "delete"]),
    ("1..3|%{ppds data delete}", ["ppds", "data", "delete"]),
    ("function f{ppds data delete};f", ["ppds", "data", "delete"]),
    ("switch(1){1{ppds data delete}}", ["ppds", "data", "delete"]),
])
def test_a_glued_script_block_is_code(text, expected):
    assert expected in [s.texts for s in sp.read(text, "ps").statements if s.code]


@pytest.mark.parametrize("text", ["$r=ppds x", "$r+=ppds x", "[string]$r=ppds x", "$x.y=ppds x",
                                  "$env:OUT=ppds x", "$r =ppds x", "$r ??= ppds x"])
def test_an_assignment_makes_the_command_after_it_a_command_position(text):
    stmt = sp.read(text, "ps").statements[-1]
    positions = [i for i, _ in sp.command_positions(stmt)]
    assert [stmt.words[i].text for i in positions] == ["ppds"]


def test_a_name_ends_at_a_glued_parenthesis():
    stmt = sp.read("ppds.exe('data','delete')", "ps").statements[-1]
    assert stmt.words[0].text == "ppds.exe"


@pytest.mark.parametrize("text,dialect,glob", [
    ("/usr/bin/ppd? x", "posix", True), ("/usr/bin/pp[d]s x", "posix", True),
    ("ppds x", "posix", False), ("'ppd?' x", "posix", False), ("ppd* x", "ps", True),
])
def test_glob_characters_mark_the_word(text, dialect, glob):
    assert sp.read(text, dialect).statements[-1].words[0].glob is glob


# -- wrappers ---------------------------------------------------------------------


@pytest.mark.parametrize("text,dialect", [
    ("pwsh -Command ppds data delete", "ps"), ("powershell ppds data delete", "ps"),
    ("cmd /c ppds data delete", "ps"), ("eval ppds data delete", "posix"),
    ("wsl -e ppds data delete", "posix"),
])
def test_every_argument_after_the_code_flag_is_code(text, dialect):
    stmts = sp.read(text, dialect).statements
    assert ["ppds", "data", "delete"] in [s.texts for s in stmts if s.wrapped]


@pytest.mark.parametrize("text,dialect,code_arg", [
    ("bash -c 'ppds'", "posix", True), ("bash script.sh", "posix", False), ("bash", "posix", False),
    ("pwsh -File x.ps1", "ps", False), ("pwsh x.ps1", "ps", False), ("powershell 'ppds'", "ps", True),
    ("$c | iex", "ps", False), ("pwsh -c -", "ps", False),
])
def test_a_wrapper_says_whether_its_code_is_in_the_text(text, dialect, code_arg):
    stmt = [s for s in sp.read(text, dialect).statements if not s.wrapped][-1]
    assert stmt.wrapper is not None
    assert stmt.code_arg is code_arg


def test_a_here_string_into_a_shell_is_flagged():
    stmt = sp.read('sh <<< "$c"', "posix").statements[-1]
    assert stmt.wrapper == "sh" and stmt.stdin_text


@pytest.mark.parametrize("text", [
    "env FOO=1 bash <<'EOF'\nppds env list\nEOF",
    "FOO=1 bash <<'EOF'\nppds env list\nEOF",
    "timeout 5 sh <<EOF\nppds env list\nEOF",
])
def test_a_here_document_fed_to_a_shell_after_a_prefix_is_code(text):
    # the shell is not the first word: the reader must still take the
    # here-document as code (`first_code or self.shell is not None`)
    statements = sp.read(text, "posix").statements
    body = [st for st in statements if st.texts == ["ppds", "env", "list"]]
    assert body and all(st.code for st in body)
    assert not any(st.stdin_text for st in statements)


@pytest.mark.parametrize("text,expected", [
    ("xargs -n 4 git push", [(3, True)]),
    ("xargs -n4 -P 2 git push", [(4, True)]),
    ("xargs --max-args=4 git push", [(2, True)]),
    ("xargs -I {} git push {}", [(3, True)]),
    ("timeout 5 git push", [(2, False)]),
    ("timeout -s KILL 5 git push", [(4, False)]),
    ("sudo -u me git push", [(3, False)]),
    ("nice -n 5 git push", [(3, False)]),
    ("find . -name x -exec git add {} ;", [(5, True)]),
    # an option the reader does not know: every later word may be the program
    ("xargs --frob 4 git push", [(2, True), (3, True), (4, True)]),
    ("parallel -j 4 git push", [(1, True), (2, True), (3, True), (4, True)]),
])
def test_prefix_options_and_operands_are_read(text, expected):
    stmt = sp.read(text, "posix").statements[-1]
    assert sp.command_positions(stmt) == expected
