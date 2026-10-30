# GitHub Actions token permissions

Every job must resolve to an explicit `permissions` map on the job or its
workflow. Avoid `read-all`, `write-all`, and implicit repository defaults.
A job-level map replaces the workflow map; omitted scopes become `none`.

Build, test, docs smoke, Marketplace listing, TUI E2E, Python regression, and
reference-cache trust workflows use `contents: read`. Native extraction and
cache-trust jobs retain that read-only scope. Their `dorny/paths-filter` jobs additionally
need `pull-requests: read` to list changed files on PR events. Status-only
aggregators use `permissions: {}`. These workflows and the integration test
checkout set `persist-credentials: false`: their later steps do not push Git
changes. Public-history fetches do not require persisted checkout credentials.

The reusable docs-smoke caller must grant `contents: read`; a called workflow
cannot elevate its caller's permissions.

## Intentional elevated scopes

The regression suite inventories all jobs and permits only these existing
write grants. Adding another writer requires an explicit policy review and
an update to the test allowlist and this table.

| Workflow / job | Elevated scope | Purpose |
| --- | --- | --- |
| `codeql.yml` / `analyze` | `security-events: write` | Upload CodeQL results; retain its existing read scopes. |
| `dependabot-label.yml` / `label-major-updates` | `issues: write`, `pull-requests: write` | Apply labels to Dependabot PRs using trusted base automation. |
| `docs-release.yml` / `release` | `contents: write`, `pull-requests: write` | Push the primary repository's baseline rollover branch and create its PR. |
| `integration-tests.yml` / `integration-tests` | `id-token: write` | Existing federated authentication; repository contents remain read-only. |
| `milestone-release-check.yml` / `check-milestone-completion` | `issues: write` | Maintain release-tracking issues. |
| `post-merge-release-check.yml` / `patch-release-detection`, `extension-corelease-detection` | `issues: write` | Maintain release-tracking issues. |
| `release-cadence-check.yml` / `cadence-check` | `issues: write` | Maintain release-tracking issues. |
| `release-cli.yml` / `release` | `contents: write` | Publish the CLI GitHub release and its assets. |

Package publication, Marketplace publication, the docs companion PR, and audit
capture use their existing separate credentials. A `GITHUB_TOKEN` permission
map does not limit those credentials. Do not broaden the docs App installation:
its token remains scoped to `ppds-docs`.

## Repository default and rollout

The intended repository setting is `default_workflow_permissions: read`.
Merge explicit workflow maps first, then update any active branches that still
depend on inherited permissions before reducing the repository default. Verify
the setting by reading the Actions workflow-permissions API after changing it.
This document describes policy, not a live settings snapshot.

Keep `can_approve_pull_request_reviews: true` while `docs-release.yml` uses
`GITHUB_TOKEN` to create the primary rollover PR. GitHub's setting controls
both PR **creation** and approval; turning it off blocks that writer even
with explicit `pull-requests: write`. Neither docs writer approves a PR.
Issue #1231 remains partially addressed until a separately reviewed migration
allows this toggle to be disabled and the remaining writer validation is complete.

Do not test this migration by publishing a release or creating a probe PR.
Use nonempty offline fixtures with mocked Git/GitHub write boundaries for the
writer scripts. These checks verify script behavior, not live credential or
repository-policy enforcement.

## Validation

Run `python -m pytest tests/ci/test_workflow_permissions.py -q` for the token
policy, then `python -m pytest tests/ -q` for the existing regression suite.
The policy tests discover all workflow jobs, check the read-only validation
workflows and checkout credentials, and enforce the elevated-scope allowlist.
Hosted PR checks verify the paths-filter API access and normal CI execution.

References: [GitHub permission maps](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#permissions),
[the PR creation and approval setting](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-github-actions-settings-for-a-repository#preventing-github-actions-from-creating-or-approving-pull-requests),
and [paths-filter permissions](https://github.com/dorny/paths-filter#supported-workflows).
