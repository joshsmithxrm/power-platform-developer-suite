# Approved net48 validation lane

Temporary, unmerged validation branch for PR #1450 with companion #1469 (AC60).
The reviewed runtime is commit 49f09af81328cdfe62e5e570a7ef03186db183a0;
its src tree is d199398b25b74dee8e9bc473953059f656d0d418. This harness changes no
product runtime source. PR #1472 remains the separate read-only preflight lane.

The user approved one bounded package create/update/execute test and cleanup.
The original opened-event run is complete and failed in its harness readback.
The current workflow permits only cleanup on the first synchronization from
the original harness head, on this exact PR and branch, on attempt 1.
Do not merge, rerun, release, or reuse this lane.

The checked-in packages are the exact previously reviewed SDK-built net48
fixtures. Both versions preserve package/assembly identity; their marker bodies
differ. Fixture source is retained for review. The workflow does not rebuild or
publish these packages.

Before writes the runner rechecks the target/application binding, Developer
organization identity, and package/type/assembly/account collisions. The
read-only PowerShell preflight is copied without changes from PR #1472.
Authentication uses the existing GitHub federation provider and an in-memory
profile. No saved profile, secret, credential, or environment setting changes.

The test calls the actual PluginRegistrationService.UpsertPackageAsync for
direct package creation. It invokes the actual DeployCommand.ExecuteAsync for
configuration dry-run and deployment, using its service-provider test seam
to supply the same guarded registration service without creating an auth
profile. This exercises command implementation, not CLI argument parsing.

A pool/client proxy checks every outgoing mutation against the approved
phase, exact attributes, fixture hash, and recorded IDs. It rejects other
tables, settings, solution association, batch requests, raw HTTP, cloning,
unexpected children, images, and secure configuration. Both account creates
are checked before transport and read back for the version-specific marker.
Package and step IDs must remain stable across update.

Create IDs are journaled before sending; ambiguous transport failures retain
known cleanup IDs. Cleanup runs in finally with its own time budget and uses
the production package unregistration service. Only the two recorded account
IDs and the recorded package with verified descendants can be deleted.
Unexpected descendants stop package cleanup. Final reads check recorded IDs
and bounded resource names for absence. Cancellation/runner loss can still
interrupt cleanup; encrypted checkpoints retain the last known IDs for
review. No automated second mutation run is authorized.

Detailed checkpoints use AES-256-GCM with an RSA-OAEP-SHA256 wrapped key.
Only the public key is checked in. The private key remains in the local
review evidence directory and is never sent to CI. Hosted artifacts contain
ciphertext plus pass/fail and cleanup status; no plaintext detailed report.

Offline validation:

    dotnet build scripts/ci/net48-smoke/Net48Smoke.csproj -c Release
    dotnet scripts/ci/net48-smoke/bin/Release/net8.0/Net48Smoke.dll --self-test scripts/ci/net48-smoke
## Cleanup continuation after the original run

Run 40997090146 created and materialized the package, assembly, and plugin type.
The harness then attempted to decode a content attribute Dataverse did not
return on retrieve. Its cleanup check repeated that readback error. No step
or account was created, and update/execution were not reached. This is a
harness failure, not evidence that net48 execution is unsupported.

The cleanup-only entry point uses ID hashes derived from the original encrypted
journal. It verifies target identity, resource names/version, creation and
modification times, exact package/assembly/type ID hashes, and no steps or
accounts before enabling deletes. Only those original IDs enter the guard.
The production package unregistration service performs cleanup; final reads
verify those IDs and names are absent. It does not require package content
readback and has no deployment or account-create path. Resource IDs remain
in encrypted evidence; only their hashes are committed.

The workflow contains only the cleanup command. Its synchronize event is bound
to the original head through the webhook before field, defined in the official
[Octokit webhook schema](https://github.com/octokit/webhooks/blob/main/payload-schemas/api.github.com/pull_request/synchronize.schema.json).