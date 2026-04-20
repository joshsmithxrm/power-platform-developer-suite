"""AC-11 through AC-18, AC-28: extension package.json marketplace fields + icon."""
from __future__ import annotations

import pytest

from tests.extension_listing._helpers import (
    EXT_DIR,
    load_package_json,
    read_png_dims,
    variant_pairs,
)


def _pkg() -> dict:
    return load_package_json()


def test_categories_exact_order() -> None:
    """AC-11: categories equals ['AI','Notebooks','Data Science','Other'] (order-sensitive)."""
    assert _pkg()["categories"] == ["AI", "Notebooks", "Data Science", "Other"]


def test_keywords_length_and_required_terms() -> None:
    """AC-12: 10–14 entries; includes dataverse, dynamics-365, power-platform, pac, mcp."""
    kws = _pkg()["keywords"]
    assert 10 <= len(kws) <= 14, f"Keywords count {len(kws)} out of [10, 14]"
    required = {"dataverse", "dynamics-365", "power-platform", "pac", "mcp"}
    kws_lower = {k.lower() for k in kws}
    missing = required - kws_lower
    assert not missing, f"Required keywords missing: {missing}"


def test_keywords_no_redundant_variants() -> None:
    """AC-13: at most one from each redundant-variant-pair row."""
    kws_lower = {k.lower() for k in _pkg()["keywords"]}
    for canonical, variants in variant_pairs():
        row_members = {canonical.lower()} | {v.lower() for v in variants}
        intersection = kws_lower & row_members
        assert len(intersection) <= 1, (
            f"Redundant variant pair detected: {intersection} "
            f"(canonical row: {canonical})"
        )


def test_gallery_banner_deep_equals() -> None:
    """AC-14."""
    assert _pkg()["galleryBanner"] == {"color": "#25c2a0", "theme": "dark"}


def test_preview_is_false() -> None:
    """AC-15."""
    pkg = _pkg()
    assert "preview" in pkg, "`preview` field must be present"
    assert pkg["preview"] is False


def test_qna_is_false() -> None:
    """AC-16."""
    pkg = _pkg()
    assert "qna" in pkg, "`qna` field must be present"
    assert pkg["qna"] is False


def test_description_content() -> None:
    """AC-17: description contains Power Platform + Dataverse + (MCP | AI), not 'comprehensive toolkit'."""
    desc = _pkg()["description"].lower()
    assert "power platform" in desc, "description must mention 'Power Platform'"
    assert "dataverse" in desc, "description must mention 'Dataverse'"
    assert ("mcp" in desc) or ("ai" in desc), (
        "description must mention at least one of 'MCP' / 'AI'"
    )
    assert "comprehensive toolkit" not in desc, (
        "description must not lead with 'comprehensive toolkit' (generic phrasing)"
    )


def test_display_name() -> None:
    """AC-18."""
    assert _pkg()["displayName"] == "Power Platform Developer Suite"


def test_icon_is_128x128_png() -> None:
    """AC-28: icon file exists, is PNG, is 128×128."""
    pkg = _pkg()
    icon_rel = pkg.get("icon")
    assert icon_rel, "`icon` field missing in package.json"
    icon_path = EXT_DIR / icon_rel
    assert icon_path.exists(), f"icon file {icon_path} does not exist"
    dims = read_png_dims(icon_path)
    assert dims == (128, 128), f"icon dimensions {dims} != (128, 128)"


# ---------------------------------------------------------------------------
# Negative-case verification (AC-13 threshold)
# ---------------------------------------------------------------------------

def test_variant_pair_detection_rejects_redundant_keywords() -> None:
    """Demonstrates AC-13 logic: inject a redundant pair into a fake keyword list
    and confirm the loop flags it. This guards against a silent pass when the
    check becomes a no-op (e.g., if `variant_pairs()` returns an empty list)."""
    fake_kws = {"dynamics-365", "d365", "mcp"}
    found_violation = False
    for canonical, variants in variant_pairs():
        row_members = {canonical.lower()} | {v.lower() for v in variants}
        if len(fake_kws & row_members) > 1:
            found_violation = True
            break
    assert found_violation, (
        "variant_pairs() must detect `dynamics-365` + `d365` as redundant; "
        "empty pair list would silently pass."
    )
