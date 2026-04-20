"""AC-08: README word count in [900, 1200]."""
from __future__ import annotations

from tests.extension_listing._helpers import EXT_README, word_count_for_marketplace


def test_readme_word_count_in_band() -> None:
    count = word_count_for_marketplace(EXT_README)
    assert 900 <= count <= 1200, (
        f"README word count {count} outside [900, 1200] band. "
        "Trim or expand content."
    )
