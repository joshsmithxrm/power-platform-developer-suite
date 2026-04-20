"""AC-26, AC-27: repo README first-paragraph marketplace link + no extension-specific phrases."""
from __future__ import annotations

import re

import pytest

from tests.extension_listing._helpers import REPO_README

MARKETPLACE_URL = (
    "https://marketplace.visualstudio.com/items?"
    "itemName=JoshSmithXRM.power-platform-developer-suite"
)

EXTENSION_SPECIFIC_PHRASES = [
    "Five-tab entity explorer",
    "Timeline waterfall with trace-level management",
    "Virtual-scrolled results",
    "Notebook cell · SQL and FetchXML",
    "Maker Portal buttons on Solutions and Plugin Traces",
]


def _first_paragraph() -> str:
    """Content between H1 (first line) and first `## ` H2 header."""
    text = REPO_README.read_text(encoding="utf-8")
    h1_match = re.search(r"^#\s+[^\n]+\n", text, re.MULTILINE)
    assert h1_match, "Repo README missing H1 header"
    after_h1 = text[h1_match.end():]
    h2_match = re.search(r"^##\s+", after_h1, re.MULTILINE)
    assert h2_match, "Repo README missing any H2 header after H1"
    return after_h1[: h2_match.start()]


def test_marketplace_link_in_first_paragraph() -> None:
    """AC-26."""
    first_para = _first_paragraph()
    assert MARKETPLACE_URL in first_para, (
        "Marketplace URL must appear within the first paragraph "
        "(content between H1 and the first H2 header). "
        f"First paragraph preview:\n{first_para[:400]}"
    )


@pytest.mark.parametrize("phrase", EXTENSION_SPECIFIC_PHRASES)
def test_extension_specific_phrase_absent(phrase: str) -> None:
    """AC-27: these phrases belong to the extension README, not the repo README."""
    text = REPO_README.read_text(encoding="utf-8").lower()
    assert phrase.lower() not in text, (
        f"Extension-specific phrase {phrase!r} found in repo README "
        "(belongs to extension README per boundary rule)"
    )
