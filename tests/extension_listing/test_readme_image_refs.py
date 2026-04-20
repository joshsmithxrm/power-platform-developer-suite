"""AC-09, AC-10: image references are relative, non-SVG, under media/."""
from __future__ import annotations

import pytest

from tests.extension_listing._helpers import EXT_README, extract_image_refs


def _refs() -> list[str]:
    text = EXT_README.read_text(encoding="utf-8")
    return extract_image_refs(text)


def test_at_least_one_image_reference() -> None:
    refs = _refs()
    assert refs, "Extension README has no image references; expected at least the hero image"


@pytest.mark.parametrize("ref_url", _refs())
def test_image_ref_is_relative_media_path(ref_url: str) -> None:
    """AC-09: every image URL starts with 'media/'; never absolute."""
    assert not ref_url.lower().startswith(("http://", "https://")), (
        f"Image ref {ref_url!r} is absolute; must be relative under media/"
    )
    assert ref_url.startswith("media/"), (
        f"Image ref {ref_url!r} must start with 'media/'"
    )


@pytest.mark.parametrize("ref_url", _refs())
def test_image_ref_is_not_svg(ref_url: str) -> None:
    """AC-10: no SVG references in README body."""
    assert not ref_url.lower().endswith(".svg"), (
        f"Image ref {ref_url!r} is SVG; Marketplace rejects SVG in README body"
    )
