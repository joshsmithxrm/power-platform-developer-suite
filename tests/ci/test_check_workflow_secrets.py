"""Unit tests for scripts/ci/check_workflow_secrets.py.

Run with: python -m pytest tests/ci/test_check_workflow_secrets.py -v
"""
from __future__ import annotations

import sys
from pathlib import Path
from unittest.mock import patch

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ci"))

import check_workflow_secrets as cws  # noqa: E402


# ---------------------------------------------------------------------------
# Reference extraction
# ---------------------------------------------------------------------------

class TestExtractRefs:
    def test_extracts_secret_ref(self):
        s, v = cws.extract_refs("token: ${{ secrets.MY_TOKEN }}")
        assert s == {"MY_TOKEN"}
        assert v == set()

    def test_extracts_var_ref(self):
        s, v = cws.extract_refs("env: ${{ vars.MY_VAR }}")
        assert s == set()
        assert v == {"MY_VAR"}

    def test_extracts_multiple(self):
        text = """
        steps:
          - run: echo ${{ secrets.A }} ${{ secrets.B }}
            env:
              X: ${{ vars.X }}
              Y: ${{ vars.Y }}
              SAME: ${{ secrets.A }}
        """
        s, v = cws.extract_refs(text)
        assert s == {"A", "B"}
        assert v == {"X", "Y"}

    def test_handles_arbitrary_whitespace_in_curly(self):
        text = "${{   secrets.WEIRD   }}"
        s, _ = cws.extract_refs(text)
        assert s == {"WEIRD"}

    def test_secret_ref_with_whitespace_around_dot(self):
        # GitHub Actions accepts whitespace around the dot — so should we.
        text = "${{ secrets . FOO }} ${{ vars  .  BAR }}"
        s, v = cws.extract_refs(text)
        assert s == {"FOO"}
        assert v == {"BAR"}

    def test_secret_refs_normalized_to_uppercase(self):
        # All extracted names should be uppercase so the inventory comparison
        # is case-insensitive (GitHub treats secret/var names case-insensitively).
        text = "${{ secrets.lowercase_secret }} ${{ vars.MixedCase_Var }}"
        s, v = cws.extract_refs(text)
        assert s == {"LOWERCASE_SECRET"}
        assert v == {"MIXEDCASE_VAR"}

    def test_ignores_non_secret_dot_refs(self):
        # github.event.foo, env.X etc should NOT be extracted as secrets.
        text = "${{ github.event.pull_request.number }} ${{ env.FOO }}"
        s, v = cws.extract_refs(text)
        assert s == set()
        assert v == set()

    def test_empty_text(self):
        s, v = cws.extract_refs("")
        assert s == set()
        assert v == set()


# ---------------------------------------------------------------------------
# Allow markers
# ---------------------------------------------------------------------------

class TestAllowMarkers:
    def test_marker_in_title(self):
        allowed = cws.find_allow_markers("[secret-ref-allow: FOO]", "")
        assert allowed == {"FOO"}

    def test_marker_in_body(self):
        allowed = cws.find_allow_markers("", "explanation\n[secret-ref-allow: BAR]\n")
        assert allowed == {"BAR"}

    def test_multiple_markers(self):
        allowed = cws.find_allow_markers(
            "[secret-ref-allow: A]",
            "[secret-ref-allow: B] and [secret-ref-allow: C]",
        )
        assert allowed == {"A", "B", "C"}

    def test_no_markers(self):
        assert cws.find_allow_markers("plain", "plain") == set()

    def test_invalid_name_not_extracted(self):
        # Names must start with letter/underscore
        assert cws.find_allow_markers("[secret-ref-allow: 123abc]", "") == set()

    def test_marker_normalized_to_uppercase(self):
        # Allow marker names are uppercased so they match the uppercased
        # extracted refs and inventory (case-insensitive).
        allowed = cws.find_allow_markers("[secret-ref-allow: lower_case]", "")
        assert allowed == {"LOWER_CASE"}


# ---------------------------------------------------------------------------
# Rule application
# ---------------------------------------------------------------------------

def _reader_for(mapping):
    """Return a reader fn that maps file path -> contents from `mapping`."""
    def reader(path):
        return mapping[path]
    return reader


