"""Shared helpers for marketplace-listing AC tests.

All tests in this directory read the current tree's files and apply
deterministic checks. Helpers here isolate the parsing logic so tests
stay focused on the assertion.
"""
from __future__ import annotations

import json
import re
import struct
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
EXT_DIR = REPO_ROOT / "src" / "PPDS.Extension"
EXT_README = EXT_DIR / "README.md"
EXT_PACKAGE_JSON = EXT_DIR / "package.json"
EXT_CHANGELOG = EXT_DIR / "CHANGELOG.md"
EXT_MEDIA_DIR = EXT_DIR / "media"
REPO_README = REPO_ROOT / "README.md"


# ---------------------------------------------------------------------------
# Markdown normalization
# ---------------------------------------------------------------------------

_BADGE_IMAGE_RE = re.compile(r"!\[[^\]]*\]\([^)]*\)")
_LINKED_BADGE_RE = re.compile(r"\[!\[[^\]]*\]\([^)]*\)\]\([^)]*\)")
_HTML_IMG_RE = re.compile(r"<img\b[^>]*>", re.IGNORECASE)
_CODE_FENCE_RE = re.compile(r"```.*?```", re.DOTALL)
_INLINE_CODE_RE = re.compile(r"`[^`]*`")
_HEADER_RE = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$", re.MULTILINE)
_LIST_MARKER_RE = re.compile(r"^\s*(?:[-*+]|\d+\.)\s+", re.MULTILINE)
_WHITESPACE_RUN_RE = re.compile(r"\s+")


def normalize_markdown(path: Path) -> str:
    """Collapse whitespace, strip badges/images per AC-01.

    Returns a single-spaced string suitable for substring matching.
    Strips Markdown image syntax, linked-image badges, and raw <img>.
    """
    text = Path(path).read_text(encoding="utf-8")
    text = _LINKED_BADGE_RE.sub("", text)
    text = _BADGE_IMAGE_RE.sub("", text)
    text = _HTML_IMG_RE.sub("", text)
    text = _WHITESPACE_RUN_RE.sub(" ", text).strip()
    return text


def word_count_for_marketplace(path: Path) -> int:
    """AC-08 word count.

    Strip fenced code blocks, badges/images, headers (keeping the text),
    list markers, and inline code. Count whitespace-delimited tokens on
    the remainder.
    """
    text = Path(path).read_text(encoding="utf-8")
    text = _CODE_FENCE_RE.sub("", text)
    text = _LINKED_BADGE_RE.sub("", text)
    text = _BADGE_IMAGE_RE.sub("", text)
    text = _HTML_IMG_RE.sub("", text)
    text = _INLINE_CODE_RE.sub("", text)

    def _strip_header(match: re.Match[str]) -> str:
        return match.group(2)

    text = _HEADER_RE.sub(_strip_header, text)
    text = _LIST_MARKER_RE.sub("", text)
    tokens = [tok for tok in text.split() if tok.strip()]
    return len(tokens)


def load_package_json() -> dict:
    return json.loads(EXT_PACKAGE_JSON.read_text(encoding="utf-8"))


def markdown_sections(path: Path) -> list[tuple[int, int, str]]:
    """Return list of (byte_offset, header_level, header_text).

    byte_offset is the position of the `#` character in the UTF-8 bytes.
    Used for ordering checks (AC-02, AC-22, AC-26).
    """
    text = Path(path).read_text(encoding="utf-8")
    results: list[tuple[int, int, str]] = []
    for match in _HEADER_RE.finditer(text):
        byte_offset = len(text[: match.start()].encode("utf-8"))
        level = len(match.group(1))
        header_text = match.group(2).strip()
        results.append((byte_offset, level, header_text))
    return results


def variant_pairs() -> list[tuple[str, list[str]]]:
    """The redundant-variant-pairs table from the spec.

    (canonical_keyword, [forbidden_variants_alongside_it])
    """
    return [
        ("dynamics-365", [
            "d365", "dynamics365", "dynamics 365", "dynamics",
            "crm", "dynamics-crm", "dynamics crm",
        ]),
        ("power-platform", ["powerplatform", "power platform"]),
        ("power-apps", ["powerapps", "power apps"]),
        ("power-automate", ["powerautomate", "power automate"]),
        ("dataverse", ["common-data-service", "common data service", "cds"]),
        ("developer-tools", ["devtools", "dev-tools", "dev tools"]),
        ("administration", ["admin"]),
        ("plugin-registration", ["plugins", "plugin"]),
        ("solutions", ["solution"]),
        ("environments", ["environment"]),
    ]


def read_png_dims(path: Path) -> tuple[int, int]:
    """Read PNG IHDR width/height. Raises on bad magic bytes."""
    data = Path(path).read_bytes()
    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG (bad magic bytes)")
    # Bytes 8..11 = chunk length (4 bytes), 12..15 = "IHDR", 16..19 = width, 20..23 = height
    if data[12:16] != b"IHDR":
        raise ValueError(f"{path} does not have IHDR as first chunk")
    width = struct.unpack(">I", data[16:20])[0]
    height = struct.unpack(">I", data[20:24])[0]
    return width, height


def extract_image_refs(markdown_text: str) -> list[str]:
    """Return all BODY image reference URLs (Markdown + HTML) from markdown text.

    Excludes:
    - code fences and inline code
    - linked-image badges (`[![alt](img)](url)`) — these are badge shields,
      not body content images, and AC-09/AC-10 scope the relative-path /
      no-SVG rules to body images. Badges are still whitelisted absolute
      URLs per the spec Validation Rules note on whitelisted badge providers.
    """
    stripped = _CODE_FENCE_RE.sub("", markdown_text)
    stripped = _INLINE_CODE_RE.sub("", stripped)
    stripped = _LINKED_BADGE_RE.sub("", stripped)
    urls: list[str] = []
    for match in re.finditer(r"!\[[^\]]*\]\(([^)]+)\)", stripped):
        urls.append(match.group(1).strip())
    for match in re.finditer(r"<img\b[^>]*\bsrc=[\"']([^\"']+)[\"']", stripped, re.IGNORECASE):
        urls.append(match.group(1).strip())
    return urls


def changelog_version_block(path: Path, version: str) -> tuple[str, int]:
    """Return (block_text, block_start_byte_offset) for `## [<version>]`.

    Block is the content from the `## [<version>]` header to the next
    `## [` header (or end of file).
    """
    text = Path(path).read_text(encoding="utf-8")
    pattern = re.compile(
        rf"^##\s*\[{re.escape(version)}\][^\n]*\n",
        re.MULTILINE,
    )
    start_match = pattern.search(text)
    if not start_match:
        raise ValueError(f"No `## [{version}]` header found in {path}")
    block_start = start_match.end()
    start_byte = len(text[: start_match.start()].encode("utf-8"))
    next_match = re.search(r"^##\s*\[[^\]]+\]", text[block_start:], re.MULTILINE)
    if next_match:
        block_end = block_start + next_match.start()
    else:
        block_end = len(text)
    return text[block_start:block_end], start_byte
