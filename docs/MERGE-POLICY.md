# Merge Policy

How PRs get merged into `main`. Operational reference for maintainers.

For contributor work, complete the [maintainer merge checklist](#maintainer-merge-checklist)
before merging. Auto-merge eligibility does not waive it or grant communication permission.

## Auto-Merge

Auto-merge is **enabled at the repo level** (`allow_auto_merge: true`). It does not bypass branch protection or skip required reviews — it only removes the "human clicks merge when CI goes green" step.

Enable auto-merge on a PR with:

```bash
gh pr merge <num> --squash --delete-branch --auto
```

### When to use auto-merge

Use auto-merge when **green CI is sufficient evidence to ship**. Specifically:

- Dependabot tooling/test bumps — dev-time lint/test/build packages (e.g. eslint, knip, vitest, esbuild, `@types/*`, `@playwright/test`, Microsoft.NET.Test.Sdk). These names are **examples only**; the authoritative allowlist is `scripts/dependabot/classify.py` (`TOOLING_PACKAGES` / `TOOLING_PREFIXES`, matched via `is_tooling_package()`) — consult it rather than this list when classifying.
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
- Pass the two required status checks, `build-status` and `test-status`

No approving review is required by branch protection. Every other check (Codecov, CodeQL, the Pre-Merge Gate, the TUI end-to-end workflow, automated review comments) is advisory: read it before merging, but GitHub does not block on it.

## Maintainer merge checklist

Before merging a PR that may incorporate outside work, apply the shared [contributor lifecycle](CONTRIBUTOR-LIFECYCLE.md): prefer the original PR when practical, verify the actual proposed squash author and message and the resulting credit, and name the owners of contributor communication and source-PR disposition. Record one route:

- [ ] No external contribution incorporated; reason recorded.
- [ ] Original contributor PR lands directly; exact source/head and credit recorded.
- [ ] Work incorporated elsewhere; reason, source PR/head, verified identity and landing PR recorded.
- [ ] If applicable: proposed squash author/message checked; communication and disposition owners named; exact reply/decision prepared when permission is needed.

No-contribution work needs no invented source or identity and no extra approval. The offline validator (`scripts/ci/check_contributor_provenance.py`) checks supplied records, not semantic overlap or live state.

The rules below bind agent sessions that work for the maintainer (the coordinating session and its worker sessions). The merge routes above and the contributor process in [CONTRIBUTING.md](../CONTRIBUTING.md) are unchanged for everyone else. The rules are numbered so the pull request template and other documents can cite them. The rules for proof before merge are in [specs/test-gates.md](../specs/test-gates.md); the rules for decisions, triage and sessions are in [MAINTAINER-SESSION-RULES.md](MAINTAINER-SESSION-RULES.md).

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
| M3 | A pull request may be merged by an agent only as the standing authorisations allow: reviewed and converged, required checks green on the final commit, no open thread, no new code-scanning alert, squash, pinned to the reviewed commit. |
| M4 | Two open pull requests do not change the same file group. The tracking issue's workstreams define the groups. |

### CI

| # | Rule |
|---|---|
| C1 | A test that fails on timing is not re-run. The first time, it gets an issue and is fixed at the cause or quarantined with that issue's number. |
| C2 | An infrastructure failure (runner hang, package feed, image pull) may be re-run once without asking, and the report quotes the log line that shows the cause; a second failure is investigated. Every other re-run asks the maintainer. A session never runs a publish or signing workflow. |
| C3 | A failure in one of the product's own release or security checks is always investigated. |

## Pre-Merge Gate Rules

The [`pre-merge-gate.yml`](../.github/workflows/pre-merge-gate.yml) workflow runs on every PR. It evaluates one rule (Rule 3, below) and reports PR size for information. Each check is its own job in the PR Checks tab; the aggregate `Pre-Merge Gate` check succeeds iff Rule 3 passes. The aggregate is not a required status check, so GitHub does not block a merge on it: a maintainer must not merge, or enable auto-merge on, a PR whose `Pre-Merge Gate` has failed.

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

This keeps `main` linear and gives one commit per PR for clean `git log` and `git bisect`.

## Dependabot Cadence

Dependabot config lives in `.github/dependabot.yml`. Bumps that meet the auto-merge criteria above can be `--auto` enabled in batch. Bumps that don't (auth, crypto, major core libs) get manual review with the verification steps documented in the relevant spec or commit message.

For roll-up branches consolidating many bumps into one PR: avoid unless auto-merge is unavailable. Per-bump squash commits give better `git bisect` granularity and clearer attribution than a single roll-up commit.

## Decision Link Check (not required)

The [`decision-link-check.yml`](../.github/workflows/decision-link-check.yml)
workflow runs `scripts/ci/check_decision_links.py` on every pull request to
`main` (#1790). It is **not** a required status check.

**What it is: a net, not a proof.** It catches the classes of decision text
listed below. It does not prove that no decision text is unlinked, so
reviewers still read specs, changelogs and release notes for decisions.

It reads the lines a pull request adds or changes in `specs/`,
`docs/releases/` and `src/<package>/CHANGELOG.md`. A pure rename is not a
change; a file moved in from outside those paths, or a new file, is read
whole; a deleted file is not read. A sentence there that states an approval,
an exception, a removal or a date must link the comment in this repository
where the maintainer made that decision, or be marked as proposed. This is
rule 9.1 of the versioning policy proposed in #1831.

**The classes** (`DECISION_PHRASES`, `FRAGMENTS`, `DECISION_COLUMNS` and
`STATE_CELLS` in the script):
- **Approval:**
  - "approved" as a verb, with any subject and object;
  - "approval" or "sign-off" given, received or held ("has maintainer
    approval", "sign-off … was given"), or as a label ("Approval: <name>,
    <date>", "Sign-off: …");
  - "decided", "chose", "have chosen", "opted to/for/against", "agreed",
    "authorised the/a …", "accepted the proposal/recommendation/split", "was
    accepted", "the release review accepted";
  - "Decision: …", "Decision (<date>): …", "the decision to … was taken",
    "per <name>,";
  - a table cell "Accepted" or "Ratified".
- **Exception:**
  - "exception" in a policy frame: "is an exception", "exception to/for",
    "made an exception", "an exception has been made", "the exception
    covers", "by exception", "Exception:" (also in a table cell);
  - "exempt" or "exemption", "excepted", "waived" or a waiver;
  - a rule, requirement, check, policy or guarantee that "does not apply
    to", "does not cover", "does not extend to" or "is not enforced for" X;
  - "excluded from", "not subject to", "not bound by" or "not covered by"
    it; it is "suspended/relaxed/lifted/disabled for";
  - "is not required to", "Except for X, …", "Not applicable to X: …", "by
    agreement", "this once".
- **Removal:**
  - a removal verb in the future or as a schedule: "will be removed,
    unlisted or withdrawn", "we'll drop", "we intend to drop", "<version>
    will remove", "is retired in 2.0", "will stop <verb>ing", "will no
    longer", "will go away";
  - two-step forms: "first ignored and then removed", "marked obsolete in
    2.1";
  - end of support: "end of support", "end of life", "support … ends/will
    end", "no further fixes/updates", "no longer supported";
  - "scheduled for removal", "Removal of …: 3.0", and labels "Removed in
    3.0: …", "Dropping in 2.2: …", "Sunset: …", also as a table cell. In a
    changelog, a removal label under a section for a released (dated) version
    is history and is not read.
- **Date:**
  - a future date in any ordinary written form, with or without a
    preposition before it, and "by mid-January", "next Monday", "the second
    half of 2027";
  - a version committed to: "until the 3.0 release", "takes effect in 2.1",
    "from 2.1 onwards", "From 2.1, …", "planned for 2.2", "continues through
    2.4", "becomes an error in 2.4", "planned to ship in 2.3", "will reach
    general availability";
  - labels "Ships in: 2.1", "Due: 2.1";
  - a duration counted in days, months or releases: "for one more release",
    "for two more minor versions", "for the lifetime of 2.x", "at least 90
    days", "within 30 days", "six months after 2.0", "deprecation period";
  - an event: "until Microsoft ends its support", "on the same day as", "no
    later than the day of".
- **Tables:** a future date under a `When`, `Date`, `Target`, `Deadline` or
  `Due` column; a version under a `Removal` or `Removed in` column; a state
  cell as above.

**Product commitment or technical fact** (`GUARDS` in the script). A match
does not count:
- when the sentence is a runtime or data fact: rows, records, columns,
  temporary files, cache, lock files, certificates, tokens, logs, validity,
  expiry, created or modified dates, samples, fixtures. It counts anyway when
  the sentence also names a product commitment: a version, a `--flag`, an
  alias, "support for", the next major or minor, a deprecation;
- when it states Microsoft's, Dataverse's, Azure's or GitHub's plans;
- when the exception is a thrown, raised, caught, wrapped, inner or
  unhandled exception, or an exception to a data shape or schema;
- when the exemption is from a check, a warning, an analyzer, a validation, a
  compiler rule such as `CS1591`, or for a code path such as `Generated/`;
- when a client, server, handshake, planner, optimizer, request, connection,
  runtime, or a command in a code span "decides", "agrees" or "accepts";
- when "approval" or "sign-off" is named as a requirement ("needs maintainer
  approval", "approval gates the merge");
- when "approved" is conditional ("once the release is approved", "approved
  … once the plan is safe") or an adjective ("the approved tool", "Approved
  runs are listed", "a separately approved setup");
- when "approved" or "exempt" is a value in sample, demo, fixture, test or
  seed data, or a code value;
- when "is not required to" is about a caller, a parameter, a field, a value,
  or a person's duty in a process (a contributor, author, user or reviewer);
- when the wording is in the past ("was marked obsolete", "reached general
  availability");
- when "approves" is habitual ("approves every release");
- when it is a spec's `**Decision:**` design-record line.

Past wording does not count: removals are matched in the future or present,
never "was removed in 1.6", and a date counts only if it is after the day of
the change (the pull request's head commit). A date inside a link target, a
URL or a code span is not read.

**Links and "proposed":**
- A decision link is a URL to a comment in this repository:
  `…/issues/N#issuecomment-M`, `…/pull/N#issuecomment-M`,
  `…/pull/N#discussion_rM` or `…/pull/N#pullrequestreview-M`, or an issue
  body, `…/issues/N#issue-M`. These do not count: a bare `#N`, an issue URL
  without a comment anchor, another repository's URL, or a URL inside another
  URL.
- "Proposed" exempts a decision only where it qualifies that decision: a
  leading "Proposed:", or "(proposed)", "is proposed" or "proposed removal"
  in the same clause (clauses are separated by `;`). A qualifier in another
  clause does not reach back.

**Limits:** headings, fenced code and HTML comments are not checked. A table
row is one unit, and "e.g." or "i.e." do not end a sentence.

**Changing the list:** each entry, alternative, guard and column has an
example sentence, and a test generated from the list proves that each one is
needed. The whole list is pinned by a digest, so deleting anything fails a
test. To add a class, add the entry with its example, check that its
generated test passes, and update the pin. Add the sentence that showed the
gap to `tests/ci/fixtures/decision_links/regression_must_fail.txt` or
`regression_must_pass.txt`; the `must_fail.txt`/`must_pass.txt` corpus is
for measurement.

`--whole-tree` reports how many existing sentences would fail, without
failing.

To make it required, the maintainer adds the check `Decision text links its
decision` to the required status checks of `main`.
