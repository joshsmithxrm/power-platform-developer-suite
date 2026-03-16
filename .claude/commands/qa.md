# QA — Blind Verification

Dispatches a **fresh agent with no source code access** to verify that implemented features actually work. The verifier interacts with the running product exactly as a user would — CDP for extension, CLI for commands, MCP Inspector for tools. If it can't see the feature working, the feature isn't done.

**Why this exists:** The builder has confirmation bias. It wrote the code, it "knows" it works. A blind verifier can't rationalize — it either sees the right thing on screen or it doesn't.

## Usage

`/qa` — auto-detect from recent commits
`/qa extension` — verify extension changes via CDP
`/qa cli` — verify CLI changes by running commands
`/qa mcp` — verify MCP changes via Inspector
`/qa "SELECT TOP 5 should return 5 rows"` — explicit behavior description

## Process

### Step 1: Build the Behavior Checklist

**If $ARGUMENTS is a quoted description:** Use it directly as the behavior to verify.

**If $ARGUMENTS is a mode (extension/cli/mcp) or empty:** Auto-generate from recent work:

```bash
git log --since="4 hours ago" --format="%s%n%b" --no-merges
```

For each commit, extract observable behaviors:
- `feat(query): add X` → "X should be visible/functional in the UI"
- `fix(ext): guard against Y` → "Y scenario should not crash"
- `feat(extension): add Z to panel` → "Z should render in the panel"

**Be specific.** Not "query works" — instead: "Execute `SELECT TOP 5 name FROM account`, verify exactly 5 rows in the results table." Not "env colors work" — instead: "Switch to a sandbox environment, verify a yellow-ish border appears on the toolbar."

### Step 2: Detect Mode

From $ARGUMENTS or changed files:
- `src/PPDS.Extension/` → extension mode (CDP)
- `src/PPDS.Cli/Commands/` (not Serve/) → CLI mode (run commands)
- `src/PPDS.Mcp/` → MCP mode (Inspector)
- `src/PPDS.Cli/Tui/` → TUI mode (snapshot tests — no blind verification yet)
- Mixed → run applicable modes sequentially

### Step 3: Dispatch Blind Verifier

Launch a subagent with these constraints. **This is the critical part — the verifier MUST NOT have implementation context.**

#### Extension Mode — Verifier Prompt

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

## Verification Protocol

For EACH check below:
1. Perform the action using CDP commands
2. Take a screenshot as evidence: `node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/qa-{check-number}.png`
3. LOOK at the screenshot (use Read tool on the PNG)
4. Read relevant DOM text: `node src/PPDS.Extension/tools/webview-cdp.mjs text "<selector>" --ext "power-platform-developer-suite"`
5. Check logs for errors: `node src/PPDS.Extension/tools/webview-cdp.mjs logs`
6. Report PASS or FAIL with evidence

If a check FAILS:
- Describe exactly what you SAW vs what was EXPECTED
- Include the screenshot path
- Include any error messages from logs
- Do NOT speculate about why it failed — you can't see the code

## Setup

First, launch VS Code with a fresh build:
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close  # clean up any prior session
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
```

## Checks

{paste generated checklist here}

## Teardown

After all checks:
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

## Report Format

Return a structured report:

| # | Check | Status | Evidence |
|---|-------|--------|----------|
| 1 | description | PASS/FAIL | screenshot path + what you saw |
| 2 | ... | ... | ... |

### Error Log
{any errors from `logs` commands}

### Verdict: PASS (all green) / FAIL (N issues found)

{for each FAIL: exact description of expected vs actual}
```

#### CLI Mode — Verifier Prompt

Same structure but:
- Tool access: Bash (only for running `ppds` CLI commands or `dotnet run`)
- Build first: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0 -v q`
- Each check: run the command, capture stdout/stderr, compare to expected
- Evidence: command output (not screenshots)

#### MCP Mode — Verifier Prompt

Same structure but:
- Tool access: Bash (only for `npx @modelcontextprotocol/inspector`)
- Each check: call the MCP tool, capture response JSON, compare to expected
- Evidence: response payloads

### Step 4: Evaluate Results

Read the verifier's report.

**If ALL PASS:** QA complete. Proceed with commit/next phase.

**If ANY FAIL:**

1. Present the failure report to the builder (yourself or the orchestrator)
2. The failures include screenshots and exact descriptions — use these to fix
3. After fixing, **re-invoke `/qa`** with the same checklist
4. The re-invocation dispatches a **fresh verifier** (no memory of prior attempt)
5. Repeat until ALL PASS

**There is no escape hatch.** You cannot skip a failing check. You cannot mark a FAIL as "known issue" and proceed. Either fix it or remove the check because the feature was descoped — in which case, explain why to the user.

### Step 5: Report

```
## QA Results

Mode: extension | Checks: 8 | Pass: 7 | Fail: 1

| # | Check | Status | Evidence |
|---|-------|--------|----------|
| 1 | Panel renders | PASS | $TEMP/qa-1.png |
| ... | ... | ... | ... |

### Verdict: FAIL — 1 issue must be resolved

**Check 4 FAILED:** Expected status bar to show "via Dataverse" but saw "via TDS".
Screenshot: $TEMP/qa-4.png
```

## Rules

1. **Blind verifier** — the QA agent never sees source code. Period.
2. **Evidence required** — every PASS/FAIL must have a screenshot or output capture.
3. **Loop until clean** — failures trigger fix → re-verify. No exceptions.
4. **Fresh agent each run** — don't resume a prior verifier. Each run is independent.
5. **Specificity** — "query works" is not a check. "SELECT TOP 5 returns 5 rows" is.
6. **Don't fix during QA** — the verifier reports, the builder fixes. Separation of concerns.
7. **Checklist before dispatch** — always show the checklist to the user/orchestrator before dispatching the verifier. This catches bad checks early.
