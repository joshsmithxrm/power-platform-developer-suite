---
name: shakedown
description: Structured multi-surface product validation — systematically test Extension, TUI, MCP, and CLI with parity comparison and architecture audit. Use before releases, after large features, or to kick the tires.
---

# Shakedown

A structured, multi-interface product validation session. Unlike `/qa` (automated, single surface) or `/verify` (self-check), shakedown is collaborative, comprehensive, and produces a documented findings report.

## When to Use

- Pre-release milestone
- After a large feature lands across multiple surfaces
- After a refactor that touched multiple surfaces
- "I want to kick the tires on this thing"
- "Test everything"

## Process

### Phase 1: Scope Declaration

Ask the user which surfaces to test:
- [ ] Extension (VS Code webview panels)
- [ ] TUI (Terminal.Gui interactive)
- [ ] MCP (tool invocations)
- [ ] CLI (command execution)

All declared surfaces MUST get interactive verification — not just code audit.

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

## Rules

- No declaring "VERIFIED (code)" for interactive features — either test it interactively or mark as "NOT TESTED"
- Do not recommend deferring issues — present findings, let user decide disposition
- Background tasks that fail must be retried or investigated, not dismissed
- AI MUST actually use the product. A passing test suite is not a shakedown.
