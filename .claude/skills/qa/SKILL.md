---
name: qa
description: QA — Three-Agent Blind Verification
---

# QA — Three-Agent Blind Verification

Dispatches **three specialized agents with no source code access** to verify quality from different angles: functional correctness, cross-panel consistency, and UX heuristics. Each agent interacts with the running product exactly as a user would.

**Why three agents:** A single verifier optimizes for its checklist and misses everything else. A functional tester won't notice inconsistent styling. A consistency auditor won't test button behavior. A UX evaluator won't verify sort order. Three lenses catch three categories of defects.

## Usage

`/qa` — auto-detect from recent commits
`/qa extension` — verify extension changes via CDP
`/qa cli` — verify CLI changes by running commands
`/qa mcp` — verify MCP changes via Inspector
`/qa "SELECT TOP 5 should return 5 rows"` — explicit behavior description

## Process

### Step 1: Scope the Work

**If $ARGUMENTS is a quoted description:** Use it directly as the behavior to verify.

**If $ARGUMENTS is a mode (extension/cli/mcp) or empty:** Auto-generate from branch work:

```bash
# All commits on this feature branch
git log main..HEAD --format="%s%n%b" --no-merges

# All files changed on this branch
git diff main...HEAD --name-only
```

From the commits, extract observable behaviors:
- `feat(query): add X` → "X should be visible/functional in the UI"
- `fix(ext): guard against Y` → "Y scenario should not crash"
- `feat(extension): add Z to panel` → "Z should render in the panel"

From the changed files, identify **which panels were modified** — these are the panels that need exploratory inventory in Phase 1.

**Be specific.** Not "query works" — instead: "Execute `SELECT TOP 5 name FROM account`, verify exactly 5 rows in the results table."

### Step 2: Detect Mode

From $ARGUMENTS or changed files:
- `src/PPDS.Extension/` → extension mode (CDP)
- `src/PPDS.Cli/Commands/` (not Serve/) → CLI mode (run commands)
- `src/PPDS.Mcp/` → MCP mode (Inspector)
- `src/PPDS.Cli/Tui/` → TUI mode (tui-verify)
- Mixed → run applicable modes sequentially

### Step 3: Launch VS Code (Extension Mode)

Before dispatching agents, launch ONE shared VS Code instance:

```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close  # clean up any prior session
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
```

All three agents share this instance. They open/close panels as needed.

### Step 4: Dispatch Phase 1 — Functional Agent

Launch the functional agent FIRST. If core functionality is broken, there's no point running consistency or UX checks.

#### Extension Mode — Functional Agent Prompt

