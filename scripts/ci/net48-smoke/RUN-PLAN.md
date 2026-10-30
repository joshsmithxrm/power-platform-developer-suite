# Prepared net48 completion run

This is a reviewable local plan, not authorization or evidence of a new live
run. The coordinator owns the next remote initiation and any required human
decision. The worker must not push this commit, dispatch or rerun a job, or
make another live request without that initiation.

## Exact proposed initiation

After reviewing the prepared commit and applicable approval, push that exact
commit to the existing personal feature branch `codex/net48-approved-smoke`
for draft PR #1474. The remote branch must still be at
`7af9468d1d195d68d2b5c25fdd0c6b91cc7cf01e`; stop if it differs.
The PR synchronize event must have that exact `before` SHA, be for PR #1474
and this same-repository branch, and be run attempt 1. There is no dispatch
entry point, no second run on later pushes, and no run on manual retry.

The prepared workflow is `.github/workflows/net48-approved-smoke.yml`.
Its only live entry point is `--approved-live`; it does not call the prior
cleanup-only continuation. Concurrency cannot cancel an in-progress cleanup.
No merge, release, package publication, or protected-branch write is included.

## Fixed inputs

The runtime remains the reviewed #1450 + #1469 combination:
`49f09af81328cdfe62e5e570a7ef03186db183a0`, with `src` tree
`d199398b25b74dee8e9bc473953059f656d0d418`. This is distinct from the
prepared harness commit and from the combined release-candidate head.

The exact existing SDK-built net48 packages remain unchanged:

| Fixture | SHA-256 |
| --- | --- |
| v1 | `98db0d59c3624e2ff98b453d1b4fe54fcd042808f8bf2361b34129a6dc2dbaab` |
| v2 | `0f6a266ff244f63840171ae71f52925efbbcbb8729bbacb273a21d276c7f33a8` |

Use the same hash-bound Developer environment, existing `test-dataverse`
CI identity, and isolated Windows runner. Target/application/tenant/organization/
environment bindings remain unchanged in the existing preflight code.
Authentication uses GitHub federation and an in-memory profile. No profile,
credential, environment setting, schema, solution, or tracing change is included.

The scope remains package `ppds1450_61e24a05` version `1.0.0), assembly
`Ppds1450Smoke_61e24a05`, plugin type `Ppds1450Smoke.MarkerPlugin`,
one enabled synchronous PreOperation step `MarkerPlugin: Create of account`
(rank 1, no images), and two accounts:
`PPDS1450_61e24a05_first` and `PPDS1450_61e24a05_second`.
Their required description markers are `PPDS1450_61e24a05_v1` and
`PPDS1450_61e24a05_v2`, respectively.

## Sequence and automatic previews

1. Build the harness and run all offline scope/fixture/readback/cleanup checks.
   Verify the pinned runtime tree and both fixture hashes.
2. Recheck target and application identity, Developer organization, and all
   bounded collisions. Stop if anything already exists. Confirm the current SDK
   caller matches, reserve fresh create IDs, and check those IDs are absent.
3. Run the actual CLI deployment dry-run. Require exactly one step creation,
   no images/deletions/warnings, and zero outgoing writes.
4. Create v1 through the actual registration service. The request boundary
   checks exact package attributes and bytes and records the ID before transport.
   Discover only the expected owned assembly/type. Read back identity/version.
5. Repeat the v1 CLI dry-run, then invoke the actual deployment command.
   This updates the same package with v1 bytes and creates its one step.
   Check the step identity/settings. Create the first account and require v1's
   execution marker.
6. Run the v2 CLI dry-run. Require exactly one step update and no extra changes.
   Deploy v2 to the same package/step, verify stable IDs and version, allow the
   existing 15-second propagation interval, then create the second account and
   require v2's execution marker.
7. Every mutation is checked immediately before transport against its phase,
   allowlisted attributes, exact fixture hash, and recorded IDs. Stop test work
   on any mismatch or failure; always enter the bounded cleanup path.
8. Cleanup uses its own 10-minute cancellation budget inside `finally`.
   Delete only the two recorded account IDs, then unregister the recorded
   package with verified owned descendants. Unknown children, images, secure
   configuration, or ownership changes stop package cleanup. Verify all recorded
   IDs and bounded resource names are absent, and preserve any residuals.

## Readback correction and cleanup independence

A missing package content value is recorded as `not-returned`; it is never
claimed as a successful byte readback. If content is returned, it must match the
expected hash. Outgoing package bytes are still checked and journaled before
transport, and package/step identity plus both execution markers remain required.

Cleanup does not request, decode, hash, or depend on package content. It checks
the recorded ID and name/version, discovers scoped descendants, then calls the
production unregistration service. Offline regressions exercise this actual
cleanup path against a transport that rejects any package-content read; they
also verify that ownership mismatches cause zero deletes.

Detailed evidence remains encrypted in the hosted artifact
`net48-corrected-smoke`, using the existing public key. The private key stays
local. The worker can authenticate/decrypt evidence locally and report only
permitted status/head/cleanup summaries. A successful result requires both
execution markers, stable package/step IDs, and final absence. No automatic
second mutation run is part of this plan.

## Preserved history

Original smoke run 40997090146 at harness head
`5d63a24d0ae4d3d1356f7c288d9b8b32155776b5` created/materialized the package
but stopped at the content-readback assumption. It created no step/account and
did not test update or execution. Cleanup run 41000961964 at
`7af9468d1d195d68d2b5c25fdd0c6b91cc7cf01e` passed: all three original
IDs were verified absent, with no create/update requests. Both journals remain
preserved. The earlier ordinary CLI diff convergence timeout remains historical
validation evidence; this preparation does not erase or rerun it.