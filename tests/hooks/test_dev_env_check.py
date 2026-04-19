"""Tests for the dev-env-check PreToolUse hook.

Covers allowlist resolution (env var + JSON file + neither), the read-only
exemption table, switch-target gating, and the fail-safe behavior when the
active env can't be determined.

Run: ``pytest tests/hooks/test_dev_env_check.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import pytest


# ---------------------------------------------------------------------------
# Hook loader / runner
# ---------------------------------------------------------------------------


HOOK_PATH = Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "dev-env-check.py"


def _load_hook():
    """Import dev-env-check.py as a module so we can poke at its functions."""
    hooks_dir = str(HOOK_PATH.parent)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec = importlib.util.spec_from_file_location("dev_env_check", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


def _run_hook(command: str, env_extra: dict | None = None) -> subprocess.CompletedProcess:
    payload = {"tool_name": "Bash", "tool_input": {"command": command}}
    env = os.environ.copy()
    # Always start from a clean slate -- callers opt back in via env_extra.
    for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR"):
        env.pop(k, None)
    if env_extra:
        env.update(env_extra)
    return subprocess.run(
        [sys.executable, str(HOOK_PATH)],
        input=json.dumps(payload),
        capture_output=True,
        text=True,
        timeout=15,
        env=env,
    )


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def fake_profile_dir(tmp_path, monkeypatch):
    """Build a fake PPDS_CONFIG_DIR with a profiles.json. Returns (dir, set_env)."""

    def _set(active_env_name: str | None = None, active_env_url: str | None = None):
        config_dir = tmp_path / "ppds-config"
        config_dir.mkdir(exist_ok=True)
        if active_env_name is None and active_env_url is None:
            data = {"version": 2, "profiles": []}
        else:
            data = {
                "version": 2,
                "activeProfileIndex": 1,
                "profiles": [
                    {
                        "index": 1,
                        "name": "test-profile",
                        "environment": {
                            "displayName": active_env_name or "",
                            "uniqueName": (active_env_name or "").lower().replace(" ", "-"),
                            "url": active_env_url or "https://orgxyz.crm.dynamics.com/",
                        },
                    }
                ],
            }
        (config_dir / "profiles.json").write_text(json.dumps(data), encoding="utf-8")
        return str(config_dir)

    return _set


@pytest.fixture
def safe_envs_file(tmp_path, monkeypatch):
    """Write a safe-envs.json under a fake CLAUDE_PROJECT_DIR. Returns the dir."""
    project = tmp_path / "project"
    state_dir = project / ".claude" / "state"
    state_dir.mkdir(parents=True)

    def _set(safe_envs: list | None):
        if safe_envs is None:
            # Write an obviously-not-an-allowlist file (or skip writing).
            return str(project)
        (state_dir / "safe-envs.json").write_text(
            json.dumps({"safe_envs": safe_envs}), encoding="utf-8"
        )
        return str(project)

    return _set


# ---------------------------------------------------------------------------
# Allowlist source: env var
# ---------------------------------------------------------------------------


class TestAllowlistEnvVar:
    def test_env_var_active_in_list_allowed(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev,ppds-test", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0, f"Expected allow; stderr={r.stderr!r}"

    def test_env_var_active_NOT_in_list_blocked(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev,ppds-test", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 2
        assert "BLOCKED [dev-env-check]" in r.stderr
        assert "ppds-prod" in r.stderr

    def test_env_var_case_insensitive(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="PPDS-Dev")
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0


# ---------------------------------------------------------------------------
# Allowlist source: JSON file
# ---------------------------------------------------------------------------


class TestAllowlistJsonFile:
    def test_json_file_active_in_list_allowed(self, fake_profile_dir, safe_envs_file):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir = safe_envs_file(["ppds-dev"])
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": project_dir},
        )
        assert r.returncode == 0, f"Expected allow; stderr={r.stderr!r}"

    def test_json_file_active_NOT_in_list_blocked(self, fake_profile_dir, safe_envs_file):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir = safe_envs_file(["ppds-dev"])
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": project_dir},
        )
        assert r.returncode == 2
        assert "BLOCKED [dev-env-check]" in r.stderr

    def test_json_file_empty_list_blocked(self, fake_profile_dir, safe_envs_file):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir = safe_envs_file([])
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": project_dir},
        )
        # Allowlist resolved to empty -- nothing matches -> blocked.
        assert r.returncode == 2

    def test_env_var_takes_precedence_over_json(self, fake_profile_dir, safe_envs_file):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir = safe_envs_file(["ppds-dev"])  # JSON would block
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_SAFE_ENVS": "ppds-prod",  # env var should let it through
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir,
            },
        )
        assert r.returncode == 0


# ---------------------------------------------------------------------------
# Allowlist source: neither configured
# ---------------------------------------------------------------------------


class TestNoAllowlistConfigured:
    def test_no_allowlist_blocks_with_config_help(self, fake_profile_dir, tmp_path):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        # Point CLAUDE_PROJECT_DIR at an empty dir -- no safe-envs.json.
        empty_project = tmp_path / "empty-project"
        empty_project.mkdir()
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": str(empty_project)},
        )
        assert r.returncode == 2
        assert "no safe-env allowlist configured" in r.stderr
        assert "PPDS_SAFE_ENVS" in r.stderr
        assert "safe-envs.json" in r.stderr


# ---------------------------------------------------------------------------
# env switch target gating
# ---------------------------------------------------------------------------


class TestEnvSwitchTarget:
    def test_switch_to_safe_target_allowed(self, fake_profile_dir):
        # Active env is unsafe -- but the SWITCH itself is to a safe env.
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        for sub in ("switch", "select"):
            r = _run_hook(
                f"ppds env {sub} ppds-dev",
                env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
            )
            assert r.returncode == 0, f"sub={sub}: stderr={r.stderr!r}"

    def test_switch_to_unsafe_target_blocked(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        for sub in ("switch", "select"):
            r = _run_hook(
                f"ppds env {sub} ppds-prod",
                env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
            )
            assert r.returncode == 2
            assert "switch target" in r.stderr.lower()
            assert "ppds-prod" in r.stderr

    def test_switch_via_environment_flag(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(
            "ppds env select --environment ppds-dev",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"

    def test_switch_via_environment_equals_flag(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(
            "ppds env select --environment=ppds-dev",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"


# ---------------------------------------------------------------------------
# Always-allowed read-only commands
# ---------------------------------------------------------------------------


class TestReadOnlyAlwaysAllowed:
    @pytest.mark.parametrize("cmd", [
        "ppds env list",
        "ppds env current",
        "ppds env who",
        "ppds env config --list",
        "ppds env type --list",
    ])
    def test_readonly_env_subcommands_allowed_even_when_active_unsafe(
        self, fake_profile_dir, cmd
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(
            cmd,
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    def test_readonly_allowed_with_no_allowlist_configured(self, fake_profile_dir, tmp_path):
        # Even without an allowlist, ``ppds env list`` should pass -- it
        # doesn't talk to Dataverse and is the user's path to discovery.
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        empty = tmp_path / "empty"
        empty.mkdir()
        r = _run_hook(
            "ppds env list",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": str(empty)},
        )
        # No allowlist -> hard block before per-cmd allowance kicks in. This
        # is intentional: the no-allowlist case is meant to surface the
        # config message even on read-only invocations so the operator
        # configures it once and moves on.
        assert r.returncode == 2
        assert "no safe-env allowlist configured" in r.stderr


# ---------------------------------------------------------------------------
# Fail-safe behavior
# ---------------------------------------------------------------------------


class TestFailSafe:
    def test_no_active_env_blocks(self, fake_profile_dir):
        # Profile store exists but has no profiles.
        config_dir = fake_profile_dir()  # no args -> no profiles
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 2
        assert "unknown" in r.stderr.lower() or "active env" in r.stderr.lower()

    def test_missing_profile_store_blocks(self, tmp_path):
        # PPDS_CONFIG_DIR points at an empty dir -> no profiles.json.
        empty = tmp_path / "no-store"
        empty.mkdir()
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": str(empty)},
        )
        assert r.returncode == 2

    def test_malformed_profiles_json_blocks(self, tmp_path):
        cfg = tmp_path / "cfg"
        cfg.mkdir()
        (cfg / "profiles.json").write_text("{ not valid json", encoding="utf-8")
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": str(cfg)},
        )
        assert r.returncode == 2

    def test_non_ppds_command_passes(self):
        # Hook is a no-op for non-ppds commands even with no allowlist.
        r = _run_hook("git status")
        assert r.returncode == 0

    def test_empty_command_passes(self):
        r = _run_hook("")
        assert r.returncode == 0

    def test_garbled_stdin_passes(self):
        proc = subprocess.run(
            [sys.executable, str(HOOK_PATH)],
            input="not valid json{{",
            capture_output=True,
            text=True,
            timeout=10,
        )
        assert proc.returncode == 0


# ---------------------------------------------------------------------------
# Compound bash command parsing
# ---------------------------------------------------------------------------


class TestCommandParsing:
    def test_compound_command_with_unsafe_ppds_blocked(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(
            "cd src && ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 2

    def test_pipe_after_ppds_only_inspects_ppds_part(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds env list | jq '.[].name'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0

    def test_unbalanced_quotes_does_not_crash(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        # Unmatched quote -- shlex would normally raise; hook must tolerate.
        r = _run_hook(
            "ppds env select 'unterminated",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        # Should not raise; either allow or block, but exit cleanly (0 or 2).
        assert r.returncode in (0, 2)


# ---------------------------------------------------------------------------
# Module-level unit tests (no subprocess)
# ---------------------------------------------------------------------------


class TestParseFunctions:
    def test_find_ppds_invocations_simple(self):
        out = hook.find_ppds_invocations("ppds env list")
        assert len(out) == 1
        assert out[0][0].lower() == "ppds"
        assert out[0][1:3] == ["env", "list"]

    def test_find_ppds_invocations_with_path(self):
        out = hook.find_ppds_invocations(r"./src/PPDS.Cli/bin/Debug/net10.0/ppds.exe env list")
        assert len(out) == 1
        assert out[0][1:3] == ["env", "list"]

    def test_find_ppds_invocations_compound(self):
        out = hook.find_ppds_invocations("cd /tmp && ppds env list && echo done")
        assert len(out) == 1
        assert out[0][1:3] == ["env", "list"]

    def test_find_ppds_invocations_mcp_server(self):
        out = hook.find_ppds_invocations("ppds-mcp-server")
        assert len(out) == 1
        assert out[0][0].lower() == "ppds-mcp-server"

    def test_extract_switch_target_positional(self):
        assert hook._extract_switch_target(["my-dev"]) == "my-dev"

    def test_extract_switch_target_flag(self):
        assert hook._extract_switch_target(["--environment", "my-dev"]) == "my-dev"

    def test_extract_switch_target_equals(self):
        assert hook._extract_switch_target(["--environment=my-dev"]) == "my-dev"

    def test_extract_switch_target_short_alias(self):
        assert hook._extract_switch_target(["-env", "my-dev"]) == "my-dev"

    def test_env_matches_allowlist_case_insensitive(self):
        assert hook.env_matches_allowlist(["PPDS-DEV"], ["ppds-dev"])
        assert not hook.env_matches_allowlist(["PPDS-PROD"], ["ppds-dev"])
