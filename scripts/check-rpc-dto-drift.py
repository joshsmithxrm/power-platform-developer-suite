#!/usr/bin/env python3
"""
RPC DTO drift check — Workstream G3 (v1 release plan).

Problem
-------
`src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` defines the C# DTOs that
cross the JSON-RPC boundary. `src/PPDS.Extension/src/types.ts` and
`src/PPDS.Extension/src/daemonClient.ts` define the TypeScript counterparts the
extension uses to type-check webview payloads. There is no sync mechanism; a field
added on one side and forgotten on the other causes silent runtime failures.

Check
-----
This script extracts the set of `*Response` DTO type names from the C# handler and
from the two TypeScript files and compares them. If a Response DTO exists in one
side but not the other, the script exits non-zero and prints the delta.

Scope note (v1)
---------------
Minimum-viable: compares top-level *Response type names only. Does not yet compare
field sets (casing differences between PascalCase C# properties and camelCase JSON
make a faithful field-level comparison non-trivial; deferred to v1.1 with a source
generator per the release plan).
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path
from typing import Set


REPO_ROOT = Path(__file__).resolve().parent.parent
CS_HANDLER = REPO_ROOT / "src" / "PPDS.Cli" / "Commands" / "Serve" / "Handlers" / "RpcMethodHandler.cs"
TS_TYPES = REPO_ROOT / "src" / "PPDS.Extension" / "src" / "types.ts"
TS_DAEMON_CLIENT = REPO_ROOT / "src" / "PPDS.Extension" / "src" / "daemonClient.ts"

# Pre-existing drift tolerated at v1.0.0 lock-in. These TypeScript types are currently
# inlined as ad-hoc Promise<{ ... }> return types in daemonClient.ts rather than named
# interfaces. Cleaning them up is tracked for v1.1 (see release plan workstream G3, which
# calls out a source-generator as the permanent fix). The allowlist prevents THIS check
# from blocking PRs until the source-generator lands; any NEW drift still fails CI.
#
# When a name is removed from this list it must also be removed from the C# side OR
# added to the TS side. Do not expand the list.
KNOWN_DRIFT_CS_ONLY: Set[str] = {
    "CustomApisAddParameterResponse",
    "CustomApisRemoveParameterResponse",
    "CustomApisSetPluginResponse",
    "CustomApisUnregisterResponse",
    "CustomApisUpdateParameterResponse",
    "CustomApisUpdateResponse",
    "DataProvidersGetResponse",
    "DataProvidersRegisterResponse",
    "DataProvidersUnregisterResponse",
    "DataProvidersUpdateResponse",
    "DataSourcesGetResponse",
    "DataSourcesRegisterResponse",
    "DataSourcesUnregisterResponse",
    "DataSourcesUpdateResponse",
    "MetadataAuthoringResponse",
    "MetadataDeleteResponse",
    "ServiceEndpointsUnregisterResponse",
    "ServiceEndpointsUpdateResponse",
    "WebResourcesGetModifiedOnResponse",
    "WebResourcesGetResponse",
    "WebResourcesListResponse",
    "WebResourcesPublishAllResponse",
    "WebResourcesPublishResponse",
    "WebResourcesUpdateResponse",
}
KNOWN_DRIFT_TS_ONLY: Set[str] = set()


# Matches lines like "public class FooResponse" or "public sealed record BarResponse"
CS_RESPONSE_RE = re.compile(
    r"^\s*public\s+(?:sealed\s+)?(?:class|record)\s+(\w*Response)\b",
    re.MULTILINE,
)

# Matches "export interface FooResponse" or "interface FooResponse" (non-exported internal DTOs
# in daemonClient.ts still count — they represent an RPC contract).
TS_RESPONSE_RE = re.compile(
    r"^\s*(?:export\s+)?interface\s+(\w*Response)\b",
    re.MULTILINE,
)


def extract_names(path: Path, pattern: re.Pattern[str]) -> Set[str]:
    if not path.exists():
        raise FileNotFoundError(f"Expected file not found: {path}")
    text = path.read_text(encoding="utf-8")
    return set(pattern.findall(text))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument(
        "--cs",
        default=str(CS_HANDLER),
        help="Path to RpcMethodHandler.cs (default: repo-relative).",
    )
    parser.add_argument(
        "--ts-types",
        default=str(TS_TYPES),
        help="Path to types.ts (default: repo-relative).",
    )
    parser.add_argument(
        "--ts-daemon-client",
        default=str(TS_DAEMON_CLIENT),
        help="Path to daemonClient.ts (default: repo-relative).",
    )
    parser.add_argument(
        "--verbose",
        "-v",
        action="store_true",
        help="Print every extracted DTO name on each side.",
    )
    args = parser.parse_args(argv)

    cs_names = extract_names(Path(args.cs), CS_RESPONSE_RE)
    ts_names = extract_names(Path(args.ts_types), TS_RESPONSE_RE) | extract_names(
        Path(args.ts_daemon_client), TS_RESPONSE_RE
    )

    if args.verbose:
        print(f"C# DTOs ({len(cs_names)}): {sorted(cs_names)}")
        print(f"TS DTOs ({len(ts_names)}): {sorted(ts_names)}")

    raw_cs_only = cs_names - ts_names
    raw_ts_only = ts_names - cs_names

    cs_only = raw_cs_only - KNOWN_DRIFT_CS_ONLY
    ts_only = raw_ts_only - KNOWN_DRIFT_TS_ONLY

    # Allowlist hygiene: if a name was allowlisted but is no longer actually drifted,
    # flag it so the list gets pruned. A stale allowlist quietly shrinks coverage.
    stale_cs = KNOWN_DRIFT_CS_ONLY - raw_cs_only
    stale_ts = KNOWN_DRIFT_TS_ONLY - raw_ts_only

    if not cs_only and not ts_only and not stale_cs and not stale_ts:
        synced = len(cs_names & ts_names)
        tolerated = len(KNOWN_DRIFT_CS_ONLY) + len(KNOWN_DRIFT_TS_ONLY)
        print(
            f"OK: {synced} Response DTOs in sync "
            f"({tolerated} tolerated pre-v1.0 drift entries).",
            flush=True,
        )
        return 0

    print("RPC DTO drift detected!\n", file=sys.stderr)
    if cs_only:
        print(
            "  Present in RpcMethodHandler.cs but missing in TypeScript "
            "(types.ts or daemonClient.ts):",
            file=sys.stderr,
        )
        for name in sorted(cs_only):
            print(f"    - {name}", file=sys.stderr)
    if ts_only:
        print(
            "\n  Present in TypeScript but missing in RpcMethodHandler.cs:",
            file=sys.stderr,
        )
        for name in sorted(ts_only):
            print(f"    - {name}", file=sys.stderr)
    if stale_cs or stale_ts:
        print(
            "\n  Allowlist is stale — these names are no longer drifted and must be "
            "removed from KNOWN_DRIFT_CS_ONLY / KNOWN_DRIFT_TS_ONLY:",
            file=sys.stderr,
        )
        for name in sorted(stale_cs | stale_ts):
            print(f"    - {name}", file=sys.stderr)
    print(
        "\nAdd the missing DTO on the other side or rename so both files agree. "
        "The RPC contract must stay in lockstep.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
