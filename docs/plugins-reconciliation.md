# Registration compatibility and reconciliation

This change restores established Update clears and adds explicit registration
file version 2.0. It does not activate CI, publish packages or add image pruning.
The plugin repair is separate from NuGet trusted-publishing adoption.

## Old/new/reason/compatibility ledger

| Area | R / initial candidate behavior | Revised contract and reason | Compatibility boundary |
| --- | --- | --- | --- |
| Update empty text | R wrote empty description/filtering/image attributes; the initial candidate dropped them | Preserve the exact empty SDK writes; null/absence remains preserve | Correction of the introduced regression, distinct from register placeholders |
| File opt-in | Version 1.0 was metadata and absent/null intent collapsed | Enforce missing/1.0 legacy semantics and explicit 2.0 reconciliation; reject unknown versions before connection | Existing files do not silently adopt omission resets |
| Declared defaults | Initial candidate mixed managed defaults with optional preservation | V2 explicitly manages supported nonsecret fields and documented defaults | V1 optional null/empty remains preserve; explicit CallingUser and false fixes remain |
| Model JSON | Initial type converters selected Deploy and erased secure input on any round trip | Generic model round trips keep input values and Patch intent; command/service codecs apply file version | Extraction/output uses explicit secure-redacting serialization |
| Public update records | Initial appended positional members replaced old constructors/deconstruction | Keep original eight/two-argument positional contracts; new fields are init properties | Old constructor and deconstruction signatures remain available |
| Provider interface | Initial user resolver added an abstract obligation | Default implementation handles CallingUser and refuses unverifiable explicit identities | Existing implementations can load; unsupported resolution is not fabricated |
| Register/RPC | Initial nullable mode/rank could omit result keys and change defaults | Keep established register defaults and non-null result fields; append new wire options after target/profile | Register placeholders and Update clears remain operation-specific |
| Diff | Partial comparison could appear clean; initial incomplete result used success=true | Keep data[] with explicit status/coverage, 0/1/2 exits, success=false when incomplete | Completed drift remains a completed comparison with exit 1; no global writer reinterpretation |
| Extraction | Existing optional overrides/reset lists could remain sticky | V2 fresh defaults are authoritative; v1 retains its deployment overlay | Explicit --registration-version selects migration; absence keeps the existing/new-file contract |
| Secure input | Non-null including empty is an existing write-only request | Preserve that write path; null/absence preserves; comparison is unknown when supplied | No equality, secret reset/deletion, plaintext/hash output or new secret authority |
| Verification | SDK acknowledgement could be mistaken for stored convergence | V2 real deployment reads back through the nonsecret comparison; drift/incomplete is non-success | Dry-run is preview-only; failures retain effects, without rollback |
| Extra registrations | Existing clean deletes orphan steps and their current children | Extra images remain drift; v2 rejects --clean before connection and no independent image deletion is added | V1 clean retains its existing scope; neither empty images nor v2 opts into pruning or proves parent cascade safety |
| System selector | Documentation advertised a registration shorthand not implemented at R | CallingUser and explicit valid users remain registration choices | Runtime CreateOrganizationService(null) SYSTEM behavior is separate plug-in code |
| Publisher/version train | Initial candidate also bundled OIDC and replacement library notes | Keep that reviewed source/evidence separate | No trust activation, token exchange, release tags or version publication here |

Defaults are PPDS contract defaults, not inferred platform defaults. In particular
canBeBypassed=true is deliberate; read-only and applicable async auto-delete default
false. All-attributes images and cleared filtering can broaden workload. Review the
versioned plan before execution. The comparison does not cover assembly/package
bytes, solution membership, Custom APIs or unrelated environment state.

## Bounded future image-prune design — not implemented

Independent orphan-image removal requires its own destructive-behavior adoption.
Recommended boundary: exact target URL/tenant/organization, assembly/type/step IDs,
and an immutable explicit set of image IDs created by this deployment or adopted
by the operator after inventory review. Names, prefixes, IsManaged, an upsert ID,
or an earlier empty query do not establish ownership.

A future preview should identify every proposed image ID and parent, preserve all
unowned/hidden/ambiguous entries, distinguish omitted from authoritative empty
collections, and bind the reviewed plan to source/config and target. Apply should
re-read scope, refuse a changed/unknown plan and delete only approved image IDs,
with bounded cancellation, acknowledged/unknown outcomes and final readback.
Conditional-delete support must be established from the target's real capability;
it cannot be assumed. No automatic retries after uncertain submission.

This does not authorize parent-step/type/assembly deletion. Their cascades can
reach children added after a client check. Parent rowversion checks and transaction
grouping do not by themselves prove an atomic exact-child-set guarantee. A stronger
server mechanism or an approved exclusive disposable environment would need its
own proof and lifecycle decision. Current fixture ownership/cascade limitations
remain unresolved, and no live prune validation is part of this preparation.

Before implementing prune, decide: which existing IDs may be adopted, how changed
inventory is handled, what conditional delete the platform supports, and whether
the target is shared or exclusively owned. The recommendation is owned leaf IDs
only, no broad --clean expansion, and fail-closed unknown scope.
