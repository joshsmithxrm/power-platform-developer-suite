# Retro Action Items Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement all recommendations from the 2026-03-23 retrospective — fix drift, improve /implement orchestration, add CDP panel targeting, reinforce pipeline chaining, and add /design handoff.

**Architecture:** Skill/command edits (Tasks 1-5, 8-9) are independent markdown changes. CDP improvement (Tasks 6-7) is a code change with test coverage. Hook edits (Tasks 8-9) are Python script changes.

**Tech Stack:** Markdown, Python, TypeScript/JavaScript (webview-cdp.mjs), Node.js

**Dependencies:**
```
Task 1 (CLAUDE.md cleanup)     ─┐
Task 2 (/implement enhancements) ─┤
Task 3 (/design handoff)         ─┤─── all independent, parallelize freely
Task 4 (retro skill fix)         ─┤
Task 5 (misc skill fixes)        ─┤
Task 8 (pre-commit hook)         ─┤
Task 9 (post-commit hook)        ─┘
Task 6 (CDP code) ──► Task 7 (ext-verify docs)   ── sequential
```

**Out of scope (tracked for next session):**
- `ext-panels` skill hardcoded file list (staleness risk) — revisit when adding new panels
- `qa` command hardcoded 8-panel list — consider dynamic discovery via `connect` once `--panel` ships
- `cleanup` skill length (206 lines) — functional, low priority

---

### Task 1: CLAUDE.md Cleanup

Single pass over CLAUDE.md addressing all drift and reinforcement items.

**Files:**
- Modify: `CLAUDE.md:71` (/implement mandatory)
- Modify: `CLAUDE.md:73-77` (skill type confusion → /verify surface)
- Modify: `CLAUDE.md:99-100` (pipeline chaining reinforcement)

- [ ] **Step 1: Fix skill invocation at lines 73-77**

Replace:
```markdown
   - Extension changed → /ext-verify (screenshots required)
   - TUI changed → /tui-verify (PTY interaction required)
   - MCP changed → /mcp-verify (tool invocation required)
   - CLI changed → /cli-verify (run the command)
```
with:
```markdown
   - Extension changed → /verify extension (screenshots required)
   - TUI changed → /verify tui (PTY interaction required)
   - MCP changed → /verify mcp (tool invocation required)
   - CLI changed → /verify cli (run the command)
```

- [ ] **Step 2: Make /implement mandatory at line 71**

Replace:
```markdown
4. /implement <plan-path>
```
with:
```markdown
4. /implement <plan-path> — MANDATORY once a plan exists, even in the same session. The plan is the handoff boundary: everything before is design, everything after is structured execution.
```

- [ ] **Step 3: Reinforce pipeline chaining after line 100**

After the existing pipeline chaining paragraph, add a new line:
```markdown
After completing implementation commits, proceed IMMEDIATELY to gates. Do NOT stop to present a summary table of what was committed. The commit messages already document the work. Your next action after the last commit is `/gates`.
```

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "fix: CLAUDE.md cleanup — verify invocation, /implement mandatory, pipeline chaining"
```

---

### Task 2: /implement Enhancements

Six additions to the implement command. Apply in order within the file.

**Files:**
- Modify: `.claude/commands/implement.md`

- [ ] **Step 1: Add findings reconciliation to Step 1**

After Step 1's bullet "Note the quality gates defined in the plan" (line 24), add:

```markdown
- **Findings reconciliation:** If the plan references a findings document (check for `**Findings:**` or links to a findings file), read it and extract all finding IDs (e.g., CC-01, V-15, CR-06). Cross-reference every finding ID against the plan text. Report any finding IDs that do not appear in the plan — these may have been accidentally dropped during plan authoring. Present the gap to the user before proceeding.
```

- [ ] **Step 2: Add shared-infrastructure scan to Step 1**

After the findings reconciliation bullet, add:

```markdown
- **Shared-infrastructure scan:** Identify files that appear in multiple phases (e.g., `message-types.ts`, `shared.css`, `dom-utils.ts`). Verify these files are modified in an earlier sequential phase, not in parallel phases. If a shared file appears in parallel phases, flag it: either serialize those modifications or designate one phase as the owner of the shared file.
```

- [ ] **Step 3: Add model selection guidance**

After Step 4 (Create Task Tracking, line 79) and before Step 5 (Execute Each Phase, line 80), add:

```markdown
### Step 4.5: Assess Model Selection

