#!/usr/bin/env bash
# commit-msg gate: enforce CLAUDE.md governance marker.
#
# Fires when the commit touches any CLAUDE.md (root or nested). Requires the
# commit message to contain a [claude-md-reviewed: YYYY-MM-DD] marker.
#
# This check lives in commit-msg (not pre-commit) because pre-commit runs
# before the commit message is finalized; reading .git/COMMIT_EDITMSG there
# would return the previous commit's message.
#
# See docs/CLAUDE-MD-GOVERNANCE.md for the rationale.

set -euo pipefail

COMMIT_MSG_FILE="${1:-}"

if [ -z "$COMMIT_MSG_FILE" ] || [ ! -f "$COMMIT_MSG_FILE" ]; then
    echo "[claudemd-marker] WARNING: commit-msg file not provided — skipping marker check." >&2
    exit 0
fi

# Find every staged CLAUDE.md. If none, marker is not required.
STAGED_CLAUDE_MD=$(git diff --cached --name-only --diff-filter=ACMR -- ':(glob)**/CLAUDE.md' ':(glob)CLAUDE.md' || true)

if [ -z "$STAGED_CLAUDE_MD" ]; then
    exit 0
fi

# Strip comment lines (starting with #) from the message before checking —
# git's prepared message may include scissors/comment lines that the user
# never intends to commit.
MESSAGE_BODY=$(grep -v '^#' "$COMMIT_MSG_FILE" || true)

# Marker format: [claude-md-reviewed: YYYY-MM-DD]
if ! printf '%s' "$MESSAGE_BODY" | grep -Eq '\[claude-md-reviewed: [0-9]{4}-[0-9]{2}-[0-9]{2}\]'; then
    cat >&2 <<EOF
[claudemd-marker] BLOCKED: CLAUDE.md change requires a review marker in the commit message.

Add this line to your commit message body (today's date, ISO 8601):

  [claude-md-reviewed: $(date -u +%Y-%m-%d)]

This marker certifies you applied the 4-question test from
docs/CLAUDE-MD-GOVERNANCE.md before changing CLAUDE.md. The check is mechanical
to protect future-you from one-line drift; the test is the part that matters.
EOF
    exit 1
fi

echo "[claudemd-marker] CLAUDE.md governance marker present."
exit 0
