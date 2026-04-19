# PPDS Release Operations

End-user reference for release-related operations: cutting a release, the
strong-name rotation procedure, key custody, and incident response.

For the full release ceremony (CHANGELOGs, version bumps, tag push
sequence, CI monitoring), see the **release skill**:
`.claude/skills/release/SKILL.md`. This doc covers the rare operations
that fall outside the routine release flow.

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
- Hook tests: `tests/test_snk_protect.py`
