"""XML documentation on public API is a build error in the four published libraries.

The compiler's CS1591 enforces it: `.editorconfig` raises it to error for the
library sources and a later `[**/Generated/**.cs]` section turns it off for
generated code. See specs/docs-generation.md (AC-01, AC-06).
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
LIBRARIES = ("PPDS.Dataverse", "PPDS.Migration", "PPDS.Auth", "PPDS.Plugins")
CS1591 = "dotnet_diagnostic.CS1591.severity"


def _editorconfig_sections():
    sections = []
    for line in (ROOT / ".editorconfig").read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("[") and line.endswith("]"):
            sections.append((line[1:-1], {}))
        elif sections and "=" in line and not line.startswith(("#", ";")):
            key, value = (part.strip() for part in line.split("=", 1))
            sections[-1][1][key] = value
    return sections


def _section_libraries(glob):
    match = re.fullmatch(r"src/\{([^}]*)\}/\*\*\.cs", glob)
    if match:
        return set(match.group(1).split(","))
    match = re.fullmatch(r"src/([^/{}*]+)/\*\*\.cs", glob)
    return {match.group(1)} if match else set()


def test_cs1591_is_an_error_in_each_library():
    covered = set()
    for glob, settings in _editorconfig_sections():
        if settings.get(CS1591) == "error":
            covered |= _section_libraries(glob)
    assert covered >= set(LIBRARIES)


def test_generated_code_stays_exempt_after_the_library_section():
    sections = _editorconfig_sections()
    globs = [glob for glob, _ in sections]
    generated = globs.index("**/Generated/**.cs")
    assert sections[generated][1].get(CS1591) == "none"
    library_sections = [i for i, (glob, settings) in enumerate(sections)
                        if settings.get(CS1591) == "error" and _section_libraries(glob)]
    assert library_sections and max(library_sections) < generated


def test_no_later_section_lowers_cs1591_for_library_sources():
    sections = _editorconfig_sections()
    first_error = min(i for i, (glob, settings) in enumerate(sections)
                      if settings.get(CS1591) == "error" and _section_libraries(glob))
    for glob, settings in sections[first_error + 1:]:
        if glob == "**/Generated/**.cs" or settings.get(CS1591, "error") == "error":
            continue
        # Only the Generated section may lower CS1591 after the library section.
        assert not any(library in glob for library in LIBRARIES), glob
        assert not glob.startswith(("*", "src/*", "src/{", "**")), glob


def _mentions_cs1591(text):
    return re.search(r"\b(CS)?1591\b", text) is not None


def test_cs1591_matcher_catches_both_spellings():
    assert _mentions_cs1591("$(NoWarn);CS1591")
    assert _mentions_cs1591("$(NoWarn);1591")
    assert _mentions_cs1591("1591;CS8618")
    assert not _mentions_cs1591("$(NoWarn);RS0026;CS15910")


def test_library_projects_generate_docs_without_overriding_the_severity():
    for library in LIBRARIES:
        project = ET.parse(ROOT / "src" / library / f"{library}.csproj").getroot()
        values = {}
        for element in project.iter():
            if element.tag in ("GenerateDocumentationFile", "NoWarn", "WarningsAsErrors"):
                values.setdefault(element.tag, []).append(element.text or "")
        assert values.get("GenerateDocumentationFile") == ["true"], library
        # NoWarn would silence CS1591; WarningsAsErrors would override the
        # Generated/ exemption in .editorconfig (command-line options win).
        for tag in ("NoWarn", "WarningsAsErrors"):
            assert not any(_mentions_cs1591(text) for text in values.get(tag, [])), (library, tag)
