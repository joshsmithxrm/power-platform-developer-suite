"""AC-24, AC-25: extension-publish.yml --baseImagesUrl and matrix target set."""
from __future__ import annotations

import re

import pytest
import yaml

from tests.extension_listing._helpers import REPO_ROOT

WORKFLOW_PATH = REPO_ROOT / ".github" / "workflows" / "extension-publish.yml"

# SHA is expanded at runtime from ${{ steps.resolve-tag.outputs.sha }}; the
# regex tolerates either a literal 40-hex SHA (runtime) or the templated
# expression (committed source).
BASE_IMAGES_URL_RUNTIME_RE = re.compile(
    r"https://raw\.githubusercontent\.com/joshsmithxrm/power-platform-developer-suite/"
    r"[0-9a-f]{40}/src/PPDS\.Extension/media/"
)
BASE_IMAGES_URL_TEMPLATE_RE = re.compile(
    r"https://raw\.githubusercontent\.com/joshsmithxrm/power-platform-developer-suite/"
    r"\$\{\{\s*steps\.resolve-tag\.outputs\.sha\s*\}\}/src/PPDS\.Extension/media/"
)
HARDCODED_SHA_IN_SOURCE_RE = re.compile(
    r"raw\.githubusercontent\.com/joshsmithxrm/power-platform-developer-suite/[0-9a-f]{40}/"
)


def _load_workflow() -> dict:
    return yaml.safe_load(WORKFLOW_PATH.read_text(encoding="utf-8"))


def _publish_run_strings() -> list[str]:
    """Return the `run:` strings for both publish steps."""
    workflow = _load_workflow()
    steps = workflow["jobs"]["publish"]["steps"]
    publish_steps = [s for s in steps if "name" in s and "Publish to VS Code Marketplace" in s["name"]]
    assert len(publish_steps) == 2, (
        f"Expected 2 publish steps (pre-release + stable); found {len(publish_steps)}"
    )
    return [s["run"] for s in publish_steps]


@pytest.mark.parametrize("run_string", _publish_run_strings())
def test_publish_step_has_base_images_url_templated(run_string: str) -> None:
    """AC-24: each publish step's run command uses --baseImagesUrl with a
    templated SHA expression. The SHA must be resolved at runtime, not
    hard-coded in the workflow source.
    """
    assert "--baseImagesUrl" in run_string, (
        f"Publish step is missing --baseImagesUrl flag:\n{run_string}"
    )
    assert BASE_IMAGES_URL_TEMPLATE_RE.search(run_string), (
        "Publish step --baseImagesUrl must template the SHA from "
        "steps.resolve-tag.outputs.sha. Got:\n"
        f"{run_string}"
    )


def test_no_hardcoded_sha_in_workflow_source() -> None:
    """AC-24 guard: the committed source must not contain a literal 40-hex SHA
    in the baseImagesUrl — that would bypass tag resolution.
    """
    text = WORKFLOW_PATH.read_text(encoding="utf-8")
    assert not HARDCODED_SHA_IN_SOURCE_RE.search(text), (
        "Found a literal 40-hex commit SHA in the workflow source. "
        "SHA must be resolved at runtime from the release tag."
    )


def test_resolve_tag_step_exists_with_tag_guard() -> None:
    """AC-24 prereq: a step resolves the tag SHA and errors out when
    github.ref is not a refs/tags/Extension-v* ref.
    """
    workflow = _load_workflow()
    steps = workflow["jobs"]["publish"]["steps"]
    resolve_steps = [s for s in steps if s.get("id") == "resolve-tag"]
    assert len(resolve_steps) == 1, (
        f"Expected exactly one step with id 'resolve-tag'; found {len(resolve_steps)}"
    )
    run = resolve_steps[0].get("run", "")
    assert "refs/tags/Extension-v" in run, (
        "resolve-tag step must guard against non-Extension-v* refs"
    )
    assert "exit 1" in run, (
        "resolve-tag step must exit 1 when the ref is invalid"
    )
    assert "steps.resolve-tag.outputs.sha" not in run, (
        "resolve-tag step writes to outputs.sha, must not read from it"
    )


def test_matrix_targets_exact_set() -> None:
    """AC-25: matrix targets equal exactly the four platform-specific values."""
    workflow = _load_workflow()
    matrix = workflow["jobs"]["publish"]["strategy"]["matrix"]
    include = matrix.get("include", [])
    targets = {entry["target"] for entry in include}
    expected = {"win32-x64", "linux-x64", "darwin-x64", "darwin-arm64"}
    assert targets == expected, (
        f"Matrix targets {targets} != expected {expected}. "
        "Generic targets are forbidden (breaks self-contained-install claim)."
    )


# ---------------------------------------------------------------------------
# Negative-case verification for AC-24 (hardcoded SHA detection)
# ---------------------------------------------------------------------------

def test_hardcoded_sha_regex_flags_literal_sha() -> None:
    """Sanity: the HARDCODED_SHA_IN_SOURCE_RE regex actually matches a literal
    40-hex SHA path. Guards against a regex that silently passes.
    """
    bad_sample = (
        "run: npx vsce publish --baseImagesUrl "
        "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/"
        "abcdef0123456789abcdef0123456789abcdef01/src/PPDS.Extension/media/"
    )
    assert HARDCODED_SHA_IN_SOURCE_RE.search(bad_sample), (
        "Regex must detect a 40-hex SHA in a hardcoded baseImagesUrl"
    )


def test_runtime_sha_regex_matches_expanded_form() -> None:
    """Sanity: the runtime regex matches a fully-expanded URL (as it would
    appear at workflow execution time, not in committed source).
    """
    expanded = (
        "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/"
        "abcdef0123456789abcdef0123456789abcdef01/src/PPDS.Extension/media/"
    )
    assert BASE_IMAGES_URL_RUNTIME_RE.search(expanded)
