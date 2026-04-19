# Merge Policy

How PRs get merged into `main`. Operational reference for maintainers.

## Auto-Merge

Auto-merge is **enabled at the repo level** (`allow_auto_merge: true`). It does not bypass branch protection or skip required reviews — it only removes the "human clicks merge when CI goes green" step.

Enable auto-merge on a PR with:

```bash
gh pr merge <num> --squash --delete-branch --auto
```

### When to use auto-merge

Use auto-merge when **green CI is sufficient evidence to ship**. Specifically:

- Dependabot tooling/test bumps (eslint, knip, vitest, esbuild, @types/*, @playwright/test, Microsoft.NET.Test.Sdk)
- Dependabot patch-level bumps on non-critical paths
- GitHub Actions group bumps
- Lockfile-only changes
- Documentation-only PRs that have completed review
- Bot PRs targeting non-critical paths where CI fully exercises the change

### When NOT to use auto-merge

Do not use auto-merge when the diff requires human judgment beyond CI signal:

- Anything touching `src/PPDS.Auth/**` or crypto code paths
- Major version bumps on core libraries (Terminal.Gui, Microsoft.Identity.Client, Azure.Identity, Microsoft.PowerPlatform.Dataverse.Client)
- Plugin assembly changes that could interact with the strong-name signing key (`PPDS.Plugins.snk`)
- Changes to `Directory.Packages.props` that pin or unpin security-sensitive packages (e.g., `System.Security.Cryptography.Xml`)
- PRs with manual verification steps in their runbook (e.g., "spot-check `npm run package`", "run TUI snapshots locally")
- Release-branch changes
- Changes to CI/CD pipelines, branch protection rules, or release automation
- Anything where the reviewer wants to inspect runtime behavior, not just diffs

When unsure, default to manual merge. The cost of waiting is low; the cost of an unwanted merge is high.

## Grouped Dependabot PRs

Dependabot can roll multiple updates into a single grouped PR (titled `Bump the <group> group ...`). Member updates may span multiple update types (e.g., a group containing both patch bumps and a minor bump).

Classify a grouped PR by **most-conservative-wins** — the whole group inherits the most conservative classification of any member:

- Group contains **any major bump** -> Group C (manual review required)
- Group contains **any minor bump** (no majors) -> Group B (verify-then-merge)
- Group contains **only patch bumps** -> Group A (auto-merge eligible)
- If any member is an auth-critical package (per the Auth-Critical Packages list below), the group is Group B regardless of update type, unless any member is major (then Group C).

Reasoning: simplest to enforce, hardest to misinterpret, and consistent with the universal-exclusion-of-major-bumps decision from the v1-prelaunch retro. If a single risky member exists, the whole group gets the safer treatment — splitting the group to land safe members earlier is not worth the complexity.

The classifier in `scripts/dependabot/classify.py` implements this rule by parsing the dependabot PR body (which lists each member bump as `Updates <pkg> from <a> to <b>`) and selecting the most-conservative type seen.

## Auth-Critical Packages

The following packages are flagged as **auth-critical** — bumps to them at any non-major version go through Group B (verify-then-merge), and major bumps go through Group C (manual review). This list is the **single source of truth**; the classifier in `scripts/dependabot/classify.py` mirrors it as `AUTH_CRITICAL_PACKAGES`, and a unit test (`TestAuthCriticalDocCodeDrift.test_doc_list_matches_code_list`) fails if the two diverge.

<!-- AUTH_CRITICAL_PACKAGES:BEGIN -->
- `Microsoft.Identity.Client`
- `Microsoft.Identity.Client.Extensions.Msal`
- `Azure.Identity`
- `Azure.Core`
- `Microsoft.PowerPlatform.Dataverse.Client`
- `System.Security.Cryptography.Xml`
- `System.Security.Cryptography.Pkcs`
<!-- AUTH_CRITICAL_PACKAGES:END -->

To add or remove a package: edit the list above (between the `BEGIN`/`END` markers), update `AUTH_CRITICAL_PACKAGES` in `scripts/dependabot/classify.py`, and re-run `pytest tests/scripts/dependabot/`. The drift test will confirm the lists match.

## Branch Protection

`main` is protected. PRs must:

- Be up to date with `main` before merging (auto-merge handles this via dependabot rebases or `gh pr update-branch`)
- Pass all required status checks
- Have any required reviews approved

## Pre-Merge Gate Rules

The [`pre-merge-gate.yml`](../.github/workflows/pre-merge-gate.yml) workflow enforces three rules on every PR before merge. All three are **enforcing from day one** — there is no soft-warn period (per the v1-launch retro). Each rule is its own job in the PR Checks tab; the aggregate `Pre-Merge Gate` check is what branch protection should require.

Rule logic lives in `scripts/ci/`. Bypass markers are **case-sensitive** in the PR title or body — a deliberate copy-paste is required, not a casual mention.

### Rule 1 — PR size (`scripts/ci/check_pr_size.py`)

Blocks merge when:

- Changed files > **50**, OR
- Total LoC (additions + deletions) > **2000**

Bypass: `[size-waived: <reason>]` in the PR title or body. The reason must be non-empty (whitespace-only is rejected). Use sparingly and explain why review of a large diff is acceptable (e.g., vendored 3p code, codegen output, mechanical rename).

Catches the failure mode from PR #792 (131 files / 7.5K LoC merged unreviewed).

### Rule 2 — Workflow secret-ref drift (`scripts/ci/check_workflow_secrets.py`)

For any PR that touches `.github/workflows/*.yml` or `*.yaml`:

1. Parses every changed workflow file.
2. Extracts every `${{ secrets.X }}` and `${{ vars.X }}` reference.
3. Compares against the actual repo's secret/variable inventory (`gh secret list --json name`, `gh variable list --json name` — names only, never values).
4. Blocks merge if any referenced name is missing.

`GITHUB_TOKEN` is built-in and always considered present. Repo-level inventories only — environment-scoped secrets are not enumerated by default; if your workflow uses one, bypass it.

**App token requirement.** Rule 2 requires the `ppds-pre-merge-gate` GitHub App installed with `secrets:read` and `variables:read`. The default `GITHUB_TOKEN` cannot enumerate repo secrets/variables (HTTP 403 — Resource not accessible by integration), so the workflow mints a short-lived App token via `actions/create-github-app-token@v1` for this rule only. App ID is stored at `vars.PPDS_GATE_APP_ID`; the private key (full `.pem`) is stored at `secrets.PPDS_GATE_APP_PRIVATE_KEY`.

Bypass: `[secret-ref-allow: <NAME>]` in the PR title or body, repeated for each missing name. Use for legitimate cases such as secrets defined on a reusable workflow caller, environment-scoped secrets, or org-level secrets that the repo's `gh secret list` doesn't enumerate.

Catches the failure mode from PR #797 / ppds-docs#15 (`AUDIT_REPO_TOKEN` referenced but didn't exist).

### Rule 3 — Major-bump test enforcement (`scripts/ci/check_major_bump_tested.py`)

For dependabot PRs (label `dependencies` OR author `app/dependabot` / `dependabot[bot]`) whose title indicates a major-version bump (per `_BUMP_TITLE_RE` + `classify_update_type` from `scripts/dependabot/classify.py`):

- Requires the actual `test` job (not the path-filter `check-changes` skip-status) to have **run AND passed** in the PR's CI rollup.
- Blocks merge if `test` is `SKIPPED`, `FAILURE`, still pending, or absent.

**No bypass marker.** A major bump that didn't trigger the real test job is by definition unverified — fix the cause (push an empty commit, re-run all jobs, or expand path filters), don't wave it through.

Catches the failure mode from PR #806 (`vite 5 → 8` — two major-version jumps, auto-merged after only `check-changes` ran).

## Squash Policy

All merges to `main` use **squash merge** with branch deletion. Rebase merges are disabled. Use:

```bash
gh pr merge <num> --squash --delete-branch
```

This keeps `main` linear and gives one commit per PR for clean `git log` and `git bisect`.

## Dependabot Cadence

Dependabot config lives in `.github/dependabot.yml`. Bumps that meet the auto-merge criteria above can be `--auto` enabled in batch. Bumps that don't (auth, crypto, major core libs) get manual review with the verification steps documented in the relevant spec or commit message.

For roll-up branches consolidating many bumps into one PR: avoid unless auto-merge is unavailable. Per-bump squash commits give better `git bisect` granularity and clearer attribution than a single roll-up commit.
