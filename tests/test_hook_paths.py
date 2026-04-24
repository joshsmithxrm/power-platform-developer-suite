"""Regression test: all hook commands in settings.json must use absolute paths.

Relative paths like `.claude/hooks/foo.py` break when the bash CWD drifts
from the project root. Every command must reference $CLAUDE_PROJECT_DIR.
See: https://github.com/ppdsw/ppds/issues/906
"""
import json
import os
import re

import pytest

SETTINGS_PATH = os.path.normpath(
    os.path.join(os.path.dirname(__file__), os.pardir, ".claude", "settings.json")
)


def _collect_hook_commands():
    with open(SETTINGS_PATH) as f:
        settings = json.load(f)

    hooks_section = settings.get("hooks", {})
    commands = []
    for event, entries in hooks_section.items():
        for entry in entries:
            for hook in entry.get("hooks", []):
                if hook.get("type") == "command":
                    commands.append((event, hook["command"]))
    return commands


HOOK_COMMANDS = _collect_hook_commands()


@pytest.mark.parametrize("event,command", HOOK_COMMANDS, ids=[c for _, c in HOOK_COMMANDS])
def test_hook_command_uses_absolute_path(event, command):
    assert "$CLAUDE_PROJECT_DIR" in command, (
        f"Hook in {event} uses a relative path that breaks when CWD drifts: {command}"
    )


def test_no_bare_relative_claude_hooks_path():
    with open(SETTINGS_PATH) as f:
        content = f.read()
    matches = re.findall(r'(?<!\$CLAUDE_PROJECT_DIR/)\.claude/hooks/', content)
    assert not matches, (
        f"Found bare relative .claude/hooks/ paths (without $CLAUDE_PROJECT_DIR): {matches}"
    )
