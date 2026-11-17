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
application-data directory. Every constrained child environment supplies
`PROGRAMDATA` and `ALLUSERSPROFILE` for that directory in addition to the existing
OS/temp values. Machine-based Strong Name key containers require this Windows
context; dropping it made common-data resolution empty on the hosted image.
The coordinator refuses missing, different, non-local or linked directory
values before generating a key. No CSP configuration, permission, profile or
verification policy is changed. Old schema-1 plans must not be replayed.

Only the PREPARE `dotnet restore`, `build`, `pack`, `msbuild` and
`nuget verify` children also receive `APPDATA` and `LOCALAPPDATA` (NuGet fails
with a null-path error without them) and a fixed English CLI language. Both
folders are inside the driver's owned temporary directory. Git children, the
`sn.exe` children and every driver process the coordinator launches, including
the SIGN driver that handles the key, get neither; tests pin each set exactly.
The owned user-level NuGet configuration is written by the driver and only
clears fallback package folders. NuGet still reads `NuGet.Config` files in the
owned source copy and its ancestors and the machine-wide configuration under
`ProgramFiles(x86)`. The repository `NuGet.config` clears inherited package
sources; what restore actually used is checked afterwards, as below.

The VERIFY `packaged-metadata` child (PowerShell running the release
inspector, which itself launches `sn.exe -vf`) additionally gets
`PATHEXT=.EXE`. Without it PowerShell 7 does not treat the `.exe` as an
application: it starts it detached through the shell, without waiting and
without capturing its output or exit code. The call returns at once (even under
`$ErrorActionPreference = 'Stop'`, no command-not-found error was observed) and
`$LASTEXITCODE` stays `$null`. This was checked with a `cmd.exe` stand-in that
leaves a marker file: the marker appeared a few seconds after the call had
returned, and a test pins that behaviour. So a verification tool would run
unobserved, not be skipped. Hosted run r12 stopped there. The inspector now
clears `$LASTEXITCODE` first and treats an unset value, or a command-not-found
error, as "no exit code reported", failing with a fixed message that names a
missing `PATHEXT` as the likely cause (no environment contents are printed).
For its own fixed throws it also writes the first line of the message as a
`PPDS_INSPECTOR_MESSAGE=` line on stderr; the driver classifies only that line,
never PowerShell's rendering of the error.
No other child gets `PATHEXT`; a test pins that the only call site asking for
it is `packaged-metadata`.

### What PREPARE binds for build dependencies

For each package in `project.assets.json`, PREPARE requires and records:

- restore used no fallback folder and exactly one package folder, the owned
  one (a fallback folder is refused by name, not as a missing file);
- `.nupkg.metadata` `source` equals the single source declared in the owned
  copy's `NuGet.config` (`https://api.nuget.org/v3/index.json`);
- the assets `sha512` and the metadata `contentHash` are both base64 SHA-512
  and equal;
- the SHA-512 of the `.nupkg` file equals NuGet's `<name>.nupkg.sha512`;
- the pinned `dotnet nuget verify --all` succeeds for the archive, reports a
  repository signature whose service index is the source above (its certificate
  SHA-256 is recorded) and reports a content hash equal to the assets `sha512`.
  Only these parsed fields are kept; the raw verification output is not
  retained in the receipt. The first verification in each of the two builds
  may take up to 10 minutes (a fresh host may fetch revocation data); the
  others in that build are bounded at 2;
- a direct reference with a central version equals its `Directory.Packages.props`
  pin. The SDK's implicit `Microsoft.NETFramework.ReferenceAssemblies` has no
  repository pin. A transitive package with a central `PackageVersion` entry
  (for example `Microsoft.NETFramework.ReferenceAssemblies.net462`) records
  that entry and whether it matched; central package management does not pin
  transitive packages here, so a mismatch is recorded, not refused.

