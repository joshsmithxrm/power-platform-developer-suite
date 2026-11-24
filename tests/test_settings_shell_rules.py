"""The checked-in permission rules and hooks treat both shell tools alike.

Sessions issue commands through the Bash tool or the PowerShell tool. Every
deny and ask rule must exist for both, with the same command text, and every
hook registered for one shell tool must be registered for the other.

What these tests prove: the text of `.claude/settings.json` -- each deny and
ask rule exists for both tools with the same command, each tool's rules use
one suffix form, the environment hook is registered for both tool names as
a `command` hook, and nothing turns the hooks off (`disableAllHooks`). The
hook process itself handles both tool names; that is tested in
tests/hooks/ (the decision tests run each case with `tool_name` Bash and
PowerShell; some process-level cases, such as payload shapes, use one).

What they do not prove: that Claude Code enforces a rule or runs the hook
for the PowerShell tool. No automated test here can observe the app's
enforcement. That rests on an observation in a real session, recorded in
docs/SAFE-SHAKEDOWN.md ("Enforcement in the PowerShell tool").
"""
from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

SETTINGS = Path(__file__).resolve().parent.parent / ".claude" / "settings.json"
SHELL_TOOLS = ("Bash", "PowerShell")
_RULE_RE = re.compile(r"^(?P<tool>[A-Za-z]+)\((?P<body>.*)\)$", re.DOTALL)


def _settings() -> dict:
    return json.loads(SETTINGS.read_text(encoding="utf-8"))


def _command_text(body: str) -> str:
    """The command a rule names, without its trailing wildcard (`:*` or ` *`)."""
    for suffix in (":*", " *"):
        if body.endswith(suffix):
            return body[: -len(suffix)]
    return body


def unmirrored(rules: list) -> list:
    """Shell rules that exist for one shell tool only, as (tool, rule) pairs."""
    by_text = {}
    out = []
    for rule in rules:
        m = _RULE_RE.match(rule)
        if not m or m.group("tool") not in SHELL_TOOLS:
            continue
        by_text.setdefault(_command_text(m.group("body")), {})[m.group("tool")] = rule
    for text, tools in by_text.items():
        for tool in SHELL_TOOLS:
            if tool not in tools:
                out.append((tool, text))
    return out


def _forms(rules: list) -> dict:
    """Rule suffix forms per tool, e.g. {"Bash": {":*"}, "PowerShell": {":*"}}."""
    out = {}
    for rule in rules:
        m = _RULE_RE.match(rule)
        if m and m.group("tool") in SHELL_TOOLS:
            body = m.group("body")
            suffix = ":*" if body.endswith(":*") else " *" if body.endswith(" *") else ""
            out.setdefault(m.group("tool"), set()).add(suffix)
    return out


@pytest.mark.parametrize("section", ["deny", "ask"])
def test_every_rule_exists_for_both_shell_tools(section):
    rules = _settings()["permissions"][section]
    missing = unmirrored(rules)
    assert not missing, f"permissions.{section}: rules missing for {missing}"


@pytest.mark.parametrize("section", ["deny", "ask"])
def test_one_suffix_form_per_tool(section):
    """Every rule for a tool uses one suffix form (`:*` or ` *`; the checked-in
    rules use `:*` for both tools). Whether Claude Code enforces that form is
    not tested here."""
    forms = _forms(_settings()["permissions"][section])
    for tool in SHELL_TOOLS:
        assert len(forms.get(tool, set())) == 1, f"{section}: mixed rule forms for {tool}: {forms}"


def test_a_rule_added_for_one_tool_only_is_caught():
    rules = ["Bash(git push --force:*)", "PowerShell(git push --force *)", "Bash(npm publish:*)"]
    assert unmirrored(rules) == [("PowerShell", "npm publish")]
    assert unmirrored(["PowerShell(npm publish *)"]) == [("Bash", "npm publish")]


def _matcher_tools(matcher: str) -> set:
    """Tool names a PreToolUse matcher selects among the shell tools."""
    if matcher in ("", "*"):
        return set(SHELL_TOOLS)
    if re.fullmatch(r"[A-Za-z0-9_|]+", matcher):
        return {t for t in matcher.split("|") if t in SHELL_TOOLS}
    return {t for t in SHELL_TOOLS if re.fullmatch(matcher, t)}


