"""Every .NET test project gets the interactive sign-in switch and tripwire (#1757).

tests/Directory.Build.props turns on the AppContext switch PPDS.BlockInteractiveSignIn and compiles
tests/Shared/InteractiveSignInTripwire.cs into each test project. The per-project self-tests vanish
together with the tripwire when a project is left out, so this test checks the condition and every
project from outside the .NET build.
"""
from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TESTS = REPO / "tests"
PROPS = TESTS / "Directory.Build.props"
SWITCH = "PPDS.BlockInteractiveSignIn"
TRIPWIRE = "Shared/InteractiveSignInTripwire.cs"
EXPECTED_CONDITION = "'$(IsTestProject)' == 'true'"


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _test_projects() -> list[Path]:
    projects = []
    for csproj in sorted(TESTS.glob("*/*.csproj")):
        text = csproj.read_text(encoding="utf-8")
        if 'Include="Microsoft.NET.Test.Sdk"' in text or 'Include="xunit"' in text:
            projects.append(csproj)
    return projects


def test_props_turn_on_the_switch_and_compile_the_tripwire_for_every_test_project():
    root = ET.parse(PROPS).getroot()
    imports = [e for e in root if _local(e.tag) == "Import"]
    assert imports and "Directory.Build.props" in imports[0].get("Project", ""), \
        "tests/Directory.Build.props must import the root Directory.Build.props"

    groups = []
    for group in (e for e in root if _local(e.tag) == "ItemGroup"):
        options = [i for i in group if _local(i.tag) == "RuntimeHostConfigurationOption"
                   and i.get("Include") == SWITCH]
        compiles = [i for i in group if _local(i.tag) == "Compile"
                    and i.get("Include", "").replace("\\", "/").endswith(TRIPWIRE)]
        if options or compiles:
            groups.append((group, options, compiles))

    assert len(groups) == 1, "the switch and the tripwire belong in one ItemGroup"
    group, options, compiles = groups[0]
    assert len(options) == 1 and options[0].get("Value") == "true"
    assert len(compiles) == 1
    condition = " ".join(group.get("Condition", "").split())
    assert condition == EXPECTED_CONDITION, (
        f"the ItemGroup must apply to every test project and nothing else; condition is {condition!r}")


def test_every_test_project_is_covered_by_the_props_condition():
    projects = _test_projects()
    assert len(projects) >= 18, f"expected the repository's test projects, found {len(projects)}"

    problems = []
    for csproj in projects:
        name = csproj.relative_to(REPO).as_posix()
        text = csproj.read_text(encoding="utf-8")
        if not re.search(r"<IsTestProject>\s*true\s*</IsTestProject>", text, re.IGNORECASE):
            problems.append(f"{name}: does not set <IsTestProject>true</IsTestProject>")
        if re.search(r"<IsTestProject>\s*false", text, re.IGNORECASE):
            problems.append(f"{name}: sets IsTestProject to false")
        if re.search(r"<ImportDirectoryBuildProps>\s*false", text, re.IGNORECASE):
            problems.append(f"{name}: does not import Directory.Build.props")
        if re.search(r"Remove=\"[^\"]*InteractiveSignInTripwire", text) or \
                re.search(rf"<RuntimeHostConfigurationOption[^>]*{re.escape(SWITCH)}", text):
            problems.append(f"{name}: removes or overrides the tripwire or the switch")
        # A Directory.Build.props between the project and tests/ would hide tests/Directory.Build.props.
        for folder in csproj.parent.relative_to(TESTS).parents:
            nested = TESTS / folder / "Directory.Build.props"
            if folder != Path(".") and nested.exists():
                problems.append(f"{name}: {nested.relative_to(REPO).as_posix()} shadows tests/Directory.Build.props")
        if (csproj.parent / "Directory.Build.props").exists():
            problems.append(f"{name}: its own Directory.Build.props shadows tests/Directory.Build.props")

    assert not problems, "\n".join(problems)
