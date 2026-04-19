#!/usr/bin/env bash
# pre-commit gate: enforce CLAUDE.md line-cap.
#
# Fires when commit touches any CLAUDE.md (root or nested). Requires the
# staged version of each file to be <=100 lines.
#
# NOTE: The commit-message marker check ([claude-md-reviewed: YYYY-MM-DD])
# lives in scripts/hooks/commit-msg.d/claudemd-marker.sh — pre-commit runs
# before the commit message is finalized, so checking the message here would
# read stale data.
#
# See docs/CLAUDE-MD-GOVERNANCE.md for the rationale.

set -euo pipefail

LINE_CAP=100

# Find every staged CLAUDE.md.
STAGED_CLAUDE_MD=$(git diff --cached --name-only --diff-filter=ACMR -- ':(glob)**/CLAUDE.md' ':(glob)CLAUDE.md' || true)

if [ -z "$STAGED_CLAUDE_MD" ]; then
    exit 0
fi

echo "[claudemd-gate] CLAUDE.md change detected — running line-cap gate..."

# Line-cap enforcement against the STAGED (index) content, not the working
# directory. This way unstaged edits cannot bypass the cap.
fail=0
while IFS= read -r path; do
    [ -z "$path" ] && continue
    # Skip if not present in the index (e.g., deletion). git show :path
    # returns non-zero in that case.
    if ! git cat-file -e ":$path" 2>/dev/null; then
        continue
    fi
    lines=$(git show ":$path" | wc -l | tr -d ' ')
    if [ "$lines" -gt "$LINE_CAP" ]; then
        echo "[claudemd-gate] BLOCKED: $path is $lines lines (cap is $LINE_CAP)." >&2
        fail=1
    fi
done <<< "$STAGED_CLAUDE_MD"

if [ "$fail" -eq 1 ]; then
    cat >&2 <<EOF

CLAUDE.md changes must keep each file at $LINE_CAP lines or fewer.

Apply the 4-question test before adding to CLAUDE.md:
  1. Globally relevant — true in EVERY session?
  2. Behavior-shaping — does removing it cause Claude to do the wrong thing?
  3. Not auto-discoverable — Claude can't find it via Read/Grep?
  4. Stable — won't change in next 90 days?

If any answer is "no", route the line elsewhere:
  - Skill .claude/skills/<name>/SKILL.md
  - README.md (project structure / tech stack)
  - docs/ (procedures, reference data)
  - Hook .claude/hooks/ (must-always rules)

See docs/CLAUDE-MD-GOVERNANCE.md.
EOF
    exit 1
fi

echo "[claudemd-gate] CLAUDE.md line-cap check passed."
exit 0
