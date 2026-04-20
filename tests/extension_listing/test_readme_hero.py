"""AC-01: hero sentence appears in the first 10 non-empty content lines."""
from __future__ import annotations

from tests.extension_listing._helpers import EXT_README, normalize_markdown

HERO = (
    "**Power Platform Developer Suite (PPDS)** is a developer platform for "
    "Microsoft Power Platform and Dataverse. This extension puts SQL/FetchXML "
    "notebooks, plugin registration, metadata browsing, and 7 other panels "
    "into VS Code — self-contained, with the `ppds` CLI daemon bundled — and "
    "an MCP server that makes your environments queryable by AI agents."
)


def _hero_normalized() -> str:
    """Normalize hero text the same way the helper normalizes the README."""
    import re
    text = HERO
    text = re.sub(r"\s+", " ", text).strip()
    return text


def test_hero_sentence_present_after_normalization() -> None:
    normalized = normalize_markdown(EXT_README)
    hero_normalized = _hero_normalized()
    assert hero_normalized in normalized, (
        "Hero sentence not found in normalized README. "
        f"Expected substring:\n{hero_normalized!r}"
    )


def test_hero_sentence_in_first_10_content_lines() -> None:
    raw_lines = EXT_README.read_text(encoding="utf-8").splitlines()
    content_lines: list[str] = []
    for line in raw_lines:
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith("#"):  # H1 title excluded
            continue
        if stripped.startswith("[!["):  # linked badge row excluded
            continue
        content_lines.append(stripped)
        if len(content_lines) >= 10:
            break

    joined = " ".join(content_lines)
    import re
    joined = re.sub(r"\s+", " ", joined)
    hero_normalized = _hero_normalized()
    assert hero_normalized in joined, (
        "Hero sentence must appear within the first 10 non-empty content lines "
        "after the H1/badge row."
    )
