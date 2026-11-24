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
from hang_bound import CHILD_PROCESS_HANG_BOUND_SECONDS  # tests/conftest.py puts tests/ on sys.path


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
        timeout=CHILD_PROCESS_HANG_BOUND_SECONDS,
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
        "ppds env config",
        "ppds env config --list",
        "ppds env config --show",
        "ppds env config https://x.example --show",
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


class TestEnvWhoGated:
    """``env who`` sends WhoAmI, so it is gated (maintainer's ruling,
    2026-11-24): it passes only when the env it reaches is listed."""

    @pytest.mark.parametrize("cmd,active,expected", [
        ("ppds env who", "ppds-prod", 2),
        ("ppds env who", "ppds-dev", 0),
        ("ppds env who --environment ppds-dev", "ppds-prod", 0),
        ("ppds env who --environment ppds-prod", "ppds-prod", 2),
        ("ppds env who --environment ppds-prod", "ppds-dev", 2),
    ])
    def test_env_who_reaches_only_a_listed_env(self, fake_profile_dir, cmd, active, expected):
        config_dir = fake_profile_dir(active_env_name=active)
        r = _run_hook(cmd, env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir})
        assert r.returncode == expected, f"cmd={cmd}: stderr={r.stderr!r}"


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
            timeout=CHILD_PROCESS_HANG_BOUND_SECONDS,
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
        "ppds env who",
        "ppds env select ppds-dev",
        "ppds env config --list",
        "ppds env type --list",
        "ppds plugins list",
        "ppds logs tail",
        "ppds auth list",
        "ppds version",
        "ppds --help",
    ])
    def test_reads_allowed_when_var_one(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    @pytest.mark.parametrize("cmd", [
        "ppds env current", "ppds env switch ppds-dev", "ppds logs show", "ppds help",
        "ppds env config --label x", "ppds env type --remove x",
    ])
    def test_commands_the_cli_does_not_have_or_that_write_are_blocked(self, fake_profile_dir, cmd):
        """Only commands the hook knows to be read-only pass during a shakedown."""
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 2, f"cmd={cmd}: stderr={r.stderr!r}"


class TestDryRunExempt:
    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy --dry-run",
        "ppds plugins deploy --config x.json --dry-run",
        "ppds plugins deploy --dry-run --config x.json",
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

    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy -n",                              # no such option
        "ppds plugins deploy --some-other-arg --dry-run",      # may be that option's value
        "ppds plugins deploy --correlation-id --dry-run",
        "ppds plugins deploy -env --dry-run",
        "ppds plugins deploy --solution --dry-run",
        "ppds plugins deploy --dry-run ' false'",              # the flag takes the word as its value
        "ppds plugins deploy --dry-run false",
        "ppds plugins deploy --dry-run=true",                  # a value attached: not placed
        "ppds plugins deploy --dry-run:true",
        "ppds plugins deploy -- --dry-run",                    # an operand after `--`
        "ppds plugins deploy --dry-run --dry-run false",
    ])
    def test_dry_run_not_positively_placed_is_blocked(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=_shakedown_env(config_dir))
        assert r.returncode == 2, f"cmd={cmd}: stderr={r.stderr!r}"


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

    def test_mcp_server_blocked_with_read_only_equals_true(self, fake_profile_dir):
        """The server honours only the bare `--read-only`; `=true` starts it writable."""
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds-mcp-server --read-only=true",
            env_extra=_shakedown_env(config_dir),
        )
        assert r.returncode == 2, f"stderr={r.stderr!r}"
        assert "only accepts the bare --read-only flag" in r.stderr


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


