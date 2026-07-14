"""Unit tests for scripts/ci/check_extension_corelease.py.

Run with: python -m pytest tests/ci/test_extension_corelease_check.py -v

Guards the co-release rule: a `Cli-v*` release includes an `Extension-v*`
bundled-CLI refresh in the same train. Each test exercises the public functions
directly — no source-code inspection, no string matching on implementation text —
in the style of tests/ci/test_release_cadence_check.py.

The three behaviors the task calls out:
  (a) Cli tag newer than Extension tag  -> flagged with the exact message
  (b) Extension tag current or newer    -> silent
  (c) prerelease Cli tags ignored
Plus dedup, no-tag edge cases, and the workflow trigger/wiring.
"""
from __future__ import annotations

import sys
from datetime import datetime, timedelta
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))
import check_extension_corelease as cec  # noqa: E402

WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "post-merge-release-check.yml"


# Fixed reference instant; tags are dated relative to it (Date.now() is avoided).
NOW = datetime(2026, 7, 14, 12, 0, 0)


# ---------------------------------------------------------------------------
# (a) Cli tag newer than Extension tag -> flagged with the exact message
# ---------------------------------------------------------------------------

class TestCliNewerThanExtensionIsFlagged:
    """A stable Cli tag dated after the latest Extension tag must be flagged."""

    def test_flags_when_cli_newer(self):
        cli = ("Cli-v1.3.0", NOW)
        ext = ("Extension-v1.4.0", NOW - timedelta(days=30))

        result = cec.evaluate_corelease(cli=cli, extension=ext, has_open_issue=False)

        assert result["should_open_issue"] is True
        assert result["reason"] == "extension stale"
        assert result["cli_tag"] == "Cli-v1.3.0"
        assert result["extension_tag"] == "Extension-v1.4.0"

    def test_flag_message_is_exact(self):
        """The surfaced message must match the wording the task specifies."""
        msg = cec.build_flag_message("Cli-v1.3.0", "Extension-v1.4.0")
        assert msg == (
            "Extension bundled-CLI refresh missing for Cli-v1.3.0 "
            "(latest Extension-v1.4.0 predates it)"
        )

    def test_incident_scenario_end_to_end(self):
        """The exact Cli-v1.3.0 / Extension-v1.4.0 incident: flag + message."""
        cli = cec.select_latest_stable_cli([
            ("Cli-v1.2.0", NOW - timedelta(days=90)),
            ("Cli-v1.3.0", NOW),
        ])
        ext = cec.select_latest_extension([
            ("Extension-v1.4.0", NOW - timedelta(days=20)),
        ])
        result = cec.evaluate_corelease(cli=cli, extension=ext, has_open_issue=False)

        assert result["should_open_issue"] is True
        message = cec.build_flag_message(result["cli_tag"], result["extension_tag"])
        assert message == (
            "Extension bundled-CLI refresh missing for Cli-v1.3.0 "
            "(latest Extension-v1.4.0 predates it)"
        )


# ---------------------------------------------------------------------------
# (b) Extension tag current or newer -> silent
# ---------------------------------------------------------------------------

class TestExtensionCurrentIsSilent:
    """When the Extension tag is at least as new as the Cli tag, stay silent."""

    def test_silent_when_extension_newer(self):
        cli = ("Cli-v1.3.0", NOW - timedelta(days=5))
        ext = ("Extension-v1.4.1", NOW)

        result = cec.evaluate_corelease(cli=cli, extension=ext, has_open_issue=False)

        assert result["should_open_issue"] is False
        assert result["reason"] == "extension current"

    def test_silent_when_same_train_same_instant(self):
        """Same-train Extension tagged at the same instant (>=) stays silent."""
        cli = ("Cli-v1.3.0", NOW)
        ext = ("Extension-v1.4.2", NOW)

        result = cec.evaluate_corelease(cli=cli, extension=ext, has_open_issue=False)

        assert result["should_open_issue"] is False
        assert result["reason"] == "extension current"


# ---------------------------------------------------------------------------
# (c) prerelease Cli tags ignored
# ---------------------------------------------------------------------------

class TestPrereleaseCliTagsIgnored:
    """A newer -rc./-beta. Cli tag must not mask the last stable release."""

    def test_is_stable_cli_tag_classification(self):
        assert cec.is_stable_cli_tag("Cli-v1.3.0") is True
        assert cec.is_stable_cli_tag("Cli-v1.4.0-rc.1") is False
        assert cec.is_stable_cli_tag("Cli-v1.4.0-beta.2") is False

    def test_prerelease_cli_excluded_from_selection(self):
        """The newest -rc. tag is skipped; the last stable tag is selected."""
        tags = [
            ("Cli-v1.3.0", NOW - timedelta(days=10)),
            ("Cli-v1.4.0-rc.1", NOW),  # newer, but prerelease
        ]
        selected = cec.select_latest_stable_cli(tags)
        assert selected is not None
        assert selected[0] == "Cli-v1.3.0"

    def test_prerelease_cli_does_not_flag_when_stable_is_covered(self):
        """
        With a newer -rc. Cli tag but a stable Cli older than the Extension,
        the guard stays silent — proving the prerelease is ignored. Were the
        prerelease counted, the newer rc date would flag a stale Extension.
        """
        cli = cec.select_latest_stable_cli([
            ("Cli-v1.3.0", NOW - timedelta(days=10)),
            ("Cli-v1.4.0-rc.1", NOW),
        ])
        ext = cec.select_latest_extension([
            ("Extension-v1.4.1", NOW - timedelta(days=2)),  # newer than stable Cli
        ])
        result = cec.evaluate_corelease(cli=cli, extension=ext, has_open_issue=False)

        assert result["should_open_issue"] is False
        assert result["reason"] == "extension current"


