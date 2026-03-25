# Status

Display current workflow enforcement state for the active branch.

## Usage

`/status` — Show what workflow steps have been completed and what's pending.

## Process

1. Read `.workflow/state.json` in the repo root.
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

### Pipeline Status

If `.workflow/pipeline.log` exists, also display pipeline progress:

1. Parse the log file for `START` and `DONE` entries
2. Show each stage with its status and duration:

```
PIPELINE STATUS:
  ✓ worktree     (2s)
  ✓ implement    (13m 24s)
  ✓ gates        (1m 14s)
  → verify       (running for 2m 30s)
  ○ review       (pending)
  ○ pr           (pending)
  ○ retro        (pending)
```

- `✓` = completed (has both START and DONE)
- `→` = in progress (has START but no DONE)
- `○` = pending (no START yet)
- `✗` = failed (DONE with non-zero exit)

Include the overall pipeline duration and plan file path from the first log entry.

## Notes

- This command does NOT write to `.workflow/state.json`. It is read-only.
- On `main` branch: output "On main branch — workflow enforcement applies to feature branches only."
- **Simple factual output only.** Do not add suggestions, analysis, or "would you like to..." prompts. Just show the state and stop.
- When pipeline is running, the most useful info is: which stage is active and how long it has been running. Lead with that.
