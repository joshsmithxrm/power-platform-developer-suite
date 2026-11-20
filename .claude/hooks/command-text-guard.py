"""PreToolUse hook: refuse release, publish and history-rewriting commands.

Reads the whole command text from the Bash tool or the PowerShell tool and
refuses, wherever the command appears in it (also inside ``pwsh -c``,
``powershell -Command``, ``bash -c``, ``cmd /c``, a pipeline, a
substitution, a script block or any quoted string):

* creating, deleting or pushing a tag (``git tag <name>``, ``git tag -d``,
  ``git push --tags`` / ``--follow-tags`` / ``--mirror``, a refspec naming a
  tag, ``git update-ref refs/tags/...``); any ``gh release`` command other
  than ``list``, ``view``, ``download`` and ``verify``;
* package publishing: ``dotnet nuget push|delete``, ``nuget push|delete``,
  npm / pnpm / yarn / bun ``publish``, ``unpublish``, ``deprecate``,
  ``dist-tag``, ``vsce`` / ``ovsx publish|unpublish``, ``Publish-Module``,
  ``Publish-Script``, ``Publish-PSResource``;
* dispatching a workflow (``gh workflow run``) other than the test
  workflows named in ``_SAFE_WORKFLOWS``, and ``gh run rerun``;
* ``gh api`` with a method other than GET, a field (``-f``/``-F``), an
  ``--input`` body, or the ``graphql`` endpoint (always sent as POST);
* ``gh pr merge`` without both ``--squash`` and ``--match-head-commit``,
  or with ``--admin``;
* ``--no-verify`` (and ``git commit -n``), any mention of ``hooksPath``,
  git configuration set from the environment (``GIT_CONFIG_*``), and
  aliases (``git config alias.*``, ``git -c alias.*``, ``gh alias set``,
  shell aliases of a guarded program) that would hide a command;
* a force push in any spelling (``--force``, ``-f``, ``--force-with-lease``,
  ``+refspec``, ``--mirror``), a push that deletes a remote ref
  (``--delete``, ``:ref``, ``--prune``), ``git reset --hard``, ``git clean``.

Listing tags (``git tag``, ``git tag -l``, ``git tag --list``,
``git describe``) and ``gh api`` reads pass.

**Fail closed.** When the command text cannot be read in full (an
unterminated quote, a cap reached), the raw text is matched instead and
any mention of a guarded program is refused. An argument only known at run
time where it could change the meaning (``git $sub``, ``gh pr merge $x``)
is refused. A program only known at run time (``& $exe push -f``) is
checked as if it were each guarded program. A hook input that cannot be
read is refused.

**Both dialects.** The text is read with the rules of the tool that issued
it (POSIX for Bash, PowerShell for PowerShell); it is also read with the
other dialect's rules, and a refusal from either reading stands.

This hook is not registered in the checked-in ``.claude/settings.json``;
the maintainer installs it in his own settings.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _shellparse as sp  # noqa: E402

HOOK = "command-text-guard"
SHELL_TOOLS = {"bash", "powershell"}

# Dispatchable workflows that neither publish nor sign (file and display name).
_SAFE_WORKFLOWS = {
    "test.yml", "test",
    "workflow-tests.yml", "python tests",
    "tui-e2e.yml", "tui e2e tests",
    "codeql.yml", "security: codeql",
    "audit-capture.yml", "audit capture",
}

_VERSIONISH_RE = re.compile(r"(^|[/\-_@])v?\d+\.\d+")
_RAW_GUARDED_RE = re.compile(
    r"(?<![a-z0-9_.-])(git|gh|nuget|npm|pnpm|yarn|bun|vsce|ovsx|dnx|dotnet)(?![a-z0-9_-])"
    r"|publish-(module|script|psresource)",
)
_GIT_CONFIG_ENV_RE = re.compile(
    r"git_config_(parameters|count|key_|value_|global|system)|\bgit_config\b", re.I
)


class Refusal(Exception):
    pass


def _refuse(what: str):
    raise Refusal(what)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _low(word) -> str:
    return word.text.lower()


def _long_prefix(text: str, option: str, minimum: int) -> bool:
    """git accepts any unambiguous prefix of a long option."""
    name = text.split("=", 1)[0].lower()
    return len(name) >= minimum and option.startswith(name)


def _is_local_tag(name: str, cwd) -> bool:
    if not cwd or not re.fullmatch(r"[A-Za-z0-9._/+-]+", name or ""):
        return False
    try:
        done = subprocess.run(
            ["git", "-C", cwd, "show-ref", "--verify", "--quiet", "refs/tags/" + name],
            stdin=subprocess.DEVNULL, capture_output=True, timeout=3,
        )
        return done.returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


# ---------------------------------------------------------------------------
# git
# ---------------------------------------------------------------------------

_GIT_VALUE_OPTS = {"-c", "-C", "--git-dir", "--work-tree", "--namespace", "--super-prefix",
                   "--config-env", "--list-cmds", "--attr-source"}
_GIT_BOOL_OPTS = {"-p", "-P", "--paginate", "--no-pager", "--bare", "--no-replace-objects",
                  "--literal-pathspecs", "--glob-pathspecs", "--noglob-pathspecs",
                  "--icase-pathspecs", "--no-optional-locks", "--no-advice", "--html-path",
                  "--man-path", "--info-path", "--version", "-v", "--help", "-h", "--exec-path",
                  "--no-lazy-fetch"}


def _check_git_config_value(value: str) -> None:
    low = value.lower()
    if low.startswith(("alias.", "include.", "includeif.")):
        _refuse(f"a git alias or include set for one command ({value})")
    if low.startswith(("push.", "remote.")):
        _refuse(f"git push configuration set for one command ({value})")


def check_git(args, spec: bool, ctx) -> None:
    i, cdir = 0, ctx.get("cwd")
    while i < len(args):
        a = args[i]
        t = a.text
        if a.dynamic:
            if spec:
                return
            _refuse("git with an option or subcommand only known when it runs")
        name = t.split("=", 1)[0]
        if name in _GIT_VALUE_OPTS:
            value = t.split("=", 1)[1] if "=" in t else (args[i + 1].text if i + 1 < len(args) else "")
            if i + 1 < len(args) and "=" not in t and args[i + 1].dynamic and name in ("-c", "--config-env"):
                _refuse("git configuration only known when it runs")
            if name in ("-c", "--config-env"):
                _check_git_config_value(value)
            if name == "-C" and value and sp.DYN not in value:
                cdir = os.path.join(cdir, value) if cdir else value
            i += 1 if "=" in t else 2
            continue
        if t.startswith("-c") and len(t) > 2:
            _check_git_config_value(t[2:])
            i += 1
            continue
        if name in _GIT_BOOL_OPTS:
            i += 1
            continue
        if t.startswith("-"):
            if spec:
                return
            _refuse(f"git with an option the hook does not know before the subcommand ({t})")
        break
    if i >= len(args):
        return
    sub = _low(args[i])
    rest = args[i + 1:]
    handler = _GIT_SUBCOMMANDS.get(sub)
    if handler is not None:
        handler(rest, spec, dict(ctx, cwd=cdir))
    elif sub in ("merge", "rebase", "am", "cherry-pick", "revert"):
        _git_no_verify(sub, rest, spec)


def _git_no_verify(sub, rest, spec) -> None:
    for a in rest:
        if a.text == "--":
            return
        if a.text.startswith("--") and _long_prefix(a.text, "--no-verify", 6):
            _refuse(f"git {sub} --no-verify (skips the repository's hooks)")


def _git_push(rest, spec, ctx) -> None:
    positionals = []
    j = 0
    while j < len(rest):
        a = rest[j]
        t = a.text
        if a.dynamic and not t.startswith("-"):
            if spec:
                j += 1
                continue
            _refuse("git push with an argument only known when it runs (it could force or push a tag)")
        if t == "--":
            positionals.extend(rest[j + 1:])
            break
        if t.startswith("--"):
            name = t.split("=", 1)[0].lower()
            if name.startswith("--fol"):
                _refuse("git push --follow-tags (pushes tags)")
            if name.startswith("--fo"):
                _refuse(f"a force push (git push {t})")
            if name.startswith("--mi"):
                _refuse("git push --mirror (force-pushes and deletes remote refs, tags included)")
            if name.startswith("--de"):
                _refuse("git push --delete (deletes a remote branch or tag)")
            if name.startswith("--ta"):
                _refuse("git push --tags (pushes tags)")
            if name.startswith("--pru"):
                _refuse("git push --prune (deletes remote refs)")
            if name.startswith("--no-ve"):
                _refuse("git push --no-verify (skips the repository's hooks)")
            if name.startswith("--all") or name == "--branches":
                pass
            if name in ("--repo", "--receive-pack", "--exec", "--push-option") and "=" not in t:
                j += 2
                continue
            j += 1
            continue
        if t.startswith("-") and len(t) > 1:
            letters = t[1:]
            for k, ch in enumerate(letters):
                if ch == "f":
                    _refuse(f"a force push (git push {t})")
                if ch == "d":
                    _refuse(f"git push {t} (deletes a remote branch or tag)")
                if ch == "o":
                    if k == len(letters) - 1:
                        j += 1
                    break
            j += 1
            continue
        positionals.append(a)
        j += 1
    for ref in positionals[1:]:
        t = ref.text
        if ref.dynamic:
            if spec:
                continue
            _refuse("git push of a refspec only known when it runs (it could force or push a tag)")
        if t.startswith("+"):
            _refuse(f"a force push (git push refspec {t})")
        if t.startswith(":"):
            _refuse(f"git push {t} (deletes a remote branch or tag)")
        low = t.lower()
        if "tags/" in low or low == "tag":
            _refuse(f"pushing a tag (git push {t})")
        if _VERSIONISH_RE.search(t):
            _refuse(f"pushing what looks like a version tag (git push {t})")
        src = t.split(":", 1)[0]
        if _is_local_tag(src, ctx.get("cwd")):
            _refuse(f"pushing the tag {src}")


_TAG_LIST_FLAGS = {"-l", "--list", "-v", "--verify", "-i", "--ignore-case", "--no-column",
                   "--omit-empty", "--column", "--sort", "--format", "--contains",
                   "--no-contains", "--points-at", "--merged", "--no-merged", "--color",
                   "--no-color", "--create-reflog"}
_TAG_IMPLY_LIST = {"-l", "--list", "--contains", "--no-contains", "--points-at", "--merged",
                   "--no-merged", "-v", "--verify"}


def _git_tag(rest, spec, ctx) -> None:
    listing = False
    positionals = []
    for a in rest:
        t = a.text
        if a.dynamic:
            if spec:
                continue
            _refuse("git tag with an argument only known when it runs (it could create or delete a tag)")
        if t == "--":
            continue
        if t.startswith("--"):
            name = t.split("=", 1)[0].lower()
            if name == "--create-reflog":
                _refuse("git tag --create-reflog (creates a tag)")
            if name not in _TAG_LIST_FLAGS:
                _refuse(f"git tag {t} (creating or deleting a tag)")
            if name in _TAG_IMPLY_LIST:
                listing = True
            continue
        if t.startswith("-") and len(t) > 1:
            m = re.fullmatch(r"-([lvi]*)(n\d*)?", t)
            if not m:
                _refuse(f"git tag {t} (creating or deleting a tag)")
            if "l" in m.group(1) or "v" in m.group(1) or m.group(2):
                listing = True
            continue
        positionals.append(a)
    if positionals and not listing:
        _refuse(f"creating a tag (git tag {' '.join(w.text for w in rest)})")


_COMMIT_VALUE_SHORT = set("mFCct")
_COMMIT_OPTIONAL_SHORT = set("Su")
_COMMIT_VALUE_LONG = {"--message", "--file", "--template", "--author", "--date", "--cleanup",
                      "--fixup", "--squash", "--reuse-message", "--reedit-message", "--trailer",
                      "--pathspec-from-file"}


def _git_commit(rest, spec, ctx) -> None:
    j = 0
    while j < len(rest):
        a = rest[j]
        t = a.text
        if t == "--":
            return
        if a.dynamic and not spec:
            _refuse("git commit with an option only known when it runs (it could be --no-verify)")
        if t.startswith("--"):
            if _long_prefix(t, "--no-verify", 6):
                _refuse(f"git commit {t} (skips the repository's hooks)")
            name = t.split("=", 1)[0]
            j += 2 if (name in _COMMIT_VALUE_LONG and "=" not in t) else 1
            continue
        if t.startswith("-") and len(t) > 1:
            letters = t[1:]
            skip = 1
            for k, ch in enumerate(letters):
                if ch == "n":
                    _refuse(f"git commit {t} (-n is --no-verify, which skips the repository's hooks)")
                if ch in _COMMIT_VALUE_SHORT:
                    if k == len(letters) - 1:
                        skip = 2
                    break
                if ch in _COMMIT_OPTIONAL_SHORT:
                    break
            j += skip
            continue
        j += 1


def _git_reset(rest, spec, ctx) -> None:
    for a in rest:
        if a.text == "--":
            return
        if a.dynamic and not spec:
            _refuse("git reset with an option only known when it runs (it could be --hard)")
        if a.text.startswith("--") and _long_prefix(a.text, "--hard", 4):
            _refuse("git reset --hard (discards uncommitted work)")


def _git_clean(rest, spec, ctx) -> None:
    _refuse("git clean (deletes untracked files)")


def _git_config(rest, spec, ctx) -> None:
    for a in rest:
        if a.dynamic and not spec:
            _refuse("git config with a key or value only known when it runs")
        _check_git_config_value(a.text)


def _git_update_ref(rest, spec, ctx) -> None:
    for a in rest:
        if a.dynamic and not spec:
            _refuse("git update-ref with a ref only known when it runs")
        if "refs/tags" in a.text.lower():
            _refuse("git update-ref on a tag (creates or deletes a tag)")


_GIT_SUBCOMMANDS = {
    "push": _git_push,
    "tag": _git_tag,
    "commit": _git_commit,
    "reset": _git_reset,
    "clean": _git_clean,
    "config": _git_config,
    "update-ref": _git_update_ref,
}


# ---------------------------------------------------------------------------
# gh
# ---------------------------------------------------------------------------


def _gh_split(args, value_long, value_short):
    """Return (positionals, flags) where flags is a list of (name, value)."""
    positionals, flags = [], []
    j = 0
    while j < len(args):
        a = args[j]
        t = a.text
        if a.dynamic and t.startswith(sp.DYN):
            flags.append((sp.DYN, None))  # could be a flag ...
            positionals.append(a)  # ... or a positional
            j += 1
            continue
        if t == "--":
            positionals.extend(args[j + 1:])
            break
        if t.startswith("--"):
            name, _, value = t.partition("=")
            if not _ and name in value_long:
                value = args[j + 1].text if j + 1 < len(args) else ""
                j += 1
            flags.append((name, value if (_ or name in value_long) else None))
            j += 1
            continue
        if t.startswith("-") and len(t) > 1:
            letters = t[1:]
            for k, ch in enumerate(letters):
                if ch in value_short:
                    value = letters[k + 1:]
                    if not value:
                        value = args[j + 1].text if j + 1 < len(args) else ""
                        j += 1
                    flags.append(("-" + ch, value))
                    break
                flags.append(("-" + ch, None))
            j += 1
            continue
        positionals.append(a)
        j += 1
    return positionals, flags


def check_gh(args, spec: bool, ctx) -> None:
    positionals, _ = _gh_split(args, {"--repo", "--hostname"}, set("R"))
    if not positionals:
        return
    group = positionals[0]
    if group.dynamic:
        if spec:
            return
        _refuse("gh with a command only known when it runs")
    g = group.text.lower()
    idx = next(k for k, a in enumerate(args) if a is group)
    after = args[idx + 1:]  # flags may come before or after the action
    action = positionals[1] if len(positionals) > 1 else None
    act = action.text.lower() if action is not None and not action.dynamic else None
    if action is not None and action.dynamic and g in ("release", "pr", "workflow", "run", "alias"):
        if spec:
            return
        _refuse(f"gh {g} with a command only known when it runs")
    if g == "release":
        if act is not None and act not in ("list", "ls", "view", "download", "verify", "verify-asset"):
            _refuse(f"gh release {act} (releases are the maintainer's)")
    elif g == "api":
        _gh_api(after, spec)
    elif g == "pr" and act == "merge":
        _gh_pr_merge(after, spec)
    elif g == "workflow" and act == "run":
        _gh_workflow_run(after, spec)
    elif g == "run" and act == "rerun":
        _refuse("gh run rerun (re-running a run can repeat a publish or signing workflow)")
    elif g == "alias" and act in ("set", "import"):
        _refuse(f"gh alias {act} (an alias would hide the command it runs)")


def _gh_api(args, spec) -> None:
    positionals, flags = _gh_split(
        args,
        {"--method", "--raw-field", "--field", "--header", "--input", "--jq", "--template",
         "--preview", "--hostname", "--cache"},
        set("XfFHqtp"),
    )
    for name, value in flags:
        if name == sp.DYN:
            if spec:
                continue
            _refuse("gh api with an option only known when it runs")
        if name in ("-X", "--method"):
            if value is None or sp.DYN in value:
                if not spec:
                    _refuse("gh api with a method only known when it runs")
                continue
            if value.upper() != "GET":
                _refuse(f"gh api --method {value} (only GET reads are allowed)")
        if name in ("-f", "-F", "--field", "--raw-field"):
            _refuse("gh api with a field (-f/-F sends a request body)")
        if name == "--input":
            _refuse("gh api --input (sends a request body)")
    for p in positionals:
        if p.text.lower().strip("/") == "graphql":
            _refuse("gh api graphql (always sent as POST)")


def _gh_pr_merge(args, spec) -> None:
    _, flags = _gh_split(
        args,
        {"--body", "--body-file", "--subject", "--author-email", "--match-head-commit", "--repo"},
        set("bFtAR"),
    )
    squash = head = False
    for name, value in flags:
        if name == sp.DYN:
            if spec:
                continue
            _refuse("gh pr merge with an option only known when it runs")
        if name == "--admin":
            _refuse("gh pr merge --admin (bypasses branch protection)")
        if name in ("-s", "--squash") and (value is None or value.lower() == "true"):
            squash = True
        if name == "--match-head-commit" and value and sp.DYN not in value:
            head = True
    if not (squash and head):
        _refuse("gh pr merge without both --squash and --match-head-commit <sha>")


def _gh_workflow_run(args, spec) -> None:
    positionals, flags = _gh_split(args, {"--ref", "--raw-field", "--field", "--repo", "--json"},
                                   set("rfFR"))
    target = positionals[1] if len(positionals) > 1 else None  # positionals[0] is "run"
    if target is None or target.dynamic or target.text.lower() not in _SAFE_WORKFLOWS:
        name = "a workflow chosen interactively" if target is None else target.text.replace(sp.DYN, "$...")
        _refuse(f"gh workflow run {name} (dispatching a workflow that is not a test workflow)")


# ---------------------------------------------------------------------------
# Package publishing
# ---------------------------------------------------------------------------


def check_dotnet(args, spec, ctx) -> None:
    words = [a for a in args if not a.text.startswith("-")]
    if not words:
        return
    if words[0].dynamic and not spec:
        return
    if words[0].text.lower() == "nuget" and len(words) > 1:
        sub = words[1]
        if sub.dynamic and not spec:
            _refuse("dotnet nuget with a command only known when it runs")
        if sub.text.lower() in ("push", "delete"):
            _refuse(f"dotnet nuget {sub.text.lower()} (package publishing)")


def check_nuget(args, spec, ctx) -> None:
    for a in args:
        if a.text.startswith("-"):
            continue
        if a.dynamic and not spec:
            _refuse("nuget with a command only known when it runs")
        if a.text.lower() in ("push", "delete"):
            _refuse(f"nuget {a.text.lower()} (package publishing)")
        return


def check_npm_like(prog):
    def check(args, spec, ctx) -> None:
        for a in args:
            low = a.text.lower()
            if "publish" in low or low in ("deprecate", "dist-tag", "dist-tags"):
                _refuse(f"{prog} {a.text} (package publishing)")
    return check


def check_marketplace(prog):
    def check(args, spec, ctx) -> None:
        for a in args:
            if "publish" in a.text.lower():
                _refuse(f"{prog} {a.text} (Marketplace publishing)")
    return check


def check_ps_publish(prog):
    def check(args, spec, ctx) -> None:
        if not spec:  # the cmdlet's name is what identifies it
            _refuse(f"{prog} (PowerShell Gallery publishing)")
    return check


HANDLERS = {
    "git": check_git,
    "gh": check_gh,
    "dotnet": check_dotnet,
    "nuget": check_nuget,
    "npm": check_npm_like("npm"),
    "pnpm": check_npm_like("pnpm"),
    "yarn": check_npm_like("yarn"),
    "bun": check_npm_like("bun"),
    "vsce": check_marketplace("vsce"),
    "ovsx": check_marketplace("ovsx"),
    "publish-module": check_ps_publish("Publish-Module"),
    "publish-script": check_ps_publish("Publish-Script"),
    "publish-psresource": check_ps_publish("Publish-PSResource"),
}

_ALIAS_DEFINERS = {"alias", "set-alias", "new-alias", "sal", "nal", "doskey"}


def _program(word):
    name = sp.program_name(word)
    if name is None:
        return None
    if "@" in name[1:]:
        name = name[0] + name[1:].split("@", 1)[0]  # npm package spec: vsce@latest
    return name


# ---------------------------------------------------------------------------
# Whole-command check
# ---------------------------------------------------------------------------


def _normalise_raw(text: str) -> str:
    return re.sub(r"[`^\\'\"‘’‚‛“”„]", "", text).lower()


def _check_statement(stmt, ctx, out: list, primary: str) -> None:
    words = stmt.words
    for i, w in enumerate(words):
        prog = _program(w)
        handler = HANDLERS.get(prog) if prog else None
        if handler is None:
            continue
        try:
            handler(words[i + 1:], False, ctx)
        except Refusal as exc:
            out.append(str(exc))
    # A program only known at run time, in code: at top level only in the
    # issuing tool's own dialect (the other reading of the same text would
    # misread its variables), in nested code in either.
    if stmt.code and (stmt.level > 0 or stmt.dialect == primary):
        for i in sp.command_positions(stmt):
            if words[i].dynamic and _program(words[i]) is None:
                for handler in HANDLERS.values():
                    try:
                        handler(words[i + 1:], True, ctx)
                    except Refusal as exc:
                        out.append(f"{exc} [through a program only known when it runs]")
    if words and _low(words[0]) in _ALIAS_DEFINERS:
        for w in words[1:]:
            for part in re.split(r"[=\s]", w.text):
                name = _program(sp.Word(part, False, False))
                if name in HANDLERS:
                    out.append(f"an alias for {name} (the hook cannot see what an alias runs)")


def check_command(command: str, tool_name=None, cwd=None) -> list:
    """Return the refusal reasons for ``command`` (empty when it may run)."""
    reasons = []
    tool = (tool_name or "").lower()
    primary = "ps" if tool == "powershell" else "posix"
    dialects = [primary, "posix" if primary == "ps" else "ps"]
    ctx = {"cwd": cwd}

    raw = _normalise_raw(command)
    if "hookspath" in raw:
        reasons.append("an override of core.hooksPath (the repository's hooks are the maintainer's)")
    if _GIT_CONFIG_ENV_RE.search(command):
        reasons.append("git configuration set from the environment (GIT_CONFIG_*)")

    # Completeness is judged on the reading in the issuing tool's own dialect
    # (both dialects when the tool is unknown); the other readings only add
    # statements, so their own parse errors are not counted.
    complete = True
    problems = []
    seen = set()
    readings = [(command, d, d == primary or tool not in SHELL_TOOLS) for d in dialects]
    if "^" in command:
        stripped = command.replace("^", "")  # cmd.exe escapes
        readings += [(stripped, d, False) for d in dialects]
    for text, dialect, judged in readings:
        result = sp.read(text, dialect)
        if judged and not result.complete:
            complete = False
            problems.extend(result.problems)
        for stmt in result.statements:
            key = (stmt.code, tuple((w.text, w.dynamic) for w in stmt.words))
            if key in seen:
                continue
            seen.add(key)
            _check_statement(stmt, ctx, reasons, primary)

    if not complete:
        m = _RAW_GUARDED_RE.search(raw)
        if any("encoded" in p for p in problems):
            reasons.append("an encoded PowerShell command that could not be decoded")
        elif m:
            reasons.append(
                f"a command that could not be read in full ({'; '.join(dict.fromkeys(problems))}) "
                f"and mentions {m.group(0)}"
            )
    return list(dict.fromkeys(reasons))


def _message(reasons) -> str:
    lines = [f"BLOCKED [{HOOK}]: refused {reasons[0]}."]
    for extra in reasons[1:5]:
        lines.append(f"  Also refused: {extra}.")
    lines += [
        "  Tags, releases, package publishing, workflow dispatches, merges outside",
        "  the squash rule, force pushes, history rewrites and hook bypasses are",
        "  the maintainer's actions: ask the maintainer to run this.",
    ]
    return "\n".join(lines)


def main() -> None:
    raw = sys.stdin.read()
    try:
        payload = json.loads(raw)
        if not isinstance(payload, dict):
            raise ValueError("not an object")
    except ValueError:
        print(_message(["a tool call whose hook input could not be read"]), file=sys.stderr)
        sys.exit(2)
    tool_name = payload.get("tool_name")
    tool_input = payload.get("tool_input")
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if command is None and str(tool_name or "").lower() not in SHELL_TOOLS:
        sys.exit(0)
    if not isinstance(command, str):
        print(_message(["a shell tool call without a readable command"]), file=sys.stderr)
        sys.exit(2)
    cwd = payload.get("cwd") if isinstance(payload.get("cwd"), str) else None
    reasons = check_command(command, tool_name if isinstance(tool_name, str) else None, cwd)
    if reasons:
        print(_message(reasons), file=sys.stderr)
        sys.exit(2)
    sys.exit(0)


if __name__ == "__main__":
    main()
