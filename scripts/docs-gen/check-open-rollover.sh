#!/usr/bin/env bash
# check-open-rollover.sh — fail-fast if a prior baseline-rollover PR is still
# open. Spec AC-27.
#
# Uses `gh api` so the PAT/GITHUB_TOKEN with repo read access is sufficient.
# Emits the open PR(s) (number + url) to stderr and exits 1 when any are
# found.  Exits 0 when the repo is clean.
#
# Options:
#   --repo <owner/name>   Override the default of GITHUB_REPOSITORY.
#
# Env:
#   GITHUB_REPOSITORY     Default target (set by GitHub Actions automatically).
#   GH                    Override the gh binary (used by tests to shim).

set -euo pipefail

REPO=""
while [ $# -gt 0 ]; do
  case "$1" in
    --repo)
      REPO="$2"
      shift 2
      ;;
    --help|-h)
      cat <<'EOF'
Usage: check-open-rollover.sh [--repo <owner/name>]

Queries open pull requests in the repo for titles starting with
"chore(release):" and containing "baseline rollover". Exits 1 with a message
on stderr if any are found.
EOF
      exit 0
      ;;
    *)
      echo "check-open-rollover: unknown argument '$1'" >&2
      exit 2
      ;;
  esac
done

if [ -z "$REPO" ]; then
  REPO="${GITHUB_REPOSITORY:-}"
fi

if [ -z "$REPO" ]; then
  echo "check-open-rollover: --repo or GITHUB_REPOSITORY must be set" >&2
  exit 2
fi

GH_BIN="${GH:-gh}"

# Use the jq filter to extract matching PR number + url to stdout. Tests
# replace the gh binary with a shim that returns a canned API response.
jq_filter='.[]
  | select((.title // "") | startswith("chore(release):"))
  | select((.title // "") | contains("baseline rollover"))
  | "#\(.number) \(.html_url // .url // "")"'

matches="$("$GH_BIN" api "repos/$REPO/pulls?state=open&per_page=100" --jq "$jq_filter" || true)"

if [ -n "${matches//[[:space:]]/}" ]; then
  echo "check-open-rollover: open prior baseline rollover PR(s) found:" >&2
  while IFS= read -r line; do
    [ -n "$line" ] && echo "  $line" >&2
  done <<<"$matches"
  echo "Open prior rollover PR must be merged or closed before new tag" >&2
  exit 1
fi

echo "check-open-rollover: no open baseline rollover PRs" >&2
exit 0
