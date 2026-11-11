# Independent Plugins rebuild: proposed control boundary

The agreed private-key isolation is already present in core `3be21b1eccac401c231ae28f667979e63d2c491e`: restore/build/pack receive no private key, a separate minimal job signs the admitted delay-signed DLL, and a keyless verifier checks the signature and signing-only/package transformation. Activation and real compatibility remain false. The producer policy currently supports `REVIEWED_CONTROLLED_PRODUCER`, not an independently authenticated reference build.

This proposal adds a separately controlled reference build and comparison. It does not claim that running this repository's comparison script in another job makes that job independent. It also does not claim that exact byte agreement proves code safety. Reproducibility means matching the specified outputs under defined inputs; it does not establish who controlled either build. [Reproducible Builds definition](https://reproducible-builds.org/docs/definition/)

## Recommended architecture

| Domain | Responsibility | Required separation |
| --- | --- | --- |
| A: existing GitHub producer | Produce candidate delay-signed DLL, portable PDB, XML documentation, nupkg and snupkg. | No private signing key. Candidate artifacts and producer assertions are untrusted inputs to reference admission. |
| B: independent reference controller | Accept the externally approved source/input/recipe manifest; run a fresh, keyless Windows build sandbox; collect outputs and provenance outside that sandbox. | A's repository administrators, automation identities, runner sessions and credentials cannot change B's controller, image, accepted inputs, collection logic or result identity. Repository build logic gets no controller credentials or result-attestation key. |
| C: independent adjudicator | Authenticate B's result, compare all required bytes, accept the exact candidate or reject it; bind acceptance to source, request, lifetime and candidate hashes. | Policy, verifier implementation and trusted result identity are anchored outside A. Neither a producer-authored JSON file nor a hash supplied by the producer establishes this authority. |
| D: minimal signer and release enforcement | Consume only exact independently accepted input, then perform the existing signing and post-signature/package checks. | If protection includes compromise of A's administration, A must not be able to bypass this acceptance, replace its verifier/policy, or obtain key use for other bytes. |

B and C may share a separately administered controller, provided the build sandbox cannot alter C or impersonate the collector. They must not share mutable build caches, working directories, privileged sessions or tokens with A. A clean second Windows job in the same GitHub repository is only a second execution. A self-hosted runner registered under A's control is also insufficient if A can dispatch arbitrary privileged work to it.

Current D remains under the repository/GitHub administrative domain. A genuinely separate B can provide useful externally recorded detection, but an A administrator could bypass an ordinary in-repository gate. Enforced independent release assurance therefore requires an independently controlled acceptance/key-use gate or separately controlled release authority. This is an infrastructure and trust decision, not a boolean source setting. Source-only preparation must report this limitation rather than silently grant an independence claim.

## Exact keyless input contract

Use one immutable request for A and B. The external owner accepts it through a channel A cannot rewrite. Bind:

- Repository identity, exact commit and tree; SHA-256 of the complete source/history bundle and tracked-file inventory; submodule/LFS content if any; clean checkout requirement. Bind Git filters, attributes, line-ending conversion and actual materialized bytes. Exclude untracked build inputs: the project enables `EmbedUntrackedSources`. Include the packed README and icon. The full reachable history and an exact allowed-ref map matter because MinVer reads Git tags and ancestry. Do not trust an abbreviated checkout or a tag label alone.
- Requested package/version and intended release tag metadata, without creating or pushing a tag during preparation. Record the exact permitted history/ref state and expected informational version. Never invent a reference tag to make a build look like a released source.
- Recipe bytes and the independently accepted recipe SHA-256. Pin commands, order, working directories, all MSBuild properties, environment allowlist, locale, timezone, encoding, source-path mapping, package inventory and output paths. Read producer recipes as reviewed source; do not execute candidate-provided commands as admission logic.
- Exact Windows image identity, architecture, installed servicing state, SDK/host/runtime/compiler/MSBuild/NuGet/Git versions and hashes, reference assemblies, all imported build props/targets, analyzers/source generators, SourceLink and MinVer inputs. An image label or version string alone is not an immutable input identity.
- A complete dependency closure with each original nupkg digest and trusted acquisition source, plus the accepted restore lock and assets/input inventory. Build using a fresh cache and approved local feed. Disable fallback/user feeds, ambient package caches and network access during build; no on-demand restore or package download in the reference run.
- Exact public-key bytes already in the repository, `PublicSign=false`, `DelaySign=true`, and no private key input. Confirm the reserved signature is empty and the expected public identity is present before comparison. This is a keyless build, not a signing compatibility experiment.
- A unique request identifier, issue/expiry timestamps, producer run/attempt/artifact identity, candidate manifest and archive digests, expected reference controller, accepted policy version and exact required output inventory.

The current workflow uses `windows-latest`, `8.0.x/9.0.x/10.0.x`, `global.json` patch roll-forward and an ordinary restore. `LangVersion=latest` also takes its meaning from the selected compiler. Those are not sufficient pins for this new contract. `NuGet.config` clears inherited package sources, but does not by itself freeze transitive bytes. No Plugins `packages.lock.json` was present at inspection. Establish and independently review the dependency closure before claiming a reproducible reference run. [NuGet lock-file guidance](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies)

## Proposed launcher sequence

The machine-readable recipe template is an inactive specification, not an executable launcher. A later reviewed implementation should perform the following sequence only after the external controller and inputs are selected:

