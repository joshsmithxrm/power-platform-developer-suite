---
name: dependabot-triage
description: Triage and merge open dependabot PRs per docs/MERGE-POLICY.md — classify each PR (auto-merge / verify-then-merge / manual review), enable auto-merge for safe ones, run targeted test suites for risky ones, and surface anything needing human judgment. Use when there's a backlog of dependabot PRs, after a quiet period, or as a routine drain.
---

# Dependabot Triage

Routine drain of open `app/dependabot` PRs against `docs/MERGE-POLICY.md`. Codifies the pattern from session `a6f07099` (v1-prelaunch retro item B4, finding #9): three-group classification (auto-merge / verify-then-merge / manual review), batched `gh pr merge --auto`, bounded retry on rebase, and silent completion when nothing needs human input.

This skill assumes `allow_auto_merge` is enabled at the repo level (it currently is). The skill will halt if it is not — it does not flip the bit itself.

## When to Use

- "Drain the dependabot backlog"
- "Triage open dependabot PRs"
- "Check on the dependabot PRs"
- After a quiet period when several dependabot PRs have accumulated
- After a release window closes and dependabot work was deferred
- Periodically as housekeeping (Wednesday afternoons after the Monday dependabot run, etc.)

## When NOT to Use

- During an active release (release branch present, tags being pushed) — wait until the release lands
- For non-dependabot dependency PRs (human-authored bumps, e.g. a deliberate Microsoft.Identity.Client major bump) — those route through `/pr` like any feature work
- For first-time enablement of auto-merge on the repo — that's a one-time admin action, not a triage step
- For changing the merge policy itself — `docs/MERGE-POLICY.md` is authoritative; if you find policy gaps, file an issue and stop

## Authoritative Reference

`docs/MERGE-POLICY.md` is the source of truth for what merges automatically and what doesn't. This skill operationalizes it; it does not redefine it. If the skill and the doc disagree, the doc wins and the skill needs a patch.

## Pre-conditions

Run all of these checks before touching any PR. Halt on any failure with a clear instruction to the user.

1. **Auto-merge enabled at the repo level.**
   ```bash
   gh repo view --json autoMergeAllowed --jq .autoMergeAllowed
   ```
   Must be `true`. If `false`, halt: "Auto-merge is not enabled on this repo. Enable via repo Settings > General > Pull Requests > Allow auto-merge, then retry."

2. **`docs/MERGE-POLICY.md` exists.**
   ```bash
   test -f docs/MERGE-POLICY.md && echo OK
   ```
   If missing, halt: "docs/MERGE-POLICY.md is missing — this skill operationalizes that policy. Restore the file before triaging."

3. **No active release in progress.**
   ```bash
   git branch -a | grep -E 'release/(v|prerelease-)' || echo "no release branches"
   git tag --list --contains HEAD | grep -E '^(Auth|Cli|Dataverse|Mcp|Migration|Plugins|Query|Extension)-v' || echo "no recent release tags"
   ```
   If a release branch exists OR HEAD already carries a release tag from the current minute, halt: "A release appears to be in progress (branch: X). Re-run after the release PR merges and tags settle."

4. **No conflicting in-flight work.**
   ```bash
   test -f .claude/state/in-flight-issues.json && cat .claude/state/in-flight-issues.json || echo '{"in_flight": []}'
   ```
   If the file exists and any entry overlaps with files this triage would touch (e.g., dependabot PR is bumping a package whose csproj is being modified by an in-flight feature), surface the conflict and ask the user before proceeding. If the file does not exist (it ships in a later PR), this check passes — log "in-flight tracking not yet present, skipping" and continue.

If any pre-condition fails, write a one-line summary of which check tripped and stop. Do not attempt to remediate.

## Process

### Phase 1 — Pre-flight checks

Run the four pre-condition checks above. Halt with a clear instruction on any failure.

### Phase 2 — Enumerate

```bash
gh pr list \
  --author "app/dependabot" \
  --state open \
  --json number,title,labels,headRefName,createdAt,files,body,url \
  > /tmp/dependabot-prs.json
```

Group by:
- **Ecosystem:** `npm`, `nuget`, `github-actions` — derived from PR labels (`nuget`, `npm`/`javascript`, `github_actions`) or, as a fallback, from `headRefName` (`dependabot/<ecosystem>/...`).
- **Update type:** `patch` / `minor` / `major` — parsed from the title (`Bump X from A.B.C to A.B.C+1` is patch; `from A.B to A.C` is minor; `from A to B` is major).

Report the grouped counts before proceeding so the user (or future skill log) knows the shape of the queue.

If zero open dependabot PRs, report "Nothing to triage" and exit successfully (no notification).

### Phase 3 — Classify each PR

For each PR, classify into exactly one of three groups using `scripts/dependabot/classify.py` (delegate to keep SKILL.md prose-focused). The classifier returns a `(group, reason)` tuple per PR.

The classification rules, summarized from `docs/MERGE-POLICY.md` plus the v1-prelaunch retro decision:

#### Group A — auto-merge eligible

PRs where green CI is sufficient evidence to ship. Includes:

- Patch-version bumps on non-critical paths (e.g., `Bump knip from 6.1.0 to 6.3.0`)
- Tooling/test bumps regardless of patch/minor (eslint, knip, vitest, esbuild, @types/*, @playwright/test, Microsoft.NET.Test.Sdk)
- GitHub Actions group bumps (patch or minor)
- Lockfile-only changes (no `package.json` / `*.csproj` / `Directory.Packages.props` changes in the diff)

**Action:**
```bash
gh pr merge <num> --squash --delete-branch --auto
```

Then move on — CI will merge it when green. Do not hold the session waiting.

#### Group B — verify-then-merge

PRs where green CI is necessary but you want to run the package-specific test suite locally first before letting auto-merge land it. Includes:

- Minor-version bumps on libraries that are not auth-critical but touch user-visible runtime (e.g., Terminal.Gui minor)
- Bumps to `Microsoft.Identity.Client`, `Azure.Identity`, `Microsoft.PowerPlatform.Dataverse.Client` at minor — these touch auth/Dataverse runtime
- Anything bumping packages flagged as security-critical in `Directory.Packages.props` (e.g., `System.Security.Cryptography.Xml`)
- Bumps that touch `src/PPDS.Auth/**` or strong-name signing paths at any version

**Action:** Spin a sub-task per PR (sequentially, not parallel — the test runs are heavy):
1. Check the PR out locally into a throwaway worktree (or fetch the head ref) — do NOT pollute the current worktree.
2. Run the package-specific test suite:
   - **NuGet bump touching `src/PPDS.Auth/**`:** `dotnet test tests/PPDS.Auth.Tests --filter "Category!=Integration" -v q`
   - **NuGet bump touching `src/PPDS.Dataverse/**`:** `dotnet test tests/PPDS.Dataverse.Tests --filter "Category!=Integration" -v q`
   - **NuGet bump elsewhere:** `dotnet test PPDS.sln --filter "Category!=Integration" -v q` (full unit suite — minor bumps are rare enough that the runtime is acceptable)
   - **Extension/npm bump:** `( cd src/PPDS.Extension && npm test && npm run typecheck )`
3. If green, enable auto-merge: `gh pr merge <num> --squash --delete-branch --auto`. If red, escalate — leave a comment on the PR with the failure summary and tag Josh.
4. Tear down the throwaway worktree (`git worktree remove`).

#### Group C — manual review required

Hard exclusions — do NOT auto-merge under any circumstances:

- **ANY major-version bump** (per the v1-prelaunch retro decision — universal exclusion, no exceptions)
- Bumps that remove or rename APIs flagged in the package's changelog (look for "Breaking" / "BREAKING CHANGE" / "removed")
- Anything in `docs/MERGE-POLICY.md`'s "When NOT to use auto-merge" list that doesn't fit Group B's verify-then-merge pattern (e.g., changes to CI/CD pipelines via `actions/*` major bumps)

**Action:**
1. Do NOT enable auto-merge.
2. Add a comment on the PR explaining the classification and tagging Josh:
   ```bash
   gh pr comment <num> --body "Triage classification: Group C (manual review required).
Reason: <reason from classifier>.
Per docs/MERGE-POLICY.md and v1-prelaunch retro decision, this requires human review before merge.
@joshsmithxrm please evaluate."
   ```
3. **Superseded check:** If a newer dependabot PR bumps the same package to a higher version (compare `headRefName` package + version), AND the newer PR's body or commits explicitly mark this one as superseded (or the version range strictly contains this one), close this PR with `gh pr close <num> --comment "Superseded by #<newer>."`. Do NOT close on guess — require explicit superseded marker or strict version-range containment. When in doubt, leave both open and let Josh decide.

### Phase 4 — Drain monitor

After Phase 3 dispatches the initial actions, monitor the Group A and Group B PRs to landing. This is bounded — do not poll forever.

For each PR in Group A or Group B:

1. Poll `gh pr view <num> --json state,mergeStateStatus,statusCheckRollup` every 30s.
2. If `state == "MERGED"`, record final state and move on.
3. If `mergeStateStatus == "BEHIND"` (main has advanced), run `gh pr update-branch <num>` once.
4. If `statusCheckRollup` shows any check `CONCLUSION == "FAILURE"`, escalate immediately — comment on the PR with the failed check name and tag Josh; do not retry.
5. Maximum **3 retry cycles per PR** (where a "cycle" is one rebase + wait-for-CI). After 3, escalate to Josh: comment "Drain stalled after 3 retry cycles. Last status: <X>. Manual intervention needed."

Total skill wall-time cap: **30 minutes**. If the monitor hasn't drained the queue in 30 minutes, report current state and exit — the user can re-run later. Do not keep the session pinned indefinitely.

### Phase 5 — Report

Print a summary table:

```
| PR # | Group | Final state | Reason |
|------|-------|-------------|--------|
| #807 | A     | merged      | patch knip 6.3.0->6.3.1 |
| #808 | A     | awaiting CI | patch eslint 9.1->9.2 |
| #809 | B     | merged      | Microsoft.Identity.Client minor (tests passed) |
| #810 | C     | manual      | Terminal.Gui major 1.x->2.x |
| #811 | C     | closed      | Superseded by #810 |
```

Then surface anything that fell out of normal flow:
- Rebase failures
- Test failures (with test name + failure summary)
- Classification ambiguity (e.g., a PR the classifier returned with low confidence)

**Notification policy** (per v1 notification criteria):
- **Silent** on routine drains (everything in Group A merged, Group B merged after green tests, Group C commented and waiting for Josh — no surprises)
- **PushNotification to Josh** ONLY if:
  - A Group B test run failed (someone needs to investigate)
  - A drain stalled past 3 retries on a PR (CI is broken or main is unstable)
  - The classifier returned an ambiguous case the skill couldn't resolve
  - A pre-condition check failed mid-run (auto-merge got disabled, MERGE-POLICY.md got deleted)

Use the standard PushNotification mechanism — see existing notification helpers in other skills (`/pr`, `/qa`).

## Post-conditions

After a successful run:
- Every open dependabot PR has been classified and either auto-merge-enabled, merged, commented for manual review, or closed-as-superseded.
- A summary table is in the session transcript.
- No PR is in a half-merged or half-rebased state.
- Throwaway worktrees from Group B verifications are cleaned up (`git worktree list` shows none).
- `.claude/state/in-flight-issues.json` (if present) is unchanged — this skill does not write workflow state; it's a maintenance task, not a workflow gate.

## Edge Cases

| Situation | Handling |
|-----------|----------|
| Zero open dependabot PRs | Report "Nothing to triage" and exit silently — no notification. |
| Dependabot PR with no labels (rare) | Fall back to `headRefName` parsing for ecosystem; if still ambiguous, classify Group C with reason "labels and head ref both ambiguous". |
| Dependabot grouped PR (`Bump the npm_and_yarn group across...`) | Classify by the highest update type in the group. If any member is a major bump, the whole PR is Group C. |
| PR bumps two packages where one is auth-critical and one is not | Treat as Group B — the auth-critical bump dominates. |
| `gh pr update-branch` fails with merge conflict | Escalate immediately — do not retry. Comment on PR with conflict detail and tag Josh. |
| CI is down / red on `main` | Pre-condition check 3 catches active releases but not "main is broken" — the drain monitor will see all checks fail. After the first PR shows non-package-specific check failures (e.g., infrastructure tests), abort the drain, report "CI on main appears broken — halting drain", and notify Josh. |
| Newer PR exists that supersedes the one being triaged but without an explicit "Superseded by" marker | Leave both open; flag in the report under "classification ambiguity" for Josh to resolve. Do NOT close on inference alone. |
| Auto-merge gets disabled mid-run (rare — admin action) | The next `gh pr merge --auto` call will fail with a clear error. Catch the failure, halt the drain, report "Auto-merge was disabled mid-run", and notify Josh. |
| A PR has been open >30 days | Surface in the report under "stale" but do NOT auto-close — Josh decides whether to close or rebase. |

## Examples

### Example 1: Quiet drain

```
$ /dependabot-triage
Pre-flight checks... OK (4/4)
Enumerating... 5 open PRs (4 npm patch, 1 nuget patch)
Classifying...
  #820 (Group A): patch eslint 9.1.1->9.1.2
  #821 (Group A): patch knip 6.3.0->6.3.1
  #822 (Group A): patch @types/node 22.5->22.6
  #823 (Group A): patch vitest 2.0.5->2.0.6
  #824 (Group A): patch Microsoft.NET.Test.Sdk 17.10->17.11
Enabling auto-merge on 5 PRs...
Monitoring drain (timeout 30m)...
  #820 merged in 2m
  #821 merged in 2m
  #822 merged in 3m
  #823 merged in 3m
  #824 merged in 4m
Done. 5/5 merged. (silent — nothing requiring input)
```

### Example 2: Mixed batch with one Group C

```
$ /dependabot-triage
Pre-flight checks... OK (4/4)
Enumerating... 3 open PRs
Classifying...
  #830 (Group A): patch eslint 9.1.1->9.1.2
  #831 (Group B): minor Microsoft.Identity.Client 4.55->4.56 (auth-critical)
  #832 (Group C): MAJOR Terminal.Gui 1.19->2.0 (universal major exclusion)
Group A: enabling auto-merge on #830
Group B: running tests/PPDS.Auth.Tests for #831...
  PASSED (24s, 142 tests)
  Enabling auto-merge on #831
Group C: commenting on #832, tagging Josh
Monitoring drain...
  #830 merged in 2m
  #831 merged in 4m
Done. 2 merged, 1 awaiting manual review (#832). (PushNotification: 1 manual review needed)
```

### Example 3: Group B test failure

```
$ /dependabot-triage
Pre-flight checks... OK (4/4)
Enumerating... 1 open PR
Classifying...
  #840 (Group B): minor Azure.Identity 1.12->1.13 (auth-critical)
Group B: running tests/PPDS.Auth.Tests for #840...
  FAILED (62s, 2 failed of 142)
    - PPDS.Auth.Tests.AzureCliCredentialTests.AcquiresTokenForResource
    - PPDS.Auth.Tests.ChainedCredentialTests.FallsBackOnFailure
Commented on #840 with failure summary, tagged Josh.
Done. 0 merged, 1 escalated. (PushNotification: Group B test failure on #840)
```

## Rules

1. **Major-version bumps are always Group C.** No exceptions. The v1-prelaunch retro made this universal.
2. **Never close a PR without strong superseded evidence.** Explicit "Superseded by #N" marker or strict version-range containment. Inference is not enough.
3. **Never enable auto-merge on Group C.** The whole point of Group C is human gating.
4. **Never modify `docs/MERGE-POLICY.md`.** It's authoritative. Surface policy gaps as issues, do not patch in flight.
5. **Bounded retries.** 3 cycles per PR, 30 minutes total. After that, report and exit — do not pin the session.
6. **Silent on routine.** Notifications only when something needs Josh's input. Routine drains are silent.
7. **Sequential Group B verifications.** Test runs are heavy; running them in parallel risks resource contention and confusing failure attribution.

## References

- Authoritative policy: `docs/MERGE-POLICY.md`
- v1-prelaunch retro item #9 (B4 finding): codifies session `a6f07099`'s 3-group pattern
- Proof points: PRs #805, #806 merged autonomously in ~2 minutes once auto-merge was on
- Dependabot config: `.github/dependabot.yml`
- Classifier: `scripts/dependabot/classify.py`
- Classifier tests: `tests/scripts/dependabot/test_classify.py`
