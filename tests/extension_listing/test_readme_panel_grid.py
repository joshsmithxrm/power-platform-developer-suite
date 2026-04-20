"""AC-04: all nine v1.0 panel labels present in a single contiguous section."""
from __future__ import annotations

import re

from tests.extension_listing._helpers import EXT_README

NINE_PANELS = [
    "Data Explorer",
    "Solutions",
    "Plugin Traces",
    "Metadata Browser",
    "Connection References",
    "Environment Variables",
    "Web Resources",
    "Import Jobs",
    "Plugin Registration",
]


def test_all_nine_panels_inside_features_section() -> None:
    text = EXT_README.read_text(encoding="utf-8")
    features_match = re.search(r"^##\s+Features\b", text, re.MULTILINE)
    assert features_match, "`## Features` H2 header not found"
    after_features = text[features_match.end():]
    next_h2_match = re.search(r"^##\s+", after_features, re.MULTILINE)
    assert next_h2_match, "No H2 header after `## Features` (section must be bounded)"
    section = after_features[: next_h2_match.start()]
    section_lower = section.lower()

    missing = [label for label in NINE_PANELS if label.lower() not in section_lower]
    assert not missing, (
        "Panel labels missing from contiguous Features section: "
        f"{missing}\n(Section length: {len(section)} chars)"
    )
