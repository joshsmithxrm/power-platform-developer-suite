# Independent rebuild comparison preparation

This source prepares a keyless input packet and compares pre-signing artifact bytes. It does not launch a compiler, authenticate a builder, use a key, or admit a release. It does not modify signing policy or workflows.

`python scripts/ci/verify_independent_rebuild.py prepare` measures six already supplied local files: `source_bundle`, `git_history`, `recipe`, `toolchain`, `dependencies`, and `settings`. Their SHA256 values must match a caller-pinned request. It does not establish that an archive contains the declared source, that history is complete for MinVer, or that those inputs were actually used by a builder. Both `prepare --help` and `compare --help` describe the required explicit paths and digest arguments.

`compare` checks the exact candidate/reference controller labels, invocation IDs, source commit/tree, input identities, request nonce and lifetime. It verifies measured artifacts against the two receipts and caller-pinned admission document. Same declared controllers, reused invocation IDs, future receipts, stale requests, incomplete inventories and altered pins are refused. Different controller names are only labels; they cannot demonstrate separate administration.

The output set is fixed to `PPDS.Plugins.dll`, `PPDS.Plugins.pdb`, `PPDS.Plugins.xml`, the versioned `.nupkg`, and the versioned `.snupkg`. Each directory must contain exactly those five files. DLL/PDB/XML bytes must match their corresponding package entries. Packages must bind the expected version/source commit, have safe unambiguous members and no unexpected executables. DLL checks inspect only PE/CLI headers, IL flags and the empty strong-name reservation; the PDB check is only its metadata signature. These are not complete CLR, target-framework, public-key, assembly-version or PDB/assembly identity checks. Existing actual signing/metadata/compatibility verification remains a separate obligation.

All five outputs are compared byte for byte through SHA256 and lengths. ZIP timestamps, order and packaging metadata are not normalized. An otherwise unexplained packaging difference remains a failed comparison. The synthetic tests do not establish that the real release is reproducible.

The request and admission documents are pinned by SHA256 supplied by the caller. This protects against changing those exact input bytes after the caller selected them. It does not authenticate where the pins came from. Producer-controlled receipt hashes or a `trusted=true` field cannot establish independence. Unknown schema fields are refused.

Every result has `independent_rebuild_verified=false` and `signing_admission=false`. A successful comparison reports `BYTE_IDENTICAL_COMPARISON_ONLY`; it also reports `controller_labels_authenticated=false`, `rebuild_executed=false`, and `source_or_history_semantics_verified=false`. Exit zero means only that the requested preparation/comparison operation succeeded. A byte mismatch emits `ARTIFACT_BYTES_DIFFER` and exits one. Structural refusals exit two without an acceptance receipt. Outputs are created exclusively, never overwritten.

The missing boundary is an independently administered reference build and comparison controller, an authenticated result channel anchored outside producer administration, and a key-admission gate the producer administrators cannot bypass. No implementation here supplies that boundary. See [the proposed control boundary](INDEPENDENT_REBUILD_DESIGN.md) for the external policy and concrete unresolved choices.

Inputs must remain stable during a run. Link/junction/hardlink checks and bounded regular-file reads reduce accidental substitution; they are not an adversarial filesystem isolation boundary. Run the eventual comparator from a reviewed, separately trusted copy. Repository-controlled code executed by a compromised producer can fabricate any result.

Tests use synthetic files and metadata only:

```powershell
python -m pytest tests/ci/test_independent_rebuild.py -q
```

No production packet is provided. Templates under `scripts/ci/independent_rebuild/` remain inactive; `request.template.json` is intentionally incomplete and fails validation until its exact real inputs are selected and pinned. Neither completing it nor invoking this helper grants authority to build, sign, connect, provision, publish, or change access.

The PE header routine is adapted with attribution from `scripts/ci/verify_plugin_signing.py` at PPDS commit `3be21b1eccac401c231ae28f667979e63d2c491e`; existing signing helpers and evidence are preserved.