def shell_hooks_registered_for_one_tool(hooks: dict) -> list:
    """(event, command, tools) for each hook command that is registered for
    some but not all shell tools."""
    registered = {}
    for event, entries in hooks.items():
        for entry in entries:
            tools = _matcher_tools(entry.get("matcher", ""))
            for hook in entry.get("hooks", []):
                key = (event, hook.get("command"))
                registered.setdefault(key, set()).update(tools)
    return [(e, c, sorted(t)) for (e, c), t in registered.items() if t and t != set(SHELL_TOOLS)]


def test_every_shell_hook_is_registered_for_both_tools():
    assert shell_hooks_registered_for_one_tool(_settings().get("hooks", {})) == []


def test_the_environment_hook_is_registered_for_both_tools():
    hooks = _settings()["hooks"]["PreToolUse"]
    tools = set()
    for entry in hooks:
        if any("shakedown-safety.py" in h.get("command", "") for h in entry.get("hooks", [])):
            tools |= _matcher_tools(entry.get("matcher", ""))
    assert tools == set(SHELL_TOOLS)


def test_the_environment_hook_is_a_command_hook():
    found = [h for entry in _settings()["hooks"]["PreToolUse"] for h in entry.get("hooks", [])
             if "shakedown-safety.py" in h.get("command", "")]
    assert found and all(h.get("type") == "command" for h in found), found


def test_nothing_turns_the_hooks_off():
    settings = _settings()
    assert "disableAllHooks" not in settings
    assert "disableAllHooks" not in SETTINGS.read_text(encoding="utf-8")