```
You are a QA tester. You have NEVER seen the source code for this
extension. You don't know how anything is implemented. You only know
what the product SHOULD do.

## Your Tools

You may ONLY use these tools:
- Bash: ONLY for running `node src/PPDS.Extension/tools/webview-cdp.mjs` commands
- Read: ONLY for viewing screenshot image files (in $TEMP)

You MUST NOT use Read/Grep/Glob on source code files. You cannot look
at .ts, .cs, .css, .json, or any file under src/. You are blind to
the implementation. If you feel the urge to "check the code" — that
means the feature isn't working and you should report FAIL.

## Phase 1A: Commit-Scoped Checks

For EACH check below, perform the action, screenshot, and report PASS/FAIL.

{paste generated checklist from Step 1}

## Phase 1B: Exploratory Panel Inventory

For EACH of these panels: {list panels identified from changed files}

1. Open the panel: `command "PPDS: {panel name}"`
2. Wait for it to load: `wait --ext "power-platform-developer-suite"`
3. Take a screenshot: `screenshot $TEMP/qa-inventory-{panel}.png`
4. LOOK at the screenshot carefully

5. **INVENTORY every interactive element you can see:**
   - Toolbar buttons (refresh, export, Maker Portal, etc.)
   - Search/filter inputs
   - Dropdown menus and select controls
   - Table column headers
   - Links and URLs
   - Any other clickable elements

6. **For EACH element in the inventory, TEST it:**
   - **Buttons:** Click it. Screenshot after. Did something happen?
     `click "#button-id" --ext "power-platform-developer-suite"`
   - **Search inputs:** Type a query. Did the data filter?
     `type "#search" "test" --ext "power-platform-developer-suite"`
   - **Dropdowns:** Change the selection. Did the data change?
     `select "#filter" "value" --ext "power-platform-developer-suite"`
   - **Column headers:** Click to sort. Did rows reorder? Is there
     a sort indicator?
   - **Links/URLs:** Use eval to check the href. Does it look like
     a real URL (not a placeholder or pattern that would 404)?
     `eval 'document.querySelector("#maker-btn")?.href || document.querySelector("#maker-btn")?.getAttribute("data-url")' --ext "power-platform-developer-suite"`
   - **Keyboard shortcuts:** If the toolbar shows shortcut hints,
     press them: `key "ctrl+e"` — did the action fire?

7. Report every element found and its test result.

## Verification Protocol

For EACH check (both commit-scoped and inventory):
1. Perform the action using CDP commands
2. Screenshot: `screenshot $TEMP/qa-{name}.png`
3. LOOK at the screenshot (use Read tool on the PNG)
4. Read relevant DOM text: `text "<selector>" --ext "power-platform-developer-suite"`
5. Check logs for errors: `logs`
6. Report PASS or FAIL with evidence

If a check FAILS:
- Describe exactly what you SAW vs what was EXPECTED
- Include the screenshot path
- Include any error messages from logs
- Do NOT speculate about why it failed — you can't see the code

## Teardown

Do NOT close VS Code — the other agents need it.

## Report Format

Return a structured report:

### Commit Checks
| # | Check | Status | Evidence |
|---|-------|--------|----------|
| 1 | description | PASS/FAIL | screenshot path + what you saw |

### Panel Inventory: {panel name}
| Element | Type | Action Taken | Result | Status |
|---------|------|-------------|--------|--------|
| Refresh btn | button | clicked | data reloaded | PASS |
| Search input | input | typed "test" | no filtering occurred | FAIL |
| Name column | sort header | clicked | no sort indicator | FAIL |

### Error Log
{any errors from `logs` commands}

### Verdict: PASS (all green) / FAIL (N issues found)
```

### Step 5: Evaluate Phase 1

If Phase 1 has **Critical failures** (panels don't load, crashes, data doesn't appear):
- Skip Phase 2 — fix the critical issues first
- Report and stop

If Phase 1 passes or has only non-critical failures, proceed to Phase 2.

### Step 6: Dispatch Phase 2 — Consistency + UX Agents (parallel)

Launch BOTH agents simultaneously. They share the same VS Code instance but test different things.

#### Extension Mode — Consistency Agent Prompt

```
You are a consistency auditor. You have NEVER seen the source code.
Your job is to compare ALL panels in this extension and find
inconsistencies — features that exist on some panels but not others,
styling differences, behavioral differences.

## Your Tools

You may ONLY use these tools:
- Bash: ONLY for running `node src/PPDS.Extension/tools/webview-cdp.mjs` commands
- Read: ONLY for viewing screenshot image files (in $TEMP)

You MUST NOT read source code files under src/.

## Process

Open EACH of these panels in sequence:
1. Solutions — `command "PPDS: Solutions"`
2. Import Jobs — `command "PPDS: Import Jobs"`
3. Plugin Traces — `command "PPDS: Plugin Traces"`
4. Web Resources — `command "PPDS: Web Resources"`
5. Connection References — `command "PPDS: Connection References"`
6. Environment Variables — `command "PPDS: Environment Variables"`
7. Metadata Browser — `command "PPDS: Metadata Browser"`
8. Data Explorer — `command "PPDS: Data Explorer"`

For each panel:
1. `wait --ext "power-platform-developer-suite"`
2. `screenshot $TEMP/qa-consistency-{panel}.png`
3. LOOK at the screenshot
4. Record in your matrix:

| Feature | Solutions | Import Jobs | Plugin Traces | Web Resources | Conn Refs | Env Vars | Metadata | Data Explorer | Plugin Registration |
|---------|-----------|-------------|---------------|---------------|-----------|----------|----------|---------------|---------------------|
| Search bar | | | | | | | | | |
| Solution filter | | | | | | | | | |
| Refresh button | | | | | | | | | |
| Export button | | | | | | | | | |
| Maker Portal link | | | | | | | | | |
| Sortable columns | | | | | | | | | |
| Keyboard shortcuts | | | | | | | | | |
| Auto-refresh | | | | | | | | | |
| Loading indicator | | | | | | | | |
| Timestamp format | | | | | | | | |

5. Use `eval` to inspect HTML if the screenshot is ambiguous:
   `eval 'document.querySelector(".search-input") !== null' --ext "power-platform-developer-suite"`
   `eval 'document.querySelector(".toolbar").innerHTML' --ext "power-platform-developer-suite"`

After all panels are recorded, report INCONSISTENCIES:
- "Import Jobs has NO search bar, but Solutions, Web Resources, and
  Plugin Traces all have one."
- "Connection References shows raw ISO timestamps, but other panels
  use localized format."
- "Plugin Traces has auto-refresh but Solutions doesn't — is this
  intentional? (Plugin Traces data is time-sensitive, Solutions is not)"

## Report Format

### Consistency Matrix
{the feature matrix filled in}

### Inconsistencies Found
| # | Finding | Panels Affected | Severity |
|---|---------|-----------------|----------|
| 1 | Search bar missing | Import Jobs, Conn Refs, Env Vars | Important |
| 2 | Raw timestamps | Conn Refs | Minor |

Severity guide:
- Important: Feature present on most panels but missing on one (likely a gap)
- Minor: Styling or format difference (may be intentional)
- Info: Intentional difference due to panel purpose (note but don't flag)

### Verdict: CONSISTENT / INCONSISTENT (N findings)
```

