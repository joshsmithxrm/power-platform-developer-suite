# PPDS Release Operations

Tool-neutral reference for release scope analysis, release operations,
strong-name rotation, key custody, and incident response. Automation may advise
what should be released, but a maintainer must approve every tag and publish.

NuGet restore, build and pack run in `build-package` without private signing keys
or OIDC permission. Non-Plugins packages continue directly to full release-input
validation. Plugins packages are delay-signed with the public key, admitted to a
fresh `sign-plugins` worker, and verified/repackaged by `verify-signed-plugins`
without the key. A fresh `publish` job downloads only the selected final package
artifact. Before login, an isolated
standard-library verifier checks the immutable event source, successful producer
job/attempt, artifact service identity/digest, raw manifest/package hashes,
package identity, current tag target and public version vacancy. It never loads
assemblies or invokes MSBuild. Only then may the pinned login and NuGet pushes run.
An individual publisher-job rerun cannot reuse an earlier attempt's build artifact;
this intentionally fails closed rather than mixing attempts.

The Plugins production policy remains **UNSELECTED**. Its early gate fails before
the secret-bearing step, and the publisher independently requires the accepted
source policy. No private-key operation or real Windows signing compatibility is
established by the offline fixture tests. Activation requires a reviewed producer
assurance choice, accepted signer binary hash/version, real compatibility receipt,
and separate authorization for key use and release. Signature validity does not
prove that a compromised producer built trustworthy source.

## Release closeout ledger

Keep one current JSON ledger under the integration owner's control. Use
[`release-ledger.example.json`](release-ledger.example.json) as the record shape;
private approval messages and local evidence paths belong in the private release
workspace, not in a public example. Freeze the candidate commit/tree and included
scope before final qualification. Preserve earlier receipts rather than editing
them to make a newer status look consistent.

```bash
python scripts/ci/release_ledger.py validate /path/to/release-ledger.json
python scripts/ci/release_ledger.py status /path/to/release-ledger.json
python scripts/ci/release_ledger.py reuse /path/to/evidence.json /path/to/target-binding.json
```

`validate` checks declared consistency. A valid ledger can contain blocked,
unstarted, stale or unknown gates; exit zero is not release readiness. `status`
generates the dated report from that same record. `reuse` compares one evidence
record with the proposed source/input, toolchain, definition, platform and claim
bindings. Exit 0 means the declared scoped evidence matches, 1 means it needs
refreshing, and 2 means the input is invalid. The tool does not fetch sources,
authenticate approvals, hash the actual inputs or prove that the declared input
set is complete. The integration owner must verify those against the receipts.
An unchanged relevant-input digest can preserve a product result across an
ancestry change; exact-head merge checks remain separate.

Each gate names its source, owner and next action and distinguishes a user hold,
repository policy, technical blocker or recommendation. Keep the original
approval's scope and source reference; a summary cannot broaden it. Any new gate
must name the concrete failure, affected deliverable and smallest sufficient
remedy. Classify new work as safe-delivery blocking, promised acceptance,
separable maintenance or future enhancement before adding it to frozen scope.
Mark `required_for_release` explicitly; optional work and denied out-of-scope
actions must not silently become additional release prerequisites.
For a companion repository, set that gate's optional `candidate` to its exact
repository, commit and tree. Its evidence and review must match that target;
core receipts cannot be relabeled as companion evidence.

Parallel workers own bounded preparation lanes. One integration owner adopts
their work, verifies source identity and updates the ledger. Independent reviews
record their claims, source/tree, assumptions, findings, open prerequisites and
nonclaims. A delta-only review cannot clear an older unresolved prerequisite.
Offline plugin evidence does not establish persisted behavior; a synthetic
signing model does not establish a production signer.

During iteration, run focused checks for changed inputs. At integration, run the
applicable regression checks; qualify the full applicable native matrix on the
frozen candidate. Reuse unchanged receipts only for their original claims and
record what invalidated evidence before repeating an expensive run. Retry only
eligible failed jobs, keeping source, run, attempt and artifact identity together.
Measure first-pass success, duplicate gate runs per source head, queue time and
failed-job retry latency from actual workflow records; do not infer improvement
from a green aggregate badge or from test counts alone.