For each phase, assess complexity to choose the appropriate model for subagents:
- **Primary model (Opus):** Phases with >3 sub-steps, complex UI work (timelines, query builders, virtual scrolling), cross-cutting refactors touching >10 files, or Constitution-sensitive Dataverse service changes
- **Lighter model (Sonnet):** Mechanical phases — one-liner fixes, find-and-replace, boilerplate application, CSS-only changes, documentation updates

Do not hardcode model IDs in the plan. Assess at dispatch time based on the phase's actual complexity. When in doubt, use the primary model — the cost of a subagent re-dispatch exceeds the cost difference between models.
```

- [ ] **Step 4: Add per-subagent gate check to Step 5A**

In Step 5A's "Each agent prompt MUST include" list, after the "Reminder: no shell redirections" bullet (line 94), add:

```markdown
  - Self-check gate: before reporting completion, run the relevant gate checks for your changed files. For TypeScript/extension changes: `npm run typecheck:all --prefix src/PPDS.Extension` and `npx eslint --quiet {changed-files}`. For C# changes: `dotnet build {project}.csproj -v q`. Report gate results in your summary — do not silently suppress failures.
```

- [ ] **Step 5: Add shared-file collision warning to Step 5A**

After the "Maximize parallelism" bullet in Step 5A (line 95), add:

```markdown
- **Shared-file guard:** If multiple parallel agents will modify the same file (identified in Step 1's shared-infrastructure scan), either: (a) serialize those agents, (b) have the first agent create the shared additions and later agents import them, or (c) designate one agent as the file owner and have others list their additions as requirements for the owner.
```

- [ ] **Step 6: Add pipeline chaining reinforcement to Step 6**

At the end of Step 6F (Final State Check, after line 185), add:

```markdown
**G. Continue Pipeline**
After final state check passes, proceed IMMEDIATELY to the tail verification pipeline. Do NOT stop to present a summary. The pipeline is: gates → verify → qa → review → pr. Execute end-to-end unless a step fails.
```

- [ ] **Step 7: Fix Co-Authored-By format at line 145**

Replace:
```
  Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```
with:
```
  Co-Authored-By: {use the format from the system prompt}
```

- [ ] **Step 8: Commit**

```bash
git add .claude/commands/implement.md
git commit -m "feat: /implement — findings reconciliation, shared-infra scan, model selection, per-agent gates, pipeline chaining"
```

---

### Task 3: /design Handoff to /implement

**Files:**
- Modify: `.claude/skills/design/SKILL.md:55-60` (Step 6: Transition)

- [ ] **Step 1: Strengthen the transition section**

Replace the current Step 6 (lines 55-60):
```markdown
### 6. Transition

After user approves the written spec:
- Write an implementation plan to `docs/plans/`
- Commit the plan
- Implementation happens in a new session with `/implement`
```
with:
```markdown
### 6. Transition

After user approves the written spec:
- Write an implementation plan to `docs/plans/`
- Commit the plan
- Present: "Plan saved to `docs/plans/<filename>.md`. Invoke `/implement <plan-path>` to execute. /implement provides structured orchestration — spec context injection, phase gates, cross-agent consistency checks, and findings reconciliation — that ad-hoc execution lacks. Do not continue to implementation without it."
- If the user wants to proceed in this session, invoke `/implement <plan-path>` directly
- If deferring, note the plan path for the next session
```

- [ ] **Step 2: Commit**

```bash
git add .claude/skills/design/SKILL.md
git commit -m "fix: /design — explicit /implement handoff after plan creation"
```

---

### Task 4: Retro Skill Fix for Windows

**Files:**
- Modify: `.claude/skills/retro/SKILL.md:95-108`

- [ ] **Step 1: Replace Unix-only commands in subagent prompt**

In the subagent prompt template, replace lines 95-108 (the `find the project path`, `find recent sessions`, and `search for correction patterns` blocks):

Replace:
```markdown
>    Find the project path:
>    ```bash
>    ls ~/.claude/projects/ | grep {repo-name}
>    ```
>
>    Find recent sessions matching this date range:
>    ```bash
>    find ~/.claude/projects/{project-path} -name "*.jsonl" -mtime -{days} -maxdepth 1
>    ```
>
>    Search for correction patterns (user frustration, redirections):
>    ```bash
>    grep -il '"role":"user"' ~/.claude/projects/{path}/*.jsonl
>    ```
>    Then use Grep to search high-hit files for correction keywords:
>    pattern: `"role":"user".*(no not|don't|wrong|instead|stop|shouldn't|you missed|why didn't)`
```

With:
```markdown
>    Find the project path:
>    Use the Glob tool: `~/.claude/projects/*{repo-name}*`
>
>    Find recent sessions matching this date range:
>    Use the Glob tool: `~/.claude/projects/{project-path}/*.jsonl`
>    Then use the Bash tool with `ls -lt` to sort by modification time
>    and filter to files modified within the date range.
>
>    Search for correction patterns (user frustration, redirections):
>    Use the Grep tool (NOT bash grep) on the .jsonl files with:
>    pattern: `"role":"user".*(no not|don't|wrong|instead|stop|shouldn't|you missed|why didn't)`
```

- [ ] **Step 2: Commit**

```bash
git add .claude/skills/retro/SKILL.md
git commit -m "fix: retro skill — replace Unix find/grep with cross-platform Glob/Grep tools"
```

---

### Task 5: Misc Skill Fixes

**Files:**
- Modify: `.claude/skills/start/SKILL.md:104` (duplicate step numbering)
- Modify: `.claude/commands/spec-audit.md:102-103` (hardcoded spec count)
- Modify: `.claude/commands/verify.md:192` (stale JSON rule)
- Modify: `.claude/commands/spec.md:80` (Co-Authored-By)

- [ ] **Step 1: Fix start skill duplicate step 7**

At line 104, change `### 7. Print Workflow Guidance` to `### 8. Print Workflow Guidance`.

- [ ] **Step 2: Fix spec-audit hardcoded count**

Replace lines 102-103 in `spec-audit.md`:
```markdown
- Specs with ACs: N/21
- Specs fully aligned: N/21
```
with:
```markdown
- Specs with ACs: N/{total} (compute {total} from glob `specs/*.md` minus CONSTITUTION.md, SPEC-TEMPLATE.md, README.md)
- Specs fully aligned: N/{total}
```

- [ ] **Step 3: Fix verify.md stale JSON rule**

Replace rule 2 at line 192:
```markdown
2. **Structured data over screenshots** -- when both are available, prefer ppds.debug.* JSON over visual inspection. For webview panels, use @ext-verify screenshots (see Extension Mode above).
```
with:
```markdown
2. **Screenshots for visual changes** -- if your change affects what users see, take a screenshot and look at it. See @ext-verify for what requires screenshots vs compile+test.
```

- [ ] **Step 4: Fix Co-Authored-By in spec.md**

At line 80, replace:
```
   Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
```
with:
```
   Co-Authored-By: {use the format from the system prompt}
```

- [ ] **Step 5: Commit**

```bash
git add .claude/skills/start/SKILL.md .claude/commands/spec-audit.md .claude/commands/verify.md .claude/commands/spec.md
git commit -m "fix: misc skill drift — step numbering, spec count, verify rule, co-author format"
```

---

### Task 6: CDP Panel Targeting — Code

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts:596` (`<body>` tag)
- Modify: `src/PPDS.Extension/src/panels/SolutionsPanel.ts:238`
- Modify: `src/PPDS.Extension/src/panels/ImportJobsPanel.ts:209`
- Modify: `src/PPDS.Extension/src/panels/PluginTracesPanel.ts:482`
- Modify: `src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts:251`
- Modify: `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts:361`
- Modify: `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts:355`
- Modify: `src/PPDS.Extension/src/panels/WebResourcesPanel.ts:426`
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs:283-363` (resolveWebviewFrame)
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs:365-368` (resolveTarget)
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs:484-528` (connect command)
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs:529-541` (wait command — also calls resolveWebviewFrame)
- Test: `src/PPDS.Extension/tools/webview-cdp.test.mjs`

- [ ] **Step 1: Add data-ppds-panel attribute to all 8 panels**

In each panel's `getHtmlContent()` method, change `<body>` to `<body data-ppds-panel="{panelId}">`.

Panel ID mapping (derived from viewType, strip `ppds.` prefix):

| Panel | viewType | data-ppds-panel |
|-------|----------|-----------------|
| QueryPanel | `ppds.dataExplorer` | `dataExplorer` |
| SolutionsPanel | `ppds.solutionsPanel` | `solutionsPanel` |
| ImportJobsPanel | `ppds.importJobs` | `importJobs` |
| PluginTracesPanel | `ppds.pluginTraces` | `pluginTraces` |
| MetadataBrowserPanel | `ppds.metadataBrowser` | `metadataBrowser` |
| ConnectionReferencesPanel | `ppds.connectionReferences` | `connectionReferences` |
| EnvironmentVariablesPanel | `ppds.environmentVariables` | `environmentVariables` |
| WebResourcesPanel | `ppds.webResources` | `webResources` |

Each edit is a single-line change: `<body>` → `<body data-ppds-panel="dataExplorer">` (etc.)

- [ ] **Step 2: Add --panel flag parsing to webview-cdp.mjs**

In the `parseArgs` function, add `--panel` as a new string flag (same pattern as `--ext`). Store in `params.panel`. This flag must be parsed in **both** the `wait` command block and the interaction commands block — `--panel` should work with all commands that accept `--ext`.

- [ ] **Step 3: Add --panel filtering to resolveWebviewFrame**

Update function signature: `async function resolveWebviewFrame(targetIndex, extFilter, panelFilter)`.

After the candidate list is built from inner frames and the fallback (around line 310, after `candidates` is assigned), add a panel filter pass — **before** the visibility preference logic (line 341) and index selection (line 358):

```javascript
// Filter by panel ID (data-ppds-panel attribute on <body>)
if (panelFilter && candidates.length > 1) {
    const panelMatches = [];
    for (const frame of candidates) {
        try {
            const panelId = await frame.evaluate(() => document.body?.dataset?.ppdsPanel);
            if (panelId === panelFilter) panelMatches.push(frame);
        } catch { /* skip detached frames */ }
    }
    if (panelMatches.length > 0) candidates = panelMatches;
}
```

- [ ] **Step 4: Update resolveTarget to pass panel filter**

At line 367, update the call:
```javascript
return resolveWebviewFrame(params.target, params.ext, params.panel);
```

- [ ] **Step 5: Update wait command to pass panel filter**

At line 534 in the `wait` case, update:
```javascript
await resolveWebviewFrame(params.target, params.ext, params.panel);
```

- [ ] **Step 6: Add --panel to connect command output**

In the `connect` case (line 484), after the title extraction and before `targets.push()`, add:

```javascript
let panelId = null;
try {
    panelId = await frame.evaluate(() => document.body?.dataset?.ppdsPanel);
} catch { /* skip */ }
```

And add `panel: panelId || '(unknown)'` to the pushed object.

- [ ] **Step 7: Add parseArgs test for --panel flag**

In `webview-cdp.test.mjs`, add test using vitest idiom (matches existing file convention):

```javascript
it('parses eval with --panel and --ext flags', () => {
    const result = parseArgs(['eval', '"document.title"', '--panel', 'dataExplorer', '--ext', 'power-platform-developer-suite']);
    expect(result).toMatchObject({
        command: 'eval',
        args: ['"document.title"'],
        panel: 'dataExplorer',
        ext: 'power-platform-developer-suite',
    });
});

