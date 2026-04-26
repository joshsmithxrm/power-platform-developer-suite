---
name: shakedown
description: Structured multi-surface product validation — systematically test Extension, TUI, MCP, and CLI with parity comparison and architecture audit. Use before releases, after large features, or to kick the tires. Also supports a workflow-mode that exercises the full pipeline end-to-end in throwaway worktrees.
---

# Shakedown

A structured, multi-interface product validation session. Unlike `/qa`
(automated, single surface) or `/verify` (self-check), shakedown is
collaborative, comprehensive, and produces a documented findings report.

Two modes, one skill:

- **Default mode** — product-level shakedown of Extension / TUI / MCP / CLI
  against live Dataverse, with parity comparison and an architecture audit.
  Use this when the question is "does the product work".
- **Workflow Mode** — behavioral integration test of the development
  workflow itself (hooks, pipeline, skills). Spins up throwaway worktrees,
  runs the pipeline end-to-end with `PPDS_SHAKEDOWN=1`, and produces a
  report. Use this when the question is "does the *workflow* work".

Pick the mode matching the question. Both modes share the same Phase 0
safety pre-checks below.

## When to Use (default mode)

- Pre-release milestone
- After a large feature lands across multiple surfaces
- After a refactor that touched multiple surfaces
- "I want to kick the tires on this thing"
- "Test everything"

## When to Use (workflow mode)

- Before shipping major workflow changes
- After modifying hooks, pipeline, or skills
- When you want to verify the full loop: start → implement → gates →
  verify → qa → review → converge → pr

## Phase 0: Safety Pre-checks

Before any other work (either mode), validate the safety guardrails are
configured and active.

1. **Verify env allowlist is configured** — read
   `.claude/settings.json` → `safety.shakedown_safe_envs`, or
   `$PPDS_SAFE_ENVS`. If neither lists at least one safe env, STOP and ask
   the user to configure (`docs/SAFE-SHAKEDOWN.md` for details).
2. **Verify active env is in the allowlist** — run `ppds env who` (or
   `ppds env current`) and confirm the displayed env matches an allowlist
   entry. The `shakedown-safety` hook will block downstream `ppds *`
   calls otherwise.
