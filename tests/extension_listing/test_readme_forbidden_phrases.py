"""AC-03: forbidden 'install the CLI separately' phrasings."""
from __future__ import annotations

import pytest

from tests.extension_listing._helpers import EXT_README

FORBIDDEN_PHRASES = [
    "PPDS CLI installed",
    "CLI installed and on your PATH",
    "install the ppds CLI first",
    "prerequisite: PPDS CLI",
]


@pytest.mark.parametrize("phrase", FORBIDDEN_PHRASES)
def test_forbidden_phrase_absent(phrase: str) -> None:
    text = EXT_README.read_text(encoding="utf-8").lower()
    assert phrase.lower() not in text, (
        f"Forbidden phrase {phrase!r} found in extension README "
        "(AC-03: extension must not imply a separate CLI install)."
    )