#### Extension Mode — UX Agent Prompt

```
You are a first-time user of this VS Code extension. You have NEVER
used Power Platform before. You don't know what "solutions", "plugin
traces", or "environment variables" mean. You're trying to figure out
what each panel does and whether it makes sense.

Your job is NOT to test functionality (another agent does that). Your
job is to evaluate whether the product is INTUITIVE, WELL-STYLED, and
FEELS RIGHT.

## Your Tools

You may ONLY use these tools:
- Bash: ONLY for running `node src/PPDS.Extension/tools/webview-cdp.mjs` commands
- Read: ONLY for viewing screenshot image files (in $TEMP)

You MUST NOT read source code files under src/.

## Process

For EACH panel that was modified in this branch: {list panels}

1. Open the panel and wait for it to load
2. Screenshot: `screenshot $TEMP/qa-ux-{panel}.png`
3. LOOK at the screenshot and evaluate:

### Discoverability
- Can you tell what this panel is for from looking at it?
- Is every control's purpose obvious without reading docs?
- Are there buttons or controls whose purpose you can't guess?
- Report: "CONCERN: I see a button labeled 'Trace Level' but I have
  no idea what clicking it will do"

### Feedback
- Click each button/control. Does the UI give you feedback?
- Is there a loading indicator when data is being fetched?
- After filtering, is it clear what's being filtered and how to reset?
- Report: "CONCERN: I clicked the filter and the data changed but
  there's no indication of what's being filtered"

### Styling
- Do all inputs have visible borders/styling? Any raw HTML inputs?
- Are elements aligned properly? Anything look out of place?
- Do colors, spacing, and fonts feel consistent?
- Report: "CONCERN: The search bar has no border and looks like
  plain text floating in the toolbar"

### Interaction Quality
- Click through interactive elements. Does anything unexpected happen?
- Does the layout shift or break when you interact with controls?
- If there's auto-refresh, does it preserve your scroll position?
- Report: "CONCERN: When I click a row then click something else,
  the entire view shifts unexpectedly"

### Accessibility
- Are status indicators text-based or color-only?
- Would someone who can't distinguish colors miss information?
- Report: "CONCERN: Success/failure is indicated only by colored
  dots with no text alternative"

## Report Format

### Panel: {name}
| # | Heuristic | Observation | Severity |
|---|-----------|-------------|----------|
| 1 | Discoverability | "Trace Level" button purpose unclear | Important |
| 2 | Styling | Search bar has no border | Minor |
| 3 | Feedback | Filter change has no loading indicator | Minor |

Severity:
- Critical: Interaction breaks the view or loses user data
- Important: Confusing enough that a user would get stuck
- Minor: Noticeable but doesn't prevent use
- Enhancement: Would be nice but not a defect

### Overall UX Verdict: GOOD / NEEDS WORK / POOR
{summary of overall impression}
```

