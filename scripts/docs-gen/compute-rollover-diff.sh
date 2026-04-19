#!/usr/bin/env bash
# compute-rollover-diff.sh — move PublicAPI.Unshipped entries to Shipped for
# every library package with a non-empty Unshipped file.
#
# For each package in "Dataverse Migration Auth Plugins":
#   * If src/PPDS.{pkg}/PublicAPI.Unshipped.txt has any non-blank, non-comment
#     lines, append those lines to PublicAPI.Shipped.txt, re-sort + dedupe the
#     Shipped file, and reset Unshipped to its minimal valid form
#     (a single "#nullable enable" line).
#   * Per-package counts and actions go to stderr (I1 — diagnostics).
#   * The affected file paths are echoed to stdout, one per line (I1 — data).
#
# Options:
#   --root <dir>   Repo root (defaults to the directory containing this
#                  script's parent).
#   --help         Show this help text.
#
# Exit status: 0 on success, non-zero on I/O failure.

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: compute-rollover-diff.sh [--root <dir>] [--help]

Moves entries from each library's PublicAPI.Unshipped.txt to
PublicAPI.Shipped.txt, sorts + deduplicates Shipped, and clears Unshipped to
its minimal valid form.

Packages processed: Dataverse, Migration, Auth, Plugins.
EOF
}

ROOT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --root)
      ROOT="$2"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "compute-rollover-diff: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [ -z "$ROOT" ]; then
  script_dir="$(cd "$(dirname "$0")" && pwd)"
  # scripts/docs-gen/.. -> scripts/..  -> repo root
  ROOT="$(cd "$script_dir/../.." && pwd)"
fi

if [ ! -d "$ROOT" ]; then
  echo "compute-rollover-diff: root '$ROOT' does not exist" >&2
  exit 1
fi

PACKAGES=(Dataverse Migration Auth Plugins)

count_meaningful() {
  # Count lines that are neither blank nor whitespace-only nor starting with '#'.
  local file="$1"
  if [ ! -f "$file" ]; then
    echo "0"
    return
  fi
  # grep -c returns exit 1 when zero matches; we handle that explicitly so
  # the caller gets a numeric count and the pipeline stays set -e clean.
  local n
  n=$(grep -Ecv '^\s*(#|$)' "$file" || true)
  echo "${n:-0}"
}

for pkg in "${PACKAGES[@]}"; do
  dir="$ROOT/src/PPDS.$pkg"
  unshipped="$dir/PublicAPI.Unshipped.txt"
  shipped="$dir/PublicAPI.Shipped.txt"

  if [ ! -d "$dir" ]; then
    echo "compute-rollover-diff: skipping $pkg — $dir does not exist" >&2
    continue
  fi

  meaningful=$(count_meaningful "$unshipped")

  if [ "$meaningful" = "0" ]; then
    echo "compute-rollover-diff: $pkg — Unshipped empty, nothing to roll" >&2
    continue
  fi

  echo "compute-rollover-diff: $pkg — moving $meaningful entries Unshipped -> Shipped" >&2

  # Preserve any existing Shipped content. Create the file if missing.
  touch "$shipped"

  # Append meaningful Unshipped lines (drop blanks + comments).
  grep -Ev '^\s*(#|$)' "$unshipped" >> "$shipped"

  # Sort + dedupe in-place using a temp file. Ordinal byte order via LC_ALL=C
  # gives deterministic output across platforms.
  tmp="$(mktemp)"
  LC_ALL=C sort -u "$shipped" > "$tmp"
  mv "$tmp" "$shipped"

  # Reset Unshipped to the minimal valid form.
  printf '#nullable enable\n' > "$unshipped"

  echo "$shipped"
  echo "$unshipped"
done
