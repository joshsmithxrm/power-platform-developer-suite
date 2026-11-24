"""Pins the decisions of the shakedown-safety hook that round 2 of the review
of #1814 found unpinned (mutants that survived), and the argument-level
principle of round 2: a pass is granted only on a positively recognised
shape; everything the hook cannot place is refused.

* every class of the write-block table (a copy of it is kept here, so a
  classification changes only with a test);
* the SQL write words one by one, and how the T-SQL lexer splits words;
* where a help flag, ``--dry-run``, ``--read-only``, ``--list`` / ``--show``
  count;
* the mention-accounting decisions and the nesting cap;
* the environment gate at the argument level: target options with every
  value delimiter, profiles, ``auth select``, response files, run-time
  text, labels in SQL, ``PPDS_`` variables, bare ``ppds``.

Run: ``pytest tests/hooks/test_shakedown_decisions.py -q``
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
HOOK_PATH = ROOT / ".claude" / "hooks" / "shakedown-safety.py"
sys.path.insert(0, str(HOOK_PATH.parent))
_spec = importlib.util.spec_from_file_location("shakedown_safety_decisions", str(HOOK_PATH))
hook = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hook)

P = "ppds"
DYN = hook.sp.DYN


def _argv(text: str) -> list:
    return ["ppds"] + text.split()


# -- the table, class by class --------------------------------------------------------

# A copy of the write-block table. Changing a classification in the hook
# fails this test until the copy is changed too: a deliberate act.
TABLE = """
.: api=g auth=g connection-references=g connections=g custom-apis=g data-providers=g data-sources=g data=g deployment-settings=g docs=r env=g environment-variables=g flows=g forms=g import-jobs=g interactive=w internal=w logs=g metadata=g model-driven-app=g plugin-traces=g plugins=g publish=w query=g roles=g schema=g serve=w service-endpoints=g solutions=g users=g version=ver views=g webresources=g
api: request=w
auth: clear=w create=w delete=w list=r name=w select=w update=w who=r
connection-references: analyze=r connections=r flows=r get=r list=r
connections: get=r list=r
custom-apis: add-parameter=w get=r list=r register=w remove-parameter=w set-plugin=w unregister=w update-parameter=w update=w
data: analyze=r copy=w delete=w export=r import=w load=w schema=r truncate=w update=w users=r
data-providers: get=r list=r register=w unregister=w update=w
data-sources: get=r list=r register=w unregister=w update=w
deployment-settings: generate=r sync=w validate=r
env: config=list list=r select=switch type=list who=r
environment-variables: export=r get=r list=r set=w url=r
flows: disable=w enable=w get=r list=r url=r
forms: add-field=w add-section=w add-subgrid=w add-tab=w find-section=r find-tab=r get=r list=r move-field=w remove-field=w remove-section=w remove-subgrid=w remove-tab=w reorder-fields-strict=w reorder-fields=w repair-bindings=w set-xml=w update-section=w update-tab=w
import-jobs: data=r get=r list=r url=r wait=r
logs: dump=r tail=r
metadata: attribute=g attributes=r choice=g column=g entities=r entity=r key=g keys=r optionset=r optionsets=r publish=w relationship=g relationships=r table=g
metadata attribute: add-option=w create=w delete=w remove-option=w update-option=w update=w
metadata choice: add-option=w create=w delete=w remove-option=w reorder=w update-option=w update=w
metadata column: create=w delete=w update=w
metadata entity: add-statusreason=w create=w delete=w list-statusreasons=r remove-statusreason=w update-statusreason=w update=w
metadata key: create=w delete=w reactivate=w
metadata optionset: add-option=w create=w delete=w remove-option=w reorder=w update-option=w update=w
metadata relationship: create=w delete=w update=w
metadata table: create=w delete=w update=w
model-driven-app: add-copilot=w add-table=w get=r inspect-app-assistant=r list-copilots=r list=r remove-copilot=w remove-table=w set-charts=w set-forms=w set-sitemap-xml=w set-views=w sitemap=r
plugin-traces: delete=w get=r list=r related=r settings=g timeline=r
plugin-traces settings: get=r set=w
plugins: clean=w deploy=dry-run diff=r disable=w download=g enable=w extract=r get=r list=r register=g unregister=w update=g
plugins download: assembly=r package=r
plugins register: assembly=w image=w package=w step=w type=w
plugins update: assembly=w image=w package=w step=w
query: explain=r fetch=r history=g sql=sql
query history: clear=w delete=w execute=w get=r list=r
roles: assign=w list=r remove=w show=r
schema: compare=r
service-endpoints: get=r list=r register=g unregister=w update=w
service-endpoints register: eventhub=w queue=w topic=w webhook=w
solutions: components=r export=r get=r import=w list=r publish=w url=r
users: list=r roles=r show=r
views: add-column=w clear-filter=w clear-sort=w get=r list=r remove-column=w reorder-columns=w set-fetchxml=w set-filter=w set-sort=w update-column=w
webresources: create=w get=r list=r publish=w pull=r push=w update=w url=r
"""


def _parse(text: str) -> dict:
    out = {(): "g"}
    for line in text.strip().splitlines():
        parent, _, children = line.partition(":")
        prefix = () if parent.strip() == "." else tuple(parent.split())
        for item in children.split():
            name, _, cls = item.partition("=")
            out[prefix + (name,)] = cls
    return out


EXPECTED = _parse(TABLE)


def test_the_table_is_the_reviewed_one():
    assert hook.CLI_COMMANDS == EXPECTED


@pytest.mark.parametrize("path", sorted(p for p, c in EXPECTED.items() if c == "w"),
                         ids=lambda p: " ".join(p))
def test_every_write_path_is_blocked(path):
    for extra in ([], ["--name", "x"], ["--dry-run"], ["--list"]):
        assert hook.is_mutation(["ppds", *path, *extra])[0], (path, extra)


@pytest.mark.parametrize("path", sorted(p for p, c in EXPECTED.items() if c == "r"),
                         ids=lambda p: " ".join(p))
def test_every_read_path_passes(path):
    assert not hook.is_mutation(["ppds", *path])[0], path


@pytest.mark.parametrize("path", sorted(p for p, c in EXPECTED.items() if c == "g" and p),
                         ids=lambda p: " ".join(p))
def test_every_group_passes_alone_and_blocks_an_unknown_child(path):
    assert not hook.is_mutation(["ppds", *path])[0], path
    assert hook.is_mutation(["ppds", *path, "frobnicate"])[0], path


# Arguments that fail each conditional class's condition.
_FAILING = {"sql": ["DELETE FROM account"], "dry-run": [], "list": ["--remove"], "ver": ["--update"],
            "switch": []}


@pytest.mark.parametrize("path", sorted(p for p, c in EXPECTED.items() if c not in ("r", "w", "g")),
                         ids=lambda p: " ".join(p))
def test_every_conditional_path_blocks_without_its_condition(path):
    assert hook.is_mutation(["ppds", *path, *_FAILING[EXPECTED[path]]])[0], path


# -- bare start, version, env select -------------------------------------------------


@pytest.mark.parametrize("argv,blocked", [
    (["ppds"], True),                                   # starts the TUI
    (["ppds.exe"], True),
    (["ppds", "--help"], False), (["ppds", "-h"], False), (["ppds", "--version"], False),
    (["ppds", "-?"], False), (["ppds", "/?"], False), (["ppds", "/h"], False),
    (["ppds", "version"], False), (["ppds", "version", "--check"], False),
    (["ppds", "version", "--update"], True), (["ppds", "version", "--update", "--yes"], True),
    (["ppds", "version", "--check", "--update"], True), (["ppds", "version", "--stable"], True),
    (["ppds", "version", "--check=true"], True),
])
def test_root_and_version(argv, blocked):
    assert hook.is_mutation(argv)[0] is blocked, argv


def test_env_select_passes_the_write_block_only_with_an_allowlist():
    argv = ["ppds", "env", "select", "--environment", "x"]
    assert hook.is_mutation(argv, allowlist_configured=True)[0] is False
    assert hook.is_mutation(argv, allowlist_configured=False)[0] is True
    assert hook.is_mutation(argv)[0] is True


# -- help flags ------------------------------------------------------------------------


@pytest.mark.parametrize("text,blocked", [
    ("data delete --help", False), ("data delete -h", False), ("data delete -?", False),
    ("data delete /?", False), ("data delete /h", False), ("plugins deploy --help", False),
    ("data --help delete", False),
    ("data delete --entity x --help", True),          # only directly after the command path
    ("data delete --help=x", True), ("data delete --HELP", True),
    ("data delete x --help", True), ("data delete --force", True),
    ("data delete --version", True),                  # a root option only
    ("data delete --entity --help", True),
])
def test_help_flags_count_only_directly_after_the_path(text, blocked):
    assert hook.is_mutation(_argv(text))[0] is blocked, text


# -- --dry-run -------------------------------------------------------------------------


@pytest.mark.parametrize("text,blocked", [
    ("plugins deploy --dry-run", False),
    ("plugins deploy --config c.json --dry-run", False),
    ("plugins deploy --dry-run --config c.json", False),
    ("plugins deploy --dry-run -v", False),
    ("plugins deploy --correlation-id --dry-run", True),
    ("plugins deploy -env --dry-run", True),
    ("plugins deploy --solution --dry-run", True),
    ("plugins deploy /x --dry-run", True),
    ("plugins deploy --dry-run false", True),
    ("plugins deploy --dry-run true", True),
    ("plugins deploy --dry-run x", True),
    ("plugins deploy --dry-run=true", True), ("plugins deploy --dry-run:true", True),
    ("plugins deploy --dry-run=false", True), ("plugins deploy --DRY-RUN", True),
    ("plugins deploy --dry-runx", True),
    ("plugins deploy -- --dry-run", True),
    ("plugins deploy --dry-run --dry-run", True),
    ("plugins deploy -n", True),
])
def test_dry_run_counts_only_when_positively_placed(text, blocked):
    assert hook.is_mutation(_argv(text))[0] is blocked, text


def test_dry_run_with_a_spaced_false_is_blocked():
    assert hook.is_mutation(["ppds", "plugins", "deploy", "--dry-run", " false"])[0]
    assert hook.is_mutation(["ppds", "plugins", "deploy", "--dry-run", "\tfalse"])[0]


# -- --read-only ----------------------------------------------------------------------


@pytest.mark.parametrize("args,blocked", [
    (["--read-only"], False),
    (["--read-only", "--allowed-env", "x"], False),
    (["--environment", "x", "--read-only"], False),
    (["--allowed-env", "--read-only"], True),           # taken as the option's value
    (["--profile", "--read-only"], True),
    (["--environment", "--read-only"], True),
    (["--allowed-env", "a", "--allowed-env", "--read-only"], True),
    (["--read-only=true"], True), (["--read-only:true"], True), (["--read-only="], True),
    (["--read-onlyx"], True), (["--READ-ONLY"], True), ([], True),
    (["--read-only", "--read-only=false"], True),
])
def test_mcp_read_only_as_its_parser_reads_it(args, blocked):
    assert hook.is_mutation(["ppds-mcp-server", *args])[0] is blocked, args


def test_mcp_read_only_through_dotnet_run_either_order():
    for command in ("dotnet run --project src/PPDS.Mcp -- --allowed-env --read-only",
                    "dotnet run --project src/PPDS.Mcp --read-only -- --allowed-env",
                    "dotnet run --project src/PPDS.Mcp --allowed-env --read-only"):
        found = hook.find_ppds_invocations(command, "Bash", None)
        assert any(hook.is_mutation(a)[0] for a in found), command
    found = hook.find_ppds_invocations("dotnet run --project src/PPDS.Mcp -- --read-only", "Bash", None)
    assert found and not any(hook.is_mutation(a)[0] for a in found)


# -- env config / env type ------------------------------------------------------------


@pytest.mark.parametrize("text,blocked", [
    ("env config", False), ("env config --list", False), ("env config --show", False),
    ("env config -s", False), ("env config https://x", False), ("env config https://x --show", False),
    ("env config https://x -s", False),
    ("env config --show https://x", True),              # the flag might take the URL as its value
    ("env config --list --remove", True), ("env config --remove --list", True),
    ("env config --list x", True), ("env config https://x --label --show", True),
    ("env config https://x --label y", True), ("env config https://x --remove", True),
    ("env config https://x --type Production", True), ("env config --list=true", True),
    ("env type", False), ("env type --list", False),
    ("env type --list --remove", True), ("env type Production", True),
    ("env type Production --color Red", True), ("env type --remove x", True),
])
def test_env_settings_pass_only_in_the_forms_that_read(text, blocked):
    assert hook.is_mutation(_argv(text))[0] is blocked, text


# -- SQL --------------------------------------------------------------------------------

SQL_WORDS = ("insert update delete merge upsert truncate drop create alter exec execute into grant "
             "revoke deny bulk openrowset declare set").split()


def test_the_write_words_are_the_reviewed_ones():
    assert hook._SQL_WRITE_WORDS == frozenset(SQL_WORDS)


@pytest.mark.parametrize("word", SQL_WORDS)
def test_each_write_word_blocks(word):
    for sql in (f"SELECT 1 {word} x", f"SELECT 1 {word.upper()} x", f"SELECT 1;{word}",
                f"SELECT 1{word} x", f"SELECT 1.5{word}", f"SELECT 1e5{word}", f"SELECT ({word})",
                f"-- note\n{word} x", f"/* c */{word}", f"SELECT '{word}'", f"SELECT [{word}]",
                f"SELECT é{word}", f"SELECT 1 {word}é"):
        assert hook.is_mutation(["ppds", "query", "sql", sql])[0], sql
        assert hook.sql_write_word(sql) == word, sql


@pytest.mark.parametrize("sql", [
    "SELECT name FROM account", "SELECT isdeleted, createdon FROM account",
    "SELECT offset1, set_x, x_set, xset, setx FROM account", "SELECT @set, #delete FROM t",
    "SELECT name FROM account WHERE x1delete = 1", "SELECT 0xDE AS lete",
])
def test_words_inside_longer_identifiers_do_not_block(sql):
    assert not hook.is_mutation(["ppds", "query", "sql", sql])[0], sql


@pytest.mark.parametrize("sql,word", [
    ("SELECT 0x12UPDATE", "update"),      # a hex literal ends where its hex digits end
    ("SELECT 0xAAINSERT x", "insert"),
    ("SELECT 0xAMERGE x", "merge"),
    ("SELECT 1DELETE account", "delete"),
    ("SELECT 1.DELETE", "delete"),
    # an exponent marker with no digits ends the number (the T-SQL lexer
    # reads `SELECT 1eDELETE FROM account` as a SELECT and a DELETE)
    ("SELECT 1eDELETE FROM account", "delete"),
    ("SELECT 1.eDELETE FROM account", "delete"),
    ("SELECT 1EDELETE FROM account", "delete"),
    ("SELECT 1.5eDELETE FROM account", "delete"),
    ("SELECT 1e+DELETE FROM account", "delete"),
    ("SELECT 1e-UPDATE x", "update"),
    ("SELECT 1e5DELETE FROM account", "delete"),
    ("SELECT 1exec", "exec"),                  # the word starting at the marker is read too
])
def test_a_number_glued_to_a_word_splits_like_the_lexer(sql, word):
    assert hook.sql_write_word(sql) == word


@pytest.mark.parametrize("args,blocked", [
    (["SELECT 1", "-f", "json"], False),                  # -f is --output-format on query sql
    (["SELECT 1", "--output-format", "json"], False),
    (["-- c\nDELETE FROM account", "--output-format", "json"], True),
    (["--file", "q.sql"], True), (["--file=q.sql"], True), (["--file:q.sql"], True),
    (["--stdin"], True), (["--stdin=true"], True), (["--stdin:true"], True),
    (["SELECT 1", "--stdin"], True), ([], True), ([f"SELECT {DYN}"], True),
])
def test_query_sql_reads_every_argument(args, blocked):
    assert hook.is_mutation(["ppds", "query", "sql", *args])[0] is blocked, args


# The SQL text passes only when it is positively a read, placed as the CLI's
# parser places it: one positional text that begins with SELECT or WITH after
# blanks and comments. A bare word is a message call to the product's parser.
@pytest.mark.parametrize("args,blocked", [
    (["--dry-run=no"], True), (["--dry-run:no"], True),      # `no` becomes the SQL text
    (["--dry-run", "no"], True),
    (["PublishAllXml"], True), (["GrantAccess @Target = 'x'"], True),
    (["(SELECT 1)"], True), (["selectx FROM t"], True),
    (["SELECT 1", "SELECT 2"], True), (["SELECT 1", "--frob"], True),
    (["SELECT 1", "--dry-run=yes"], True),
    (["SELECT 1"], False), (["  -- c\n/* x */ select name from account"], False),
    (["WITH x AS (SELECT 1 AS n) SELECT n FROM x"], False),
    (["WITH x AS (SELECT 1 AS n) DELETE FROM account"], True),   # the write-word test still applies
    (["--dry-run", "SELECT 1"], False), (["--dry-run", "true", "SELECT 1"], False),
    (["--dry-run=false", "SELECT 1"], False), (["SELECT 1", "--explain", "--top", "5"], False),
    (["-p", "dev", "-env", "zz-dev", "SELECT 1"], False), (["--", "SELECT 1"], False),
])
def test_query_sql_passes_only_sql_that_is_positively_a_read(args, blocked):
    assert hook.is_mutation(["ppds", "query", "sql", *args])[0] is blocked, args


# -- mention accounting and caps -------------------------------------------------------


def _unreadable(command: str, cwd=None) -> bool:
    return any(hook.UNREADABLE in argv for argv in hook.find_ppds_invocations(command, "Bash", cwd))


@pytest.mark.parametrize("command", [
    # every covering word must place the mention, not one of them
    'git commit -m "$(cat <<\'EOF\'\nfix(cli): ppds data export handles empty sets\nEOF\n)"',
    "/usr/bin/pp[d]s x",                 # quoting between the letters: not placed inside a word
    "echo ppd\u017f",                    # a word that is only the mention but names no program
    "$'\\x70pds' x", '& "`u{70}pds" x',  # a name spelled with escapes
    "[scriptblock]::Create($c).Invoke()",
    "eval ppds data list",               # arguments from elsewhere
    "dotnet --foo build", "dotnet --verbose build",   # an option before the verb it does not know
    "bash -c 'echo hi' <<< \"$x\"",      # a shell fed by a here-string
])
def test_what_the_hook_cannot_place_is_unreadable(command):
    assert _unreadable(command), command


def test_an_unread_cd_like_word_makes_the_working_directory_unknown():
    found = hook.find_ppds_invocations("pwsh -c 'Set-Location C:\\'; dotnet run -- ", "Bash", None)
    assert found


def _nested(depth: int) -> str:
    return "echo " + "$(echo " * depth + "hi" + ")" * depth


def test_the_depth_cap_is_what_refuses_deep_nesting():
    assert not hook.find_ppds_invocations(_nested(hook.sp.MAX_DEPTH), "Bash", None)
    result = hook.sp.read(_nested(hook.sp.MAX_DEPTH + 1), "posix")
    assert result.capped and "nesting deeper than the cap" in result.problems
    assert _unreadable(_nested(hook.sp.MAX_DEPTH + 1))


def test_past_the_program_word_cap_is_unreadable():
    below = "echo " + "ppds " * (hook._MAX_PROGRAM_WORDS // 2 - 1)
    assert not _unreadable(below)
    # Bounded: the answer comes from the cap, not from the time budget (with
    # the cap removed this must fail, not run on).
    start = time.monotonic()
    found = hook.find_ppds_invocations("echo " + "ppds " * (hook._MAX_PROGRAM_WORDS + 1), "Bash", None,
                                       deadline=time.monotonic() + 5)
    assert time.monotonic() - start < 3
    assert any(hook.UNREADABLE in argv for argv in found)


def test_past_the_argument_cap_is_unreadable():
    assert not _unreadable("ppds data list " + "a " * (hook._MAX_ARGS - 3))
    assert _unreadable("ppds data list " + "a " * (hook._MAX_ARGS + 1))


def test_past_the_argument_cap_is_unreadable_in_the_reader_alone():
    # `'ppds'` is invisible to the original matcher, so only the reader's cap can mark it
    assert not _unreadable("'ppds' data list " + "a " * (hook._MAX_ARGS - 3))
    assert _unreadable("'ppds' data list " + "a " * (hook._MAX_ARGS + 1))


def test_past_the_argument_element_cap_is_unreadable():
    # each `dotnet run` without `--` gives one reading per suffix of its
    # forwarded tokens: few words, many argument words
    one = "dotnet run --project src/PPDS.Cli " + "-x " * 390
    assert not _unreadable(one)
    assert _unreadable(" ; ".join([one] * 3))


def test_a_reader_only_program_word_cap():
    # `dnx` is not seen by the original matcher, so only the reader's cap counts it
    assert _unreadable("echo " + "dnx " * (hook._MAX_PROGRAM_WORDS + 1))


def test_the_env_gate_refuses_a_response_file_itself():
    store = {"activeProfileIndex": 1, "profiles": [{"index": 1, "name": "p", "environment": {"displayName": "d"}}]}
    for argv in (["ppds", "@args.rsp"], ["ppds", "env", "@args.rsp"], ["ppds", "data", "list", "@x"]):
        allow, reason = hook.decide_env(argv, ["d"], ["d"], store)
        assert not allow and "response file" in reason, argv
        # a text naming no switch or target: the listed active env decides
        # also when the text names no switch or target: it is placed text
        assert not hook.decide_env(argv, ["d"], ["d"], store, names_target=False)[0], argv
        # after a program the hook could not identify it is a guess: with a
        # text naming no switch or target, the listed active env decides
        guess = hook._Guess(argv)
        assert hook.decide_env(guess, ["d"], ["d"], store, names_target=False)[0], argv
        assert not hook.decide_env(guess, ["d"], ["x"], store, names_target=False)[0], argv
        assert not hook.decide_env(guess, ["d"], ["d"], store, names_target=True)[0], argv
    assert hook.decide_env(["ppds", "data", "list"], ["d"], ["d"], store)[0]


def _run_patched(patch: str, budget: str, command: str = "ppds data list"):
    """Run the hook as __main__ with ``patch`` applied to its module first."""
    code = ("import runpy, sys, time; sys.path.insert(0, sys.argv[1]);"
            "g = runpy.run_path(sys.argv[2], run_name='hook');" + patch +
            "g['main']()")
    env = {k: v for k, v in os.environ.items() if not k.startswith("PPDS_")}
    env.update({"PPDS_SAFE_ENVS": "x", "PPDS_HOOK_BUDGET_SECONDS": budget,
                "PPDS_CONFIG_DIR": str(HOOK_PATH.parent / "no-such-dir")})
    payload = json.dumps({"tool_name": "Bash", "tool_input": {"command": command}})
    start = __import__("time").monotonic()
    r = subprocess.run([sys.executable, "-c", code, str(HOOK_PATH.parent), str(HOOK_PATH)],
                       input=payload.encode("utf-8"), env=env, capture_output=True, timeout=60)
    return r.returncode, r.stderr.decode("utf-8", "replace"), __import__("time").monotonic() - start


def test_the_launcher_refuses_a_hook_that_does_not_answer(tmp_path):
    # a reading that hangs past the budget (no deadline check can run): the
    # launcher the hook is registered through kills it and refuses (exit 2)
    import shutil
    launcher = HOOK_PATH.parent / "shakedown-safety-launcher.py"
    shutil.copy2(launcher, tmp_path / launcher.name)
    (tmp_path / "shakedown-safety.py").write_text(
        "import runpy, sys, time\n"
        f"g = runpy.run_path({str(HOOK_PATH)!r}, run_name='hook')\n"
        "g['main'].__globals__['find_ppds_invocations'] = lambda *a, **k: time.sleep(30)\n"
        "g['main']()\n", encoding="utf-8")
    env = {k: v for k, v in os.environ.items() if not k.startswith("PPDS_")}
    env.update({"PPDS_SAFE_ENVS": "x", "PPDS_HOOK_BUDGET_SECONDS": "0.2",
                "PPDS_CONFIG_DIR": str(HOOK_PATH.parent / "no-such-dir")})
    payload = json.dumps({"tool_name": "Bash", "tool_input": {"command": "ppds data list"}})
    start = time.monotonic()
    r = subprocess.run([sys.executable, str(tmp_path / launcher.name)], input=payload.encode("utf-8"),
                       env=env, capture_output=True, timeout=60)
    elapsed = time.monotonic() - start
    err = r.stderr.decode("utf-8", "replace")
    assert r.returncode == 2 and "did not answer within" in err, err
    assert elapsed < 30, elapsed


def test_the_decision_loop_runs_under_the_deadline():
    # many invocations, each decision slow: the deadline refuses before the launcher's limit
    code, err, _ = _run_patched(
        "G = g['main'].__globals__;"
        "G['find_ppds_invocations'] = lambda *a, **k: [['ppds', 'data', 'list']] * 50;"
        "G['decide_env'] = lambda *a, **k: (time.sleep(0.05), (True, ''))[1];", "0.5")
    assert code == 2 and "within its time budget" in err, err


def test_past_the_mention_cap_is_unreadable():
    text = "echo " + " ".join(f"src/PPDS.x{i}" for i in range(hook._MAX_MENTIONS + 1))
    assert _unreadable(text)


def test_past_the_dotnet_cap_is_unreadable():
    text = " && ".join(["dotnet --info"] * (hook._DOTNET_MAX_OCCURRENCES + 1))
    assert _unreadable(text)


def test_a_guess_shows_the_words_as_written():
    found = hook.find_ppds_invocations("if [ -f package.json ]; then npm ci; fi", "Bash", None)
    assert found and all(isinstance(a, hook._Guess) for a in found)
    line = hook._command_line(found[0])
    assert line.startswith("[ -f package.json ]") and "cannot identify" in line
    assert hook._command_line(hook._unknown([hook.UNREADABLE])[0]).startswith("(not shown")


# -- the environment gate at the argument level (as a process) -------------------------


@pytest.fixture(scope="module")
def gate_env(tmp_path_factory):
    base = tmp_path_factory.mktemp("gate")
    stores = {}
    for active in (1, 2):
        stores[active] = _store(base / f"config{active}", active)
    project = base / "project"
    (project / ".claude").mkdir(parents=True)
    (project / ".claude" / "settings.json").write_text("{}", encoding="utf-8")
    # "unlisted": the active env (zz-prod) is not listed; "listed": it is (zz-dev).
    modes = {"listed": (1, {"PPDS_SAFE_ENVS": "zz-dev"}), "unlisted": (2, {"PPDS_SAFE_ENVS": "zz-dev"}),
             "shakedown": (1, {"PPDS_SAFE_ENVS": "zz-dev", "PPDS_SHAKEDOWN": "1"}),
             "shakedown-no-allowlist": (1, {"PPDS_SHAKEDOWN": "1"})}

    def run(mode, tool, command, **extra):
        active, mode_env = modes[mode]
        env = {k: v for k, v in os.environ.items() if not k.startswith("PPDS_")}
        env.update(mode_env)
        env.update({"PPDS_CONFIG_DIR": str(stores[active]), "CLAUDE_PROJECT_DIR": str(project)})
        env.update(extra)
        payload = json.dumps({"tool_name": tool, "cwd": str(project), "tool_input": {"command": command}})
        r = subprocess.run([sys.executable, str(HOOK_PATH)], input=payload.encode("utf-8"), env=env,
                           capture_output=True, timeout=60)
        return r.returncode, r.stderr.decode("utf-8", "replace")

    return run


def _store(config, active):
    config.mkdir()
    (config / "profiles.json").write_text(json.dumps({"version": 2, "activeProfileIndex": active, "profiles": [
        {"index": 1, "name": "p", "environment": {"displayName": "zz-dev", "url": "https://zzdev.crm.example.com/"}},
        {"index": 2, "name": "other", "environment": {"displayName": "zz-prod",
                                                       "url": "https://zzprod.crm.example.com/"}},
        {"index": 3, "name": "dev2", "environment": {"displayName": "zz-dev"}},
        {"index": 4, "name": "zz-dev", "environment": {"displayName": "zz-prod"}},
    ]}), encoding="utf-8")
    (config / "environments.json").write_text(json.dumps({"version": 1, "environments": [
        {"url": "https://zzprod.crm.example.com/", "label": "PRODLBL"},
        {"url": "https://zzdev.crm.example.com/", "label": "DEVLBL"}]}), encoding="utf-8")
    return config


GATE_CASES = [
    # target options, every delimiter, with a listed active env
    ("listed", f"{P} data delete --entity account", 0),
    ("listed", f"{P} data delete --environment zz-dev", 0),
    ("listed", f"{P} data delete --environment:zz-dev", 0),
    ("listed", f"{P} data delete -env=zz-dev", 0),
    ("listed", f"{P} data delete --environment zz-prod", 2),
    ("listed", f"{P} data delete --environment=zz-prod", 2),
    ("listed", f"{P} data delete --environment:zz-prod", 2),
    ("listed", f"{P} data delete -env:zz-prod", 2),
    ("listed", f"{P} solutions list -e zz-prod", 2),    # -e is --environment on solutions
    ("listed", f"{P} data delete -e account", 0),       # and --entity on data delete
    ("listed", f"{P} data delete --ENVIRONMENT zz-prod", 2),
    ("listed", f"{P} data delete --environment", 2),
    ("listed", f"{P} data delete --environment https://zzprod.crm.example.com", 2),
    ("listed", f"{P} data copy --source-env zz-dev --target-env zz-prod", 2),
    ("listed", f"{P} data copy -se zz-dev -te zz-dev", 0),
    # profiles: resolved in the store; the profile's own name does not count
    ("listed", f"{P} data delete --profile other", 2),
    ("listed", f"{P} data delete --profile dev2", 0),
    ("listed", f"{P} data delete -p 3", 0),
    ("listed", f"{P} data delete --profile=zz-dev", 2),
    ("listed", f"{P} data delete --profile nosuch", 2),
    ("listed", f"{P} users list --target-profile other", 2),
    # bundles of short options that may hold -e / -p
    ("listed", f"{P} data delete -pother", 2),
    ("listed", f"{P} data delete -vp other", 2),
    ("listed", f"{P} data delete -ezz-prod", 2),
    # cannot place: response file, run-time text, unreadable. With a listed
    # active env (no shakedown) refused only when the text names a switch or
    # a target option; otherwise the listed active env decides.
    ("listed", f"{P} @args.rsp", 2),  # a response file after `ppds` is placed: refused
    ("listed", f"{P} env @args.rsp", 2),
    ("listed", f"$X @args.rsp", 0),  # after a program known only at run time: a guess
    ("listed", f"{P} data list $ARGS", 0),
    ("listed", f"xargs {P} data list", 0),
    ("listed", f"{P} @args.rsp --profile other", 2),
    ("listed", f"{P} env @args.rsp select", 2),
    ("listed", f"{P} data list $ARGS --profile dev2", 2),   # the text names a target option
    ("listed", f"{P} data list $ARGS -p dev2", 0),          # a bare -p in the text does not
    ("listed", f"{P} data list --environment $E", 2),
    ("listed", f"xargs {P} env select", 2),
    ("listed", f"xargs {P} data list --allowed-env zz-dev", 2),
    ("unlisted", f"{P} @args.rsp", 2),
    ("unlisted", f"{P} data list $ARGS", 2),
    ("unlisted", f"xargs {P} data list", 2),
    # variables that move the target
    ("listed", f"PPDS_CONFIG_DIR=/x {P} data list", 2),
    ("listed", f"PPDS_PROFILE=other {P} data list", 2),
    ("listed", f"PPDS_ENVIRONMENT_URL=https://x {P} data list", 2),
    ("listed", f"$env:PPDS_CONFIG_DIR = '/x'; {P} data list", 2),
    ("listed", "$env:PPDS_CONFIG_DIR = '/x'", 2),
    ("listed", "[Environment]::SetEnvironmentVariable('PPDS_PROFILE', 'other')", 2),
    ("listed", "grep -rn PPDS_CONFIG_DIR src", 0),
    ("listed", f"PPDS_FORCE_TUI=1 {P} data list", 0),
    # the MCP server
    ("listed", f"{P}-mcp-server --environment zz-prod", 2),
    ("listed", f"{P}-mcp-server --environment=zz-prod", 2),
    ("listed", f"{P}-mcp-server --allowed-env https://zzprod.crm.example.com", 2),
    ("listed", f"{P}-mcp-server --allowed-env zz-dev", 0),
    ("listed", f"{P}-mcp-server --profile other", 2),
    ("listed", f"{P}-mcp-server --profile p", 0),
    # labels in SQL
    ("listed", f'{P} query sql "SELECT name FROM [PRODLBL].account"', 2),
    ("listed", f'{P} query sql "SELECT name FROM prodlbl.account"', 2),
    ("listed", f'{P} query explain "SELECT name FROM [PRODLBL].account"', 2),
    ("listed", f'{P} query sql "SELECT name FROM [DEVLBL].account"', 0),
    ("listed", f'{P} query sql "SELECT name FROM account"', 0),
    ("listed", f"{P} query sql --file q.sql", 2),
    ("listed", f"{P} query history execute 3", 2),
    # bare start: the TUI against the active env
    ("listed", f"{P}", 0),
    ("unlisted", f"{P}", 2),
    ("unlisted", f"PPDS_FORCE_TUI=1 {P}", 2),
    ("unlisted", f"{P}.exe", 2),
    ("unlisted", "dotnet run --project src/PPDS.Cli", 2),
    # switches
    ("unlisted", f"{P} env select --environment zz-dev", 0),
    ("unlisted", f"{P} env select --environment:zz-dev", 0),
    ("unlisted", f"{P} env select zz-dev", 0),
    ("listed", f"{P} env select --environment:zz-prod", 2),
    ("listed", f"{P} env select -env:zz-prod", 2),
    ("listed", f"{P} org select --environment:zz-prod", 2),
    ("listed", f"dotnet run --project src/PPDS.Cli -- env select --environment:zz-prod", 2),
    ("listed", f"{P} env select", 2),
    ("listed", f"{P} env select --verbose", 2),
    ("listed", f"{P} env select -e zz-dev", 2),
    ("unlisted", f"{P} auth select --name p", 0),
    ("unlisted", f"{P} auth select -i 1", 0),
    ("listed", f"{P} auth select --name other", 2),
    ("listed", f"{P} auth select --name:other", 2),
    ("listed", f"{P} auth select -i 2", 2),
    ("listed", f"{P} auth select --name zz-dev", 2),
    ("listed", f"{P} auth select", 2),
    ("listed", f"{P} auth select -ni 2", 2),
    # env forms with an unlisted active env: only the reads, each recognised
    ("unlisted", f"{P} env", 0),
    ("unlisted", f"{P} env --help", 0),
    ("unlisted", f"{P} env list", 0),
    ("unlisted", f"{P} env who", 0),
    ("unlisted", f"{P} env config", 0),
    ("unlisted", f"{P} env config --list", 0),
    ("unlisted", f"{P} env config https://x --show", 0),
    ("unlisted", f"{P} env type --list", 0),
    ("unlisted", f"{P} env config https://x --label y", 2),
    ("unlisted", f"{P} env config https://x --type Production", 2),
    ("unlisted", f"{P} env type Production --color Red", 2),
    ("unlisted", f"{P} env current", 2),
    ("unlisted", f"{P} env who --environment zz-prod", 2),
    # the write-block: env select needs an allowlist; version --update writes
    ("shakedown", f"{P} env select --environment zz-dev", 0),
    ("shakedown-no-allowlist", f"{P} env select --environment zz-dev", 2),
    ("shakedown", f"{P} version --update --yes", 2),
    ("shakedown", f"{P}", 2),
]


@pytest.mark.parametrize("tool", ["Bash", "PowerShell"])
@pytest.mark.parametrize("mode,command,expected", GATE_CASES, ids=[f"{m}:{c}" for m, c, _ in GATE_CASES])
def test_the_gates_at_the_argument_level(gate_env, tool, mode, command, expected):
    code, err = gate_env(mode, tool, command)
    assert code == expected, err


@pytest.mark.parametrize("extra,expected", [
    ({"PPDS_PROFILE": "other"}, 2), ({"PPDS_PROFILE": "dev2"}, 0), ({"PPDS_PROFILE": "nosuch"}, 2),
    ({"PPDS_ENVIRONMENT_URL": "https://zzprod.crm.example.com"}, 2),
    ({"PPDS_ENVIRONMENT_URL": "https://zz-dev.crm.example.com"}, 0),
])
def test_target_variables_in_the_hooks_own_environment(gate_env, extra, expected):
    code, err = gate_env("listed", "Bash", f"{P} data list", **extra)
    assert code == expected, err


def test_an_unreadable_environment_settings_file_refuses_sql(gate_env, tmp_path):
    config = tmp_path / "cfg"
    config.mkdir()
    (config / "profiles.json").write_text(json.dumps({"activeProfileIndex": 1, "profiles": [
        {"index": 1, "name": "p", "environment": {"displayName": "zz-dev"}}]}), encoding="utf-8")
    (config / "environments.json").write_text("{not json", encoding="utf-8")
    code, err = gate_env("listed", "Bash", f'{P} query sql "SELECT 1"', PPDS_CONFIG_DIR=str(config))
    assert code == 2 and "could not be read" in err
    code, _ = gate_env("listed", "Bash", f"{P} data list", PPDS_CONFIG_DIR=str(config))
    assert code == 0


def test_a_block_for_a_text_with_no_mention_shows_no_invented_command(gate_env):
    code, err = gate_env("shakedown", "Bash", "if [ -f package.json ]; then npm ci; fi")
    assert code == 2
    assert "Command: ppds" not in err
    assert "[ -f package.json ]" in err and "cannot identify" in err
