"""Contracts separating contributor tooling from the supported extension host."""
from pathlib import Path
import json
import re

import yaml


ROOT = Path(__file__).resolve().parents[2]


def workflow(name: str) -> dict:
    return yaml.load(
        (ROOT / ".github" / "workflows" / name).read_text(encoding="utf-8"),
        Loader=yaml.BaseLoader,
    )


def test_tooling_jobs_share_the_declared_runtime():
    assert (ROOT / ".nvmrc").read_text().strip() == "26"
    assert (ROOT / "tests/PPDS.Tui.E2eTests/.nvmrc").read_text().strip() == "26"
    package = json.loads((ROOT / "package.json").read_text(encoding="utf-8"))
    lock = json.loads((ROOT / "package-lock.json").read_text(encoding="utf-8"))
    assert package["engines"] == lock["packages"][""]["engines"]
    assert package["engines"]["node"] == ">=26 <27"
    container = (ROOT / ".devcontainer/Dockerfile").read_text(encoding="utf-8")
    assert "ARG NODE_MAJOR=" + (ROOT / ".nvmrc").read_text().strip() in container
    assert "setup_${NODE_MAJOR}.x" in container

    for filename in ("build.yml", "test.yml", "tui-e2e.yml", "audit-capture.yml", "extension-publish.yml"):
        for name, job in workflow(filename)["jobs"].items():
            for step in job.get("steps", []):
                if not step.get("uses", "").startswith("actions/setup-node@"):
                    continue
                if filename == "build.yml" and name == "extension-node22":
                    assert step["with"]["node-version"] == "22"
                else:
                    assert step["with"]["node-version-file"] == ".nvmrc"
                    assert "node-version" not in step["with"]
                assert step["with"]["check-latest"] == "true"


def test_runtime_changes_trigger_validation():
    build = workflow("build.yml")["jobs"]
    filters = next(step for step in build["changes"]["steps"] if step.get("id") == "filter")
    extension_paths = yaml.load(filters["with"]["filters"], Loader=yaml.BaseLoader)["extension"]
    assert {".nvmrc", "package.json", "package-lock.json"} <= set(extension_paths)
    assert "needs.changes.outputs.extension == 'true'" in build["extension"]["if"]
    assert build["extension"]["if"] == build["extension-node22"]["if"]
    for event in ("push", "pull_request"):
        assert ".nvmrc" in workflow("tui-e2e.yml")["on"][event]["paths"]
    tests = workflow("test.yml")["jobs"]
    test_filter = next(step for step in tests["changes"]["steps"] if step.get("id") == "filter")
    test_paths = yaml.load(test_filter["with"]["filters"], Loader=yaml.BaseLoader)
    assert {".nvmrc", "package.json", "package-lock.json"} <= set(test_paths["extraction"])
    assert ".nvmrc" in test_paths["python"]
    assert ".devcontainer/Dockerfile" in test_paths["python"]


def test_extension_node_typings_preserve_the_host_contract():
    extension = ROOT / "src/PPDS.Extension"
    package = json.loads((extension / "package.json").read_text(encoding="utf-8"))
    lock = json.loads((extension / "package-lock.json").read_text(encoding="utf-8"))
    declared = package["devDependencies"]["@types/node"]
    # Exact, tilde, and caret ranges within major 22 keep newer tooling APIs out
    # of extension source. Broad ranges or aliases need explicit policy review.
    assert re.fullmatch(r"[\^~]?22\.\d+\.\d+", declared), declared
    assert lock["packages"][""]["devDependencies"]["@types/node"] == declared
    resolved = lock["packages"]["node_modules/@types/node"]["version"]
    assert re.fullmatch(r"22\.\d+\.\d+", resolved), resolved


def test_tui_dependency_composition_preserves_native_and_glob_overrides():
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            assert key not in result, f"Duplicate package key: {key}"
            result[key] = value
        return result

    tui = ROOT / "tests/PPDS.Tui.E2eTests"
    package = json.loads(
        (tui / "package.json").read_text(encoding="utf-8"),
        object_pairs_hook=unique_keys,
    )
    lock = json.loads((tui / "package-lock.json").read_text(encoding="utf-8"))
    overrides = package["overrides"]["@microsoft/tui-test"]
    assert overrides["@homebridge/node-pty-prebuilt-multiarch"] == "0.14.1"
    assert overrides["glob"] == "^14.0.2"
    assert lock["packages"]["node_modules/@homebridge/node-pty-prebuilt-multiarch"]["version"] == "0.14.1"
    assert lock["packages"]["node_modules/glob"]["version"].startswith("14.")
    extension_lock = json.loads(
        (ROOT / "src/PPDS.Extension/package-lock.json").read_text(encoding="utf-8")
    )
    playwright = extension_lock["packages"]["node_modules/@playwright/test"]["version"]
    assert package["devDependencies"]["@playwright/test"] == playwright
    assert lock["packages"]["node_modules/@playwright/test"]["version"] == playwright


def test_compatibility_is_blocking():
    jobs = workflow("build.yml")["jobs"]
    assert "extension" in jobs  # Stable name required by the dependency evidence gate.
    compatibility = jobs["extension-node22"]
    assert "continue-on-error" not in compatibility
    test_step = next(step for step in compatibility["steps"] if step.get("run") == "npm test")
    assert test_step["working-directory"] == "src/PPDS.Extension"
    assert "continue-on-error" not in test_step
    gate = jobs["build-status"]
    assert {"extension", "extension-node22", "extension-e2e"} <= set(gate["needs"])
    script = next(step["run"] for step in gate["steps"] if "run" in step)
    assert 'needs.extension-node22.result' in script
    assert 'needs.extension-e2e.result' in script
    assert 'exit 1' in script
