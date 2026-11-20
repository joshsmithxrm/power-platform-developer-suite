"""Outside container images run by CI checks: one definition, pinned, and a
failed pull reported as an infrastructure failure, not a product failure."""
import importlib.util
from pathlib import Path
import re
import subprocess
import sys

import pytest
import yaml

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/ci"))
try:
    import ci_images
    spec = importlib.util.spec_from_file_location("native_distribution_images", ROOT / "scripts/ci/verify_native_distribution.py")
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
finally:
    sys.path.pop(0)

DEFINITION = "scripts/ci/ci_images.py"
THIS_TEST = "tests/ci/test_ci_images.py"
DIGEST = re.compile(r"[a-z0-9.-]+(?::[0-9]+)?/[a-z0-9._/-]+@sha256:[0-9a-f]{64}")


def repository(image):
    """The image name without tag or digest, as a second definition would spell it."""
    name = image.split("@", 1)[0]
    host, _, path = name.partition("/")
    return host + "/" + path.split(":", 1)[0]


def tracked_text_files():
    names = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True).stdout
    for name in names.decode("utf-8").split("\0"):
        path = ROOT / name
        if not name or not path.is_file():
            continue
        try:
            yield name, path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue


def second_definitions(files):
    found = []
    for name, text in files:
        if name in {DEFINITION, THIS_TEST}:
            continue
        for image in ci_images.IMAGES.values():
            short = repository(image).split("/", 1)[1]
            if repository(image) in text or re.search(r"(?<![\w.-])" + re.escape(short) + r"(?![\w-])", text):
                found.append((name, short))
    return found


def test_each_image_is_defined_once_in_the_repository():
    assert second_definitions(tracked_text_files()) == []


def test_a_second_definition_of_the_image_is_detected():
    image = repository(ci_images.UBI8_DOTNET8_RUNTIME)
    workflow = f"      - run: docker run --rm {image}:latest true\n"
    script = f'IMAGE = "{image.split("/", 1)[1]}:8.0"\n'
    assert second_definitions([(".github/workflows/x.yml", workflow), ("scripts/ci/x.py", script)]) == [
        (".github/workflows/x.yml", image.split("/", 1)[1]), ("scripts/ci/x.py", image.split("/", 1)[1])]


@pytest.mark.xfail(strict=True, reason="#1794: the UBI 8 digest has not yet been read from the registry; "
                                       "fill it in scripts/ci/ci_images.py and remove this marker")
@pytest.mark.parametrize("name", sorted(ci_images.IMAGES))
def test_each_image_is_pinned_by_digest_not_a_moving_tag(name):
    assert DIGEST.fullmatch(ci_images.IMAGES[name]), ci_images.IMAGES[name]


def test_digest_pattern_accepts_only_a_full_sha256_reference():
    good = "registry.access.redhat.com/ubi8/dotnet-80-runtime@sha256:" + "0" * 64
    assert DIGEST.fullmatch(good)
    for bad in ["registry.access.redhat.com/ubi8/dotnet-80-runtime:latest",
                "registry.access.redhat.com/ubi8/dotnet-80-runtime:latest@sha256:" + "0" * 63,
                "registry.access.redhat.com/ubi8/dotnet-80-runtime@sha256:" + "0" * 63,
                "registry.access.redhat.com/ubi8/dotnet-80-runtime@sha256:" + "G" * 64]:
        assert not DIGEST.fullmatch(bad), bad


def test_verifier_and_workflows_read_the_one_definition():
    assert gate.IMAGE == ci_images.UBI8_DOTNET8_RUNTIME
    workflow = yaml.safe_load((ROOT / ".github/workflows/reference-cache-trust.yml").read_text())
    assert "scripts/ci/ci_images.py" in workflow[True]["pull_request"]["paths"]
    step = next(s for s in workflow["jobs"]["native"]["steps"] if s.get("name", "").startswith("Older supported glibc ABI"))
    assert "image=$(python scripts/ci/ci_images.py ubi8-dotnet8-runtime)" in step["run"]
    assert 'docker run --rm --pull=never --entrypoint /bin/bash' in step["run"] and '"$image" -euc' in step["run"]
    assert "Infrastructure failure" in step["run"] and "The product was not tested" in step["run"]
    tests = yaml.safe_load((ROOT / ".github/workflows/test.yml").read_text())
    paths = next(s for s in tests["jobs"]["changes"]["steps"] if str(s.get("uses", "")).startswith("dorny/paths-filter@"))
    assert "scripts/ci/ci_images.py" in yaml.safe_load(paths["with"]["filters"])["extraction"]


def test_cli_prints_a_known_image_and_rejects_an_unknown_name(capsys):
    assert ci_images.main(["ubi8-dotnet8-runtime"]) == 0
    assert capsys.readouterr().out == ci_images.UBI8_DOTNET8_RUNTIME + "\n"
    assert ci_images.main(["nope"]) == 2 and ci_images.main([]) == 2


@pytest.mark.parametrize("failure", [ValueError("image-pull failed (exit 1); retained raw logs"),
                                     subprocess.TimeoutExpired(["docker", "pull"], 180),
                                     FileNotFoundError("docker")])
def test_failed_pull_is_an_infrastructure_failure_naming_the_registry(monkeypatch, tmp_path, failure):
    calls = []

    def refuse(command, env, evidence, label, timeout=120, cwd=None):
        calls.append((command, label, timeout))
        raise failure

    monkeypatch.setattr(gate, "run", refuse)
    with pytest.raises(gate.InfrastructureFailure) as raised:
        gate.pull_image(gate.IMAGE, {}, tmp_path)
    assert calls == [(["docker", "pull", gate.IMAGE], "image-pull", 180)]
    message = str(raised.value)
    assert "registry.access.redhat.com" in message and gate.IMAGE in message
    assert "The product was not tested" in message and "\n" not in message
    assert not isinstance(raised.value, ValueError)


def test_glibc_mode_pulls_through_the_infrastructure_classifier():
    source = (ROOT / "scripts/ci/verify_native_distribution.py").read_text()
    assert source.count("pull_image(IMAGE, env, evidence)") == 1
    assert '["docker", "pull", IMAGE]' not in source
    assert '"::error title=Infrastructure failure::' in source