# (id, command, argv the hook must find)
_DEL = "data delete --entity account"
DOTNET_HOST_FORMS = [
    ("run-project-dir", f"dotnet run --project src/PPDS.Cli -- {_DEL}", f"ppds {_DEL}"),
    ("run-csproj-windows-path", rf'dotnet run --project "C:\repo\src\PPDS.Cli\PPDS.Cli.csproj" -- {_DEL}', f"ppds {_DEL}"),
    ("run-project-equals-options", f"dotnet run -c Release --no-build --project=src/PPDS.Cli/ -f net10.0 -- {_DEL}", f"ppds {_DEL}"),
    ("run-p-path", f"dotnet run -p:Configuration=Release -p src/ppds.cli -- {_DEL}", f"ppds {_DEL}"),
    ("run-no-separator", f"dotnet run --project src/PPDS.Cli {_DEL}", f"ppds {_DEL}"),
    ("run-mcp", "dotnet run --project src/PPDS.Mcp/PPDS.Mcp.csproj -- --profile x", "ppds-mcp-server --profile x"),
    ("dll", f"dotnet src/PPDS.Cli/bin/Debug/net10.0/ppds.dll {_DEL}", f"ppds {_DEL}"),
    ("exec-dll", f"dotnet exec --depsfile d.json bin/ppds.dll {_DEL}", f"ppds {_DEL}"),
    ("cd-then-run", f"cd src/PPDS.Cli && dotnet run -- {_DEL}", f"ppds {_DEL}"),
    ("watch-run", f"dotnet watch run --project src/PPDS.Cli -- {_DEL}", f"ppds {_DEL}"),
    ("tool-run", f"dotnet tool run --allow-roll-forward ppds {_DEL}", f"ppds {_DEL}"),
    ("quoted-dotnet-exe", rf'"C:\Program Files\dotnet\dotnet.exe" run --project src/PPDS.Cli -- {_DEL}', f"ppds {_DEL}"),
]
DOTNET_NOT_PPDS = [
    "dotnet build src/PPDS.Cli/PPDS.Cli.csproj",
    "dotnet test tests/PPDS.Cli.Tests",
    "dotnet pack src/PPDS.Cli -c Release",
    "dotnet format PPDS.sln && dotnet restore src/PPDS.Mcp",
    f"dotnet run --project src/PPDS.DocsGen -- {_DEL}",
    f"dotnet run --project scripts/docs-gen/cli-reflect -- {_DEL}",
    f"dotnet run --project tests/PPDS.Cli.Tests -- {_DEL}",
    f"dotnet run --project tests/PPDS.Cli.DaemonTests/PPDS.Cli.DaemonTests.csproj -- {_DEL}",
    f"cd src/PPDS.Cli && cd .. && dotnet run -- {_DEL}",
]