Release documentation edits select Python contracts without rebuilding native
artifacts. Changes to actual native inputs or their workflows still select the
complete five-native/four-VSIX matrix. For final artifact qualification, the
read-only `Test` workflow accepts manual dispatch on the selected ref and binds
checkout/provenance to its resolved commit. It also runs Python contracts; it
does not establish full .NET or Extension-suite qualification. Its distinct
`Native candidate qualification` check and concurrency group preserve automatic
required checks and active runs. This prepares a route; it does not dispatch it.
A reused artifact keeps its original compiled Git/MinVer identity.

The pre-merge gate preserves an active evaluation when a draft becomes ready
because readiness is not a rule input. Title/body/base edits and source
synchronization still cancel obsolete evaluations. Every resulting run evaluates
both rules; the default pending slot can coalesce events. This reduces one source
of cancellation and does not cache or substitute a previous required check.

## Release Scope Analysis

### NuGet trusted publishing preparation

The proposed publisher uses pinned official NuGet/login and a temporary key for
both package and symbol uploads. The login user is **JoshSmithMSFT**, confirmed
by Josh as the **individual policy creator** profile. The step-local nonsecret
`NUGET_PUBLISH_USER` guard uses that same identity; no repository variable is needed.
NuGet's token service searches policies by their creator; a package owner may
be an organization and is not necessarily the login user. Missing or malformed
identity fails before login. There is no stored-key fallback.

On November 2, 2026 Josh supplied **user-reported** evidence of an active
`PPDS-GitHub-Actions` policy with package owner **JoshSmithMSFT**, new-version-only
push scope, and these exact package IDs: PPDS.Auth, PPDS.Cli, PPDS.Dataverse,
PPDS.Mcp, PPDS.Migration, PPDS.Plugins and PPDS.Query. Its GitHub tuple is
`joshsmithxrm/power-platform-developer-suite`, workflow `publish-nuget.yml`.
This is not an independent policy inspection or a tested token exchange.
Josh subsequently confirmed the individual creator username as JoshSmithMSFT.
Earlier public package-owner metadata named joshsmithxrm; that does not establish
the newer policy owner's package authorization. That ownership/authorization
discrepancy and actual login/upload remain unverified; an Active policy with
matching subjects alone is not proof it can publish these packages. Keep the
exact seven IDs; do not broaden them.

The workflow currently has no GitHub environment. Keep the matching policy's
environment blank unless a separately reviewed coordinated environment change
is approved. Only the upload-only `publish` job requests `id-token: write`; public
verification remains read-only. The permission is available to every step in
that job, so it remains upload-only: no restore/build/pack or signing runs there.
The artifact-integrity handoff checks the producer and its output before login.
The reviewed tag, workflow, verifier, runner and build inputs remain trusted.

Keep source/dependency/signing/vacancy/public-artifact gates and sequential
publication verification. New source must contain this workflow; retrying an old
immutable tag still executes its historical secret-based publisher. Actual
token exchange and package/symbol uploads are untested by offline checks and
require separate authorization. Trust/identity settings and final publication
approval remain separate from preparing this change. Do not remove old secrets,
create trust settings or invoke login as part of local validation.