1. Verify request authentication/expiry, controller policy and all input digests before materializing a fresh sandbox. Refuse missing, unknown or conflicting fields. Inventory ambient environment/configuration rather than inheriting user settings.
2. Materialize the exact Git source/history/ref snapshot and a digest-verified local dependency feed. Select the pinned SDK directly; refuse roll-forward or a different compiler. Apply the same reviewed path mapping and deterministic properties to both builds.
3. Run project-scoped restore in locked mode against the local feed, then Release build with no restore and pack with no build. Preserve exact net462 public-key/delay-sign settings. Capture the evaluated build input/command inventory; fail if undeclared inputs or network dependencies are encountered.
4. Collect delay-signed DLL, portable PDB, XML documentation, complete nupkg and snupkg into a fresh output tree. The trusted collector reads bytes as data and emits the observed digest inventory. It does not load the DLL or trust a manifest written by the build process.
5. Compare exact inventory and bytes outside the sandbox. If a raw package archive differs due to metadata, record that as a failed comparison. Do not normalize, delete metadata, ignore PDBs or compare only IL to turn it into a pass. A reproducible packaging change requires a reviewed recipe used by both builds and a new candidate.
6. Seal the authenticated reference result and adjudication with the accepted request, inputs, complete output inventory, comparison result and expiry. Destroying a sandbox or changing infrastructure is separate operational scope; this source preparation performs neither.

Compiler determinism depends on the compiler, reference/input bytes, command-line order, paths and relevant environment. A `Deterministic=true` property alone is not proof of reproducibility. The recipe must address SourceLink and PDB source paths as well as the DLL. [Microsoft deterministic-build documentation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/code-generation#deterministic)

## Authenticated result and signing handoff

Recommended future result transport: a DSSE envelope over the exact versioned reference-result payload, signed by an existing independently controlled result identity, with its public trust anchor pinned in C/D's separately controlled policy. The signature key ID is only a hint; trust is established by verification against accepted key material and controller identity. Use a maintained verifier, not custom cryptography. An independently authenticated service result can replace this transport if its exact issuer, audience, subject, replay controls and verifier are pinned. No key or credential is created, read or used in this preparation. [DSSE protocol](https://github.com/dsse-lf/dsse/blob/master/protocol.md)

The payload must bind the accepted request hash/nonce, exact source/tree/history/recipe/toolchain/dependency identities, controller and sandbox identities, completion time, full observed output inventory, candidate inventory, and comparison result. The adjudicator rejects expiration, replay to another candidate/run/tag, missing/unrecognized outputs, wrong controller or signature, changed policy and incomplete inputs. A claimed `independent=true` field or two unequal domain labels cannot prove independence. [SLSA verification guidance](https://slsa.dev/spec/v1.2/verifying-artifacts)

On acceptance, bind the exact keyless input DLL hash and entire accepted package/symbol inventory into signing admission. The signer receives only that DLL and the authenticated admission, never build scripts or an arbitrary file path from candidate data. Recheck immediately before key use. Preserve existing post-signature checks, unchanged-package-member checks and final publisher handoff. Add the reference/adjudication digest to the release chain; a later policy/recipe/input change invalidates the old acceptance. These workflow changes remain unimplemented and inactive until the boundary and real authentication mechanism are selected.

The offline comparator being prepared is deliberately weaker: it can establish local byte equality and contract consistency, but cannot authenticate an external controller or grant independent-rebuild/release clearance. Its result must keep `independent_rebuild_verified=false` and `release_authorized=false`, even when every comparison passes.

## Tests and acceptance evidence

Offline contract tests should reject altered DLL/PDB/XML/package bytes, extra or missing files, unsafe/duplicate archive members, wrong source/tree/recipe/input hashes, stale request or nonce, expired lifetime, same-domain declarations, and substituted candidate/reference bundles. Test exact package-to-DLL/PDB linkage so an equal loose DLL cannot conceal a different package payload. All positive fixtures remain simulation evidence, not actual reference-build proof.

After external infrastructure selection, separately review and test result-authentication failures and the actual key-use enforcement boundary. A real rehearsal must build the same declared source independently, compare all specified outputs and retain the authenticated result. Deliberately changed candidate bytes must be rejected before signing admission. An unexplained mismatch remains failed/incomplete. No real rehearsal or signing is authorized by this document.

Even accepted matching outputs can contain malicious reviewed source, a shared compromised compiler or dependencies. Independent reproduction reduces producer-output substitution risk; it is not a code audit or proof of safety. [SLSA source/build-integrity distinction](https://slsa.dev/spec/v1.2/threats-overview)

## The precise remaining decision

The controlled-build assessment is accepted and remains the engineering default. For the first independent reference experiment, designate an existing separately controlled Windows host/service, its responsible operator, and the authority that owns its accepted recipe and result identity. Use a disposable keyless build sandbox and collect the reference result outside that sandbox. Do not assume Josh-PC or any existing service satisfies that boundary without evidence.

The first experiment provides independently recorded detection evidence while signing/publishing remain under the existing GitHub administration. It cannot claim enforcement against compromise of that administration. Only if independent acceptance is separately selected as a mandatory condition before production key use must an additional independent key-use authority and enforcement boundary be designated. That stronger requirement is not a new release gate introduced by this preparation. No new credentials, permissions, machine provisioning, signing-key custody changes or external-service enrollment are selected or performed here.

The inactive external-controller policy template describes that optional stronger architecture. Its `require_external_admission_before_key_use=true` field is not a runtime policy selection or a requirement for the first detection experiment.
