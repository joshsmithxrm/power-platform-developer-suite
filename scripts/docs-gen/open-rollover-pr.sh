#!/usr/bin/env bash
# open-rollover-pr.sh — run the baseline rollover, push a branch, and open a
# PR that moves PublicAPI.Unshipped entries to Shipped.
#
# Env (all set by the workflow):
#   GITHUB_TOKEN   Token with contents:write + pull-requests:write on this repo.
#   TAG_REF        The release tag that triggered the workflow (e.g. v1.1.0).
#
# Must be run from the repo root (the workflow checkout).

set -euo pipefail

if [ -z "${GITHUB_TOKEN:-}" ]; then
  echo "open-rollover-pr: GITHUB_TOKEN is required" >&2
  exit 1
fi
if [ -z "${TAG_REF:-}" ]; then
  echo "open-rollover-pr: TAG_REF is required" >&2
  exit 1
fi

BRANCH="chore/rollover-${TAG_REF}"

echo "open-rollover-pr: running compute-rollover-diff.sh" >&2
CHANGED_FILES=$(bash scripts/docs-gen/compute-rollover-diff.sh)

if [ -z "$CHANGED_FILES" ]; then
  echo "open-rollover-pr: no Unshipped entries to roll — skipping PR" >&2
  exit 0
fi

echo "open-rollover-pr: creating branch $BRANCH" >&2
git checkout -b "$BRANCH"

echo "open-rollover-pr: staging changed files" >&2
echo "$CHANGED_FILES" | while IFS= read -r f; do
  [ -n "$f" ] && git add "$f"
done

git \
  -c user.name="github-actions[bot]" \
  -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
  commit -m "chore(release): $TAG_REF baseline rollover"

echo "open-rollover-pr: pushing $BRANCH" >&2
git push origin "$BRANCH"

PR_TITLE="chore(release): $TAG_REF baseline rollover"
PR_BODY="Moves PublicAPI.Unshipped.txt entries to Shipped.txt for the $TAG_REF release.

Merge this **before** the ppds-docs PR so future release diffs have a clean baseline."

echo "open-rollover-pr: opening PR" >&2
PR_URL=$(gh pr create \
  --base main \
  --head "$BRANCH" \
  --title "$PR_TITLE" \
  --body "$PR_BODY")

echo "open-rollover-pr: created $PR_URL" >&2
