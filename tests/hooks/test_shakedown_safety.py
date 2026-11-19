"""Tests for the shakedown-safety PreToolUse hook.

Covers both concerns folded into the consolidated hook:

1. Env allowlist gating (was `dev-env-check.py`) -- allowlist resolution
   from env var / settings.json / legacy safe-envs.json, active-env read
   from the profile store, read-only subcommand exemptions, switch-target
   gating, fail-safe behavior.
2. Write-block during shakedown (was `shakedown-readonly.py`) -- no-op
   outside the configured env var, mutation verb table, `--dry-run`
   carve-out, `--read-only` exemption for `ppds-mcp-server`. Also covers
   sentinel-file activation (works around Claude Code's per-invocation
   Bash-tool shells where inline `PPDS_SHAKEDOWN=1` does not propagate
   to this hook subprocess) and the 24h staleness self-heal.

Run: ``pytest tests/hooks/test_shakedown_safety.py -v``
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import pytest


HOOK_PATH = (
    Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "shakedown-safety.py"
)


def _load_hook():
    hooks_dir = str(HOOK_PATH.parent)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec = importlib.util.spec_from_file_location("shakedown_safety", str(HOOK_PATH))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


def _run_hook(command: str, env_extra: dict | None = None) -> subprocess.CompletedProcess:
    payload = {"tool_name": "Bash", "tool_input": {"command": command}}
    env = os.environ.copy()
    # Clean slate -- callers opt back in via env_extra.
    for k in ("PPDS_SAFE_ENVS", "PPDS_CONFIG_DIR", "PPDS_SHAKEDOWN"):
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
def fake_profile_dir(tmp_path):
    """Build a fake PPDS_CONFIG_DIR with a profiles.json."""

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
def project_dir(tmp_path):
    """Build a fake CLAUDE_PROJECT_DIR. Returns helpers for writing settings.json / legacy file."""
    project = tmp_path / "project"
    (project / ".claude" / "state").mkdir(parents=True)

    def write_settings(safety: dict | None):
        settings = {}
        if safety is not None:
            settings["safety"] = safety
        (project / ".claude" / "settings.json").write_text(
            json.dumps(settings), encoding="utf-8"
        )

    def write_legacy(safe_envs: list | None):
        if safe_envs is None:
            return
        (project / ".claude" / "state" / "safe-envs.json").write_text(
            json.dumps({"safe_envs": safe_envs}), encoding="utf-8"
        )

    return {
        "path": str(project),
        "write_settings": write_settings,
        "write_legacy": write_legacy,
    }


# ===========================================================================
# Concern 1: env allowlist gating
# ===========================================================================


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
        assert "BLOCKED [shakedown-safety/env]" in r.stderr
        assert "ppds-prod" in r.stderr

    def test_env_var_case_insensitive(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="PPDS-Dev")
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0


class TestAllowlistFromSettings:
    """Finding #19: allowlist lives in .claude/settings.json under safety.shakedown_safe_envs."""

    def test_settings_allowlist_active_in_list_allowed(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"

    def test_settings_allowlist_active_NOT_in_list_blocked(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 2
        assert "BLOCKED [shakedown-safety/env]" in r.stderr

    def test_settings_allowlist_empty_list_leaves_gating_off(self, fake_profile_dir, project_dir):
        """An empty list is the checked-in default: env gating is opt-in."""
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": []})
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, r.stderr

    def test_env_var_takes_precedence_over_settings(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})  # would block
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_SAFE_ENVS": "ppds-prod",  # env var should let it through
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0

    def test_block_message_references_settings_json(self, fake_profile_dir, project_dir):
        """Help text on an env block must mention settings.json, not the old safe-envs.json."""
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 2
        assert "settings.json" in r.stderr
        assert "shakedown_safe_envs" in r.stderr


class TestSettingsMigrationFallback:
    """Finding #19 migration path: legacy safe-envs.json still honored when settings has no safety block."""

    def test_legacy_file_used_when_settings_missing_safety_block(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"](None)  # no safety block
        project_dir["write_legacy"](["ppds-dev"])
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        # Migration fallback -- legacy file wins when new key absent.
        assert r.returncode == 0, f"stderr={r.stderr!r}"

    def test_settings_safety_block_takes_precedence_over_legacy(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        # Legacy file would allow ppds-prod; settings.json must win.
        project_dir["write_legacy"](["ppds-prod"])
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 2


class TestNoAllowlistConfigured:
    """Env gating is opt-in: with no allowlist a fresh clone is not gated."""

    def test_no_allowlist_leaves_env_gating_off(self, fake_profile_dir, tmp_path):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        # CLAUDE_PROJECT_DIR at an empty dir -> no settings.json, no legacy file.
        empty_project = tmp_path / "empty-project"
        empty_project.mkdir()
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": str(empty_project)},
        )
        assert r.returncode == 0, r.stderr
        assert r.stderr == ""

    def test_no_allowlist_does_not_need_a_profile_store(self, tmp_path):
        """The fail-safe on an unreadable profile store only applies once gating is on."""
        empty_project = tmp_path / "empty-project"
        empty_project.mkdir()
        r = _run_hook(
            "ppds --help",
            env_extra={
                "PPDS_CONFIG_DIR": str(tmp_path / "no-such-config"),
                "CLAUDE_PROJECT_DIR": str(empty_project),
            },
        )
        assert r.returncode == 0, r.stderr

    def test_write_block_still_applies_without_an_allowlist(self, fake_profile_dir, tmp_path):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        empty_project = tmp_path / "empty-project"
        empty_project.mkdir()
        r = _run_hook(
            "ppds solutions import --file s.zip",
            env_extra={
                "PPDS_SHAKEDOWN": "1",
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": str(empty_project),
            },
        )
        assert r.returncode == 2
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr


class TestMisconfiguredAllowlistBlocks:
    """An allowlist that is present but unusable must block, not turn the gate off.

    The active env is the safe one in every case, so a block can only come
    from the misconfiguration check, not from the env match.
    """

    MISCONFIGURED = "BLOCKED [shakedown-safety/env]: the safe-env allowlist is misconfigured."

    @staticmethod
    def _env(config_dir: str, project: dict, **extra) -> dict:
        env = {"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": project["path"]}
        env.update(extra)
        return env

    @staticmethod
    def _write_raw(project: dict, rel: str, text: str) -> None:
        (Path(project["path"]) / rel).write_text(text, encoding="utf-8")

    def test_settings_list_not_a_list_blocks(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": "ppds-dev"})
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert self.MISCONFIGURED in r.stderr
        assert "`safety.shakedown_safe_envs` in .claude/settings.json is a string" in r.stderr

    def test_unparseable_settings_json_blocks(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        self._write_raw(project_dir, ".claude/settings.json", "{ not valid json")
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert self.MISCONFIGURED in r.stderr
        assert ".claude/settings.json could not be parsed as JSON" in r.stderr

    def test_empty_settings_list_falls_through_to_legacy_file(
        self, fake_profile_dir, project_dir
    ):
        """Checked-in [] must not hide a legacy list: the legacy list gates."""
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": []})
        project_dir["write_legacy"](["ppds-dev"])
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert "active env is not in the safe-env allowlist" in r.stderr
        assert "Allowlist:  ppds-dev" in r.stderr

    def test_empty_settings_list_legacy_file_allows_safe_env(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": []})
        project_dir["write_legacy"](["ppds-dev"])
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 0, r.stderr

    def test_env_var_naming_no_env_blocks(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra=self._env(config_dir, project_dir, PPDS_SAFE_ENVS=","),
        )
        assert r.returncode == 2, r.stderr
        assert self.MISCONFIGURED in r.stderr
        assert "PPDS_SAFE_ENVS is set to ','" in r.stderr

    def test_unparseable_legacy_file_blocks(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        self._write_raw(project_dir, ".claude/state/safe-envs.json", "{ not valid json")
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert self.MISCONFIGURED in r.stderr
        assert ".claude/state/safe-envs.json could not be parsed as JSON" in r.stderr

    def test_legacy_safe_envs_not_a_list_blocks(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        self._write_raw(
            project_dir, ".claude/state/safe-envs.json", json.dumps({"safe_envs": "ppds-dev"})
        )
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert self.MISCONFIGURED in r.stderr
        assert "`safe_envs` in .claude/state/safe-envs.json is a string" in r.stderr

    def test_settings_list_naming_no_env_blocks(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["", "  "]})
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert self.MISCONFIGURED in r.stderr
        assert "has entries but names no env" in r.stderr

    def test_misconfigured_message_includes_config_help(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": "ppds-dev"})
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2
        assert "Configure the allowlist via either:" in r.stderr

    def test_empty_everywhere_stays_off(self, fake_profile_dir, project_dir):
        """[] in settings plus a legacy file with [] is still 'not configured'."""
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        project_dir["write_settings"]({"shakedown_safe_envs": []})
        project_dir["write_legacy"]([])
        r = _run_hook("ppds query sql 'SELECT 1'", env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 0, r.stderr
        assert r.stderr == ""


class TestEnvSwitchTarget:
    def test_switch_to_safe_target_allowed(self, fake_profile_dir):
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


class TestFailSafe:
    def test_no_active_env_blocks(self, fake_profile_dir):
        config_dir = fake_profile_dir()  # no args -> no profiles
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 2
        assert "unknown" in r.stderr.lower() or "active env" in r.stderr.lower()

    def test_missing_profile_store_blocks(self, tmp_path):
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
        r = _run_hook(
            "ppds env select 'unterminated",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode in (0, 2)

    def test_quoted_semicolon_not_command_boundary(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds query \"SELECT FROM t WHERE n=';'\"",
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"


# ===========================================================================
# Command matching: .NET host forms and command-position false positives
# ===========================================================================


ENV_BLOCK = "BLOCKED [shakedown-safety/env]: active env is not in the safe-env allowlist."
READONLY_BLOCK = "BLOCKED [shakedown-safety/readonly]: write/mutation command refused."

# (id, command, normalised command shown in the block, write-block pattern)
DOTNET_HOST_SHAPES = [
    ("run-project-dir",
     "dotnet run --project src/PPDS.Cli -- data truncate --entity account",
     "ppds data truncate --entity account", "data truncate"),
    ("run-project-csproj",
     "dotnet run --project src/PPDS.Cli/PPDS.Cli.csproj -- data delete --entity account",
     "ppds data delete --entity account", "data delete"),
    ("run-project-backslash-absolute",
     r"dotnet run --project 'C:\repo\src\PPDS.Cli\PPDS.Cli.csproj' -- data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("run-project-posix-absolute-with-options",
     "dotnet run --project /c/repo/src/PPDS.Cli/ -c Release --no-build -f net10.0 -- data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("run-project-equals",
     "dotnet run --project=src/PPDS.Cli -- data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("run-p-alias",
     "dotnet run -p src/PPDS.Cli -- data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("run-p-property-then-project",
     "dotnet run -p:Configuration=Release --project src/PPDS.Cli -- data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("run-project-no-separator",
     "dotnet run --project src/PPDS.Cli data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("run-mcp-project",
     "dotnet run --project src/PPDS.Mcp/PPDS.Mcp.csproj -- --profile x",
     "ppds-mcp-server --profile x", "missing --read-only flag"),
    ("dll",
     "dotnet src/PPDS.Cli/bin/Debug/net10.0/ppds.dll data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("exec-dll",
     "dotnet exec src/PPDS.Cli/bin/Debug/net10.0/ppds.dll data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("mcp-dll",
     "dotnet src/PPDS.Mcp/bin/Debug/net10.0/ppds-mcp-server.dll",
     "ppds-mcp-server", "missing --read-only flag"),
    ("cd-then-run",
     "cd src/PPDS.Cli && dotnet run -- data delete --entity a",
     "ppds data delete --entity a", "data delete"),
    ("cd-then-run-mcp",
     "cd src/PPDS.Mcp && dotnet run",
     "ppds-mcp-server", "missing --read-only flag"),
]

NOT_PPDS_DOTNET = [
    "dotnet build PPDS.sln",
    "dotnet test PPDS.sln --filter Category!=Integration",
    "dotnet run --project src/PPDS.DocsGen -- data delete --entity a",
    "dotnet run --project tests/PPDS.Cli.Tests -- data delete --entity a",
    "dotnet run -- data delete --entity a",
    "cd src/PPDS.Cli && cd .. && dotnet run -- data delete --entity a",
]

FALSE_POSITIVES = [
    "grep -rn ppds docs/",
    'git commit -m "fix: ppds query crash"',
    'git commit -m "chore: ppds data delete cleanup"',
    'echo "a && ppds data delete --entity x"',
    "ls src/ppds",
    "cat notes.txt # ppds data delete",
    "git commit -m \"$(cat <<'EOF'\nfix: don't crash\n\nppds data delete --entity account now asks first.\nEOF\n)\"",
]


def _gate_env(config_dir: str, project: dict, **extra) -> dict:
    env = {
        "PPDS_SAFE_ENVS": "ppds-dev",
        "PPDS_CONFIG_DIR": config_dir,
        "CLAUDE_PROJECT_DIR": project["path"],
    }
    env.update(extra)
    return env


class TestDotnetHostInvocations:
    """PPDS run through the .NET host gets both gates, like a direct ``ppds``."""

    @pytest.mark.parametrize(
        "cmd,shown", [(c, s) for _, c, s, _ in DOTNET_HOST_SHAPES],
        ids=[i for i, *_ in DOTNET_HOST_SHAPES],
    )
    def test_env_gate_blocks_unsafe_active_env(self, fake_profile_dir, project_dir, cmd, shown):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert ENV_BLOCK in r.stderr
        assert f"Command:    {shown}\n" in r.stderr

    @pytest.mark.parametrize(
        "cmd,pattern", [(c, p) for _, c, _, p in DOTNET_HOST_SHAPES],
        ids=[i for i, *_ in DOTNET_HOST_SHAPES],
    )
    def test_write_block_during_shakedown(self, fake_profile_dir, project_dir, cmd, pattern):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 2, r.stderr
        assert READONLY_BLOCK in r.stderr
        assert pattern in r.stderr

    @pytest.mark.parametrize(
        "cmd", [c for _, c, _, _ in DOTNET_HOST_SHAPES],
        ids=[i for i, *_ in DOTNET_HOST_SHAPES],
    )
    def test_safe_env_allowed(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir))
        assert r.returncode == 0, r.stderr
        assert r.stderr == ""

    def test_mcp_run_with_read_only_allowed_during_shakedown(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "dotnet run --project src/PPDS.Mcp -- --read-only",
            env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"),
        )
        assert r.returncode == 0, r.stderr

    def test_bare_run_of_cli_project_treated_like_bare_ppds(self, fake_profile_dir, project_dir):
        """``dotnet run --project src/PPDS.Cli`` alone is ``ppds`` with no args: allowed."""
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(
            "dotnet run --project src/PPDS.Cli",
            env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"),
        )
        assert r.returncode == 0, r.stderr
        assert r.stderr == ""

    @pytest.mark.parametrize("cmd", NOT_PPDS_DOTNET)
    def test_other_dotnet_commands_not_gated(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"
        assert r.stderr == ""


class TestCommandPositionOnly:
    """A ``ppds`` the parse proves is data (an argument of a data-only program,
    a comment, a heredoc body such a program reads) is not an invocation.

    Run with an allowlist and an unsafe active env (and a shakedown armed),
    the situation where these used to block.
    """

    @pytest.mark.parametrize("cmd", FALSE_POSITIVES)
    def test_ppds_outside_command_position_allowed(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"
        assert r.stdout == ""
        assert r.stderr == ""

    @pytest.mark.parametrize("cmd", [
        "echo $(ppds data delete --entity a)",
        "echo `ppds data delete --entity a`",
        'echo "$(ppds data delete --entity a)"',
        "(cd x; ppds data delete --entity a)",
        'bash -c "ppds data delete --entity a"',
        "FOO=1 ppds data delete --entity a",
        "sudo -u me ppds data delete --entity a",
        "ppds env who 2>&1 | ppds data delete --entity a",
        r"C:\tools\ppds.exe data delete --entity a",
        '"ppds" data delete --entity a',
    ])
    def test_command_position_forms_still_blocked(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir))
        assert r.returncode == 2, f"cmd={cmd}: stderr={r.stderr!r}"
        assert ENV_BLOCK in r.stderr
        assert "data delete --entity a" in r.stderr


class TestFindInvocationsParsing:
    """Unit view of ``find_ppds_invocations`` for the same inputs."""

    @pytest.mark.parametrize(
        "cmd,shown", [(c, s) for _, c, s, _ in DOTNET_HOST_SHAPES],
        ids=[i for i, *_ in DOTNET_HOST_SHAPES],
    )
    def test_dotnet_host_shapes_normalised(self, cmd, shown):
        assert shown.split(" ") in hook.find_ppds_invocations(cmd)

    def test_bare_run_of_cli_project(self):
        assert hook.find_ppds_invocations("dotnet run --project src/PPDS.Cli") == [["ppds"]]

    def test_run_mcp_read_only(self):
        assert hook.find_ppds_invocations("dotnet run --project src/PPDS.Mcp -- --read-only") == [
            ["ppds-mcp-server", "--read-only"]
        ]

    def test_dotnet_local_tool(self):
        assert hook.find_ppds_invocations("dotnet ppds env who") == [["ppds", "env", "who"]]
        assert hook.find_ppds_invocations("dotnet tool run ppds env who") == [["ppds", "env", "who"]]

    @pytest.mark.parametrize("cmd", NOT_PPDS_DOTNET)
    def test_other_dotnet_commands(self, cmd):
        assert hook.find_ppds_invocations(cmd) == []

    @pytest.mark.parametrize("cmd", FALSE_POSITIVES)
    def test_false_positives(self, cmd):
        assert hook.find_ppds_invocations(cmd) == []

    @pytest.mark.parametrize("cmd,expected", [
        ("echo $(ppds env who)", [["ppds", "env", "who"]]),
        ("echo `ppds env who`", [["ppds", "env", "who"]]),
        ('echo "$(ppds env who)"', [["ppds", "env", "who"]]),
        ("(cd x; ppds env who)", [["ppds", "env", "who"]]),
        ("bash -c 'ppds env who'", [["ppds", "env", "who"]]),
        ("FOO=1 BAR=2 ppds env who", [["ppds", "env", "who"]]),
        ("sudo -u me env X=1 ppds env who", [["ppds", "env", "who"]]),
        ("ppds env who 2>&1 | jq .", [["ppds", "env", "who"]]),
        ("ppds env who > out.json", [["ppds", "env", "who"]]),
        ("ppds env who; ppds env list", [["ppds", "env", "who"], ["ppds", "env", "list"]]),
        ("if ppds env who; then echo ok; fi", [["ppds", "env", "who"]]),
        (r"C:\tools\ppds.exe env who", [["ppds.exe", "env", "who"]]),
        ('"ppds" env who', [["ppds", "env", "who"]]),
        ("ppds query \"SELECT FROM t WHERE n=';'\"", [["ppds", "query", "SELECT FROM t WHERE n=';'"]]),
    ])
    def test_command_position_forms(self, cmd, expected):
        found = hook.find_ppds_invocations(cmd)
        for argv in expected:
            assert argv in found, found


D = "data delete --entity account"

# The numbered regressions an independent review found in the first
# version of the command-position parser (old matcher blocked, it allowed).
LEGACY_REGRESSIONS = [
    ("1-heredoc-odd-apostrophe",
     "cat > accounts.csv <<'EOF'\nname\nO'Brien Ltd\nEOF\nppds data import --file accounts.csv --entity account"),
    ("1-commit-heredoc-then-ppds",
     f"git commit -m \"$(cat <<'EOF'\nfix: don't crash\nEOF\n)\" && ppds {D}"),
    ("2-ansi-c-quote", f"echo $'it\\'s' ; ppds {D}"),
    ("3-pwsh-noprofile", f"pwsh -NoProfile -Command ppds {D}"),
    ("3-pwsh-call-operator", f'pwsh -Command "& ppds {D}"'),
    ("3-cmd-c", f"cmd /c ppds {D}"),
    ("3-cmd-c-compound", f'cmd /c "cd x && ppds {D}"'),
    ("4-bash-euo-pipefail", f'bash -euo pipefail -c "cd x && ppds {D}"'),
    ("4-bash-rcfile", f'bash --rcfile x -c "cd y && ppds {D}"'),
    ("4-su-c", f'su -c "cd x && ppds {D}"'),
    ("4-bash-here-string", f"bash <<< 'cd x && ppds {D}'"),
    ("5-sudo-long-user", f"sudo --user me ppds {D}"),
    ("5-timeout-quoted-duration", f'timeout "$T" ppds {D}'),
    ("5-xargs-i", f"echo a | xargs -i ppds {D} --id {{}}"),
    ("5-xargs-delimiter", f"echo a | xargs --delimiter '\\n' ppds {D}"),
    ("5-usr-bin-time-f", f"/usr/bin/time -f %e ppds {D}"),
    ("5-watch-d", f"watch -d ppds {D}"),
    ("6-find-exec", f"find . -name '*.json' -exec ppds {D} {{}} \\;"),
    ("6-npx", f"npx ppds {D}"),
    ("6-setsid", f"setsid ppds {D}"),
    ("6-strace", f"strace -f ppds {D}"),
    ("6-parallel", f"parallel ppds {D} ::: a b"),
    ("6-flock", f"flock /tmp/l ppds {D}"),
    ("6-start", f"start ppds {D}"),
    ("6-wsl", f"wsl ppds {D}"),
    ("6-coproc", f"coproc ppds {D}"),
    ("7-tool-run-options", f"dotnet tool run --allow-roll-forward ppds {D}"),
    ("8-function", f"function f {{ ppds {D}; }}; f"),
    ("8-case-in-substitution", f"echo $(case $y in a) ppds {D};; esac)"),
    ("8-escaped-nested-backticks", f"echo `echo \\`ppds {D}\\``"),
    ("8-deep-nesting", "echo " + "$(echo " * 9 + f"$(ppds {D})" + ")" * 9),
    ("8-command-v-substitution", f'"$(command -v ppds)" {D}'),
]

# (exonerated: allowed, nearest case that must still block)
EXONERATION_PAIRS = [
    ("echo-arg", f'echo "a && ppds {D}"', f'echo "a && ppds {D}" | bash'),
    ("echo-to-file", f'echo "a && ppds {D}"', f'echo "a && ppds {D}" > x.sh && bash x.sh'),
    ("echo-under-wrapper", f"echo 'a && ppds {D}'", f"sudo echo 'a && ppds {D}'"),
    ("git-message", f'git commit -m "fix: ppds {D}"', f'git -c core.sshCommand="ppds {D}" fetch'),
    ("git-rebase-exec", f'git commit -m "fix: ppds {D}"', f'git rebase --exec "ppds {D}" main'),
    ("git-alias", f'git log --grep "ppds {D}"', f"git config alias.x '!ppds {D}'"),
    ("gh-alias", f'gh pr create --title "ppds {D}" --body x', f"gh alias set x 'ppds {D}'"),
    ("rg-pre", f"rg 'a && ppds {D}' docs", f"rg --pre 'ppds {D}' docs"),
    ("quoted-heredoc", f"cat <<'EOF'\nppds {D}\nEOF", f"cat <<EOF | bash\nppds {D}\nEOF"),
    ("heredoc-substitution", f"cat <<'EOF'\n$(ppds {D})\nEOF", f"cat <<EOF\n$(ppds {D})\nEOF"),
    ("comment", f"echo hi # ppds {D}", f"echo hi #x\nppds {D}"),
    ("unmodelled-construct", f'echo "a && ppds {D}"', f'echo "a && ppds {D}" ${{X}}'),
    ("unterminated-quote", f'echo "a && ppds {D}"', f"echo \"a && ppds {D}\" 'oops"),
    ("commit-F-stdin", f"git commit -F - <<'EOF'\nfix: x\nppds {D}\nEOF",
     f"git commit -F - <<'EOF'\nfix: x\nEOF\nppds {D}"),
]

HEREDOC_CASES = [
    # (id, command, blocked)
    ("commit-message-form", f"git commit -m \"$(cat <<'EOF'\nfix: ppds {D}\nEOF\n)\"", False),
    ("commit-message-form-then-ppds", f"git commit -m \"$(cat <<'EOF'\nfix: ppds {D}\nEOF\n)\" && ppds {D}", True),
    ("dash-strips-tabs", f"cat <<-EOF\n\tppds {D}\n\tEOF\nppds {D}", True),
    ("dash-strips-tabs-body-only", f"cat <<-EOF\n\tppds {D}\n\tEOF", False),
    ("two-heredocs-one-line", f"cat <<A <<B\nppds {D}\nA\nppds {D}\nB\nppds {D}", True),
    ("two-heredocs-bodies-only", f"cat <<A <<B\nppds {D}\nA\nppds {D}\nB", False),
    ("unclosed-heredoc", f"cat <<EOF\nnever closed\nppds {D}", True),
    ("double-quoted-delimiter", f'cat <<"EOF"\nppds {D}\nEOF', False),
    ("heredoc-to-shell", f"bash <<'EOF'\nppds {D}\nEOF", True),
]

# dotnet run with an option this table does not know before `--`.
DUAL_READING = [
    f"dotnet run --project src/PPDS.Cli --tl off -- {D}",
    f"dotnet run --project src/PPDS.Cli --self-contained false -- {D}",
    f"dotnet run --project src/PPDS.Cli --ucr -- {D}",
    f"dotnet run --project src/PPDS.Cli -bl -- {D}",
    f"dotnet run --project src/PPDS.Cli --no-launch-profile-arguments -- {D}",
    f"dotnet watch run --project src/PPDS.Cli -- {D}",
    f"dotnet watch --project src/PPDS.Cli run -- {D}",
]


class TestLegacyRegressions:
    """Every form the legacy matcher blocked still blocks (both gates)."""

    @pytest.mark.parametrize("cmd", [c for _, c in LEGACY_REGRESSIONS],
                             ids=[i for i, _ in LEGACY_REGRESSIONS])
    def test_env_gate_blocks(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert ENV_BLOCK in r.stderr

    @pytest.mark.parametrize("cmd", [c for i, c in LEGACY_REGRESSIONS if i.startswith(("1-", "2-", "3-", "4-"))],
                             ids=[i for i, _ in LEGACY_REGRESSIONS if i.startswith(("1-", "2-", "3-", "4-"))])
    def test_write_block_blocks(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 2, r.stderr
        assert READONLY_BLOCK in r.stderr
        assert "Pattern: data " in r.stderr

    @pytest.mark.parametrize("cmd", [c for _, c in LEGACY_REGRESSIONS],
                             ids=[i for i, _ in LEGACY_REGRESSIONS])
    def test_parse_finds_the_invocation(self, cmd):
        found = hook.find_ppds_invocations(cmd)
        assert any("delete" in argv or "import" in argv for argv in found), found


class TestExoneration:
    """A legacy hit is cleared only when the parse proves it is data."""

    @pytest.mark.parametrize("cmd", [a for _, a, _ in EXONERATION_PAIRS],
                             ids=[i for i, _, _ in EXONERATION_PAIRS])
    def test_exonerated_allowed(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 0, r.stderr
        assert r.stdout == ""
        assert r.stderr == ""

    @pytest.mark.parametrize("cmd", [b for _, _, b in EXONERATION_PAIRS],
                             ids=[i for i, _, _ in EXONERATION_PAIRS])
    def test_nearest_case_still_blocked(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert ENV_BLOCK in r.stderr
        assert "data delete" in r.stderr

    @pytest.mark.parametrize("pair", EXONERATION_PAIRS, ids=[i for i, _, _ in EXONERATION_PAIRS])
    def test_parse_view(self, pair):
        _, allowed, blocked = pair
        assert hook.find_ppds_invocations(allowed) == []
        assert any(argv[1:3] == ["data", "delete"] for argv in hook.find_ppds_invocations(blocked))

    def test_legacy_hits_are_unchanged(self):
        """The legacy matcher itself is the pre-2026-11-19 one."""
        assert hook._legacy_invocations('git commit -m "fix: ppds query crash"') == [
            (20, ["ppds", "query", 'crash"'])
        ]

    @pytest.mark.parametrize("cmd", [
        f'echo "a && ppds {D}" ${{X}}',
        f"echo $(( 1 + 2 )) 'a && ppds {D}'",
        f"echo <(true) 'a && ppds {D}'",
        f"echo 'a && ppds {D}' <<< x",
        f"case $x in a) echo 'a && ppds {D}';; esac",
        f"echo \"a && ppds {D}",
    ])
    def test_unmodelled_input_exonerates_nothing(self, cmd):
        assert hook._ShellParse(cmd).unclean
        assert hook.find_ppds_invocations(cmd)

    def test_mixed_string_is_not_cleared(self):
        """Accepted trade: one non-data command keeps every legacy hit."""
        cmd = 'dotnet build && git commit -m "fix: ppds query crash"'
        assert hook.find_ppds_invocations(cmd) == [["ppds", "query", 'crash"']]


# Strings that really run `ppds`, were blocked on origin/main, and that
# per-hit exoneration let through (independent review, by class).
WHOLE_STRING_BYPASSES = [
    ("1-loop-output", "for i in 1; do echo ppds data delete --entity account; done | sh"),
    ("1-subshell-output", "( echo ppds data delete --entity account ) | sh"),
    ("1-group-to-file", "{ echo ppds data delete --entity account; } > s.sh; sh s.sh"),
    ("1-if-output", "if true; then echo ppds data delete --entity account; fi | sh"),
    ("2-dup-to-file", "echo ppds data delete --entity account >& s.sh; sh s.sh"),
    ("2-pipe-cat-to-file", "echo ppds data delete --entity account | cat > s.sh; sh s.sh"),
    ("2-git-output", "git log -1 --format='env ppds data delete --entity account' --output=s.sh; sh s.sh"),
    ("2-stderr-to-file", "echo ppds data delete --entity account 2> s.sh; sh s.sh"),
    ("3-last-argument", "echo 'env ppds data delete --entity account'; $_"),
    ("3-printf-v", "printf -v c '%s ' ppds data delete --entity account; $c"),
    ("3-assignment", "c=$(echo ppds data delete --entity account); $c"),
    ("4-function", 'echo() { "$@"; }; echo ppds data delete --entity account'),
    ("4-hash-p", "hash -p /bin/sh cat; cat <<'EOF'\nppds data delete --entity account\nEOF"),
    ("4-path-qualified", "./echo ppds data delete --entity account"),
    ("5-backslash-delimiter", "cat <<E\\OF\nx\nEOF\nppds data delete --entity account\nE\\OF"),
    ("5-partly-quoted-delimiter", "cat <<'E'OF\nx\nEOF\nppds data delete --entity account\nEOF"),
    ("6-git-abbreviated-option", "git fetch --upload-pa 'env ppds data delete --entity account' ."),
    ("6-git-attached-value", "git grep -O'env ppds data delete --entity account' foo"),
    ("6-gh-passthrough", "gh repo clone o/r -- -c core.fsmonitor='env ppds data delete --entity account'"),
    ("6-git-push-receive-pack", "git push --receive-pack='env ppds data delete --entity account' origin"),
]


class TestWholeStringBypasses:
    @pytest.mark.parametrize("cmd", [c for _, c in WHOLE_STRING_BYPASSES],
                             ids=[i for i, _ in WHOLE_STRING_BYPASSES])
    def test_env_gate_blocks(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        assert ENV_BLOCK in r.stderr
        assert "data delete" in r.stderr

    @pytest.mark.parametrize("cmd", [c for _, c in WHOLE_STRING_BYPASSES],
                             ids=[i for i, _ in WHOLE_STRING_BYPASSES])
    def test_not_all_data(self, cmd):
        assert not hook._all_data(hook._ShellParse(cmd))
        assert any("delete" in argv for argv in hook.find_ppds_invocations(cmd))

    @pytest.mark.parametrize("cmd", [
        "git commit -m 'x' -F notes.txt",
        "git commit --mess 'ppds data delete'",
        "rg -g'*.md' ppds",
        "rg --pre-glob '*' ppds",
        "gh api repos/o/r --input ppds",
        "git log --output=x ppds",
        "git -C . log ppds",
        "git log -- ppds",
    ])
    def test_option_rules_are_exact(self, cmd):
        assert not hook._all_data(hook._ShellParse(cmd))


class TestHeredocs:
    @pytest.mark.parametrize("cmd,blocked", [(c, b) for _, c, b in HEREDOC_CASES],
                             ids=[i for i, _, _ in HEREDOC_CASES])
    def test_decision(self, fake_profile_dir, project_dir, cmd, blocked):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        if blocked:
            assert r.returncode == 2, r.stderr
            assert READONLY_BLOCK in r.stderr
            assert "Pattern: data delete" in r.stderr
        else:
            assert r.returncode == 0, r.stderr
            assert r.stderr == ""

    def test_body_ends_at_delimiter_line(self):
        parse = hook._ShellParse("cat <<'EOF'\nit's\nEOF\necho done")
        assert not parse.unclean
        assert [c.words for c in parse.commands] == [["cat"], ["echo", "done"]]
        assert parse.commands[0].heredoc_bodies == ["it's\n"]

    def test_two_heredocs_in_order(self):
        parse = hook._ShellParse("cat <<A <<'B'\none\nA\ntwo\nB\n")
        assert not parse.unclean
        assert parse.commands[0].heredoc_bodies == ["one\n", "two\n"]

    def test_unclosed_heredoc_is_unclean(self):
        assert hook._ShellParse("cat <<EOF\nno end").unclean


class TestDotnetRunDualReading:
    """Unknown ``dotnet run`` options: block if either reading blocks."""

    @pytest.mark.parametrize("cmd", DUAL_READING)
    def test_write_block(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_gate_env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 2, r.stderr
        assert READONLY_BLOCK in r.stderr
        assert "Pattern: data delete" in r.stderr

    def test_both_readings_returned(self):
        found = hook.find_ppds_invocations(f"dotnet run --project src/PPDS.Cli --tl off -- {D}")
        assert ["ppds", "data", "delete", "--entity", "account"] in found
        assert ["ppds", "off", "data", "delete", "--entity", "account"] in found

    def test_single_reading_when_they_agree(self):
        assert hook.find_ppds_invocations(f"dotnet run --project src/PPDS.Cli -c Release -- {D}") == [
            ["ppds", "data", "delete", "--entity", "account"]
        ]


# ===========================================================================
# Concern 2: write-block during shakedown
# ===========================================================================


def _shakedown_env(config_dir: str) -> dict:
    """Env for write-block tests: allowlist satisfied, shakedown active.

    We set the allowlist so that the env-gating concern is NOT what blocks;
    the mutation check is isolated as the subject under test.
    """
    return {
        "PPDS_SAFE_ENVS": "ppds-dev",
        "PPDS_CONFIG_DIR": config_dir,
        "PPDS_SHAKEDOWN": "1",
    }


class TestNoOpOutsideShakedown:
    """Write-block is off only when BOTH activation sources are absent --
    no ``PPDS_SHAKEDOWN=1`` env var AND no fresh sentinel file in the
    project dir. Tests use ``CLAUDE_PROJECT_DIR`` pointing at a clean
    temp dir so an ambient sentinel (e.g., from a running /shakedown
    session in the repo itself) cannot leak into the test.
    """

    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy",
        "ppds plugins delete --name Foo",
        "ppds env delete --name dev",
        "ppds data create account --name test",
        "ppds solutions import --file foo.zip",
    ])
    def test_writes_pass_when_var_unset(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        # No PPDS_SHAKEDOWN set + sentinel-free project dir -> write-block is off.
        r = _run_hook(
            cmd,
            env_extra={
                "PPDS_SAFE_ENVS": "ppds-dev",
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    def test_writes_pass_when_var_zero(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_SAFE_ENVS": "ppds-dev",
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
                "PPDS_SHAKEDOWN": "0",
            },
        )
        assert r.returncode == 0


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
    def test_writes_blocked_when_var_one(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 2, f"cmd={cmd}: stderr={r.stderr!r}"
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr
        assert "PPDS_SHAKEDOWN" in r.stderr

    def test_block_message_includes_unset_hint(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook("ppds plugins deploy", env_extra=_shakedown_env(config_dir))
        assert r.returncode == 2
        assert "unset PPDS_SHAKEDOWN" in r.stderr


class TestReadOnlyAllowedDuringShakedown:
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
    def test_reads_allowed_when_var_one(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"


class TestDryRunExempt:
    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy --dry-run",
        "ppds plugins deploy -n",
        "ppds plugins deploy --some-other-arg --dry-run",
    ])
    def test_dry_run_allowed(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    def test_real_deploy_still_blocked(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds plugins deploy --some-arg value",
            env_extra=_shakedown_env(config_dir),
        )
        assert r.returncode == 2

    def test_dry_run_equals_true_treated_as_dry_run(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds plugins deploy --dry-run=true",
            env_extra=_shakedown_env(config_dir),
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"


class TestMcpServerReadOnly:
    def test_mcp_server_blocked_without_read_only(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook("ppds-mcp-server", env_extra=_shakedown_env(config_dir))
        assert r.returncode == 2, f"stderr={r.stderr!r}"
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr
        assert "--read-only" in r.stderr

    def test_mcp_server_allowed_with_read_only(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds-mcp-server --read-only",
            env_extra=_shakedown_env(config_dir),
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"

    def test_mcp_server_allowed_with_read_only_equals_true(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds-mcp-server --read-only=true",
            env_extra=_shakedown_env(config_dir),
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"


class TestSentinelActivation:
    """Sentinel-file activation: the reliable source from inside Claude Code,
    where the Bash tool spawns a fresh shell per invocation and inline
    ``PPDS_SHAKEDOWN=1`` prefixes do not propagate to this hook subprocess.
    """

    @staticmethod
    def _sentinel(project: dict) -> Path:
        return Path(project["path"]) / ".claude" / "state" / "shakedown-active.json"

    def _write(self, project: dict, *, started_at: float | None = None) -> Path:
        path = self._sentinel(project)
        if started_at is None:
            started_at = time.time()
        # ISO-8601 UTC string -- same format the C# IShakedownGuard parses,
        # so one sentinel file satisfies both defense layers.
        started_at_iso = datetime.fromtimestamp(started_at, tz=timezone.utc).isoformat()
        path.write_text(
            json.dumps({"started_at": started_at_iso, "session_id": "test-session"}),
            encoding="utf-8",
        )
        return path

    def test_fresh_sentinel_blocks_mutation(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        self._write(project_dir)
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 2, f"stderr={r.stderr!r}"
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr
        # Block message should mention the sentinel as the active source.
        assert "shakedown-active.json" in r.stderr

    def test_no_sentinel_no_env_var_passes_through_to_env_gate(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        # No sentinel, no PPDS_SHAKEDOWN -> write-block is off, env gate allows.
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"

    def test_stale_sentinel_self_heals(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        # 100 days ago -- well past the 24h threshold.
        sentinel = self._write(project_dir, started_at=time.time() - 100 * 86400)
        assert sentinel.exists()
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"
        assert not sentinel.exists(), "stale sentinel should self-heal on read"

    def test_env_var_alone_still_blocks_when_no_sentinel(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        # Out-of-Claude path: env var is the only activation source.
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
                "PPDS_SHAKEDOWN": "1",
            },
        )
        assert r.returncode == 2
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr
        assert "PPDS_SHAKEDOWN=1" in r.stderr

    def test_malformed_sentinel_self_heals(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        sentinel = self._sentinel(project_dir)
        sentinel.write_text("{not json", encoding="utf-8")
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"
        assert not sentinel.exists()

    def test_sentinel_missing_started_at_self_heals(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        sentinel = self._sentinel(project_dir)
        sentinel.write_text(json.dumps({"session_id": "x"}), encoding="utf-8")
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"
        assert not sentinel.exists()

    def test_integer_started_at_rejected_as_corrupt(
        self, fake_profile_dir, project_dir
    ):
        """Regression guard for the pre-ISO-8601 sentinel format.

        A prior version of this hook and skill wrote ``started_at`` as a unix
        timestamp (integer seconds). The C# ``IShakedownGuard`` parses via
        ``JsonElement.TryGetDateTimeOffset`` which accepts only ISO-8601
        strings, so an integer-format sentinel produced a split-brain: hook
        activated, guard did not. The two parsers must now agree -- integer
        ``started_at`` is rejected at both layers.
        """
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        sentinel = self._sentinel(project_dir)
        sentinel.write_text(
            json.dumps({"started_at": int(time.time()), "session_id": "x"}),
            encoding="utf-8",
        )
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"
        assert not sentinel.exists()

    def test_sentinel_tolerates_future_timestamp_within_window(
        self, fake_profile_dir, project_dir
    ):
        """Spec: ``|now - started_at| <= 24h`` tolerates clock skew in both
        directions. A 3h-in-the-future sentinel counts as fresh and blocks.
        """
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        self._write(project_dir, started_at=time.time() + 3 * 3600)
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 2, f"stderr={r.stderr!r}"
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr

    def test_sentinel_does_not_block_readonly_subcommand(
        self, fake_profile_dir, project_dir
    ):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        self._write(project_dir)
        r = _run_hook(
            "ppds env list",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        assert r.returncode == 0, f"stderr={r.stderr!r}"


class TestCustomReadonlyEnvVar:
    """Finding #19: settings.json can override the env-var name via safety.readonly_env_var."""

    def test_custom_env_var_activates_write_block(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({
            "shakedown_safe_envs": ["ppds-dev"],
            "readonly_env_var": "PPDS_MY_SHAKEDOWN",
        })
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
                "PPDS_MY_SHAKEDOWN": "1",
            },
        )
        assert r.returncode == 2
        assert "PPDS_MY_SHAKEDOWN" in r.stderr

    def test_default_env_var_name_used_when_not_configured(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": ["ppds-dev"]})
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
                "PPDS_SHAKEDOWN": "1",
            },
        )
        assert r.returncode == 2


# ===========================================================================
# Module-level unit tests (no subprocess)
# ===========================================================================


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
        for verb in ("tail", "show", "stream", "anything"):
            is_mut, _ = hook.is_mutation(["ppds", "logs", verb])
            assert not is_mut, f"verb={verb}"

    def test_short_argv_not_mutation(self):
        is_mut, _ = hook.is_mutation(["ppds"])
        assert not is_mut
        is_mut, _ = hook.is_mutation(["ppds", "env"])
        assert not is_mut


class TestMetadataMutations:
    """#870: 3-level metadata commands (metadata <noun> <verb>) must be detected as mutations."""

    @pytest.mark.parametrize("argv", [
        ["ppds", "metadata", "table", "create", "--solution", "s", "--name", "t"],
        ["ppds", "metadata", "table", "update", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "table", "delete", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "column", "create", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "column", "update", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "column", "delete", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "relationship", "create", "--solution", "s"],
        ["ppds", "metadata", "relationship", "delete", "--solution", "s"],
        ["ppds", "metadata", "key", "create", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "key", "delete", "--solution", "s", "--entity", "t"],
        ["ppds", "metadata", "choice", "create", "--solution", "s"],
        ["ppds", "metadata", "choice", "update", "--solution", "s"],
        ["ppds", "metadata", "choice", "delete", "--solution", "s"],
    ])
    def test_metadata_noun_verb_detected_as_mutation(self, argv):
        is_mut, reason = hook.is_mutation(argv)
        assert is_mut, f"argv={argv}: expected mutation but got is_mutation=False"
        verb = argv[3]
        assert verb in reason, f"reason should mention '{verb}': {reason}"

    def test_metadata_publish_still_detected(self):
        is_mut, reason = hook.is_mutation(["ppds", "metadata", "publish"])
        assert is_mut
        assert "publish" in reason

    @pytest.mark.parametrize("argv", [
        ["ppds", "metadata", "entities"],
        ["ppds", "metadata", "entity", "account"],
    ])
    def test_metadata_readonly_not_mutation(self, argv):
        is_mut, _ = hook.is_mutation(argv)
        assert not is_mut, f"argv={argv}: should not be a mutation"

    @pytest.mark.parametrize("cmd", [
        "ppds metadata entity create --solution s --name t",
        "ppds metadata attribute delete --solution s --entity t --column c",
        "ppds metadata optionset update --solution s --name c",
    ])
    def test_metadata_mutations_blocked_during_shakedown(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 2, f"cmd={cmd}: stderr={r.stderr!r}"
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr


class TestLoadSafetyConfig:
    def test_load_from_settings(self, tmp_path, monkeypatch):
        project = tmp_path / "p"
        (project / ".claude").mkdir(parents=True)
        (project / ".claude" / "settings.json").write_text(
            json.dumps({"safety": {"shakedown_safe_envs": ["a"], "readonly_env_var": "X"}}),
            encoding="utf-8",
        )
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", str(project))
        cfg = hook.load_safety_config()
        assert cfg["shakedown_safe_envs"] == ["a"]
        assert cfg["readonly_env_var"] == "X"

    def test_load_defaults_when_missing(self, tmp_path, monkeypatch):
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", str(tmp_path))
        cfg = hook.load_safety_config()
        assert cfg["shakedown_safe_envs"] is None
        assert cfg["readonly_env_var"] == "PPDS_SHAKEDOWN"

    def test_load_tolerates_malformed_settings(self, tmp_path, monkeypatch):
        project = tmp_path / "p"
        (project / ".claude").mkdir(parents=True)
        (project / ".claude" / "settings.json").write_text("{not json", encoding="utf-8")
        monkeypatch.setenv("CLAUDE_PROJECT_DIR", str(project))
        cfg = hook.load_safety_config()
        # Must not raise; falls back to defaults.
        assert cfg["readonly_env_var"] == "PPDS_SHAKEDOWN"
