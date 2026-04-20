"""AC-22: CHANGELOG v1.0.0 block contains a Marketplace review CTA before any H3."""
from __future__ import annotations

import re

from tests.extension_listing._helpers import EXT_CHANGELOG, changelog_version_block

CTA_URL_RE = re.compile(
    r"https://marketplace\.visualstudio\.com/items\?itemName=JoshSmithXRM\.power-platform-developer-suite[^)\s]*review",
    re.IGNORECASE,
)


def test_v1_block_has_review_cta_before_first_h3() -> None:
    block, _ = changelog_version_block(EXT_CHANGELOG, "1.0.0")

    link_pattern = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
    cta_match = None
    for match in link_pattern.finditer(block):
        if CTA_URL_RE.search(match.group(1)):
            cta_match = match
            break
    assert cta_match, (
        "v1.0.0 block is missing a Marketplace review link. Expected URL matching "
        "marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite"
        "...review"
    )

    h3_match = re.search(r"^###\s+", block, re.MULTILINE)
    if h3_match:
        assert cta_match.start() < h3_match.start(), (
            "Marketplace review CTA must appear before the first H3 (### ) subheader "
            "inside the v1.0.0 block"
        )
