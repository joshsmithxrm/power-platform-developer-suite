"""CodeQL scans both shipped languages: the C# code and the extension's TypeScript."""
from __future__ import annotations

from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]


def load(path: str) -> dict:
    return yaml.safe_load((ROOT / path).read_text(encoding="utf-8"))


def test_both_languages_are_analysed_with_their_own_configuration():
    workflow = load(".github/workflows/codeql.yml")
    job = workflow["jobs"]["analyze"]
    legs = {leg["language"]: leg for leg in job["strategy"]["matrix"]["include"]}
    assert set(legs) == {"csharp", "javascript-typescript"}
    assert job["strategy"]["fail-fast"] is False  # One language failing must not hide the other's result.
    assert job["name"] == "Analyze ${{ matrix.name }}" and legs["csharp"]["name"] == "C#"
    for leg in legs.values():
        assert (ROOT / leg["config"]).is_file(), leg["config"]

    steps = {step["name"]: step for step in job["steps"]}
    assert steps["Initialize CodeQL"]["with"] == {
        "languages": "${{ matrix.language }}", "config-file": "${{ matrix.config }}"}
    assert steps["Perform CodeQL Analysis"]["with"]["category"] == "/language:${{ matrix.language }}"
    # Only the C# leg builds; neither the analysis nor the upload is conditional or optional.
    for name in ("Setup .NET SDK", "Build solution"):
        assert steps[name]["if"] == "matrix.language == 'csharp'"
    for name in ("Initialize CodeQL", "Perform CodeQL Analysis"):
        assert "if" not in steps[name] and "continue-on-error" not in steps[name]


def test_a_change_to_the_extension_or_to_the_scan_itself_triggers_the_scan():
    workflow = load(".github/workflows/codeql.yml")
    triggers = workflow.get("on", workflow.get(True))
    for event in ("push", "pull_request"):
        paths = triggers[event]["paths"]
        for required in ("src/**/*.cs", "src/PPDS.Extension/**", ".github/codeql/**", ".github/workflows/codeql.yml"):
            assert required in paths, (event, required)


def test_typescript_scan_covers_the_extension_source_with_security_queries():
    config = load(".github/codeql/codeql-typescript-config.yml")
    assert config["paths"] == ["src/PPDS.Extension"]
    assert config["queries"] == [{"uses": "security-extended"}]
    # Source directories must not be excluded; only tests, build output and dependencies are.
    assert not any(entry.startswith("src/PPDS.Extension/src") for entry in config["paths-ignore"])
