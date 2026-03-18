# Reset

Clear workflow state for the current branch.

## Usage

`/reset` — Delete `.workflow/state.json` and start fresh.

## Process

1. Check if `.workflow/state.json` exists
2. If it exists, delete it
3. Print confirmation: "Workflow state cleared. Run /gates to begin tracking."
4. If it doesn't exist, print: "No workflow state to clear."

## When to Use

- Corrupted workflow state (JSON parse errors)
- Want to restart the workflow pipeline on the current branch
- State is stale and doesn't match actual progress

## Rules

1. This is a destructive operation — it erases all tracked progress (gates, verify, QA, review timestamps)
2. Does NOT delete the `.workflow/` directory itself — only the `state.json` file
3. Does NOT affect git history or branch state
4. Does NOT require confirmation — the state file is ephemeral and easily recreated by running workflow steps
