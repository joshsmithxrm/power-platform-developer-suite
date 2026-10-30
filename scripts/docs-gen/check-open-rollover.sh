#!/usr/bin/env bash
# check-open-rollover.sh — fail-fast if a prior baseline-rollover PR is still
# open. Spec AC-27.
#
# Uses `gh api` so the PAT/GITHUB_TOKEN with repo read access is sufficient.
# Emits the open PR(s) (number + url) to stderr and exits 1 when any are
# found. Exits 0 only after a complete, successful query finds no matches;
# exits 2 if the query or response validation fails.
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
      if [ $# -lt 2 ] || [ -z "$2" ]; then
        echo "check-open-rollover: --repo requires <owner/name>" >&2
        exit 2
      fi
      REPO="$2"
      shift 2
      ;;
    --help|-h)
      cat <<'EOF'
Usage: check-open-rollover.sh [--repo <owner/name>]

Queries every page of open pull requests in the repo for titles starting with
"chore(release):" and containing "baseline rollover". Exits 1 with a message
on stderr if any are found, or 2 if the query cannot be completed and validated.
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

# Validate every page before filtering: an error object or missing title must
# not become a successful no-match result. gh evaluates this filter on each
# page and propagates both JSON parsing errors and jq error() failures.
jq_filter='if type != "array" then error("Expected an array of pull requests") else . end
  | .[]
  | if type != "object" then error("Expected a pull request object") else . end
  | if (.title | type) != "string" or (.number | type) != "number"
      or ((.html_url // .url) | type) != "string"
    then error("Invalid pull request title, number, or URL") else . end
  | if .number <= 0 or (.number | floor) != .number
      or ((.html_url // .url) | length) == 0
    then error("Invalid pull request number or empty URL") else . end
  | select(.title | startswith("chore(release):"))
  | select(.title | contains("baseline rollover"))
  | "#\(.number) \(.html_url // .url)"'

# Capture all pages before allowing the release to continue. In particular,
# failure on a later page must discard earlier output rather than claim clean.
if ! response="$("$GH_BIN" api "repos/$REPO/pulls?state=open&per_page=100" --paginate --include --jq "$jq_filter")"; then
  echo "check-open-rollover: unable to complete and validate the open PR query; aborting release" >&2
  exit 2
fi

# gh skips jq entirely for HTTP 204. Require the list endpoint's expected 200
# response on every page, so an unexpected empty response cannot look clean.
seen_response=false
matches=""
while IFS= read -r line; do
  line="${line%$'\r'}"
  case "$line" in
    HTTP/*)
      if [[ ! "$line" =~ ^HTTP/[0-9.]+\ 200(\ |$) ]]; then
        echo "check-open-rollover: unexpected API response '$line'; aborting release" >&2
        exit 2
      fi
      seen_response=true
      ;;
    \#*) matches+="$line"$'\n' ;;
  esac
done <<< "$response"

if [ -n "${matches//[[:space:]]/}" ]; then
  echo "check-open-rollover: open prior baseline rollover PR(s) found:" >&2
  while IFS= read -r line; do
    [ -n "$line" ] && echo "  $line" >&2
  done <<<"$matches"
  echo "Open prior rollover PR must be merged or closed before new tag" >&2
  exit 1
fi

if [ "$seen_response" != true ]; then
  echo "check-open-rollover: no validated API response; aborting release" >&2
  exit 2
fi

echo "check-open-rollover: no open baseline rollover PRs" >&2
exit 0
