---
name: dependabot-triage
description: Drain open dependabot PRs per docs/MERGE-POLICY.md - classify each as auto-merge, verify-then-merge or manual review, enable auto-merge for the safe ones, run the area's tests for the risky ones, and comment on the rest. Use when dependabot PRs have piled up or as routine housekeeping.
---

# Dependabot Triage

The policy is `docs/MERGE-POLICY.md` (auto-merge rules, grouped PRs, the auth-critical package list). The classification is `scripts/dependabot/classify.py`. If this skill disagrees with either, they win. Do not edit the policy while triaging; file an issue for a gap.

## Before starting

Stop and tell the user if any of these hold:

- `gh repo view --json autoMergeAllowed --jq .autoMergeAllowed` is not `true`.
- A release is in progress (a `release/*` branch exists or release tags are being pushed).
- `main` CI is red (every PR will fail the same checks; drain nothing).

## Procedure

1. List the PRs: `gh pr list --author "app/dependabot" --state open --json number,title,labels,headRefName,files,body,url`. None open: say so and stop.
2. Classify each PR with `scripts/dependabot/classify.py` (it returns a group and a reason). Groups:
   - **A, auto-merge:** `gh pr merge <n> --squash --delete-branch --auto`.
   - **B, verify then merge:** run the area's tests below, then enable auto-merge as for A. On failure, comment with the failing tests and stop on that PR.
   - **C, manual review:** do not enable auto-merge. Comment with the group and the classifier's reason so the maintainer can decide.
3. Every major bump is C. Any non-major bump of an auth-critical package (the list in MERGE-POLICY.md) is B, patch included: the classifier sends any non-major auth-critical bump to B, and so does the policy. A grouped PR takes its most conservative member.
4. Close a PR as superseded only when a newer PR names it explicitly or strictly contains its version range. Otherwise leave both open and say so in the report.

## Verification commands (group B, one PR at a time, in a throwaway worktree)

- Touches `src/PPDS.Auth/**`: `dotnet test tests/PPDS.Auth.Tests --filter "Category!=Integration" -v q`
- Touches `src/PPDS.Dataverse/**`: `dotnet test tests/PPDS.Dataverse.Tests --filter "Category!=Integration" -v q`
- Other NuGet: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- npm (extension): `( cd src/PPDS.Extension && npm test && npm run typecheck )`

Run them sequentially; they are heavy. Remove each worktree afterwards.

## Draining

After enabling auto-merge, watch the PRs with `gh pr view <n> --json state,mergeStateStatus,statusCheckRollup`.

- `BEHIND`: run `gh pr update-branch <n>` once per cycle.
- A failed check or a merge conflict: comment on the PR and stop on it; do not retry.
- At most three update-and-wait cycles per PR, and thirty minutes in total. Then report where things stand and exit; do not hold the session open.

## Report

Finish with a table of PR, group, final state and reason. Call out failed verifications, stalled PRs, ambiguous classifications and PRs open more than 30 days (flag; do not close).

References: `.github/dependabot.yml`, `tests/scripts/dependabot/test_classify.py`.
