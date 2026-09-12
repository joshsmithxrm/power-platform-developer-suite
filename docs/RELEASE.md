# PPDS Release Operations

Tool-neutral reference for release scope analysis, release operations,
strong-name rotation, key custody, and incident response. Automation may advise
what should be released, but a maintainer must approve every tag and publish.

## Release Scope Analysis

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

The repository's optional `.claude/skills/release/SKILL.md` documents the same
ceremony for supported agents; this public document remains the authoritative,
tool-neutral entry point for generated GitHub issues.

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
| `<DelaySign>true</DelaySign>` + `<PublicSign>true</PublicSign>` | csproj | Yes |

**Public-only signing for local builds.** csproj files declare
`<PublicSign>true</PublicSign>` so local `dotnet build` succeeds against the
public key alone. CI overrides this at pack time with the real keypair to
produce signed release assemblies.

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

## CI-Automated Decode (Routine Release Flow)

On every NuGet publish, `.github/workflows/publish-nuget.yml` decodes the
`PLUGINS_SNK_BASE64` secret into a runner-temp file and points MSBuild at
it for the pack step. No human action is required.

The flow is roughly:

```yaml
# Excerpt from .github/workflows/publish-nuget.yml
env:
  PLUGINS_SNK_BASE64: ${{ secrets.PLUGINS_SNK_BASE64 }}
run: |
  SNK_PATH="$RUNNER_TEMP/PPDS.Plugins.snk"
  echo "$PLUGINS_SNK_BASE64" | base64 -d > "$SNK_PATH"
  echo "PLUGINS_SNK_PATH=$SNK_PATH" >> "$GITHUB_ENV"

# Then pack invokes MSBuild with:
#   /p:AssemblyOriginatorKeyFile="$PLUGINS_SNK_PATH"
```

The temp file lives in the runner sandbox and disappears at job end. There
is no persistence to the runner image, the artifact bundle, or any cache.

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
