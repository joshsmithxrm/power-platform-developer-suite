# Merge Policy

How pull requests get merged into `main`, and who may merge which. Operational reference for the
maintainer and for the agent sessions that merge for him.

Whoever merges a pull request that may incorporate outside work completes the
[maintainer merge checklist](#maintainer-merge-checklist) first. Being one that an agent may merge
does not waive the checklist or grant communication permission.

## Who merges

Every pull request to `main` is in one of two tiers. Before an agent session merges a pull request,
it decides the tier from the diff, the labels, the issues the pull request closes and its author.
If the tier is unclear, the pull request is **maintainer first**. No fixed approval prompt stands
before a merge: this written rule is the guard. A release is the exception (category 11): the
release skill asks the maintainer before each release pull request merge. Tags and publications are
his own, and no session runs them ([VERSIONING.md](VERSIONING.md#9-decisions-in-documents), rule 9.2).

### Agents merge

An agent session merges a pull request that is not maintainer first, without asking, when all of
these hold:

- the review converged (R5);
- the required checks are green on the final commit;
- no review thread is open;
- there is no new code-scanning alert;
- it is a squash pinned to the reviewed commit:
  `gh pr merge <n> --squash --delete-branch --match-head-commit <reviewed sha>`.

Because of the fourth condition, the agent waits for the code-scanning result and then merges
itself; it does not use auto-merge, which does not wait for that result.

Dependabot pull requests follow the same rule. `scripts/dependabot/classify.py` sorts each one into
a group:

- **Group A (routine):** one short fresh review, then an agent merges it as above. This group
  includes patch bumps, lockfile-only changes, and tooling and test packages at any non-major
  version (`TOOLING_PACKAGES` and `TOOLING_PREFIXES` in the classifier).
- **Group B (routine):** the area's tests run first, then one short fresh review, then an agent
  merges it. One exception: a bump of an [auth-critical package](#auth-critical-packages) is
  maintainer first (category 5). After the area's tests and one fresh review, it waits for the
  maintainer's yes.
- **Group C (not routine):** the maintainer decides. Major bumps stay his under the existing
  universal exclusion of major bumps ([Grouped Dependabot PRs](#grouped-dependabot-prs)). A bump
  whose notes signal a breaking change stays his under the classifier's existing Group C rule. A
  reviewer who finds a breaking change the classifier did not detect treats the bump as Group C: its
  tier is not clear.
  Every GitHub Actions bump stays his as well (category 9). The agent comments on the pull request with the group and the classifier's reason,
  and puts it on the list of decisions waiting (D5).

A Dependabot pull request that falls in any maintainer-first category below is maintainer first,
whatever its group.

If `main` goes red after a merge, whoever made it, merging stops, for whoever merges, until it is
fixed or reverted. The fix or revert is merged under the same tiers as any change: an agent merges it when it is ordinary and its review converged, and it is the
maintainer's when it falls in his tier.

**Daily list.** At the end of its day (by UTC date), the coordinating session writes one comment on
the release tracking issue (for 2.0, #1812). It lists every pull request an agent merged that day.
For each one it gives the number and title, the reviewer's verdict (the converged round and the
commit it reviewed), and how to revert it (`git revert <squash sha>`). A day with no agent merge
has no comment.

### Maintainer first

No agent merges a pull request in any of the categories below, or enables auto-merge on it, until
the maintainer has answered for it. For categories 1 to 5, 7 and 8, a pull request is in the
category when its diff changes anything the category names, in any way, comments and renames
included. The one exception is in category 7: a change only to help text or a description that does
not say whether a write happens or is confirmed.

1. **Workflows and the rest of `.github/`.** This covers workflows, composite actions,
   `dependabot.yml`, and the pull request and issue templates. The agents' account cannot push
   anything under `.github/workflows/`. An agent prepares such a change as a patch file
   (`git format-patch`) for the maintainer to push. If a push is refused for this reason, the agent
   stops there and does not look for another way.
2. **Agent rules, settings and hooks.** This covers everything under `.claude/`, plus `AGENTS.md`,
   `CLAUDE.md`, `CONTRIBUTING.md`, `docs/MERGE-POLICY.md`, `docs/MAINTAINER-SESSION-RULES.md`,
   `docs/CONTRIBUTOR-LIFECYCLE.md` and `specs/test-gates.md`.
3. **Scripts that a workflow runs, or that decide what merges.** Today these are `scripts/ci/`,
   `scripts/dependabot/` and `scripts/docs-gen/`. A script in any other folder joins this category
   the day a workflow runs it.
4. **Signing.** This covers:
   - the plugin key files: `src/PPDS.Plugins/PPDS.Plugins.PublicKey` and any `.snk` file;
   - the properties `SignAssembly`, `PublicSign`, `DelaySign` and `AssemblyOriginatorKeyFile` in any
     `.csproj`, `.props` or `.targets` file;
   - the signing workflows and scripts, which categories 1 and 3 already cover.
5. **Sign-in and crypto code.** This covers `src/PPDS.Auth/`. Code elsewhere that encrypts,
   decrypts or stores credentials or tokens has no folder of its own. Whether a change to it is in
   this category is not clear, so it is maintainer first under the rule at the top of this section.
   This category also covers a Dependabot bump of an
   [auth-critical package](#auth-critical-packages) at any version: after the area's tests and one
   fresh review, it waits for the maintainer's yes.
6. **Data safety.** This covers a pull request labelled `data-safety`, and one that closes or fixes
   an issue labelled `data-safety`, label or not. The label means wrong data, lost data, an unguarded
   or wrong-target write, or a silently wrong result. A pull request that only reads is in this
   category through the label, or because it closes or fixes a `data-safety` issue, and not
   otherwise.
7. **Write paths.** This covers any file that contains code that runs when PPDS creates, changes or
   deletes records or metadata in an environment. A pull request that changes such a file is in this
   category whatever part of the file the diff changes (a read method, a comment, a rename). Write
   code is:
   - code that builds or sends the request;
   - code that chooses the target environment for a write;
   - code that decides whether to ask for confirmation.

   Most of this code lives in:
   - `src/PPDS.Dataverse/BulkOperations/` and `src/PPDS.Dataverse/Metadata/Authoring/`;
   - `src/PPDS.Dataverse/Query/Planning/Nodes/DmlExecuteNode.cs` and
     `src/PPDS.Query/Planning/ExecutionPlanBuilder.Dml.cs`;
   - `src/PPDS.Migration/Import/`;
   - `src/PPDS.Cli/Services/Metadata/Authoring/` and `src/PPDS.Cli/Plugins/Registration/`;
   - the write commands under `src/PPDS.Cli/Commands/`, the write tools under `src/PPDS.Mcp/Tools/`,
     and the service files under `src/PPDS.Cli/Services/` that contain write code.

   A file with write code outside these paths is still in this category. Where it is not clear whether code runs
   during a write, the rule at the top of this section applies. Refactors count, even when no
   behaviour is meant to change. A change only to help text or a
   description in a write command is not in this category, unless the text says whether a write
   happens or is confirmed.
8. **The environment-safety checks.** These are:
   - `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`;
   - `src/PPDS.Cli/Services/Environment/WriteProtectionResolver.cs` and `EnvironmentConfirmationService.cs`;
   - `src/PPDS.Cli/Services/WebApi/WebApiWriteGuard.cs`;
   - `src/PPDS.Cli/Services/Metadata/Authoring/DataverseMetadataAuthoringService.WriteConfirmation.cs`;
   - `src/PPDS.Cli/Infrastructure/Safety/`;
   - `src/PPDS.Auth/Profiles/EnvironmentType.cs` and `QuerySafetySettings.cs`;
   - `src/PPDS.Extension/src/utils/dmlSafety.ts`;
   - `.claude/hooks/shakedown-safety.py`;
   - any pull request that closes or fixes issue #1814.
9. **Dependabot's GitHub Actions bumps**, whatever any skill or list says. The classifier puts
   every one in Group C.
10. **Outside contributions.** This covers any pull request whose author is not the maintainer, the
    agents' account or Dependabot. It also covers any pull request, whoever opened it, that carries
    a commit authored by anyone other than the maintainer, the agents' account or Dependabot (an
    outside contributor's work). If the change needs a live run (P1 in
    [specs/test-gates.md](../specs/test-gates.md)), the maintainer or one of his sessions does it
    before the merge and records it in the pull request. The contributor is never asked to.
11. **Releases.** A release pull request (one the release runbook opens: the release PR of its
    step 7, or the focused CHANGELOG PR of its patch procedure) is merged only after the maintainer's
    yes, whatever tier its files would otherwise be in. The maintainer creates and pushes every tag
    and makes every publication himself; no session runs them, even after his yes
    ([VERSIONING.md](VERSIONING.md#9-decisions-in-documents), rule 9.2).

**How an agent asks.** The agent puts the pull request on the list of decisions waiting
(D5 in [MAINTAINER-SESSION-RULES.md](MAINTAINER-SESSION-RULES.md)). It gives the pull request, its
head commit, the category that applies and the review verdict. The answer is the maintainer's own
words, or the maintainer merging it himself. D3 applies. The place of record for a go-first answer
is the pull request's "Review record": the agent records the answer there, quoted and dated, before
it merges, and the squash commit message carries that record. Because the merge is pinned to the
reviewed commit, the answer is recorded for that head commit, and a new head commit has no recorded
answer. After the answer, the agent merges under the same conditions as in
[Agents merge](#agents-merge).

## Auto-Merge

Auto-merge is **enabled at the repo level** (`allow_auto_merge: true`). It does not bypass branch protection or skip required reviews — it only removes the "human clicks merge when CI goes green" step.

Agents merge directly, after the code-scanning result is in ([Agents merge](#agents-merge)). The
maintainer enables auto-merge with:

```bash
gh pr merge <num> --squash --delete-branch --auto
```

The maintainer uses auto-merge only where green CI is enough evidence to ship, so not for these:

- a pull request with manual verification steps in its runbook (for example, "spot-check `npm run package`" or "run TUI snapshots locally");
- a release-branch change;
- a change whose runtime behaviour the reviewer wants to inspect, not only its diff.

## Grouped Dependabot PRs

Dependabot can roll multiple updates into a single grouped PR (titled `Bump the <group> group ...`). Member updates may span multiple update types (e.g., a group containing both patch bumps and a minor bump).

Classify a grouped PR by **most-conservative-wins** — the whole group inherits the most conservative classification of any member:

- Group contains **any major bump** -> Group C (manual review required)
- Group contains **any minor bump** (no majors) -> Group B (the area's tests, one fresh review, then agents merge; for the auth-critical exception see [Who merges](#who-merges))
- Group contains **only patch bumps** -> Group A (one fresh review, then agents merge)
- If any member is an auth-critical package (per the Auth-Critical Packages list below), the group is Group B regardless of update type, unless any member is major (then Group C). Such a group is maintainer first ([Who merges](#who-merges), category 5).
- A GitHub Actions group is Group C whatever its members.

Reasoning: simplest to enforce, hardest to misinterpret, and consistent with the universal-exclusion-of-major-bumps decision from the v1-prelaunch retro. If a single risky member exists, the whole group gets the safer treatment — splitting the group to land safe members earlier is not worth the complexity.

The classifier in `scripts/dependabot/classify.py` implements this rule by parsing the dependabot PR body (which lists each member bump as `Updates <pkg> from <a> to <b>`) and selecting the most-conservative type seen.

## Auth-Critical Packages

The following packages are flagged as **auth-critical** — bumps to them at any non-major version are Group B in the classifier and are maintainer first ([Who merges](#who-merges), category 5), and major bumps go through Group C (manual review). This list is the **single source of truth**; the classifier in `scripts/dependabot/classify.py` mirrors it as `AUTH_CRITICAL_PACKAGES`, and a unit test (`TestAuthCriticalDocCodeDrift.test_doc_list_matches_code_list`) fails if the two diverge.

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

`main` is protected. A pull request must pass the two required status checks, `build-status` and `test-status`. It does not have to be up to date with `main`, so a branch is not updated only in order to merge it.

No approving review is required by branch protection. Every other check (Codecov, CodeQL, the Pre-Merge Gate, the TUI end-to-end workflow, automated review comments) is advisory: read it before merging, but GitHub does not block on it.

## Maintainer merge checklist

An outside contributor's pull request is maintainer first ([Who merges](#who-merges), category 10). Before merging a PR that may incorporate outside work, apply the shared [contributor lifecycle](CONTRIBUTOR-LIFECYCLE.md): prefer the original PR when practical, verify the actual proposed squash author and message and the resulting credit, and name the owners of contributor communication and source-PR disposition. Record one route:

- [ ] No external contribution incorporated; reason recorded.
- [ ] Original contributor PR lands directly; exact source/head and credit recorded.
- [ ] Work incorporated elsewhere; reason, source PR/head, verified identity and landing PR recorded.
- [ ] If applicable: proposed squash author/message checked; communication and disposition owners named; exact reply/decision prepared when permission is needed.

No-contribution work needs no invented source or identity and no extra approval. The offline validator (`scripts/ci/check_contributor_provenance.py`) checks supplied records, not semantic overlap or live state.

The rules below bind agent sessions that work for the maintainer (the coordinating session and its worker sessions). Which pull requests they may merge is in [Who merges](#who-merges). The merge routes above and the contributor process in [CONTRIBUTING.md](../CONTRIBUTING.md) are unchanged for everyone else. The rules are numbered so the pull request template and other documents can cite them. The rules for proof before merge are in [specs/test-gates.md](../specs/test-gates.md); the rules for decisions, triage and sessions are in [MAINTAINER-SESSION-RULES.md](MAINTAINER-SESSION-RULES.md).

### Review

| # | Rule |
|---|---|
| R1 | The reviewer is a fresh agent that did not write the change. It gets the diff and the requirement, never the author's description. Its working directory does not contain the coordinating session's drafts. The brief names the model. |
| R2 | Before review, every acceptance criterion and spec sentence that mentions a changed behaviour is listed and reconciled. |
| R3 | A fix round changes only what the findings name. New scope is a new pull request. |
| R4 | Two rounds at most by the same method. If the second round still has a must-fix finding, stop and change the method (live proof, a smaller change, a design note) and tell the maintainer. |
| R5 | "Converged" means: a round returned no must-fix finding, and the commit that applied the previous round's fixes was part of what that round reviewed. A pull request that has not converged does not merge. |
| R6 | Reviewer transcripts are kept for the life of the release. |

### Pull requests and merging

| # | Rule |
|---|---|
| M1 | One concern per pull request. Size is not a gate; the aim is a change one reviewer can hold, about 600 changed lines of product code. A larger one says why it cannot be split. |
| M2 | The changelog entry is in the same pull request as the behaviour change, and the template's "Breaking" line is filled in and checked by the reviewer. |
| M3 | An agent merges a pull request only as [Who merges](#who-merges) allows: not maintainer first (or with the maintainer's recorded answer), reviewed and converged, required checks green on the final commit, no open thread, no new code-scanning alert, squash, pinned to the reviewed commit. |
| M4 | Two open pull requests do not change the same file group. The tracking issue's workstreams define the groups. |

### CI

| # | Rule |
|---|---|
| C1 | A test that fails on timing is not re-run. The first time, it gets an issue and is fixed at the cause or quarantined with that issue's number. |
| C2 | An infrastructure failure (runner hang, package feed, image pull) may be re-run once without asking, and the report quotes the log line that shows the cause; a second failure is investigated. Every other re-run asks the maintainer. A session never runs a publish or signing workflow. |
| C3 | A failure in one of the product's own release or security checks is always investigated. |

## Pre-Merge Gate Rules

The [`pre-merge-gate.yml`](../.github/workflows/pre-merge-gate.yml) workflow runs on every PR. It evaluates one rule (Rule 3, below) and reports PR size for information. Each check is its own job in the PR Checks tab; the aggregate `Pre-Merge Gate` check succeeds iff Rule 3 passes. The aggregate is not a required status check, so GitHub does not block a merge on it: whoever merges must not merge, or enable auto-merge on, a PR whose `Pre-Merge Gate` has failed.

The gate runs again when a PR is opened, reopened, pushed to, marked ready for review, or edited (title, description or base branch: Rule 3 reads the title and description). Only one run per PR is active. A new commit cancels the run in progress, because that run evaluates a superseded head. Marking ready and editing do not cancel: the new run waits until the run in progress ends, then evaluates the PR as it is at that time, so its result is the newest. A later event replaces a run that is still waiting. Read the result of the newest run: while a run is waiting, the result shown can be from before the latest edit.

Rule logic lives in `scripts/ci/`.

### PR size (informational, `scripts/ci/check_pr_size.py`)

Not a rule and never a merge blocker. The `PR size (informational)` job writes the PR's changed files, additions and deletions to the job log and the GitHub step summary, and marks the PR "large" when it exceeds 50 files or 2000 LoC (additions + deletions) so reviewers can plan for it. It always exits 0. If the PR data cannot be read (for example a `gh` failure) the job still succeeds and shows a `::warning::` annotation and a summary note instead of numbers. There is no bypass marker because there is nothing to bypass.

Large diffs remain a review-quality concern (PR #792 merged 131 files / 7.5K LoC unreviewed): prefer splitting, and say in the description how a large diff was reviewed.

### Rule 3 — Major dependency evidence (`scripts/ci/check_major_bump_tested.py`)

For dependency PRs (label `dependencies` OR author `app/dependabot` / `dependabot[bot]`), the shared classifier in `scripts/dependabot/classify.py` determines both update type and ecosystem. Major-version updates require passing evidence from the affected surface. Unclassifiable versions are treated like majors so ambiguity cannot silently bypass the gate.

| Ecosystem | Required workflow / job | Evidence |
|-----------|-------------------------|----------|
| NuGet | `Test / test` | The real .NET unit-test job ran |
| npm | `Build / extension` | Extension typecheck, lint, build, and tests ran |
| GitHub Actions | `Python Tests / workflow-tests` | Executable workflow-policy regression tests ran |

The gate matches both workflow and job name, so an unrelated generic `test` check cannot satisfy the rule. It evaluates only the newest matching GitHub Actions attempt, ordered by start time, workflow-run ID, run-attempt number, and job ID; an older success cannot mask a newer failure or in-progress rerun. It blocks when the required job is `SKIPPED`, failed, still pending, absent, or missing unambiguous ordering metadata. If the ecosystem cannot be determined from labels, Dependabot branch naming, or a single dependency-manifest/workflow surface, the gate also blocks rather than choosing an unrelated test.

`.github/workflows/dependabot-label.yml` uses the same classifier to apply `status:needs-evaluation` to major or unclassifiable Dependabot PRs. Do not duplicate version heuristics in workflow YAML.

**No bypass marker.** A major update without relevant executable evidence is unverified — fix the cause or re-run the relevant workflow after it passes; don't wave it through.

Catches the failure mode from PR #806 (`vite 5 → 8` — two major-version jumps, auto-merged after only `check-changes` ran).

## Squash Policy

All merges to `main` use **squash merge** with branch deletion. Rebase merges are disabled. Use:

```bash
gh pr merge <num> --squash --delete-branch
```

An agent also passes `--match-head-commit <reviewed sha>` (M3).

This keeps `main` linear and gives one commit per PR for clean `git log` and `git bisect`.

## Dependabot Cadence

Dependabot config lives in `.github/dependabot.yml`. Each Dependabot pull request is classified, reviewed and merged one at a time, as [Who merges](#who-merges) says. None is enabled for auto-merge in batch. Group C bumps (majors, breaking changes, GitHub Actions) wait for the maintainer, with the verification steps documented in the relevant spec or commit message.

For roll-up branches consolidating many bumps into one PR: avoid them. Per-bump squash commits give better `git bisect` granularity and clearer attribution than a single roll-up commit.

## Changelog Check (not required)

The [`changelog-check.yml`](../.github/workflows/changelog-check.yml) workflow
runs `scripts/ci/check_changelog_entry.py` on every pull request to `main`,
and again when the description or labels change (#1789). It is **not** a
required status check.

A pull request that changes a package under `src/<package>/` must, in the same
pull request, do two things.

**1. Add a changelog entry** to `src/<package>/CHANGELOG.md`, under that
package's `## [X.Y.Z] - Unreleased` section.
- An entry under the empty `## [Unreleased]` section fails, and so does an
  entry under a released version.
- A line that is only a list marker, only a heading or only an HTML comment
  is not an entry.
- A pull request that changes two packages needs an entry in each. A file
  moved from one package to another counts for both.
- Deleting a package's CHANGELOG does not take the package out of the check.
- If the CHANGELOG has no `## [X.Y.Z] - Unreleased` section at all, the
  message says a maintainer step is missing (release runbook, step 3, "Open
  the next version").

**2. State in the description whether it is breaking**: the template's
"Breaking changes" section, or a `Breaking:` line. The statement is the whole
section, with HTML comments and a trailing "Generated with …" footer removed.
- **Not breaking**: only exactly `Breaking: no` (or `No`, `None`, `N/A`,
  `Not breaking`), in any case, with optional emphasis or backticks and a
  final full stop, and nothing else. A reason for "no" belongs in the other
  sections of the description.
- **Breaking**: a statement that starts with `Yes`, followed by what breaks
  and how to migrate. A `Breaking: yes` line anywhere in the description wins.
- **Any other text is not a "no"**, whatever the separator: "No, but exit
  code 3 changes to 12", "None; `plugins diff` now exits 12", "No: internal
  refactor", "None" followed by a list item, "This is not a breaking change."
  With the `breaking-change` label it is the description of the break. Without
  the label it fails as unclear, with a message that says how to write the
  line.
- A breaking statement needs the `breaking-change` label, and the label needs
  a breaking statement.
- The changelog entry of a breaking change must say "breaking" ("non-breaking"
  does not count).
- The label is what makes the release plan refuse a non-major version for that
  package ([Release Scope Analysis](RELEASE.md#breaking-changes-refuse-a-non-major-plan)).

**Not checked:**
- Pull requests labelled `internal` (nothing user-visible changed).
  `internal` together with `breaking-change` fails.
- Dependabot pull requests, by author; major updates are covered by Rule 3.
- These files under `src/`, which the check knows by location:
  - `src/PPDS.Extension/src/**/__tests__/`;
  - `src/PPDS.Extension/e2e/`;
  - `src/PPDS.Extension/tools/*.test.mjs`;
  - `src/PPDS.Extension/tsconfig.test.json` and `vitest.config.ts`;
  - `*.md` and `*.mdx` documentation;
  - the package's own CHANGELOG.
- `src/PPDS.Analyzers`, which has no CHANGELOG.

A product folder named `Test`, `tests` or `fixtures` is checked. Generated code
such as `src/PPDS.Dataverse/Generated/` ships in the package and is checked. A
revert that only removes its own unreleased entry fails, with a message that
says so; it needs the `internal` exemption.

To make the check required, the maintainer adds the check `Changelog entry and
breaking statement` to the required status checks of `main`. Before that, the
check should have run on open pull requests long enough to show it does not
fail correct ones.
