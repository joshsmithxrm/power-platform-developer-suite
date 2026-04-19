# Docs Release Runbook

Step-by-step for cutting a docs release — triggers the auto-generated reference update in ppds-docs.

Governing spec: [`specs/docs-generation.md`](../../specs/docs-generation.md). Generators and helpers: [`scripts/docs-gen/README.md`](../../scripts/docs-gen/README.md).

## Prerequisites (one-time)

- **GitHub App provisioned** — `ppds-docs-bot` installed on both `ppds` and `ppds-docs` repos. App ID + private key stored as `PPDS_DOCS_APP_ID` and `PPDS_DOCS_APP_PRIVATE_KEY` secrets in the ppds repo. See the generator README § "GitHub App setup" for the full procedure.
- **Phase 0 baselines current** — every library in `src/PPDS.{Dataverse,Migration,Auth,Plugins}/` has a non-empty `PublicAPI.Shipped.txt` and an `Unshipped.txt` that either is empty or reflects the net-new public API since the last release.

## Release steps

### 1. Pre-flight check

```bash
# No open rollover PR from a prior tag
gh pr list --state open --search 'in:title "chore(release):" "baseline rollover"'

# Should return empty. If not, merge or close the prior PR first (AC-27).
```

```bash
# No Unshipped drift on main
for proj in Dataverse Migration Auth Plugins; do
  test -s src/PPDS.$proj/PublicAPI.Unshipped.txt && \
    echo "WARN: $proj has accumulated public API since last release"
done
```

If any library has accumulated public API in `Unshipped.txt`, that's expected — the release rollover PR will move those entries to `Shipped.txt`.

### 2. Tag and push

```bash
git checkout main
git pull origin main
git tag v1.1.0   # use the appropriate semver
git push origin v1.1.0
```

The tag push triggers `.github/workflows/docs-release.yml`.

### 3. Monitor the workflow

```bash
gh run watch --exit-status
```

Expected steps (in order):

1. Check open prior rollover PR (AC-27) — fails fast if violated
2. Build `PPDS.sln -c Release` into `artifacts/bin`
3. Run all four generators against the built assemblies
4. Compute surface-change summary (added / removed / modified public API vs the previous tag)
5. Acquire GitHub App installation token (via `scripts/docs-gen/app-token/`)
6. **ppds-docs PR:** create branch `release/v1.1.0-ref-{run_id}` in ppds-docs, push generated markdown, open PR titled `chore(reference): regenerate for v1.1.0`
7. **Rollover PR:** in this repo, move Unshipped entries to Shipped, open PR `chore(release): v1.1.0 baseline rollover`

### 4. Review the ppds-docs PR

- Docusaurus CI should have auto-built a preview — click through a handful of generated pages
- Check the PR body's surface-change summary against your expectations
- The smoke test reusable workflow (`docs-smoke.yml`) runs automatically if ppds-docs is configured to call it — confirms fenced C# blocks still compile against the tagged product assemblies

### 5. Review and merge the rollover PR (in this repo)

- The rollover PR diff should show only `PublicAPI.Shipped.txt` additions and `PublicAPI.Unshipped.txt` resets to `#nullable enable`
- Merge this PR **first** — it resets the baseline so future releases have a clean diff
- **Then** merge the ppds-docs PR — docs go live via Docusaurus/GitHub Pages deploy

## Dry-run

To validate the pipeline without opening real PRs:

```bash
gh workflow run docs-release.yml -f dry_run=true
gh run watch --exit-status
```

The workflow executes steps 1–4 normally, uploads the generated output + PR body + rollover diff as a workflow artifact, and exits. Download the artifact from the run page to inspect.

Use this when:
- Testing a change to a generator's output shape
- Verifying the App token is working before a real release
- Spot-checking a surface-change summary before cutting the tag

## Recovery

### Release workflow fails mid-run

- **Before App-token step:** safe to re-run after fixing the underlying issue. No cross-repo state mutated.
- **After App-token but before PRs opened:** re-run; tokens are short-lived (auto-rotate).
- **After ppds-docs PR opened, before rollover PR opened:** manually close the ppds-docs PR, re-run the workflow. The branch name includes the `run_id` suffix so there's no collision.
- **Both PRs opened but one failed to push commits:** treat as a normal PR — push fixes manually; no workflow re-run needed.

### Surface-change summary looks wrong

- Verify `compute-surface-summary.sh --since-tag vPREVIOUS` locally against the same tag. The script diffs `PublicAPI.Shipped.txt` contents between refs.
- If the summary still looks wrong, the baseline may have drifted — inspect `PublicAPI.Shipped.txt` on main vs the tag.

### Smoke test fails on the ppds-docs PR

- The fenced C# block references product surface that changed or was removed.
- Fix the sample in the docs PR to match the new surface, OR restore the removed surface if the removal was unintended.
- Do **not** skip the smoke check — that's the entire point of Phase 4.

## References

- Spec: [`specs/docs-generation.md`](../../specs/docs-generation.md)
- Generator README: [`scripts/docs-gen/README.md`](../../scripts/docs-gen/README.md)
- Release workflow: [`.github/workflows/docs-release.yml`](../../.github/workflows/docs-release.yml)
- Smoke workflow: [`.github/workflows/docs-smoke.yml`](../../.github/workflows/docs-smoke.yml)
