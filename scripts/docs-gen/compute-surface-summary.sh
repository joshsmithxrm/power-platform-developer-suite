#!/usr/bin/env bash
# compute-surface-summary.sh — emit a markdown PR body summarising the public
# API surface diff between the current tree and the previous release tag.
#
# For each library in "Dataverse Migration Auth Plugins", diff
# src/PPDS.{lib}/PublicAPI.Shipped.txt against the version in the previous
# tag (passed as --since-tag, or inferred from git tags). Split entries into
# added / removed / modified and render:
#
#   ## PPDS.{lib}
#   Added (N):
#   - `entry1`
#   ...
#   Removed (M):
#   ...
#   Modified (K):
#   ...
#
# When a category has more than 20 entries, only the first 20 are shown with
# a trailing "... and X more" line.
#
# Options:
#   --root <dir>        Repo root (default: parent of scripts/docs-gen).
#   --since-tag <ref>   Baseline git ref (default: previous semver tag or
#                       'HEAD~1' if no tags exist).
#   --help              Show this help.
#
# Output:
#   stdout — markdown body suitable for writing to artifacts/pr-body.md.
#   stderr — progress diagnostics.

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: compute-surface-summary.sh [--root <dir>] [--since-tag <ref>]

Diffs each library's PublicAPI.Shipped.txt against --since-tag and emits a
markdown summary to stdout. Safe for use as the PR body.
EOF
}

ROOT=""
SINCE_TAG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --root)
      ROOT="$2"
      shift 2
      ;;
    --since-tag)
      SINCE_TAG="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "compute-surface-summary: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [ -z "$ROOT" ]; then
  script_dir="$(cd "$(dirname "$0")" && pwd)"
  ROOT="$(cd "$script_dir/../.." && pwd)"
fi

cd "$ROOT"

if [ -z "$SINCE_TAG" ]; then
  # Pick the most recent annotated tag matching v*; fall back to HEAD~1.
  SINCE_TAG="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)"
  if [ -z "$SINCE_TAG" ]; then
    SINCE_TAG="HEAD~1"
  fi
fi

echo "compute-surface-summary: diffing against $SINCE_TAG" >&2

PACKAGES=(Dataverse Migration Auth Plugins)
MAX_PER_CATEGORY=20

render_category() {
  local label="$1"
  local file="$2"
  local count
  count=$(grep -cv '^$' "$file" 2>/dev/null || true)
  count=${count:-0}

  echo "${label} (${count}):"
  if [ "$count" = "0" ]; then
    echo "- _(none)_"
    return
  fi

  local shown=0
  while IFS= read -r line; do
    [ -z "$line" ] && continue
    if [ "$shown" -ge "$MAX_PER_CATEGORY" ]; then
      break
    fi
    # Wrap in triple-backticks if the entry contains a backtick; otherwise
    # single-backticks are fine. Entries are rarely that exotic.
    if [[ "$line" == *"\`"* ]]; then
      printf -- "- \`\`\`%s\`\`\`\n" "$line"
    else
      printf -- "- \`%s\`\n" "$line"
    fi
    shown=$((shown + 1))
  done <"$file"

  local remaining=$((count - shown))
  if [ "$remaining" -gt 0 ]; then
    echo "- _... and ${remaining} more_"
  fi
}

echo "# Public API surface changes"
echo ""
echo "Baseline: \`${SINCE_TAG}\`"
echo ""

for pkg in "${PACKAGES[@]}"; do
  shipped_rel="src/PPDS.${pkg}/PublicAPI.Shipped.txt"
  current="$ROOT/$shipped_rel"

  if [ ! -f "$current" ]; then
    echo "## PPDS.${pkg}"
    echo ""
    echo "_(no PublicAPI.Shipped.txt — skipping)_"
    echo ""
    continue
  fi

  # Grab the previous version from git (empty if it did not exist).
  prev="$(mktemp)"
  if git show "${SINCE_TAG}:${shipped_rel}" > "$prev" 2>/dev/null; then
    :
  else
    : > "$prev"
  fi

  # Strip comments + blanks to compare entries only.
  prev_clean="$(mktemp)"
  curr_clean="$(mktemp)"
  grep -Ev '^\s*(#|$)' "$prev" | LC_ALL=C sort -u > "$prev_clean" || true
  grep -Ev '^\s*(#|$)' "$current" | LC_ALL=C sort -u > "$curr_clean" || true

  added="$(mktemp)"
  removed="$(mktemp)"
  modified="$(mktemp)"

  LC_ALL=C comm -13 "$prev_clean" "$curr_clean" > "$added" || true
  LC_ALL=C comm -23 "$prev_clean" "$curr_clean" > "$removed" || true

  # "Modified" heuristic: entries whose symbol-identifying prefix (up to the
  # first '(' or ' -> ') exists in both added and removed. This correlates
  # signature tweaks without pulling in a full parser. Written without
  # process substitution so Git-for-Windows bash runs it reliably.
  modified_keys="$(mktemp)"
  add_keys="$(mktemp)"
  rem_keys="$(mktemp)"
  add_keys_sorted="$(mktemp)"
  rem_keys_sorted="$(mktemp)"
  sed -E 's/[ \t]*(->|\().*$//' "$added" > "$add_keys" || true
  sed -E 's/[ \t]*(->|\().*$//' "$removed" > "$rem_keys" || true
  LC_ALL=C sort -u "$add_keys" > "$add_keys_sorted" || true
  LC_ALL=C sort -u "$rem_keys" > "$rem_keys_sorted" || true
  LC_ALL=C comm -12 "$add_keys_sorted" "$rem_keys_sorted" > "$modified_keys" || true

  if [ -s "$modified_keys" ]; then
    # Pick the "added" version as the canonical modified display.
    grep -Ff "$modified_keys" "$added" | LC_ALL=C sort -u > "$modified" || true
    # Remove modified entries from pure add/remove buckets.
    grep -vFf "$modified_keys" "$added" > "$added.filtered" || true
    grep -vFf "$modified_keys" "$removed" > "$removed.filtered" || true
    mv "$added.filtered" "$added"
    mv "$removed.filtered" "$removed"
  fi

  echo "## PPDS.${pkg}"
  echo ""
  render_category "Added" "$added"
  echo ""
  render_category "Removed" "$removed"
  echo ""
  render_category "Modified" "$modified"
  echo ""

  rm -f "$prev" "$prev_clean" "$curr_clean" \
    "$added" "$removed" "$modified" \
    "$modified_keys" "$add_keys" "$rem_keys" \
    "$add_keys_sorted" "$rem_keys_sorted"
done
