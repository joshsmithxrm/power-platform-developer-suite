# Disposable hosted signing compatibility

The manual `plugin-signing-compatibility.yml` workflow runs the existing
`tests/compatibility/plugin_signing_compatibility.py` experiment across three
fresh standard GitHub-hosted Windows VMs. It tests the controlled-producer model:
reviewed source, bootstrap tools, GitHub administration and hosted infrastructure
remain trusted. It does not establish an independently administered rebuild.

The workflow has no automatic trigger. The
`admit_network_connected_disposable_signing` input defaults to false. Source
integration, ordinary CI and an authorization-reference string do not authorize
key use. Before dispatch, the execution owner must reconcile applicable approval
with these concrete effects:

- One disposable 1024-bit strong-name key is generated and used on SIGN only.
- SIGN is network connected while holding the key. It exchanges public data with
  the current run's artifact service; it is not an air-gapped signing context.
- PREPARE downloads public build dependencies. Only allowlisted public plans,
  candidate assemblies/packages and receipts reach Actions artifact storage,
  retained for seven days. Nothing is published to a package index or release.
- Original and temporary key copies must both be removed before a qualified
  signed handoff is uploaded. File absence does not establish secure-media
  erasure. Hard cancellation, timeout or runner loss can prevent cleanup; such a
  run cannot supply passing compatibility evidence. Hosted VM disposal remains
  provider trust. Never recover by retrying a signing step.

No production secret, repository/environment credential, OIDC grant, access
change, release tag, deployment or publication is part of the workflow.
`GITHUB_TOKEN` retains only `contents: read`. The official artifact client's
default current-run API uses the existing action runtime token. The adapter
does not select `findBy`, REST, another run/repository or `actions: read`.

## Execution contract

The workflow must be reviewed and available on the default branch before normal
manual dispatch. Select the reviewed branch and provide its full SHA as
`expected_source`, a safe experiment identifier and the applicable authorization
reference. The SHA must equal the actual selected workflow/source revision.
Attempt numbers other than one are refused before key generation. A new
experiment is a new execution requiring its own scope/state reconciliation.

Use `bootstrap_only: true` for a keyless host/tool admission diagnostic. It stops
both initial jobs after bootstrap and skips VERIFY; it never enters a signing,
preparation or verification stage. Fixed phase markers and bounded failure
categories expose progress and acknowledged key cleanup without raw exceptions,
commands, process output or key material. Missing cleanup markers remain unknown.

All pinned actions, locked npm dependencies and tool discovery complete before
SIGN creates its key. The source checkout remains clean; dependency staging and
all outputs are outside it. Each VM must present the exact same git, dotnet,
PowerShell and fixed NETFX 4.8 `sn.exe` identities sealed in the public plan.
Tool drift or unavailable tooling fails closed. The SDK/source identity is bound
at execution time, including servicing changes; earlier candidate evidence is
not substituted.

SIGN and PREPARE start concurrently with no `needs` edge. SIGN seals and uploads
the public plan, retains the private key locally, and waits at most 45 minutes
for PREPARE's public handoff. PREPARE waits at most 10 minutes for the plan.
SIGN admits the handoff against its original local plan, calls the existing
sign driver once and removes its original key in `finally`. VERIFY starts only
after both jobs succeed, using their completed artifact IDs and digests.

Public transport uses one raw JSON envelope per stage, with a run/attempt/source/
experiment binding, exact file inventory and content hashes. It validates the
raw artifact digest and every envelope member before creating the destination;
there is no ZIP extraction. The bounded adapter refuses duplicates, overwrite,
private-key filenames, links, traversal, stale bindings and mismatched digests.

Only a fresh VERIFY result containing all six real COMPAT PASS cases plus both
cleanup records qualifies this experiment. Mocked/offline tests and green
workflow wrappers are not signing compatibility evidence. Production signing
and release decisions remain separate.

## Offline qualification

`python -m pytest tests/ci/test_hosted_signing_compatibility.py
tests/ci/test_plugin_signing_compatibility.py` exercises admission, workflow
structure, failure cleanup and the compatibility driver's contracts. The first test
module also runs the dependency-injected Node transport/action tests. These
tests use fake public/"key" bytes and subprocess doubles; they never generate a
cryptographic key or contact Actions/Dataverse.

The transport package has an exact `@actions/artifact` version and lockfile.
Assess its advisories and the selected raw-transfer call paths before execution;
an npm install or a clean test run is not an advisory review. Do not apply a
suggested package downgrade without checking the required raw-transfer APIs.

The compatibility plan now uses schema 2 and binds the admitted Windows common
application-data directory. The constrained child environment supplies only
`PROGRAMDATA` and `ALLUSERSPROFILE` for that directory in addition to the existing
OS/temp values. Machine-based Strong Name key containers require this Windows
context; dropping it made common-data resolution empty on the hosted image.
The coordinator refuses missing, different, non-local or linked directory
values before generating a key. No CSP configuration, permission, profile or
verification policy is changed. Old schema-1 plans must not be replayed.

The disposable test-key generator uses a 1024-bit AT_SIGNATURE RSA key with
`CspProviderFlags.CreateEphemeralKey`, exports the owned SNK with CreateNew,
clears the export buffer and disposes the RSA object. It does not configure or
use an existing machine key container. The pinned Strong Name tool still must
export the public identity and perform actual `-R`, `-vf` and `-D` operations.
This changes only disposable fixture generation; production key handling and
activation remain held. See [Microsoft's ephemeral-key contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cspproviderflags).
