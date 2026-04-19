"""Tests for the shakedown-readonly PreToolUse hook.

Verifies the hook is a no-op outside ``PPDS_SHAKEDOWN=1`` and blocks
write/mutation patterns when active. Read-only commands (env list, query,
plugins list, --dry-run deploy) must always pass.

Run: ``pytest tests/hooks/test_shakedown_readonly.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest


HOOK_PATH = Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "shakedown-readonly.py"


def _load_hook():
    spec = importlib.util.spec_from_file_location("shakedown_readonly", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


def _run_hook(command: str, env_extra: dict | None = None) -> subprocess.CompletedProcess:
    payload = {"tool_name": "Bash", "tool_input": {"command": command}}
    env = os.environ.copy()
    env.pop("PPDS_SHAKEDOWN", None)
    if env_extra:
        env.update(env_extra)
    return subprocess.run(
        [sys.executable, str(HOOK_PATH)],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=10,
        env=env,
    )


# ---------------------------------------------------------------------------
# No-op behavior outside shakedown mode
# ---------------------------------------------------------------------------


class TestNoOpOutsideShakedown:
    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy",
        "ppds plugins delete --name Foo",
        "ppds env delete --name dev",
        "ppds data create account --name test",
        "ppds solutions import --file foo.zip",
    ])
    def test_writes_pass_when_var_unset(self, cmd):
        # No PPDS_SHAKEDOWN set -- hook is a no-op.
        r = _run_hook(cmd)
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    def test_writes_pass_when_var_zero(self):
        r = _run_hook("ppds plugins deploy", env_extra={"PPDS_SHAKEDOWN": "0"})
        assert r.returncode == 0

    def test_writes_pass_when_var_empty(self):
        r = _run_hook("ppds plugins deploy", env_extra={"PPDS_SHAKEDOWN": ""})
        assert r.returncode == 0


# ---------------------------------------------------------------------------
# Mutation patterns are blocked when PPDS_SHAKEDOWN=1
# ---------------------------------------------------------------------------


class TestMutationsBlocked:
    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy",
        "ppds plugins delete --name Foo",
        "ppds env delete --name dev",
        "ppds env update --name dev",
        "ppds data create account --name test",
        "ppds data update account --id 123",
        "ppds data delete account --id 123",
        "ppds solutions import --file foo.zip",
        "ppds migrate apply",
    ])
    def test_writes_blocked_when_var_one(self, cmd):
        r = _run_hook(cmd, env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 2, f"cmd={cmd}: stderr={r.stderr!r}"
        assert "BLOCKED [shakedown-readonly]" in r.stderr
        assert "PPDS_SHAKEDOWN" in r.stderr  # message references the env var

    def test_block_message_includes_unset_hint(self):
        r = _run_hook("ppds plugins deploy", env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 2
        assert "unset PPDS_SHAKEDOWN" in r.stderr


# ---------------------------------------------------------------------------
# Read-only commands always allowed
# ---------------------------------------------------------------------------


class TestReadOnlyAllowed:
    @pytest.mark.parametrize("cmd", [
        "ppds env list",
        "ppds env current",
        "ppds env who",
        "ppds env switch ppds-dev",
        "ppds env select ppds-dev",
        "ppds env config --list",
        "ppds env type --list",
        "ppds plugins list",
        "ppds logs tail",
        "ppds logs show",
        "ppds auth list",
        "ppds version",
        "ppds help",
    ])
    def test_reads_allowed_when_var_one(self, cmd):
        r = _run_hook(cmd, env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"


# ---------------------------------------------------------------------------
# --dry-run carve-out for plugins deploy
# ---------------------------------------------------------------------------


class TestDryRunExempt:
    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy --dry-run",
        "ppds plugins deploy -n",
        "ppds plugins deploy --some-other-arg --dry-run",
    ])
    def test_dry_run_allowed(self, cmd):
        r = _run_hook(cmd, env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    def test_real_deploy_still_blocked(self):
        r = _run_hook("ppds plugins deploy --some-arg value", env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 2


# ---------------------------------------------------------------------------
# Edge: malformed input handling
# ---------------------------------------------------------------------------


class TestEdgeCases:
    def test_garbled_stdin_passes(self):
        proc = subprocess.run(
            [sys.executable, str(HOOK_PATH)],
            input="not valid json{{",
            capture_output=True,
            text=True,
            timeout=10,
            env={**os.environ, "PPDS_SHAKEDOWN": "1"},
        )
        # Garbled input -> allow (don't break unrelated tools).
        assert proc.returncode == 0

    def test_empty_command_passes(self):
        r = _run_hook("", env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 0

    def test_non_ppds_command_passes(self):
        r = _run_hook("git push origin main", env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 0

    def test_unbalanced_quotes_in_ppds_command(self):
        # Hook must not crash on bad shell input.
        r = _run_hook("ppds data create account --name 'unterminated", env_extra={"PPDS_SHAKEDOWN": "1"})
        # Either 0 or 2 is fine -- the rule is "must not raise".
        assert r.returncode in (0, 2)

    def test_compound_command_with_blocked_ppds(self):
        r = _run_hook(
            "cd src && ppds plugins deploy && echo done",
            env_extra={"PPDS_SHAKEDOWN": "1"},
        )
        assert r.returncode == 2

    def test_mcp_server_launch_allowed(self):
        # The MCP server is long-running; we can't reason about whether it
        # will write. Allow -- dev-env-check still gates the env it talks to.
        r = _run_hook("ppds-mcp-server", env_extra={"PPDS_SHAKEDOWN": "1"})
        assert r.returncode == 0


# ---------------------------------------------------------------------------
# Module-level unit tests
# ---------------------------------------------------------------------------


class TestIsMutation:
    def test_known_mutation_verb(self):
        is_mut, reason = hook.is_mutation(["ppds", "data", "create", "account"])
        assert is_mut
        assert "create" in reason

    def test_readonly_subcommand_explicit(self):
        is_mut, _ = hook.is_mutation(["ppds", "env", "list"])
        assert not is_mut

    def test_plugins_deploy_no_dry_run(self):
        is_mut, _ = hook.is_mutation(["ppds", "plugins", "deploy"])
        assert is_mut

    def test_plugins_deploy_with_dry_run(self):
        is_mut, _ = hook.is_mutation(["ppds", "plugins", "deploy", "--dry-run"])
        assert not is_mut

    def test_plugins_deploy_with_short_dry_run(self):
        is_mut, _ = hook.is_mutation(["ppds", "plugins", "deploy", "-n"])
        assert not is_mut

    def test_logs_wildcard(self):
        # Anything under 'logs' is read-only.
        for verb in ("tail", "show", "stream", "anything"):
            is_mut, _ = hook.is_mutation(["ppds", "logs", verb])
            assert not is_mut, f"verb={verb}"

    def test_short_argv_not_mutation(self):
        is_mut, _ = hook.is_mutation(["ppds"])
        assert not is_mut
        is_mut, _ = hook.is_mutation(["ppds", "env"])
        assert not is_mut