it('parses wait with --panel flag', () => {
    const result = parseArgs(['wait', '5000', '--panel', 'pluginTraces', '--ext', 'power-platform-developer-suite']);
    expect(result).toMatchObject({
        command: 'wait',
        timeout: 5000,
        panel: 'pluginTraces',
        ext: 'power-platform-developer-suite',
    });
});
```

- [ ] **Step 8: Run tests**

```bash
npm run test --prefix src/PPDS.Extension
```
Expected: all tests pass including new --panel tests.

- [ ] **Step 9: Run typecheck**

```bash
npm run typecheck:all --prefix src/PPDS.Extension
```
Expected: 0 errors.

- [ ] **Step 10: Commit**

```bash
git add src/PPDS.Extension/src/panels/*.ts src/PPDS.Extension/tools/webview-cdp.mjs src/PPDS.Extension/tools/webview-cdp.test.mjs
git commit -m "feat(ext): CDP panel targeting — data-ppds-panel attribute + --panel flag"
```

---

### Task 7: Update ext-verify Skill for --panel Flag

**Depends on:** Task 6 (CDP code must be committed first)

**Files:**
- Modify: `.claude/skills/ext-verify/SKILL.md`

- [ ] **Step 1: Add --panel to flags list**

After line 73 (`--ext "<id>"` flag), add:
```markdown
- `--panel "<id>"` — select webview by panel ID (most precise for multi-panel scenarios). IDs: `dataExplorer`, `solutionsPanel`, `importJobs`, `pluginTraces`, `metadataBrowser`, `connectionReferences`, `environmentVariables`, `webResources`
```

- [ ] **Step 2: Update the "always use --ext" note**

At line 47, replace:
```markdown
**Always use `--ext "power-platform-developer-suite"`** on `eval`, `click`, `type`, `select`, and `wait` commands. VS Code may have other webviews open (walkthrough, settings, etc.) — without `--ext`, you might interact with the wrong panel.
```
with:
```markdown
**Always use `--ext "power-platform-developer-suite"`** on `eval`, `click`, `type`, `select`, and `wait` commands. VS Code may have other webviews open (walkthrough, settings, etc.) — without `--ext`, you might interact with the wrong panel. When multiple PPDS panels are open simultaneously, add `--panel "<id>"` to target a specific one (e.g., `--panel "dataExplorer"`, `--panel "pluginTraces"`).
```

- [ ] **Step 3: Add multi-panel pattern**

After the "Open a panel and verify it loaded" pattern (after line 91), add:

```markdown
### Target a specific panel (when multiple are open)
```bash
# List all open panels with their IDs
node src/PPDS.Extension/tools/webview-cdp.mjs connect

# Target a specific panel by ID
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/traces.png --panel "pluginTraces" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'document.querySelector(".status-bar").textContent' --panel "solutionsPanel" --ext "power-platform-developer-suite"
```

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/ext-verify/SKILL.md
git commit -m "docs: ext-verify skill — document --panel flag for multi-panel targeting"
```

---

### Task 8: Pre-Commit Hook — Extension Lint Optimization

**Files:**
- Modify: `.claude/hooks/pre-commit-validate.py:81-102`

- [ ] **Step 1: Add staged-file check before extension lint**

The hook at line 81 says "Run extension lint if src/PPDS.Extension/ has changes or exists" but only checks existence, not staged files. Add a staged-file check before running lint.

Replace lines 81-102:
```python
        # Run extension lint if src/PPDS.Extension/ has changes or exists
        extension_dir = os.path.join(project_dir, "src", "PPDS.Extension")
        if os.path.exists(extension_dir) and os.path.exists(os.path.join(extension_dir, "package.json")):
```
with:
```python
        # Run extension lint only if staged files include extension changes
        extension_dir = os.path.join(project_dir, "src", "PPDS.Extension")
        has_ext_changes = False
        try:
            staged = subprocess.run(
                ["git", "diff", "--cached", "--name-only"],
                cwd=project_dir,
                capture_output=True,
                text=True,
                timeout=10,
            )
            if staged.returncode == 0:
                has_ext_changes = any(
                    line.startswith("src/PPDS.Extension/")
                    for line in staged.stdout.strip().split("\n") if line
                )
        except (subprocess.TimeoutExpired, FileNotFoundError):
            has_ext_changes = True  # If git check fails, run lint to be safe

        if has_ext_changes and os.path.exists(extension_dir) and os.path.exists(os.path.join(extension_dir, "package.json")):
```

The rest of the lint block (lines 84-102) stays unchanged.

- [ ] **Step 2: Commit**

```bash
git add .claude/hooks/pre-commit-validate.py
git commit -m "fix: pre-commit hook — only lint extension when extension files are staged"
```

---

### Task 9: Post-Commit Pipeline Nudge

**Files:**
- Modify: `.claude/hooks/post-commit-state.py`

- [ ] **Step 1: Add conditional pipeline continuation reminder**

After the existing logic that clears gates and updates last_commit (after line 48), add a pipeline nudge. The condition must be narrow to avoid noise on non-implementation commits: only nudge when workflow state has both `started` AND `plan` set (indicating an active /implement session), and `gates.passed` is null (just cleared).

Add before the final `try: with open(state_path, "w")` block:

```python
    # Pipeline continuation nudge — only during active /implement sessions
    started = state.get("started")
    plan = state.get("plan")
    gates_passed = state.get("gates", {}).get("passed") if isinstance(state.get("gates"), dict) else None
    if started and plan and not gates_passed:
        print("Commit recorded. Proceed to /gates — do not stop for summary.", file=sys.stderr)
```

- [ ] **Step 2: Commit**

```bash
git add .claude/hooks/post-commit-state.py
git commit -m "feat: post-commit hook — pipeline continuation nudge during /implement sessions"
```
