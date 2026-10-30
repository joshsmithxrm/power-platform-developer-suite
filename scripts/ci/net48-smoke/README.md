# Approved net48 validation lane

Temporary, unmerged validation branch for PR #1450 with companion #1469 (AC60).
The reviewed runtime is commit 49f09af81328cdfe62e5e570a7ef03186db183a0;
its src tree is d199398b25b74dee8e9bc473953059f656d0d418. This harness changes no
product runtime source. PR #1472 remains the separate read-only preflight lane.

The user approved one bounded package create/update/execute test and cleanup.
The workflow runs only when this draft PR is opened, only on this exact
same-repository branch, and only on run attempt 1. It has no dispatch or
synchronize trigger. Do not merge, rerun, release, or reuse this lane.

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