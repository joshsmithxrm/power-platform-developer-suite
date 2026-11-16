"""Keep CI's declared SDK installation compatible with the serviced repo floor.

These are configuration contracts, not an emulator of the dotnet resolver.
Hosted build/test logs must still establish the selected SDK and actual results.
"""
from copy import deepcopy
import json
from pathlib import Path

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
CHECKOUT_JOBS = (
    ("audit-capture.yml", "capture"),
    ("build.yml", "build"),
    ("codeql.yml", "analyze"),
    ("credential-store-interop.yml", "interop"),
    ("docs-release.yml", "release"),
    ("docs-smoke.yml", "smoke"),
    ("extension-publish.yml", "publish"),
    ("publish-nuget.yml", "build-package"),
    ("reference-cache-trust.yml", "native"),
    ("release-cli.yml", "build"),
    ("test.yml", "test"),
    ("test.yml", "single-file-extraction"),
    ("test.yml", "vsix-artifacts"),
    ("tui-e2e.yml", "tui-e2e"),
)
# Microsoft advisory CVE-2026-105544, fixed SDK feature bands used by this repo.
FIXED_SDK_FLOORS = {(8, 0, 4): 428, (9, 0, 3): 321,
                    (10, 0, 1): 115, (10, 0, 4): 404}


def version(value):
    parts = value.split(".")
    assert len(parts) == 3 and all(p.isascii() and p.isdigit() for p in parts), value
    return tuple(map(int, parts))


def repository_floor(config):
    sdk = config["sdk"]
    selected = version(sdk["version"])
    assert (10, 0, 115) <= selected < (10, 0, 200), selected
    assert sdk["rollForward"] == "latestPatch"
    return selected


def checkout_sdk_contract(config, job):
    floor = repository_floor(config)
    installed = []
    for step in job.get("steps", []):
        if not str(step.get("uses", "")).startswith("actions/setup-dotnet@"):
            continue
        options = step.get("with", {})
        if "global-json-file" in options:
            assert options["global-json-file"] == "global.json"
            installed.append(floor)
        for value in options.get("dotnet-version", "").splitlines():
            if not value.strip():
                continue
            candidate = version(value.strip())
            band = (*candidate[:2], candidate[2] // 100)
            assert band in FIXED_SDK_FLOORS, candidate
            assert candidate[2] >= FIXED_SDK_FLOORS[band], candidate
            installed.append(candidate)
    # A newer 4xx SDK alone cannot satisfy global.json's latestPatch 1xx policy.
    assert any(v[:2] == floor[:2] and v[2] // 100 == floor[2] // 100
               and v >= floor for v in installed), (floor, installed)


def config():
    return json.loads((ROOT / "global.json").read_text(encoding="utf-8"))


def workflow_job(workflow, job):
    value = yaml.safe_load((ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8"))
    return value["jobs"][job]


def test_repository_sdk_floor_is_serviced():
    repository_floor(config())


@pytest.mark.parametrize("change", [
    {"version": "10.0.100"}, {"version": "10.0.114"},
    {"version": "10.0.404"}, {"rollForward": "latestFeature"},
])
def test_unsafe_repository_sdk_policy_is_rejected(change):
    value = config()
    value["sdk"].update(change)
    with pytest.raises(AssertionError):
        repository_floor(value)


@pytest.mark.parametrize("workflow,job", CHECKOUT_JOBS)
def test_checkout_jobs_install_compatible_serviced_sdks(workflow, job):
    checkout_sdk_contract(config(), workflow_job(workflow, job))


@pytest.mark.parametrize("versions", [
    "10.0.114", "10.0.403", "10.0.404", "10.0.x", "",
    "8.0.427\n10.0.115", "9.0.320\n10.0.115", "10.0.403\n10.0.115",
])
def test_incompatible_or_vulnerable_installations_are_rejected(versions):
    job = deepcopy(workflow_job("test.yml", "test"))
    setup = next(s for s in job["steps"] if str(s.get("uses", "")).startswith("actions/setup-dotnet@"))
    setup["with"] = {"dotnet-version": versions}
    with pytest.raises(AssertionError):
        checkout_sdk_contract(config(), job)


def test_missing_sdk_installation_is_rejected():
    job = deepcopy(workflow_job("test.yml", "test"))
    job["steps"] = [s for s in job["steps"] if not str(s.get("uses", "")).startswith("actions/setup-dotnet@")]
    with pytest.raises(AssertionError):
        checkout_sdk_contract(config(), job)