class TestCheckWorkflowSecrets:
    def test_no_workflow_files_passes(self):
        passed, msg = cws.check_workflow_secrets(
            [], set(), set(), "", "",
        )
        assert passed
        assert "not applicable" in msg

    def test_all_secrets_resolved_passes(self):
        files = ["a.yml"]
        contents = {"a.yml": "k: ${{ secrets.PRESENT }}"}
        passed, msg = cws.check_workflow_secrets(
            files, {"PRESENT"}, set(), "", "",
            reader=_reader_for(contents),
        )
        assert passed
        assert "resolved" in msg

    def test_missing_secret_fails(self):
        files = ["a.yml"]
        contents = {"a.yml": "k: ${{ secrets.MISSING }}"}
        passed, msg = cws.check_workflow_secrets(
            files, set(), set(), "", "",
            reader=_reader_for(contents),
        )
        assert not passed
        assert "secret 'MISSING'" in msg
        assert "[secret-ref-allow:" in msg

    def test_pr797_audit_repo_token_scenario(self):
        # The retro item: AUDIT_REPO_TOKEN referenced but didn't exist.
        files = [".github/workflows/audit.yml"]
        contents = {
            ".github/workflows/audit.yml": "token: ${{ secrets.AUDIT_REPO_TOKEN }}",
        }
        passed, msg = cws.check_workflow_secrets(
            files, {"GITHUB_TOKEN", "OTHER"}, set(), "", "",
            reader=_reader_for(contents),
        )
        assert not passed
        assert "AUDIT_REPO_TOKEN" in msg

    def test_missing_var_fails(self):
        files = ["a.yml"]
        contents = {"a.yml": "k: ${{ vars.MISSING_VAR }}"}
        passed, msg = cws.check_workflow_secrets(
            files, set(), set(), "", "",
            reader=_reader_for(contents),
        )
        assert not passed
        assert "var 'MISSING_VAR'" in msg

    def test_github_token_always_present(self):
        files = ["a.yml"]
        contents = {"a.yml": "token: ${{ secrets.GITHUB_TOKEN }}"}
        passed, _ = cws.check_workflow_secrets(
            files, set(), set(), "", "",
            reader=_reader_for(contents),
        )
        assert passed

    def test_allow_marker_bypasses(self):
        files = ["a.yml"]
        contents = {"a.yml": "k: ${{ secrets.NEEDS_ALLOW }}"}
        passed, msg = cws.check_workflow_secrets(
            files, set(), set(),
            title="[secret-ref-allow: NEEDS_ALLOW]",
            body="",
            reader=_reader_for(contents),
        )
        assert passed

    def test_allow_marker_partial_does_not_bypass_others(self):
        files = ["a.yml"]
        contents = {
            "a.yml": "x: ${{ secrets.ALLOWED }}\ny: ${{ secrets.STILL_MISSING }}",
        }
        passed, msg = cws.check_workflow_secrets(
            files, set(), set(),
            title="[secret-ref-allow: ALLOWED]",
            body="",
            reader=_reader_for(contents),
        )
        assert not passed
        assert "STILL_MISSING" in msg
        assert "ALLOWED" not in msg.replace("[secret-ref-allow:", "")

    def test_multiple_files_all_scanned(self):
        files = ["a.yml", "b.yaml"]
        contents = {
            "a.yml": "x: ${{ secrets.S1 }}",
            "b.yaml": "y: ${{ secrets.S2 }}",
        }
        passed, msg = cws.check_workflow_secrets(
            files, set(), set(), "", "",
            reader=_reader_for(contents),
        )
        assert not passed
        assert "S1" in msg
        assert "S2" in msg

    def test_secret_ref_case_insensitive_match(self):
        # Workflow references secrets.PPDS_TOKEN; gh secret list returns
        # 'ppds_token'. GitHub treats names case-insensitively, so this
        # must NOT trigger drift. (Mirrors the inventory normalization that
        # fetch_repo_secrets / fetch_repo_variables apply.)
        files = ["a.yml"]
        contents = {"a.yml": "token: ${{ secrets.PPDS_TOKEN }}"}
        # Simulate the inventory normalization fetch_repo_secrets does.
        repo_secrets = {"ppds_token".upper()}
        passed, msg = cws.check_workflow_secrets(
            files, repo_secrets, set(), "", "",
            reader=_reader_for(contents),
        )
        assert passed, msg

    def test_var_ref_case_insensitive_match(self):
        # Symmetric coverage for vars.
        files = ["a.yml"]
        contents = {"a.yml": "v: ${{ vars.MyVar }}"}
        repo_variables = {"MYVAR".upper()}
        passed, msg = cws.check_workflow_secrets(
            files, set(), repo_variables, "", "",
            reader=_reader_for(contents),
        )
        assert passed, msg

    def test_allow_marker_case_insensitive_match(self):
        # Workflow has secrets.MyLegitSecret; allow marker uses lowercase.
        files = ["a.yml"]
        contents = {"a.yml": "k: ${{ secrets.MyLegitSecret }}"}
        passed, msg = cws.check_workflow_secrets(
            files, set(), set(),
            title="[secret-ref-allow: mylegitsecret]",
            body="",
            reader=_reader_for(contents),
        )
        assert passed, msg

    def test_unreadable_file_fails(self):
        def bad_reader(path):
            raise OSError("disk on fire")
        passed, msg = cws.check_workflow_secrets(
            ["a.yml"], set(), set(), "", "", reader=bad_reader,
        )
        assert not passed
        assert "could not read" in msg


