#!/usr/bin/env bash
# open-docs-pr.sh — clone ppds-docs, write generated reference markdown,
# push a branch, and open a PR.
#
# Env (all set by the workflow):
#   GITHUB_TOKEN   GitHub App installation token with contents:write +
#                  pull-requests:write on the docs repo.
#   DOCS_REPO      Owner/name of the docs repo (e.g. joshsmithxrm/ppds-docs).
#   TAG_REF        The release tag that triggered the workflow (e.g. v1.1.0).
#   RUN_ID         GitHub Actions run ID (used for branch uniqueness).
#
# Expects artifacts/docs/ to contain the generated reference markdown and
# artifacts/pr-body.md to contain the surface-change summary.

set -euo pipefail

if [ -z "${GITHUB_TOKEN:-}" ]; then
  echo "open-docs-pr: GITHUB_TOKEN is required" >&2
  exit 1
fi
if [ -z "${DOCS_REPO:-}" ]; then
  echo "open-docs-pr: DOCS_REPO is required" >&2
  exit 1
fi
if [ -z "${TAG_REF:-}" ]; then
  echo "open-docs-pr: TAG_REF is required" >&2
  exit 1
fi
if [ -z "${RUN_ID:-}" ]; then
  echo "open-docs-pr: RUN_ID is required" >&2
  exit 1
fi

BRANCH="release/${TAG_REF}-ref-${RUN_ID}"
CLONE_DIR="$(mktemp -d)"

echo "open-docs-pr: cloning $DOCS_REPO into $CLONE_DIR" >&2
git clone --depth 1 "https://x-access-token:${GITHUB_TOKEN}@github.com/${DOCS_REPO}.git" "$CLONE_DIR"

echo "open-docs-pr: creating branch $BRANCH" >&2
git -C "$CLONE_DIR" checkout -b "$BRANCH"

echo "open-docs-pr: copying generated docs to $CLONE_DIR/docs/reference/" >&2
mkdir -p "$CLONE_DIR/docs/reference"
cp -r artifacts/docs/reference/* "$CLONE_DIR/docs/reference/"

git -C "$CLONE_DIR" add docs/reference/
CHANGES=$(git -C "$CLONE_DIR" diff --cached --name-only)

if [ -z "$CHANGES" ]; then
  echo "open-docs-pr: no reference changes to push — skipping PR" >&2
  rm -rf "$CLONE_DIR"
  exit 0
fi

echo "open-docs-pr: committing $(echo "$CHANGES" | wc -l | tr -d ' ') file(s)" >&2
git -C "$CLONE_DIR" \
  -c user.name="ppds-docs-bot" \
  -c user.email="ppds-docs-bot[bot]@users.noreply.github.com" \
  commit -m "chore(reference): regenerate for $TAG_REF"

echo "open-docs-pr: pushing $BRANCH" >&2
git -C "$CLONE_DIR" push origin "$BRANCH"

PR_TITLE="chore(reference): regenerate for $TAG_REF"
PR_BODY="$(cat artifacts/pr-body.md 2>/dev/null || echo "Reference docs regenerated from $TAG_REF.")"

echo "open-docs-pr: opening PR on $DOCS_REPO" >&2
PR_URL=$(GH_TOKEN="$GITHUB_TOKEN" gh pr create \
  --repo "$DOCS_REPO" \
  --base main \
  --head "$BRANCH" \
  --title "$PR_TITLE" \
  --body "$PR_BODY")

echo "open-docs-pr: created $PR_URL" >&2
rm -rf "$CLONE_DIR"
