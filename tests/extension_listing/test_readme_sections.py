"""AC-02, AC-05, AC-06, AC-07: section ordering and required headers."""
from __future__ import annotations

import re

from tests.extension_listing._helpers import EXT_README, markdown_sections


def _read() -> str:
    return EXT_README.read_text(encoding="utf-8")


def test_zero_config_install_between_hero_image_and_features() -> None:
    """AC-02: `## Zero-config install` lives between the hero image ref and `## Features`."""
    text = _read()
    hero_match = re.search(r"!\[[^\]]*\]\(media/notebook-hero\.png\)", text)
    assert hero_match, "Hero image reference media/notebook-hero.png not found"
    hero_offset = len(text[: hero_match.start()].encode("utf-8"))

    sections = markdown_sections(EXT_README)
    zci = next(
        ((off, lvl, t) for off, lvl, t in sections if lvl == 2 and t.strip().lower() == "zero-config install"),
        None,
    )
    features = next(
        ((off, lvl, t) for off, lvl, t in sections if lvl == 2 and t.strip().lower() == "features"),
        None,
    )
    assert zci, "`## Zero-config install` H2 header missing"
    assert features, "`## Features` H2 header missing"
    assert hero_offset < zci[0] < features[0], (
        "Ordering constraint broken: hero image offset "
        f"{hero_offset} < zero-config offset {zci[0]} < features offset {features[0]}"
    )


def _collapse_backticks(text: str) -> str:
    return text.replace("`", "")


def test_ppds_cli_h2_present() -> None:
    """AC-05: H2 whose text (case-insensitive, backticks collapsed) contains 'ppds cli'."""
    sections = markdown_sections(EXT_README)
    h2_texts = [_collapse_backticks(t).lower() for off, lvl, t in sections if lvl == 2]
    assert any("ppds cli" in t for t in h2_texts), (
        "No H2 header contains 'ppds cli' after collapsing backticks. "
        f"H2 headers found: {h2_texts}"
    )


def test_ai_and_mcp_h2_present() -> None:
    """AC-06: H2 header containing both 'AI' and 'MCP' (case-insensitive)."""
    sections = markdown_sections(EXT_README)
    matches = [
        t for off, lvl, t in sections
        if lvl == 2 and "ai" in t.lower() and "mcp" in t.lower()
    ]
    assert matches, (
        "No H2 header contains both 'AI' and 'MCP'. "
        f"H2 headers: {[t for off, lvl, t in sections if lvl == 2]}"
    )


def test_part_of_ppds_signpost() -> None:
    """AC-07: H2 'Part of PPDS' (or 'Part of the PPDS platform') followed by ≥3 items
    collectively mentioning CLI, MCP, NuGet."""
    text = _read()
    pattern = re.compile(
        r"^##\s+(Part of PPDS(?: platform)?|Part of the PPDS platform)[^\n]*\n(.*?)(?=^##\s+|\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = pattern.search(text)
    assert match, "`## Part of PPDS` H2 signpost not found"
    body = match.group(2)

    list_items = re.findall(r"^\s*[-*+]\s+(.+)$", body, re.MULTILINE)
    assert len(list_items) >= 3, (
        f"Expected ≥3 unordered-list items under 'Part of PPDS'; found {len(list_items)}"
    )

    joined = " ".join(list_items)
    for token in ("CLI", "MCP", "NuGet"):
        assert token in joined or token.lower() in joined.lower(), (
            f"'{token}' not mentioned in 'Part of PPDS' list items"
        )