# ---------------------------------------------------------------------------
# Built-in secret list
# ---------------------------------------------------------------------------

def test_builtin_secrets_includes_github_token():
    assert "GITHUB_TOKEN" in cws.BUILTIN_SECRETS


# ---------------------------------------------------------------------------
# Inventory fetchers (normalization + null safety)
# ---------------------------------------------------------------------------

class TestInventoryFetchers:
    def test_fetch_repo_secrets_uppercases_names(self):
        with patch.object(
            cws, "_run_gh",
            return_value='[{"name": "ppds_token"}, {"name": "Mixed"}]',
        ):
            assert cws.fetch_repo_secrets() == {"PPDS_TOKEN", "MIXED"}

    def test_fetch_repo_variables_uppercases_names(self):
        with patch.object(
            cws, "_run_gh",
            return_value='[{"name": "envOne"}, {"name": "VAR_TWO"}]',
        ):
            assert cws.fetch_repo_variables() == {"ENVONE", "VAR_TWO"}

    def test_fetch_repo_secrets_handles_null_name_field(self):
        # `gh` shouldn't return null name, but be defensive.
        with patch.object(
            cws, "_run_gh",
            return_value='[{"name": null}, {"name": "ok"}]',
        ):
            assert cws.fetch_repo_secrets() == {"", "OK"}

    def test_fetch_pr_metadata_handles_null_files_list(self):
        with patch.object(
            cws, "_run_gh",
            return_value='{"title": null, "body": null, "files": null}',
        ):
            meta = cws.fetch_pr_metadata(1)
        assert meta == {"title": "", "body": "", "workflow_files": []}

    def test_fetch_pr_metadata_handles_null_path_in_file(self):
        with patch.object(
            cws, "_run_gh",
            return_value=(
                '{"title": "t", "body": "b", '
                '"files": [{"path": null}, {"path": ".github/workflows/a.yml"}]}'
            ),
        ):
            meta = cws.fetch_pr_metadata(1)
        assert meta["workflow_files"] == [".github/workflows/a.yml"]


# ---------------------------------------------------------------------------
# Main entry point
# ---------------------------------------------------------------------------

class TestMain:
    def test_main_no_workflow_files_returns_0(self):
        with patch.object(
            cws, "fetch_pr_metadata",
            return_value={"title": "", "body": "", "workflow_files": []},
        ):
            rc = cws.main(["--pr", "1"])
        assert rc == 0

    def test_main_with_files_arg_passes_when_resolved(self, tmp_path):
        wf = tmp_path / "x.yml"
        wf.write_text("k: ${{ secrets.HELLO }}")
        with patch.object(cws, "fetch_repo_secrets", return_value={"HELLO"}), \
             patch.object(cws, "fetch_repo_variables", return_value=set()):
            rc = cws.main([
                "--files", str(wf),
                "--title", "", "--body", "",
            ])
        assert rc == 0

    def test_main_with_files_arg_fails_when_missing(self, tmp_path):
        wf = tmp_path / "x.yml"
        wf.write_text("k: ${{ secrets.MISSING }}")
        with patch.object(cws, "fetch_repo_secrets", return_value=set()), \
             patch.object(cws, "fetch_repo_variables", return_value=set()):
            rc = cws.main([
                "--files", str(wf),
                "--title", "", "--body", "",
            ])
        assert rc == 1

    def test_main_gh_failure_returns_2(self):
        with patch.object(
            cws, "fetch_pr_metadata",
            side_effect=RuntimeError("gh boom"),
        ):
            rc = cws.main(["--pr", "1"])
        assert rc == 2