def test_a_hook_registered_for_one_tool_only_is_caught():
    hooks = {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": "x"}]}]}
    assert shell_hooks_registered_for_one_tool(hooks) == [("PreToolUse", "x", ["Bash"])]
    both = {"PreToolUse": [{"matcher": "Bash|PowerShell", "hooks": [{"type": "command", "command": "x"}]}]}
    assert shell_hooks_registered_for_one_tool(both) == []


def test_the_command_text_guard_is_not_registered():
    """The checked-in settings stay contributor-neutral; the maintainer
    installs the guard in his own settings."""
    assert "command-text-guard" not in SETTINGS.read_text(encoding="utf-8")


def test_merge_reset_apply_am_do_not_ask_but_rebase_does():
    """The git merge ask rule went first (maintainer decision of 2026-11-20);
    #1861 and #1862 on main removed git reset, git apply, git am and
    gh pr merge. The git rebase ask rules stay, in both shells."""
    ask = _settings()["permissions"]["ask"]
    gone = {"git merge", "git reset", "git apply", "git am", "gh pr merge"}
    assert not [r for r in ask if _command_text(_RULE_RE.match(r).group("body")) in gone]
    for text in ("git rebase -i", "git rebase --interactive", "git rebase --onto",
                 "git rebase --root", "git rebase --exec"):
        for tool in SHELL_TOOLS:
            assert any(r.startswith(f"{tool}({text}:") for r in ask), (tool, text)


def test_allow_list_is_bash_only_and_unchanged_in_kind():
    """The allow list is not mirrored (no permission is widened)."""
    allow = _settings()["permissions"]["allow"]
    assert not [r for r in allow if r.startswith("PowerShell(")]


# The rule lists as reviewed for #1780. Any change to them (an allow rule
# added or widened, a deny or ask rule removed in both forms) must change
# these lists in the same pull request. `git merge` is absent from the ask
# list by the maintainer's decision of 2026-11-20; `git reset`, `git apply`,
# `git am` and `gh pr merge` were removed on main by #1861 and #1862.
EXPECTED_DENY_COMMANDS = [
    "git push --force",
    "git push -f",
    "git reset --hard",
    "git clean",
    "dotnet nuget push",
    "npm publish",
    "npm install -g",
    "Publish-Module",
    "curl",
    "wget",
    "Invoke-WebRequest",
    "chmod 777",
    "sudo",
    "su",
    "rm -rf /",
    "ppds data truncate",
]
EXPECTED_ASK_COMMANDS = [
    "git rebase -i",
    "git rebase --interactive",
    "git rebase --onto",
    "git rebase --root",
    "git rebase --exec",
    "git tag -d",
    "git tag -fa",
    "gh pr close",
    "gh issue close",
    "gh release create",
    "gh repo create",
    "gh repo delete",
]
EXPECTED_ALLOW = [
    "Bash(ls:*)",
    "Bash(dir:*)",
    "Bash(cd:*)",
    "Bash(pwd:*)",
    "Bash(cat:*)",
    "Bash(head:*)",
    "Bash(tail:*)",
    "Bash(less:*)",
    "Bash(more:*)",
    "Bash(echo:*)",
    "Bash(printf:*)",
    "Bash(which:*)",
    "Bash(where:*)",
    "Bash(grep:*)",
    "Bash(findstr:*)",
    "Bash(sort:*)",
    "Bash(uniq:*)",
    "Bash(wc:*)",
    "Bash(test:*)",
    "Bash([:*)",
    "Bash(git status:*)",
    "Bash(git diff:*)",
    "Bash(git log:*)",
    "Bash(git show:*)",
    "Bash(git ls-files:*)",
    "Bash(git ls-tree:*)",
    "Bash(git rev-parse:*)",
    "Bash(git rev-list:*)",
    "Bash(git describe:*)",
    "Bash(git fetch:*)",
    "Bash(git worktree list:*)",
    "Bash(dotnet build:*)",
    "Bash(dotnet test:*)",
    "Bash(dotnet restore:*)",
    "Bash(dotnet clean:*)",
    "Bash(npm ci)",
    "Bash(npm run compile --prefix src/PPDS.Extension)",
    "Bash(npm run typecheck:all --prefix src/PPDS.Extension)",
    "Bash(npm run lint --prefix src/PPDS.Extension)",
    "Bash(npm run lint:css --prefix src/PPDS.Extension)",
    "Bash(npm run dead-code --prefix src/PPDS.Extension)",
    "Bash(npm run test --prefix src/PPDS.Extension)",
    "Bash(npm run compile)",
    "Bash(npm run typecheck)",
    "Bash(npm run typecheck:all)",
    "Bash(npm run lint)",
    "Bash(npm run lint:css)",
    "Bash(npm run test)",
    "Bash(npm run ext:compile)",
    "Bash(npm run ext:test)",
    "Bash(npm run ext:test:e2e)",
    "Bash(npm run tui:test)",
    "Bash(npm test:*)",
    "Bash(tsc:*)",
    "Bash(gh pr list:*)",
    "Bash(gh pr view:*)",
    "Bash(gh pr status:*)",
    "Bash(gh pr diff:*)",
    "Bash(gh pr checks:*)",
    "Bash(gh issue list:*)",
    "Bash(gh issue view:*)",
    "Bash(gh issue status:*)",
    "Bash(gh repo view:*)",
    "Bash(gh run list:*)",
    "Bash(gh run view:*)",
    "Bash(gh workflow list:*)",
    "Bash(gh workflow view:*)",
    "Bash(gh auth status)",
    "Bash(gh release list:*)",
    "Bash(gh release view:*)",
    "Bash(gh label list:*)",
    "Bash(actionlint:*)",
    "Bash(yamllint:*)",
    "WebSearch",
]


def _both_forms(commands: list) -> list:
    return [f"{tool}({text}:*)" for text in commands for tool in SHELL_TOOLS]


@pytest.mark.parametrize("kind,commands", [("deny", EXPECTED_DENY_COMMANDS), ("ask", EXPECTED_ASK_COMMANDS)])
def test_deny_and_ask_rules_are_pinned_in_both_forms(kind, commands):
    assert sorted(_settings()["permissions"][kind]) == sorted(_both_forms(commands))


def test_allow_rules_are_pinned():
    assert sorted(_settings()["permissions"]["allow"]) == sorted(EXPECTED_ALLOW)


def test_git_merge_is_not_pinned_as_an_ask_rule():
    assert "git merge" not in EXPECTED_ASK_COMMANDS


@pytest.mark.parametrize("change", [
    ("allow", "add", "Bash(git push:*)"),
    ("allow", "widen", None),
    ("deny", "remove-both", "git push --force"),
    ("ask", "remove-both", "gh pr close"),
])
def test_the_pins_catch_a_change(change):
    """Each kind of change makes a pinned comparison fail."""
    kind, action, item = change
    perms = json.loads(json.dumps(_settings()["permissions"]))
    if action == "add":
        perms[kind].append(item)
    elif action == "widen":
        narrow = next(r for r in perms[kind] if r.endswith(":*)") and " " in r)
        perms[kind][perms[kind].index(narrow)] = narrow.split(" ", 1)[0] + ":*)"
    else:
        perms[kind] = [r for r in perms[kind] if f"({item}:" not in r]
    expected = {"allow": sorted(EXPECTED_ALLOW), "deny": sorted(_both_forms(EXPECTED_DENY_COMMANDS)),
                "ask": sorted(_both_forms(EXPECTED_ASK_COMMANDS))}
    assert sorted(perms[kind]) != expected[kind]
