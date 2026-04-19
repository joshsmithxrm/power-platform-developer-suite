#!/usr/bin/env bash
# pre-commit gate: enforce CLAUDE.md governance.
#
# Fires when commit touches any CLAUDE.md (root or nested). Requires:
#   1. Commit message contains [claude-md-reviewed: YYYY-MM-DD] marker.
#   2. Post-edit CLAUDE.md is <=100 lines.
#
# See docs/CLAUDE-MD-GOVERNANCE.md for the rationale.

set -euo pipefail

REPO_ROOT=$(git rev-parse --show-toplevel)
LINE_CAP=100

# Find every staged CLAUDE.md.
STAGED_CLAUDE_MD=$(git diff --cached --name-only --diff-filter=ACMR -- ':(glob)**/CLAUDE.md' ':(glob)CLAUDE.md' || true)

if [ -z "$STAGED_CLAUDE_MD" ]; then
    exit 0
fi

echo "[claudemd-gate] CLAUDE.md change detected — running governance gate..."

# 1. Line-cap enforcement.
fail=0
while IFS= read -r path; do
    [ -z "$path" ] && continue
    abs="$REPO_ROOT/$path"
    if [ ! -f "$abs" ]; then
        # Deleted file — skip line check.
        continue
    fi
    lines=$(wc -l < "$abs" | tr -d ' ')
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

# 2. Marker enforcement. The marker must appear in the commit message.
# Read the prepared commit message — git passes it via $1 to commit-msg, but
# in pre-commit we read it from .git/COMMIT_EDITMSG (the staging file Git
# writes when ``git commit -m`` or ``-F`` is invoked).
COMMIT_MSG_FILE="$REPO_ROOT/.git/COMMIT_EDITMSG"

# In a worktree, .git is a file pointing to the real gitdir.
if [ -f "$REPO_ROOT/.git" ]; then
    GITDIR=$(sed -n 's/^gitdir: //p' "$REPO_ROOT/.git" | tr -d '\r')
    COMMIT_MSG_FILE="$GITDIR/COMMIT_EDITMSG"
fi

if [ ! -f "$COMMIT_MSG_FILE" ]; then
    echo "[claudemd-gate] WARNING: $COMMIT_MSG_FILE not found — skipping marker check." >&2
    exit 0
fi

# Marker format: [claude-md-reviewed: YYYY-MM-DD]
if ! grep -Eq '\[claude-md-reviewed: [0-9]{4}-[0-9]{2}-[0-9]{2}\]' "$COMMIT_MSG_FILE"; then
    cat >&2 <<EOF
[claudemd-gate] BLOCKED: CLAUDE.md change requires a review marker in the commit message.

Add this line to your commit message body (today's date, ISO 8601):

  [claude-md-reviewed: $(date -u +%Y-%m-%d)]

This marker certifies you applied the 4-question test from
docs/CLAUDE-MD-GOVERNANCE.md before changing CLAUDE.md. The check is mechanical
to protect future-you from one-line drift; the test is the part that matters.
EOF
    exit 1
fi

echo "[claudemd-gate] CLAUDE.md governance checks passed."
exit 0
