"""Contracts separating contributor tooling from the supported extension host."""
from pathlib import Path
import json

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

    for filename in ("build.yml", "tui-e2e.yml", "audit-capture.yml", "extension-publish.yml"):
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
