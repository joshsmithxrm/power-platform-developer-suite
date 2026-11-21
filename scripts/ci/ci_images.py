"""The one definition of each container image listed in IMAGES: today the UBI 8 image (glibc 2.28)
that the Single-file extraction (linux-x64) job and the Reference Cache Trust workflow run.

Every workflow step and script that runs one of these images reads it from
here, so the image they run changes only with a commit to this file.
tests/ci/test_ci_images.py fails if a second definition appears. Other images
CI runs are not defined here yet (the Reference Cache Trust cross-user step
still names an SDK image by tag).

Each image is referenced by digest, never by a moving tag. The UBI 8 digest
below is the top-level (multi-platform manifest list) digest of
registry.access.redhat.com/ubi8/dotnet-80-runtime:latest, read on 2026-11-20.

Inspect the recorded digest (on a machine with Docker):

    docker buildx imagetools inspect registry.access.redhat.com/ubi8/dotnet-80-runtime@sha256:<digest>

Read the current digest of the tag:

    docker buildx imagetools inspect registry.access.redhat.com/ubi8/dotnet-80-runtime:latest

Before changing the digest, check that the new one is a manifest list or OCI
index with a linux/amd64 entry. Change it in a pull request, and see both jobs
that run it pass there: Single-file extraction (linux-x64) and Cache trust
(linux-x64).

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