class TestDotnetHostForms:
    """PPDS started through ``dotnet`` is gated like the direct program."""

    ENV_BLOCK = "BLOCKED [shakedown-safety/env]: active env is not in the safe-env allowlist."

    @staticmethod
    def _env(config_dir, project, **extra):
        env = {"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir,
               "CLAUDE_PROJECT_DIR": project["path"]}
        env.update(extra)
        return env

    @pytest.mark.parametrize("cmd,argv", [(c, a) for _, c, a in DOTNET_HOST_FORMS],
                             ids=[i for i, _, _ in DOTNET_HOST_FORMS])
    def test_env_gate_blocks(self, fake_profile_dir, project_dir, cmd, argv):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        r = _run_hook(cmd, env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 2, r.stderr
        if "--profile x" in argv:
            # a profile the store does not have: its environment is not known
            assert "Problem:   --profile x: profile 'x' is not in the profile store" in r.stderr
            assert f"Command:   {argv}\n" in r.stderr or f"  (read as: {argv})\n" in r.stderr
            return
        assert self.ENV_BLOCK in r.stderr
        # a `dotnet run` reading shows the words as written, then the reading
        assert f"Command:    {argv}\n" in r.stderr or (
            "Command:    dotnet run " in r.stderr and f"  (read as: {argv})\n" in r.stderr)

    @pytest.mark.parametrize("cmd", [c for i, c, _ in DOTNET_HOST_FORMS if i in (
        "run-project-dir", "run-no-separator", "run-mcp", "exec-dll", "cd-then-run", "tool-run")])
    def test_write_block_during_shakedown(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=self._env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 2, r.stderr
        assert "BLOCKED [shakedown-safety/readonly]" in r.stderr
        assert "Pattern: data delete" in r.stderr or "missing --read-only" in r.stderr

    @pytest.mark.parametrize("cmd", [c for i, c, _ in DOTNET_HOST_FORMS if i in (
        "run-project-dir", "dll", "cd-then-run", "watch-run")])
    def test_safe_env_allowed(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=self._env(config_dir, project_dir))
        assert r.returncode == 0, r.stderr
        assert r.stderr == ""

    @pytest.mark.parametrize("cmd", [
        "dotnet run --project src/PPDS.Cli -- plugins deploy --dry-run",
        "dotnet run --project src/PPDS.Mcp -- --read-only",
    ])
    def test_exemptions_as_for_the_direct_program(self, fake_profile_dir, project_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(cmd, env_extra=self._env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 0, r.stderr

    def test_dual_reading_blocks(self, fake_profile_dir, project_dir):
        """An option the hook does not know before `--` cannot hide the verb."""
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(f"dotnet run --project src/PPDS.Cli --tl off -- {_DEL}",
                      env_extra=self._env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
        assert r.returncode == 2, r.stderr
        assert "Pattern: data delete" in r.stderr

    def test_other_dotnet_commands_allowed(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        for cmd in DOTNET_NOT_PPDS[:4] + DOTNET_NOT_PPDS[4:6]:
            r = _run_hook(cmd, env_extra=self._env(config_dir, project_dir, PPDS_SHAKEDOWN="1"))
            assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"
            assert r.stderr == ""

    @pytest.mark.parametrize("cmd,argv", [(c, a) for _, c, a in DOTNET_HOST_FORMS],
                             ids=[i for i, _, _ in DOTNET_HOST_FORMS])
    def test_parse(self, cmd, argv):
        assert argv.split(" ") in hook.find_ppds_invocations(cmd)

    @pytest.mark.parametrize("cmd", DOTNET_NOT_PPDS)
    def test_parse_not_ppds(self, cmd):
        assert hook.find_ppds_invocations(cmd) == []

    def test_parse_dual_reading(self):
        found = hook.find_ppds_invocations(f"dotnet run --project src/PPDS.Cli --tl off -- {_DEL}")
        assert found == [["ppds"] + _DEL.split(), ["ppds", "off"] + _DEL.split()]

    @pytest.mark.parametrize("target", ["_dotnet_argvs", "sp.read"])
    def test_a_failure_inside_the_reading_is_not_swallowed(self, monkeypatch, target):
        """An error anywhere in the reading reaches main(), which refuses a
        command that mentions a guarded program (the process tests in
        test_shakedown_safety_process.py); no part of it turns into a pass."""
        def boom(*_args, **_kwargs):
            raise RuntimeError("bug")

        owner, name = (hook.sp, "read") if target == "sp.read" else (hook, target)
        monkeypatch.setattr(owner, name, boom)
        with pytest.raises(RuntimeError):
            hook.find_ppds_invocations(f"ppds {_DEL} && dotnet run --project src/PPDS.Cli -- x")


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

    def test_plugins_deploy_has_no_short_dry_run(self):
        is_mut, _ = hook.is_mutation(["ppds", "plugins", "deploy", "-n"])
        assert is_mut

    def test_logs_reads_pass_and_unknown_logs_commands_block(self):
        for verb in ("tail", "dump"):
            assert not hook.is_mutation(["ppds", "logs", verb])[0], verb
        for verb in ("show", "stream", "anything"):
            assert hook.is_mutation(["ppds", "logs", verb])[0], verb

    def test_bare_ppds_starts_the_tui_and_is_a_mutation(self):
        is_mut, reason = hook.is_mutation(["ppds"])
        assert is_mut and "TUI" in reason
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