3. **Arm the shakedown write-block (sentinel file).** Before any other
   Bash work, write `.claude/state/shakedown-active.json` containing at
   least `started_at` (ISO-8601 UTC timestamp string, consumed by both
   the Python `shakedown-safety` hook and the C# `IShakedownGuard`) and,
   when available, `session_id`:

   ```bash
   python -c "import json, os; from datetime import datetime, timezone; \
     os.makedirs('.claude/state', exist_ok=True); \
     json.dump({'started_at': datetime.now(timezone.utc).isoformat(), \
                'session_id': os.environ.get('CLAUDE_SESSION_ID', '')}, \
               open('.claude/state/shakedown-active.json', 'w'))"
   ```

   The `shakedown-safety` hook reads this sentinel on every Bash call
   and refuses mutation verbs (`plugins deploy` without `--dry-run`,
   `<surface> create/update/delete`, `solutions import`, etc.) for the
   duration of the shakedown. The sentinel auto-expires after 24h so a
   crashed session cannot wedge the write-block for future sessions.

   **Why the sentinel and not just `export PPDS_SHAKEDOWN=1`?** Claude
   Code's Bash tool spawns a fresh shell per invocation — both inline
   `PPDS_SHAKEDOWN=1 ppds ...` prefixes and `export` from a prior call
   fail to propagate to this hook subprocess. The sentinel file is the
   reliable activation source from inside a Claude Code session. The
   env var is still honored as a secondary source for shell scripts and
   CI where sentinel-file management is awkward. (The env-var name is
   configurable via `safety.readonly_env_var` in settings.json; default
   is `PPDS_SHAKEDOWN`.)
4. **Acknowledge the model:** plugin deploys MUST be invoked with <!-- enforcement: T3 -->
   `--dry-run` during shakedown. To run a real deploy, delete the
   sentinel file (`rm .claude/state/shakedown-active.json`) and unset
   `PPDS_SHAKEDOWN` if it's also in the environment — deliberate action,
   there is no softer bypass.

See `docs/SAFE-SHAKEDOWN.md` for the full safety model and how to add envs
to the allowlist.

## Default Mode

### Phase 1: Scope Declaration

Ask the user which surfaces to test:
- [ ] Extension (VS Code webview panels)
- [ ] TUI (Terminal.Gui interactive)
- [ ] MCP (tool invocations)
- [ ] CLI (command execution)

All declared surfaces MUST get interactive verification — not just code audit. <!-- enforcement: T3 -->

### Phase 2: Test Matrix

Before testing begins, create an explicit test matrix:

1. Enumerate features per surface from specs and code:
   - Read `specs/` for feature specs
   - Read `src/` for implemented features
   - List each feature and which surfaces implement it

2. Create a checklist table:

| Feature | Extension | TUI | MCP | CLI | Notes |
|---------|-----------|-----|-----|-----|-------|
| Query execution | ☐ | ☐ | ☐ | ☐ | |
| Profile management | ☐ | ☐ | — | ☐ | |
| ... | | | | | |

3. Get user confirmation on the matrix before proceeding.

### Phase 3: Interactive Verification

Test each surface using the appropriate verification tool:

- **Extension:** Use `/ext-verify` — open panels, click buttons, type queries, take screenshots
- **TUI:** Use `/tui-verify` — launch, navigate, read text, send keystrokes
- **MCP:** Use `/mcp-verify` — invoke tools, validate responses
- **CLI:** Use `/cli-verify` — run commands, check stdout/stderr

For each feature in the matrix:
1. Exercise it in each applicable surface
2. Mark pass/fail in the matrix
3. Note any bugs or unexpected behavior
4. Take evidence (screenshots, output) for failures

### Phase 4: Parity Comparison

For features that exist in multiple surfaces:

1. Compare behavior side-by-side
2. Note differences (acceptable vs. bugs)
3. Assess "who does it better" for each feature
4. Document in a parity table:

| Feature | Extension | TUI | Better? | Notes |
|---------|-----------|-----|---------|-------|
| Query results | Sortable table | Fixed table | Ext | TUI needs column sorting |
| Profile selector | Dropdown | Dialog | TUI | TUI shows more detail |

### Phase 5: Architecture Audit

Automated + manual checks:

1. **Service bypass check:** Grep for direct `ServiceClient` usage outside pool
2. **Silent error check:** Grep for empty catch blocks or catches without logging
3. **Dead code check:** Look for commands/handlers that aren't wired up
4. **Handler wiring:** Verify all TUI dialogs are reachable from menus
5. **Constitution compliance:** Spot-check A1 (logic in services), A2 (single code path)

### Phase 6: Findings Document

Write findings to `docs/qa/{date}-{scope}.md`:

```markdown
# {Scope} QA Shakedown — {date}

## Surfaces Tested
{list}

## Bugs Found and Fixed
{numbered list with commit references}

## Bugs Found — Not Fixed
{numbered list with severity and recommended action}

## Test Matrix Results
{the completed matrix from Phase 2}

## Parity Comparison
{the parity table from Phase 4}

## Architecture Audit
{findings from Phase 5}

## Untested Areas
{features/surfaces explicitly not covered, with reason}
```

### Phase 7: Gap Check

Before declaring complete:
1. Enumerate features NOT tested (from the matrix)
2. Present to user for explicit sign-off on skipping
3. Do NOT declare the shakedown complete without this sign-off
4. **Disarm the shakedown write-block** — delete the sentinel file as
   the very last act, after sign-off on (1)–(3):

   ```bash
   rm .claude/state/shakedown-active.json
   ```

   A leftover sentinel keeps the write-block armed for any subsequent
   session in this worktree until it expires 24h later (or until the
   `session-start-workflow` hook self-heals it). Cleaning up explicitly
   is the rule; the staleness self-heal is a safety net, not the plan.

### Default-mode Rules

- No declaring "VERIFIED (code)" for interactive features — either test it interactively or mark as "NOT TESTED"
- Do not recommend deferring issues — present findings, let user decide disposition
- Background tasks that fail must be retried or investigated, not dismissed
- AI MUST actually use the product. A passing test suite is not a shakedown. <!-- enforcement: T3 -->

## Workflow Mode

End-to-end behavioral verification of the development workflow. Creates
throwaway worktrees from the current branch, runs the pipeline in each,
and verifies the entire flow works.

### Step 0: Set Phase

```bash
python scripts/workflow-state.py set phase shakedown
```

### Step 1: Select Test Paths

`$ARGUMENTS` may contain `--paths feature,bug,resume` to select which
scenarios to run.

Available scenarios:
- **feature**: Full feature development path (design → implement → ship)
- **bug**: Bug fix path (direct implement → ship)
- **resume**: Partial-state resume test

If no paths specified, run `feature` and `bug` by default.

### Step 2: Create Throwaway Worktrees

For each selected scenario:

```bash
# Create worktree from CURRENT branch (not main) — inherits modified
# .claude/, scripts/, specs/
git worktree add .worktrees/shakedown-<scenario> --detach HEAD
```

### Step 3: Run Pipelines

For each worktree, run the pipeline with `PPDS_SHAKEDOWN=1`:

```bash
cd .worktrees/shakedown-<scenario>
PPDS_SHAKEDOWN=1 python scripts/pipeline.py --worktree . --spec specs/SPEC-TEMPLATE.md --dry-run
```

Use `--dry-run` mode — we're testing the orchestration logic, not running
real AI sessions.

`PPDS_SHAKEDOWN=1` ensures:
- No `gh pr create` (PR stage logs `PR_SKIPPED_SHAKEDOWN`)
- No `gh issue create` or `gh issue comment` (retro filing suppressed)
- No desktop notifications (`notify.py` exits 0)
- Stop hook exits 0 immediately

### Step 4: Collect Results

Read `.workflow/pipeline-result.json` from each worktree. Aggregate:
- Which scenarios passed/failed
- Stage durations
- Any errors or timeouts

### Step 5: Run Retro

Run retro across all shakedown session transcripts:

```bash
PPDS_SHAKEDOWN=1 python -c "
import sys; sys.path.insert(0, 'scripts')
from retro_helpers import discover_transcripts, extract_transcript_signals
for wt in ['.worktrees/shakedown-feature', '.worktrees/shakedown-bug']:
    for t in discover_transcripts(wt):
        signals = extract_transcript_signals(t)
        print(f'{t}: corrections={len(signals[\"user_corrections\"])}, failures={len(signals[\"tool_failures\"])}')
"
```

### Step 6: Produce Report

Write `.workflow/shakedown-report.json`:

```json
{
  "timestamp": "<ISO>",
  "scenarios": {
    "feature": {"status": "pass|fail", "duration": "Ns", "error": null},
    "bug": {"status": "pass|fail", "duration": "Ns", "error": null}
  },
  "findings": [],
  "retro_summary": "..."
}
```

### Step 7: Cleanup

Remove throwaway worktrees:

```bash
git worktree remove .worktrees/shakedown-feature --force
git worktree remove .worktrees/shakedown-bug --force
```

### Workflow-mode Rules

1. Always use `PPDS_SHAKEDOWN=1` — never file real issues or create real
   PRs from shakedown
2. Worktrees branch from current branch, not main — this tests the
   current code
3. `--dry-run` mode tests orchestration without AI sessions
4. Clean up ALL worktrees even if some scenarios fail
5. Report findings, don't try to fix them — shakedown is read-only
