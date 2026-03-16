# Status

Display current workflow enforcement state for the active branch.

## Usage

`/status` — Show what workflow steps have been completed and what's pending.

## Process

1. Read `.claude/workflow-state.json` in the repo root.
2. If the file does not exist, output: "No workflow state tracked. Start with /gates, /verify, /qa, or /review to begin tracking."
3. If the file exists, read it and display the following:

### Output Format

```
WORKFLOW STATE for branch {branch}:
  {✓|✗} Gates {passed (commit {ref}, current)|passed (commit {ref}, STALE — HEAD is {head})|not run}
  {✓|✗} Extension {verified|not verified}
  {✓|✗} TUI {verified|not verified}
  {✓|✗} MCP {verified|not verified}
  {✓|✗} CLI {verified|not verified}
  {✓|✗} QA {surfaces tested: ext, tui|not completed}
  {✓|✗} Review {passed (N findings)|not completed}
  {✓|✗|⚠} PR {url|not created}
  Required before PR: {list of missing steps}
```

### Staleness Detection

Compare `gates.commit_ref` to current HEAD (`git rev-parse HEAD`). If they differ, gates are **STALE** — the code has changed since gates last ran.

### Verify Surfaces

Check each key in `verify` object (`ext`, `tui`, `mcp`, `cli`). Only show surfaces that have timestamps or are relevant to the current changes.

### Missing Steps

List what the PR gate hook would block on:
- Gates not current → "/gates"
- No verify entries → "/verify for affected surface"
- No QA entries → "/qa"
- No review → "/review"

## Notes

- This command does NOT write to `workflow-state.json`. It is read-only.
- On `main` branch: output "On main branch — workflow enforcement applies to feature branches only."