# ---------------------------------------------------------------------------
# Dedup / idempotency
# ---------------------------------------------------------------------------

class TestNoDuplicateIssue:
    def test_no_duplicate_when_issue_open(self):
        cli = ("Cli-v1.3.0", NOW)
        ext = ("Extension-v1.4.0", NOW - timedelta(days=30))

        result = cec.evaluate_corelease(cli=cli, extension=ext, has_open_issue=True)

        assert result["should_open_issue"] is False
        assert result["reason"] == "duplicate"


# ---------------------------------------------------------------------------
# Edge cases: missing tags
# ---------------------------------------------------------------------------

class TestMissingTags:
    def test_no_stable_cli_tag_is_silent(self):
        result = cec.evaluate_corelease(
            cli=None,
            extension=("Extension-v1.4.0", NOW),
            has_open_issue=False,
        )
        assert result["should_open_issue"] is False
        assert result["reason"] == "no stable cli tag"

    def test_no_extension_tag_flags(self):
        cli = ("Cli-v1.3.0", NOW)
        result = cec.evaluate_corelease(cli=cli, extension=None, has_open_issue=False)
        assert result["should_open_issue"] is True
        assert result["reason"] == "no extension tag"

    def test_flag_message_handles_missing_extension(self):
        msg = cec.build_flag_message("Cli-v1.3.0", None)
        assert msg == (
            "Extension bundled-CLI refresh missing for Cli-v1.3.0 "
            "(latest (none) predates it)"
        )

    def test_select_latest_stable_cli_all_prerelease_returns_none(self):
        selected = cec.select_latest_stable_cli([
            ("Cli-v1.4.0-rc.1", NOW),
            ("Cli-v1.4.0-beta.2", NOW - timedelta(days=1)),
        ])
        assert selected is None

    def test_select_latest_extension_empty_returns_none(self):
        assert cec.select_latest_extension([]) is None


# ---------------------------------------------------------------------------
# Selection ranks by version, not list order
# ---------------------------------------------------------------------------

class TestSelectionRanksByVersion:
    def test_latest_stable_cli_picks_highest_version(self):
        tags = [
            ("Cli-v1.3.0", NOW - timedelta(days=1)),
            ("Cli-v1.10.0", NOW - timedelta(days=2)),  # highest version, out of order
            ("Cli-v1.2.0", NOW),
        ]
        selected = cec.select_latest_stable_cli(tags)
        assert selected[0] == "Cli-v1.10.0"

    def test_latest_extension_picks_highest_version(self):
        tags = [
            ("Extension-v1.4.0", NOW),
            ("Extension-v1.5.0", NOW - timedelta(days=3)),  # odd minor = pre-release channel
        ]
        selected = cec.select_latest_extension(tags)
        assert selected[0] == "Extension-v1.5.0"


# ---------------------------------------------------------------------------
# Issue body formatting
# ---------------------------------------------------------------------------

class TestBuildIssueBody:
    def test_body_mentions_tags_and_bundle(self):
        body = cec.build_issue_body("Cli-v1.3.0", "Extension-v1.4.0")
        assert "Cli-v1.3.0" in body
        assert "Extension-v1.4.0" in body
        assert "bundle:cli" in body

    def test_body_mentions_opt_out(self):
        body = cec.build_issue_body("Cli-v1.3.0", "Extension-v1.4.0")
        assert "opt-out" in body.lower() or "opt out" in body.lower()


# ---------------------------------------------------------------------------
# Workflow wiring — the guard is actually reachable from CI
# ---------------------------------------------------------------------------

class TestWorkflowWiring:
    """The guard is worthless unless the workflow can see a Cli tag push."""

    def _load_workflow(self) -> dict:
        try:
            import yaml  # type: ignore
            with WORKFLOW_PATH.open(encoding="utf-8") as f:
                return yaml.safe_load(f)
        except ImportError:
            pytest.skip("PyYAML not installed; cannot parse workflow YAML")

    def test_workflow_triggers_on_cli_tag_push(self):
        wf = self._load_workflow()
        on = wf.get("on") or wf.get(True)  # some loaders parse `on` as True
        push = on.get("push", {}) if isinstance(on, dict) else {}
        tags = push.get("tags") or []
        assert any("Cli-v" in str(t) for t in tags), (
            f"Workflow must trigger on a Cli-v* tag push; got tags={tags!r}"
        )

    def test_corelease_job_files_issue_with_label(self):
        text = WORKFLOW_PATH.read_text(encoding="utf-8")
        assert "check_extension_corelease.py" in text, (
            "Workflow must invoke the co-release guard script"
        )
        assert "release:extension-corelease" in text, (
            "Workflow must file/dedup on the release:extension-corelease label"
        )