Reference assemblies: the SDK adds the implicit reference-assemblies package
only when MSBuild finds no installed .NET Framework 4.6.2 targeting pack
(`IncludeTargetingPackReference`, `Microsoft.NET.Sdk.FrameworkReferenceResolution.targets`).
Both outcomes are legitimate, and the publish workflow lets the image decide,
so PREPARE does too. It reads `TargetFrameworkDirectory` after
`GetReferenceAssemblyPaths` and records either the package (id and version,
bound by its signature row; the directory must be that package's folder) or
the installed targeting pack (its directory under `ProgramFiles(x86)` and the
hashes of its `mscorlib.dll` and `System.dll`). The receipt must carry exactly
one of the two; it must match the recorded `TargetFrameworkDirectory`, the
targeting-pack directory must be exactly
`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2`,
and the delay and public builds must record the same source. Every
`TargetFrameworkDirectory` entry must be the first one or its `Facades` folder.
For the package form SIGN and VERIFY also bind the first entry, lexically, to
`<PREPARE output>\temp\packages\<id>\<version>\build\.NETFramework\v4.6.2`,
taking `<PREPARE output>` from the parent of the recorded key-file overlay item
(they cannot see PREPARE's disk). The comparison is case-, separator- and
`..`-insensitive, as PREPARE's own. The other reference-assembly files in a
targeting pack are not hashed.

This proves that each archive the build resolved carries a valid repository
signature, chaining to a trusted root, over content whose hash the resolved
graph names; that the signer names nuget.org's service index; that the sidecars
agree; and that direct pins were honoured. The service index is an attribute
the signer writes, and with no `trustedSigners` any certificate chaining to a
trusted code-signing root is accepted: the certificate is recorded but not
pinned. It does not prove that a reviewer expected that hash for that
id/version (there is no lock file), or that the extracted files MSBuild loaded
(targets, task and tool DLLs) match the archive. The stronger follow-up is a
committed `packages.lock.json` restored in locked mode, with the same change in
the publish workflow; this experiment does not change the production build.

SIGN and VERIFY re-check what they can of these bindings when they admit the
prepare receipt: the recorded compiler paths must sit under the plan's dotnet
root, the overlay must equal exactly the one PREPARE builds from the plan and
its output directory, and VERIFY (which has its own source copy) requires the
recorded package source to equal the one in `NuGet.config`.

### Version properties

Production lets MinVer derive every version property from the `Plugins-v` tag:
`AssemblyVersion` `{Major}.0.0.0`, `FileVersion` `{Major}.{Minor}.{Patch}.0`,
`PackageVersion`/`Version` and `InformationalVersion`, to which the SDK appends
`+<SourceRevisionId>` from SourceLink. The owned copy has no `.git`, so PREPARE
passes `MinVerVersionOverride=<candidate>`: MinVer's own target still runs and
applies the production rule, and `SourceRevisionId` is supplied explicitly. The
recorded overlay must equal that exact list, so no explicit version property,
`MinVerSkip` or source-revision switch can be added in any switch form. PREPARE reads the properties after `GetAssemblyAttributes` runs and
checks the built DLL's assembly version, file version and product version in
PREPARE and again in VERIFY. The hosted version `0.0.0-compat.<run id>` gives
`0.0.0.0` under every rule; offline tests use non-zero versions. Not covered:
MinVer's tag discovery and height calculation, and SourceLink's repository
discovery.

The disposable test-key generator uses a 1024-bit AT_SIGNATURE RSA key with
`CspProviderFlags.CreateEphemeralKey`, exports the owned SNK with CreateNew,
clears the export buffer and disposes the RSA object. It does not configure or
use an existing machine key container. The pinned Strong Name tool still must
export the public identity and perform actual `-R`, `-vf` and `-D` operations.
This changes only disposable fixture generation; production key handling and
activation remain held. See [Microsoft's ephemeral-key contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cspproviderflags).

If PREPARE fails after admission, it attempts one constant-only `prepare-failed`
artifact bound to this run, attempt, source and experiment. A waiting signer
refuses that handoff and enters its existing original-key cleanup. The signal
can only abort; it cannot admit bytes or establish any PASS case. If signaling
also fails, the original bounded wait and cleanup remain. No failed job is
retried, and a subsequent experiment requires fresh hosts and a new identity.
Fixed phase/refusal codes distinguish transport from driver failures without
publishing captured command output, exception text, paths or runtime tokens.

Every constant refusal reason in the driver and the coordinator has its own
fixed code; a test parses both sources and fails if a reason is missing or two
share a code. Other driver refusals print `unregistered`; other exceptions print
only their class from a fixed list, otherwise `exception-other`. When a driver
stage fails after a tool call it also prints `PPDS_COMPAT_OPERATION=<name>`
(a fixed operation name) and `PPDS_COMPAT_STATUS=<exit code|timeout|launch-failure>`
for the last call. That is the last tool call recorded, which need not be the
cause: a refusal after a successful call reports that call with status 0.
`PPDS_COMPAT_OPERATION_INDEX=<n>` says which call of that name it was
(1-based, bounded integer; passed through as `driver_operation_index`). When
the failed call is `packaged-metadata`, the driver also classifies the release
inspector's fixed failure messages and prints
`PPDS_COMPAT_DIAGNOSTIC=<code>` (`inspector-sn-no-result`, `inspector-public-key`,
`inspector-framework`, ... or `inspector-unclassified`). Only the one anchored
`PPDS_INSPECTOR_MESSAGE=` line the inspector writes for its own throws is read;
the rest of the child's output is neither searched, printed nor stored. The
coordinator passes
through only registry codes, listed operation names, that status pattern, a
bounded index and the driver's fixed diagnostic codes, as `command_refusal`,
`driver_operation`, `driver_status`, `driver_operation_index` and
`driver_diagnostic` in its failure JSON, and VERIFY prints
a checkpoint after each download, the plan, custody and context checks, the
driver, the case check and the evidence upload.
