"""The one definition of each outside container image that a CI check runs.

Every workflow step and script that runs one of these images reads it from
here, so the image a check runs changes only with a commit to this file.
tests/ci/test_ci_images.py fails if a second definition appears.

Each image is referenced by digest, never by a moving tag. To update a digest,
read it from the registry's own record, for example on a machine with Docker:

    docker buildx imagetools inspect registry.access.redhat.com/ubi8/dotnet-80-runtime:latest

and copy the top-level "Digest:" line (the multi-platform index digest), or:

    docker pull registry.access.redhat.com/ubi8/dotnet-80-runtime:latest
    docker image inspect --format '{{json .RepoDigests}}' registry.access.redhat.com/ubi8/dotnet-80-runtime:latest

Then replace the digest below in a pull request. The Single-file extraction
(linux-x64) job and the Reference Cache Trust workflow run the new image.

The UBI 8 digest below was read on 2026-11-20: the top-level (multi-platform manifest list) digest of
registry.access.redhat.com/ubi8/dotnet-80-runtime:latest. To read it again:

    docker buildx imagetools inspect registry.access.redhat.com/ubi8/dotnet-80-runtime:latest

Usage from a workflow step: python scripts/ci/ci_images.py <name>
"""
from __future__ import annotations

import sys

# UBI 8 (glibc 2.28) with the .NET 8 runtime: the oldest supported glibc ABI.
UBI8_DOTNET8_RUNTIME = (
    "registry.access.redhat.com/ubi8/dotnet-80-runtime@sha256:f3a542b933ff4885ab6ab82a65d61f4123f0c1191a4a14fa41fedf7f93a27948"
)

IMAGES = {"ubi8-dotnet8-runtime": UBI8_DOTNET8_RUNTIME}


def registry(image):
    """The registry host of an image reference, for failure messages."""
    return image.split("/", 1)[0]


def main(argv):
    if len(argv) != 1 or argv[0] not in IMAGES:
        print(f"usage: ci_images.py {{{'|'.join(sorted(IMAGES))}}}", file=sys.stderr)
        return 2
    print(IMAGES[argv[0]])
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