Primary references: [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing),
[individual creator binding](https://github.com/NuGet/NuGetGallery/blob/main/src/NuGetGallery.Services/Authentication/Federated/FederatedCredentialService.cs#L396),
and [official login action](https://github.com/NuGet/login).

Before preparing CHANGELOGs or choosing versions, generate an explained scope
advisory from the exact commit range:

```bash
python scripts/ci/release_plan.py \
  --base <base-commit> \
  --head <release-commit> \
  --release-kind patch \
  --channel stable \
  --format markdown
```

Use `--release-kind minor` or `major` for coordinated releases, and
`--channel prerelease` for a prerelease plan. The command is read-only: it does
not create or push tags, publish packages, or dispatch a workflow.

The advisory deliberately separates five concepts:

1. **Direct product changes** — publishable runtime/package inputs that changed.
   Deterministic documentation, specification, test, fixture, CHANGELOG, and
   comment-only changes in modeled MSBuild XML are excluded unless a file is
   declared as a packed package input in a project. Packed README/icon assets
   are product inputs for every package that includes them. C# and arbitrary
   XML remain product changes because comment-looking text can be a raw string
   or mixed-content value. Uncertain product-source changes are included
   conservatively. Dot-directories such as `.github/` remain intact during path
   normalization and are explained as automation/tooling changes. Repository-wide
   .NET build inputs (`global.json`, `NuGet.config`, `.editorconfig`, and shared
   MSBuild props/targets) apply to every .NET package. NuGet package IDs are
   matched case-insensitively. A central package version
   change is scoped to its consumers only when no simultaneous central-management
   setting changed; mixed or unclassified central changes apply to every .NET
   package.
2. **Internal build changes** — non-publishable projects that changed, such as
   analyzers. These nodes are never release targets themselves, but their
   publishable consumers are.
3. **Downstream deliverables** — publishable projects that consume a directly
   changed project or internal build node. Project dependencies, including
   build/analyzer references with `ReferenceOutputAssembly="false"`, are
   discovered from MSBuild `ProjectReference` XML; the Extension-to-CLI bundle
   relationship is declared in `scripts/ci/release_surfaces.json`.
4. **Same-commit delivery tag prerequisites** — unchanged bundled deliverables
   whose publisher resolves content from an exact tag on the release commit.
   In particular, an Extension-only release requires a `Cli-v*` tag on that
   commit or `extension-publish.yml` stops before bundling.
5. **Same-commit MinVer tag prerequisites** — unchanged library dependencies
   that need a stable tag on the release commit. For example, a stable Query or
   Migration plan includes Dataverse as a prerequisite. Without that tag,
   MinVer derives an `alpha` version and NuGet rejects the stable package's
   prerelease dependency.

The latest-tag section uses strict SemVer 2.0 ordering with ASCII digits only.
Stable versions outrank
prereleases of the same version, numeric identifiers compare numerically
(`beta.10` after `beta.2`), build metadata does not affect precedence, and
malformed tags are surfaced as diagnostics instead of silently winning a git
refname sort.

For a patch, review the proposed direct and downstream targets plus any delivery
or MinVer tag prerequisites. A coordinated minor or major intentionally plans all release
surfaces, even when some have no user-facing change. Update each target's
CHANGELOG, create tags only after review, push tags individually, monitor every
publish workflow, and verify the public artifacts before closing the release
record.

### Documentation for per-product releases

Per-product tags such as `Cli-v*` and `Extension-v*` do not trigger
`docs-release.yml`, which listens for unified `v*` tags and explicit dispatch.
For a coordinated patch, record an explicit docs dispatch against the final
reviewed release ref; do not invent a unified version tag just to trigger docs.
After the applicable release/docs approval, select a co-located product tag
whose peeled commit equals the reviewed release SHA and dispatch, for example:

```bash
gh workflow run docs-release.yml --ref <reviewed-product-release-tag> -f dry_run=true
```

Review the generated artifacts, then separately authorize the `dry_run=false`
dispatch that opens the paired docs and API-rollover PRs. Check the existing
rollover-PR gate and review both outputs, including an expected no-op rollover
when no unshipped API entries exist. Recapture companion skills against the
same final CLI/MCP versions. A successful product publish alone does not prove
that docs or skills were refreshed.

### Patch release records

`post-merge-release-check.yml` evaluates a merged PR when `release:patch` is
already present at merge time or when the label is added later. Each generated
issue contains a stable marker derived only from the PR number. Reruns and label
removal/re-addition search both open and closed `release:patch` issues for that
marker, so an unlabeled issue cannot spoof a workflow-owned audit record and an
existing labeled record is neither duplicated nor reopened. Runs are serialized
per PR to keep simultaneous close/label events from racing; different PRs retain
independent records.

The workflow uses `pull_request_target` so merged PRs from forks receive the
base repository's issue-write token. Its trust boundary is intentionally narrow:
every executable repository script comes from `github.workflow_sha`, checkout
credentials are not persisted, and the original merge commit is a separate
data-only checkout used for semantic diff, MSBuild XML, and its contemporaneous
delivery manifest. No code, action, or script from that historical checkout is
executed. If the old revision predates the manifest, the trusted workflow
revision supplies a fallback; project references that did not exist historically
are mapped conservatively to all historical build nodes. This keeps old merges
analyzable without substituting a moving `main` graph or silently under-reporting.

Current checkout actions reject fork PR merge SHAs even after the PR is merged.
The two patch-job checkouts therefore set `allow-unsafe-pr-checkout` only when
the event confirms the PR is merged into `main`. The automation checkout needs
the same gate because `github.workflow_sha` can equal that accepted merge SHA.
This opt-in does not permit open-PR execution or change the data-only boundary.

Workflow-owned release labels are declared in
`scripts/ci/release_labels.json`. Release workflows reconcile all four labels
from that manifest before use and stop with an explicit setup error if GitHub
does not permit the create/update operation. Patch issue titles and bodies are
built by `scripts/ci/patch_release_issue.py`. PR titles are treated as untrusted
text, and issue bodies reach GitHub through `--body-file` rather than shell
interpolation.

The repository's optional `.claude/skills/release/SKILL.md` documents the same
ceremony for supported agents; this public document remains the authoritative,
tool-neutral entry point for generated GitHub issues.

## CLI/Extension Co-release Reconciliation

The VS Code Extension bundles the CLI selected from an exact `Cli-v*` tag on
the Extension release commit. The automated guard therefore treats the
relationship as satisfied only when a strict-SemVer `Extension-v*` tag and the
highest stable strict-SemVer `Cli-v*` tag point to the same commit. It does not
compare tag timestamps or the two independent version numbers.

Both CLI and Extension tag pushes trigger one serialized reconciliation. If the
CLI arrives first, the workflow may create an advisory issue containing a
stable marker keyed to that CLI tag. When the co-located Extension tag arrives,
the workflow comments on and closes the matching open issue. If the Extension
arrives first, the later CLI event observes both refs and remains silent. A
higher stable CLI release closes older open alerts as superseded and creates at
most one new alert for the current CLI when its co-located Extension tag is
still missing. The replacement is created before the old alert closes, so a
partial GitHub failure cannot remove the only actionable warning. Closed
workflow records are not reopened.

Alerts created before CLI-keyed markers were introduced are migrated from the
workflow's exact legacy `Latest stable Cli tag` table field. The workflow-owned
label is still mandatory, so unrelated issues containing similar prose do not
participate in reconciliation.

The comparison uses peeled commits, so annotated and lightweight tags behave
the same way. CLI prereleases do not require a Marketplace refresh; malformed
CLI or Extension release tags are reported as diagnostics and cannot outrank a
valid tag. This automation only manages advisory issues. It never creates a tag,
publishes an artifact, or invokes a release workflow.

The weekly cadence check similarly validates release-looking tags through the
shared strict SemVer implementation, discovers valid package tag prefixes from
the release graph, and normalizes git's ISO-strict timestamps and the current
time to UTC before calculating elapsed weeks.

## Automated Public Artifact Verification

Every publish workflow finishes by validating the artifact through the same
public channel an end user receives it from. The checks use no publishing
credentials and do not modify release state:

- NuGet publishes are polled through nuget.org, then restored or installed in
  a clean temporary directory whose only package source is nuget.org. A
  `PPDS.Cli` or `PPDS.Mcp` tool release is also executed with `--version`.
- CLI GitHub Releases must expose all five platform binaries plus
  `checksums.sha256`. Every downloaded binary is hashed, and the public Linux
  binary is executed to confirm its version.
- Extension publishes are verified only after all four Marketplace matrix
  jobs complete. The verifier downloads the public `win32-x64`, `linux-x64`,
  `darwin-x64`, and `darwin-arm64` VSIX packages and checks extension identity,
  package version, runtime target, and bundled CLI version.

Availability checks run at two-minute intervals for at most 30 attempts. A
timeout, missing target, checksum mismatch, or version mismatch fails the
publish workflow. Treat that failure as a release incident: stop and escalate
for investigation. Automation must never unpublish, delete, deprecate, replace,
or otherwise attempt to roll back an artifact that has reached a public feed.

The shared implementation is `scripts/ci/verify_public_release.py`; its tests
inject HTTP and process adapters, so CI policy tests never contact production
distribution channels.

Downloaded tools and binaries execute only in fresh follow-on jobs with
read-only repository permissions. Checkout credentials are not persisted, and
publishing secrets are confined to the preceding publish jobs.

## Strong-Name Keys

PPDS strong-names its assemblies. The key custody model is:

| Artifact | Where it lives | Tracked in git? |
|----------|----------------|-----------------|
| `*.PublicKey` files (e.g. `src/PPDS.Plugins/PPDS.Plugins.PublicKey`) | Repo | Yes |
| `*.snk` private keypair | GitHub Actions secret `PLUGINS_SNK_BASE64` | **No — never commit** |
| Public signing default; separate release delay-sign overrides | Project/workflow | Yes |

**Public-only signing for local builds.** csproj files declare
`<PublicSign>true</PublicSign>` so local `dotnet build` succeeds against the
public key alone. Plugins release preparation selects `PublicSign=false` and
`DelaySign=true` for both build and no-build pack. These modes are mutually
exclusive. The private key is used only by the isolated post-build signer.

### Why the .snk is sacred

The assembly's `PublicKeyToken` is derived from the keypair. Every consumer
of `PPDS.Plugins`, `PPDS.Dataverse`, `PPDS.Migration`, etc. binds against
the existing `PublicKeyToken`. Rotating the keypair changes the token,
which is a **SemVer breaking change** for every downstream consumer — they
must rebuild against the new identity.

For this reason:

- The `.snk` is treated like a production secret.
- Regenerating it is an incident-response procedure, not a routine release task.
- A **PreToolUse hook** (`.claude/hooks/snk-protect.py`) blocks Claude from
  writing or editing any `.snk` file. Bypassing it requires deliberate
  intent (delete the hook, or disable the matcher in `.claude/settings.json`).

## Isolated Plugins signing preparation

`plugin-signing-policy.json` starts inactive. The workflow must not be activated
by changing a boolean without the recorded acceptance and key-use authorization.
The implemented assurance mode is a reviewed controlled producer; requiring an
independent reference rebuild would need a real additional lane and its evidence.

The signer checks the exact admitted source/run/attempt, public key and tool bytes.
Only its signing step receives `PLUGINS_SNK_BASE64`; the value is never exported to
`GITHUB_ENV`. It signs the already-built single-module net462 DLL with Microsoft's
`sn.exe -R`, removes its fresh temporary key file, then checks `-vf` and `-D` before
writing a receipt. No MSBuild, restore, pack or package code runs in that worker.

A separate worker verifies the actual signature again, checks the signature-only
assembly change and full public key, and replaces only the nupkg's exact Plugins
DLL member. Other member bytes and the snupkg remain unchanged. The original
full release-input guard runs before a schema-2 handoff binds the build, signer
and verifier artifacts/jobs, exact bytes and chronological ordering. Failed
Plugins verification cannot fall back to publishing the keyless build artifact.

Offline tests exercise real ZIP and PE-header handling with controlled byte
fixtures and mocked metadata/tool calls. They are not real cryptographic or
hosted runner evidence. AC-64 remains pending until a separately authorized
representative delay-sign/sign/verify/package check passes. The initial tool
identity and compatibility receipt are unset, and no production key is needed
to prepare or review this source.

## Manual Strong-Name Rotation (Incident Response Only)

Rotate the keypair only when one of the following is true:

- The `.snk` has been disclosed (committed, leaked, exposed in a log).
- A signing-algorithm migration is required (e.g. SHA1 -> SHA256, already done).
- A planned major-version bump where breaking the assembly identity is
  acceptable and announced to consumers.

**Never rotate as a routine cadence.** Each rotation breaks every consumer.

### Procedure

1. **Inform consumers ahead of time.** A rotation is a SemVer major bump
   for affected packages. Coordinate with the next planned release.

2. **Generate the new keypair.**

   ```bash
   # On a workstation with .NET SDK installed.
   sn -k PPDS.Plugins.new.snk

   # Verify the new public key.
   sn -p PPDS.Plugins.new.snk PPDS.Plugins.new.PublicKey
   sn -tp PPDS.Plugins.new.PublicKey
   ```

3. **Update the public key in the repo.**

   Replace `src/PPDS.Plugins/PPDS.Plugins.PublicKey` with the new public
   key file. Verify any csproj `<AssemblyOriginatorPublicKey>` references
   point at the new file. Commit the change to a `feat/strong-name-rotate`
   branch.

4. **Update the GitHub Actions secret.**

   ```bash
   base64 -w0 < PPDS.Plugins.new.snk
   # Copy the output. Set it as PLUGINS_SNK_BASE64 in:
   #   GitHub repo settings -> Secrets and variables -> Actions
   ```

5. **Securely destroy the old keypair.**

   ```bash
   shred -u PPDS.Plugins.new.snk      # also the new one once it is in the secret
   shred -u PPDS.Plugins.old.snk      # any locally cached copy
   ```

   Workstation copies should never persist past the rotation.

6. **Bump major versions** for all packages that re-sign with the new key.
   This is mandatory — a `PublicKeyToken` change is a binary-incompatible
   change.

7. **Run the release skill** (`/release`) to publish the new majors with
   the rotation noted in CHANGELOG.

8. **Post-release verification.** Confirm `sn -T <published.dll>` shows
   the new `PublicKeyToken` matching `PPDS.Plugins.new.PublicKey`.

### Why a hook, not just a runbook

Past incidents (and the ppds-prelaunch retro) found that
"please-don't-do-X" instructions in CLAUDE.md were ignored under stress.
The PreToolUse hook (`snk-protect.py`) makes accidental regeneration
mechanically impossible — an agent attempting to write a `.snk` file
hits exit code 2 and a rationale message pointing back at this doc.

For the rare valid case (a deliberate rotation), the operator removes the
matcher entry from `.claude/settings.json` for the duration of the
rotation, performs the steps above, and restores it. This is friction by
design.

## Related

- Routine release ceremony: `.claude/skills/release/SKILL.md`
- Hook implementation: `.claude/hooks/snk-protect.py`
- CI workflow: `.github/workflows/publish-nuget.yml`
- Public artifact verifier: `scripts/ci/verify_public_release.py`
- Hook tests: `tests/test_snk_protect.py`

## Distributed native runtime

Native GitHub CLI executables and the four platform-specific VSIX payloads select
net10.0, self-contained/single-file with compression. Libraries and CLI/MCP tool
packages retain their existing 8/9/10 framework assets; Plugins retains net462.
The runtime switch adds no separate runtime install requirement for native/VSIX
users. .NET 8/9 servicing ends November 10, 2026; compatibility assets do not extend
Microsoft support. [Lifecycle](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/).

Distribution builders pin SDK 10.0.115 in the existing feature band, and candidate
validation requires runtime 10.0.15 or a later stable 10.0 patch. This addresses
[CVE-2026-109475](https://github.com/dotnet/runtime/security/advisories/GHSA-3g4v-f5gv-c7xf),
the Linux/macOS managed HttpListener denial of service. The
[servicing release](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md)
maps that SDK to runtime 10.0.15. Rebuild and verify the self-contained artifacts;
updating a user's installed runtime does not update their bundled runtime.
Earlier artifact results using 10.0.14 do not qualify the rebuilt candidates.

The Linux native ABI floor changes from glibc 2.23 to2.27. VS Code already requires
2.28. An ABI-compatible system is not necessarily a supported distro. The current
Microsoft 8/10 Windows/macOS support rows match; do not present macOS14's absence
as a new migration-only drop. Debian12 differs between those tables, with an
ambiguous future-dated out-of-support entry in .NET 10; confirm support before promising
that distro. Existing linux-x64 artifacts are not musl/Alpine packages. Extension
support intersects its four targets, the CLI runtime and VS Code requirements.
[Runtime matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md),
[VS Code requirements](https://code.visualstudio.com/docs/supporting/requirements).

PR/main Test checks build four candidate VSIX archives on matching native hosts,
inspect the actual archived binary and execute local extraction plus a bounded
private-stdio heartbeat. Five native CLI lanes verify the same runtime baseline;
Linux also executes the actual payload in a network-disabled, read-only UBI8
glibc 2.28 container with fixed synthetic identity/private tmpfs. The independent
net8 cache probe and net462 fixtures remain accurately labeled. Candidate outputs
are retained as CI artifacts; no publisher workflow or Marketplace token is used.

Candidate evidence records actual compiled MinVer/source, RID, runtime pack,
runtimeconfig/deps inputs and binary/VSIX hashes without forging a stable tag.
The existing ppds.version.json schema identifies RID/version, not CLR version.
Final immutable tags, signing, package/source/version guards and patched runtime
pack review still apply before publication. A net10.0 TFM alone is not proof of
the currently serviced patch or of every supported OS. Foreign-RID packaging is
not native execution evidence.

The harness alone invokes hidden `serve --quiet`: that explicit command/flag
combination skips the entire startup diagnostics block before status-file,
update-cache or background NuGet access. Default daemon launchers and other
commands retain their behavior. Heartbeat calls only `_heartbeat`; lazy auth DI
and the empty pool manager do not resolve profile/credential/Dataverse services.
That zero-provider boundary is source-reviewed; passing runtime observations
must be recorded separately. No real OS-profile/status-file fixture is required.
