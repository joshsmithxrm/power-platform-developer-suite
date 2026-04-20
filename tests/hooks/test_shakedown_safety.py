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

    def test_settings_allowlist_empty_list_blocked(self, fake_profile_dir, project_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        project_dir["write_settings"]({"shakedown_safe_envs": []})
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={
                "PPDS_CONFIG_DIR": config_dir,
                "CLAUDE_PROJECT_DIR": project_dir["path"],
            },
        )
        # Allowlist resolved to empty -- nothing matches -> block.
        assert r.returncode == 2

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
        """Help text in no-allowlist path must mention settings.json, not the old safe-envs.json."""
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        # No safety block at all.
        project_dir["write_settings"](None)
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
    def test_no_allowlist_blocks_with_config_help(self, fake_profile_dir, tmp_path):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        # CLAUDE_PROJECT_DIR at an empty dir -> no settings.json, no legacy file.
        empty_project = tmp_path / "empty-project"
        empty_project.mkdir()
        r = _run_hook(
            "ppds query sql 'SELECT 1'",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": str(empty_project)},
        )
        assert r.returncode == 2
        assert "no safe-env allowlist configured" in r.stderr
        assert "PPDS_SAFE_ENVS" in r.stderr


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

    def test_no_allowlist_still_blocks_readonly_cmds(self, fake_profile_dir, tmp_path):
        """Intentional: no-allowlist surfaces the config message on ANY ppds call."""
        config_dir = fake_profile_dir(active_env_name="ppds-prod")
        empty = tmp_path / "empty"
        empty.mkdir()
        r = _run_hook(
            "ppds env list",
            env_extra={"PPDS_CONFIG_DIR": config_dir, "CLAUDE_PROJECT_DIR": str(empty)},
        )
        assert r.returncode == 2
        assert "no safe-env allowlist configured" in r.stderr


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
    @pytest.mark.parametrize("cmd", [
        "ppds plugins deploy",
        "ppds plugins delete --name Foo",
        "ppds env delete --name dev",
        "ppds data create account --name test",
        "ppds solutions import --file foo.zip",
    ])
    def test_writes_pass_when_var_unset(self, fake_profile_dir, cmd):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        # No PPDS_SHAKEDOWN set -- write-block is off.
        r = _run_hook(
            cmd,
            env_extra={"PPDS_SAFE_ENVS": "ppds-dev", "PPDS_CONFIG_DIR": config_dir},
        )
        assert r.returncode == 0, f"cmd={cmd}: stderr={r.stderr!r}"

    def test_writes_pass_when_var_zero(self, fake_profile_dir):
        config_dir = fake_profile_dir(active_env_name="ppds-dev")
        r = _run_hook(
            "ppds plugins deploy",
            env_extra={
                "PPDS_SAFE_ENVS": "ppds-dev",
                "PPDS_CONFIG_DIR": config_dir,
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
        path.write_text(
            json.dumps({"started_at": int(started_at), "session_id": "test-session"}),
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
