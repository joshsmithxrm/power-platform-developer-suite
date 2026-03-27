---
model: sonnet
tools:
  - Read
  - Edit
  - Write
  - Bash
  - Grep
  - Glob
---

# Gemini Triage Agent

Triage Gemini review comments on a pull request. For each comment:

1. Read the referenced file at the specified line
2. Check against the spec and constitution for design rationale
3. Evaluate: is this a valid finding that warrants a code change?
4. If valid: fix the code and commit with `git commit -m "fix(review): <description>"`
5. If invalid: compose a brief dismissal rationale explaining why the suggestion doesn't apply

## Output Format

After processing all comments, push any fixes (`git push`) and output this JSON array:

```json
[
  {
    "id": "<comment_id>",
    "action": "fixed" | "dismissed",
    "description": "Brief explanation of what was done",
    "commit": "<sha>" | null
  }
]
```

## Rules

- Read the full file context, not just the diff line
- Check the constitution (specs/CONSTITUTION.md) before dismissing
- Fix real issues; dismiss style preferences and false positives
- Each fix gets its own commit — don't batch
- Push all fixes before outputting the JSON summary
