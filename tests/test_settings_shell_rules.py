"""The checked-in permission rules and hooks treat both shell tools alike.

Sessions issue commands through the Bash tool or the PowerShell tool. Every
deny and ask rule must exist for both, with the same command text, and every
hook registered for one shell tool must be registered for the other.
Claude Code's enforcement itself is observed in a real session (see #1780);
these tests keep the files consistent.
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
    """Rule suffix form per tool: {"Bash": {":*"}, "PowerShell": {" *"}}."""
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
    """Every rule for a tool uses the form observed to be enforced for it."""
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


def test_a_hook_registered_for_one_tool_only_is_caught():
    hooks = {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": "x"}]}]}
    assert shell_hooks_registered_for_one_tool(hooks) == [("PreToolUse", "x", ["Bash"])]
    both = {"PreToolUse": [{"matcher": "Bash|PowerShell", "hooks": [{"type": "command", "command": "x"}]}]}
    assert shell_hooks_registered_for_one_tool(both) == []


def test_the_command_text_guard_is_not_registered():
    """The checked-in settings stay contributor-neutral; the maintainer
    installs the guard in his own settings."""
    assert "command-text-guard" not in SETTINGS.read_text(encoding="utf-8")


def test_git_merge_does_not_ask_but_gh_pr_merge_and_git_reset_do():
    """Maintainer decision of 2026-11-20: the git merge ask rule is removed;
    gh pr merge and git reset keep asking in both shells."""
    ask = _settings()["permissions"]["ask"]
    assert not [r for r in ask if _command_text(_RULE_RE.match(r).group("body")) == "git merge"]
    for text in ("gh pr merge", "git reset"):
        for tool in SHELL_TOOLS:
            assert any(r.startswith(f"{tool}({text}") for r in ask), (tool, text)


def test_allow_list_is_bash_only_and_unchanged_in_kind():
    """The allow list is not mirrored (no permission is widened)."""
    allow = _settings()["permissions"]["allow"]
    assert not [r for r in allow if r.startswith("PowerShell(")]
