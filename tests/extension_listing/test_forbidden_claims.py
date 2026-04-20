"""AC-23: neither README contains forbidden marketing phrases."""
from __future__ import annotations

import pytest

from tests.extension_listing._helpers import EXT_README, REPO_README

FORBIDDEN_CLAIMS = [
    "PAC parity",
    "full ALM replacement",
    "superset of PAC",
]


@pytest.mark.parametrize(
    "path_label, path",
    [
        ("extension README", EXT_README),
        ("repo README", REPO_README),
    ],
)
@pytest.mark.parametrize("claim", FORBIDDEN_CLAIMS)
def test_forbidden_claim_absent(path_label: str, path, claim: str) -> None:
    text = path.read_text(encoding="utf-8").lower()
    assert claim.lower() not in text, (
        f"Forbidden marketing claim {claim!r} found in {path_label}"
    )