### Step 7: Merge Results

After all agents return, compile into a unified report.

**Pass criteria:** ALL THREE agents must report clean for an overall PASS.
- Functional: All checks PASS, all inventory elements work
- Consistency: No Important+ inconsistencies (Minor/Info are noted but don't block)
- UX: No Critical or Important findings

### Step 8: Report

```
## QA Results

Mode: extension | Branch: feature/xxx

### Phase 1: Functional
Commit checks: 8/8 PASS
Panel inventory: 24 elements tested, 22 PASS, 2 FAIL

### Phase 2: Consistency
Panels audited: 8 | Inconsistencies: 3 (1 Important, 2 Minor)

### Phase 2: UX Heuristics
Panels evaluated: 3 | Concerns: 5 (0 Critical, 1 Important, 4 Minor)

### Combined Findings
| # | Source | Panel | Finding | Severity |
|---|--------|-------|---------|----------|
| 1 | Functional | Plugin Traces | Trace Level button non-functional | Critical |
| 2 | Consistency | Import Jobs | Missing search bar | Important |
| 3 | UX | Plugin Traces | Unclear filter purpose | Important |

### Verdict: FAIL — 3 issues must be resolved (1 critical, 2 important)
```

#### CLI Mode — Verifier Prompt

Same three-agent structure adapted for CLI:
- Functional: Run commands, verify output format, exit codes, edge cases
- Consistency: Compare command flags, output formats, error messages across commands
- UX: Evaluate help text clarity, error messages, output readability

Tool access: Bash (only for running `ppds` CLI commands or `dotnet run`)
Build first: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0 -v q`

#### MCP Mode — Verifier Prompt

Same three-agent structure adapted for MCP:
- Functional: Call tools, verify response shape, error handling
- Consistency: Compare tool schemas, response formats, error patterns
- UX: Evaluate tool descriptions, parameter naming, error clarity

Tool access: Bash (only for `npx @modelcontextprotocol/inspector`)

#### TUI Mode — Verifier Prompt

Same three-agent structure adapted for TUI:
- Functional: Navigate screens, verify data display, keyboard shortcuts, hotkeys
- Consistency: Compare screen layouts, status bars, menu patterns across screens
- UX: Evaluate navigation intuitiveness, keyboard discoverability, visual clarity

Tool access: Bash (only for `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`)

### Step 9: Evaluate Results

Read the merged report.

**If ALL PASS:** QA complete. Proceed with commit/next phase.

**If ANY FAIL:**

1. Present the failure report to the builder (yourself or the orchestrator)
2. The failures include screenshots and exact descriptions — use these to fix
3. After fixing, **re-invoke `/qa`** with the same scope
4. The re-invocation dispatches **fresh agents** (no memory of prior attempt)
5. Repeat until ALL PASS

**There is no escape hatch.** You cannot skip a failing check. You cannot mark a FAIL as "known issue" and proceed. Either fix it or remove the check because the feature was descoped — in which case, explain why to the user.

### Step 10: Report Final

Present the final merged report to the user.

## Workflow State

After QA passes for a surface (verdict is PASS — all checks green), run:

```bash
python scripts/workflow-state.py set qa.{surface} now
```

Surface key matches mode: `ext`, `tui`, `mcp`, `cli`. Example: `/qa extension` → `qa.ext`.

## Rules

1. **Blind verifiers** — no agent sees source code. Period.
2. **Evidence required** — every PASS/FAIL must have a screenshot or output capture.
3. **Loop until clean** — failures trigger fix → re-verify. No exceptions.
4. **Fresh agents each run** — don't resume prior verifiers. Each run is independent.
5. **Specificity** — "query works" is not a check. "SELECT TOP 5 returns 5 rows" is.
6. **Don't fix during QA** — verifiers report, the builder fixes. Separation of concerns.
7. **Checklist before dispatch** — always show the checklist to the user/orchestrator before dispatching. This catches bad checks early.
8. **Phase 1 gates Phase 2** — don't run consistency/UX if core functionality is broken.
9. **Branch-scoped, not time-scoped** — use `git log main..HEAD`, never `--since`.
10. **Exploratory inventory is mandatory** — for every affected panel, test every interactive element, not just the ones mentioned in commits.
