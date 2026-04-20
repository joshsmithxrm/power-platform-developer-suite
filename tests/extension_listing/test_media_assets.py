"""AC-19, AC-20, AC-21: media/ PNGs exist, uniform dimensions, PNG magic bytes."""
from __future__ import annotations

import pytest

from tests.extension_listing._helpers import EXT_MEDIA_DIR, read_png_dims

REQUIRED_ASSETS = ["notebook-hero.png", "metadata-browser.png", "plugin-traces.png"]
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


@pytest.mark.parametrize("filename", REQUIRED_ASSETS)
def test_media_asset_exists(filename: str) -> None:
    """AC-19."""
    path = EXT_MEDIA_DIR / filename
    assert path.exists(), f"Media asset missing: {path}"
    assert path.is_file(), f"{path} is not a regular file"


@pytest.mark.parametrize("filename", REQUIRED_ASSETS)
def test_media_asset_is_png_magic(filename: str) -> None:
    """AC-21: begins with PNG magic bytes 89 50 4E 47 0D 0A 1A 0A."""
    path = EXT_MEDIA_DIR / filename
    data = path.read_bytes()
    assert len(data) >= 8, f"{path} is too short to be a PNG"
    assert data[:8] == PNG_MAGIC, (
        f"{path} does not start with PNG magic bytes. Got {data[:8].hex()}"
    )


def test_media_assets_uniform_dimensions() -> None:
    """AC-20: all three PNGs share the same pixel dimensions."""
    dims = {filename: read_png_dims(EXT_MEDIA_DIR / filename) for filename in REQUIRED_ASSETS}
    unique_dims = set(dims.values())
    assert len(unique_dims) == 1, (
        f"Media asset dimensions must be uniform; got {dims}"
    )
