# Retrospective Tooling Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix stale/broken tooling surfaced by the March 13-15 retrospective — move extension into src/, clean up memory, fix broken commands, update skills, and create a retrospective skill.

**Architecture:** Phase 1 moves `src/PPDS.Extension/` to `src/PPDS.Extension/` and updates all path references (must complete first — every other task references these paths). Phase 2 is 8 independent parallel tasks for instructions, commands, skills, and the new retrospective skill.

**Tech Stack:** Markdown files + one directory move + package.json update. No application code changes. No tests needed (paths are validated by subsequent `/gates` run).

---

## File Structure

```
Phase 1 (sequential — path foundation):
  src/PPDS.Extension/                               → src/PPDS.Extension/ (git mv)
  package.json                             ← Modify: --prefix extension → --prefix src/PPDS.Extension
  CLAUDE.md                                ← Modify: update Testing section paths
  .claude/commands/gates.md                ← Modify: --prefix paths
  .claude/commands/verify.md               ← Modify: tool paths (also rewritten for webview-cdp)
  .claude/commands/implement.md            ← Modify: src/PPDS.Extension/ directory references
  .claude/commands/debug.md                ← Modify: src/PPDS.Extension/ directory references
  .claude/commands/spec-audit.md           ← Modify: --prefix path
  .claude/skills/webview-cdp/SKILL.md      ← Modify: tool path references
  .claude/skills/webview-panels/SKILL.md   ← No change (paths are relative within extension)

Phase 2 (parallel — content changes):
  CLAUDE.md                                ← Modify: add Workflow + Gotchas sections
  C:\VS\ppdsw\CLAUDE.md (workspace)        ← Modify: add Worktrees section
  ~/.claude/projects/.../memory/*           ← Rewrite index, delete stale files
  .claude/commands/verify.md               ← Rewrite: extension mode uses webview-cdp
  .claude/commands/implement.md            ← Modify: add extension spec mapping
  .claude/commands/gates.md                ← Modify: add typecheck:all gate
  .claude/skills/webview-panels/SKILL.md   ← Modify: add Daemon/RPC sections
  .claude/skills/webview-cdp/SKILL.md      ← Modify: add Error Recovery + CSS workflow
  .claude/skills/retrospective/SKILL.md    ← Create: retrospective process skill
```

---

## Phase 1: Move src/PPDS.Extension/ to src/PPDS.Extension/

This phase MUST complete before Phase 2 begins. All path references across the repo change.

### Task 1: Move Directory and Update All References

**Files:**
- Move: `src/PPDS.Extension/` → `src/PPDS.Extension/`
- Modify: `package.json` (19 `--prefix` lines)
- Modify: `CLAUDE.md` (testing section)
- Modify: `.claude/commands/gates.md`
- Modify: `.claude/commands/verify.md`
- Modify: `.claude/commands/implement.md`
- Modify: `.claude/commands/debug.md`
- Modify: `.claude/commands/spec-audit.md`
- Modify: `.claude/skills/webview-cdp/SKILL.md`

- [ ] **Step 1: Move the directory**

```bash
git mv src/PPDS.Extension/ src/PPDS.Extension/
```

- [ ] **Step 2: Update root package.json**

Replace all 19 instances of `--prefix extension` with `--prefix src/PPDS.Extension`:

```json
{
  "private": true,
  "description": "PPDS workspace — root proxy scripts for extension and TUI",
  "scripts": {
    "ext:compile": "npm run compile --prefix src/PPDS.Extension",
    "ext:watch": "npm run watch --prefix src/PPDS.Extension",
    "ext:package": "npm run package --prefix src/PPDS.Extension",
    "ext:lint": "npm run lint --prefix src/PPDS.Extension",
    "ext:test": "npm run test --prefix src/PPDS.Extension",
    "ext:test:watch": "npm run test:watch --prefix src/PPDS.Extension",
    "ext:test:e2e": "npm run test:e2e --prefix src/PPDS.Extension",
    "ext:vsce:package": "npm run vsce:package --prefix src/PPDS.Extension",
    "ext:local": "npm run local --prefix src/PPDS.Extension",
    "ext:local:install": "npm run local:install --prefix src/PPDS.Extension",
    "ext:local:uninstall": "npm run local:uninstall --prefix src/PPDS.Extension",
    "ext:local:revert": "npm run local:revert --prefix src/PPDS.Extension",
    "ext:release:test": "npm run release:test --prefix src/PPDS.Extension",
    "ext:bundle:cli": "npm run bundle:cli --prefix src/PPDS.Extension",
    "ext:package:win32-x64": "npm run package:win32-x64 --prefix src/PPDS.Extension",
    "ext:package:linux-x64": "npm run package:linux-x64 --prefix src/PPDS.Extension",
    "ext:package:darwin-x64": "npm run package:darwin-x64 --prefix src/PPDS.Extension",
    "ext:package:darwin-arm64": "npm run package:darwin-arm64 --prefix src/PPDS.Extension",
    "ext:dev:webview": "npm run dev:webview --prefix src/PPDS.Extension",
    "tui:test": "npm test --prefix tests/tui-e2e",
    "tui:test:update": "npm run test:update --prefix tests/tui-e2e",
    "tui:test:headed": "npm run test:headed --prefix tests/tui-e2e"
  }
}
```

- [ ] **Step 3: Update CLAUDE.md testing section**

Replace:
```
- Extension unit: `npm run ext:test`
- Extension E2E: `npm run ext:test:e2e`
```
These use the root proxy scripts which are already updated, so no change needed. But verify the proxy scripts work:

Run: `npm run ext:compile`
Expected: compiles without errors from the new path

- [ ] **Step 4: Update .claude/commands/gates.md — replace `--prefix extension` with `--prefix src/PPDS.Extension`**

Three replacements in Gates 3, 4, 5:
- Line 47: `npm run compile --prefix extension` → `npm run compile --prefix src/PPDS.Extension`
- Line 56: `npm run lint --prefix extension` → `npm run lint --prefix src/PPDS.Extension`
- Line 65: `npm test --prefix extension` → `npm test --prefix src/PPDS.Extension`
- Line 76: `npx vitest run -t "{method}" --prefix extension` → `npx vitest run -t "{method}" --prefix src/PPDS.Extension`

- [ ] **Step 5: Update .claude/commands/verify.md — replace `--prefix extension` with `--prefix src/PPDS.Extension`**

- Line 40: `npm run test --prefix extension` → `npm run test --prefix src/PPDS.Extension`

(The Phase A/B content with `src/PPDS.Extension/tools/` paths will be fully rewritten in Task 5, so don't update those here.)

- [ ] **Step 6: Update .claude/commands/implement.md — replace `src/PPDS.Extension/` with `src/PPDS.Extension/`**

- Line 104: `` `src/PPDS.Extension/` directory `` → `` `src/PPDS.Extension/` directory ``

- [ ] **Step 7: Update .claude/commands/debug.md — replace `src/PPDS.Extension/` with `src/PPDS.Extension/`**

- Line 20: `src/PPDS.Extension/` → `src/PPDS.Extension/`
- Line 63: `cd <root>/extension &&` → `cd <root>/src/PPDS.Extension &&`
- Line 68: `or from src/PPDS.Extension/ directly` → `or from src/PPDS.Extension/ directly`

- [ ] **Step 8: Update .claude/commands/spec-audit.md — replace `--prefix extension` with `--prefix src/PPDS.Extension`**

- Line 35: `--prefix extension` → `--prefix src/PPDS.Extension`

- [ ] **Step 9: Update .claude/skills/webview-cdp/SKILL.md — replace `src/PPDS.Extension/tools/` with `src/PPDS.Extension/tools/`**

Replace ALL instances of `src/PPDS.Extension/tools/webview-cdp.mjs` with `src/PPDS.Extension/tools/webview-cdp.mjs` throughout the file (approximately 30+ occurrences).

Also replace:
- `src/PPDS.Extension/tools/webview-cdp.mjs` in the Setup section description
- `--prefix src/PPDS.Extension` in the allowed-tools frontmatter

- [ ] **Step 10: Verify the move didn't break anything**

Run: `npm run ext:compile`
Expected: compiles successfully from `src/PPDS.Extension/`

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "refactor: move src/PPDS.Extension/ to src/PPDS.Extension/

All product code now lives under src/. Update root package.json
proxy scripts, all .claude/ commands, and webview-cdp skill to
use the new path."
```

**Note:** Do NOT update historical plan files in `docs/plans/` — they are records of what was done at the time.

---

## Phase 2: Content Changes (All Independent — Parallel)

All tasks in Phase 2 use the new `src/PPDS.Extension/` paths from Phase 1.

### Task 2: Add Workflow + Gotchas to Repo CLAUDE.md

**Files:**
- Modify: `CLAUDE.md` (currently 57 lines, limit 100)

- [ ] **Step 1: Add sections after Architecture**

Append after the Architecture section:

```markdown

## Workflow

- Spec: /spec → /spec-audit
- Implement: /implement → dispatches subagents, runs /gates and /verify at phase gates
- Review: /review → /converge
- Skills: @webview-panels (panel dev), @webview-cdp (visual verification)

## Gotchas

- VS Code `LogOutputChannel` writes to `exthost/<extId>/Name.log`, NOT `N-Name.log`
- Agent research summaries may be wrong — read code yourself before stating codebase behavior as fact
```

New total: ~69 lines. Under 100.

- [ ] **Step 2: Verify line count**

Run: `wc -l CLAUDE.md`
Expected: under 100

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: add workflow mapping and gotchas to CLAUDE.md

Promote feedback corrections from private memory into shared
instructions so all contributors and AI agents benefit."
```

---

### Task 3: Add Worktrees to Workspace CLAUDE.md

**Files:**
- Modify: `C:\VS\ppdsw\CLAUDE.md` (currently 58 lines, limit 100)

- [ ] **Step 1: Add Worktrees section after Bash Commands**

Insert after the Bash Commands section:

```markdown

## Worktrees

- Always use `<repo>/.worktrees/<name>`, never `.claude/worktrees/`
- New branch: `git worktree add .worktrees/<name> -b <branch>`
- Existing branch: `git worktree add .worktrees/<name> <branch>`
- Cleanup: `git worktree list` before creating; `git worktree remove` for stale
```

- [ ] **Step 2: Verify line count**

Run: `wc -l "C:\VS\ppdsw\CLAUDE.md"`
Expected: under 100

- [ ] **Step 3: Commit**

```bash
git add "C:\VS\ppdsw\CLAUDE.md"
git commit -m "docs: add worktree conventions to workspace CLAUDE.md"
```

---

### Task 4: Clean Up Memory

**Files:**
- Rewrite: `C:\Users\josh_\.claude\projects\C--VS-ppdsw-ppds\memory\MEMORY.md`
- Create: `C:\Users\josh_\.claude\projects\C--VS-ppdsw-ppds\memory\user_preferences.md`
- Delete: `C:\Users\josh_\.claude\projects\C--VS-ppdsw-ppds\memory\feedback_verify_before_stating.md`
- Delete: `C:\Users\josh_\.claude\projects\C--VS-ppdsw-ppds\memory\feedback_vscode_log_paths.md`
- Delete: `C:\Users\josh_\.claude\projects\C--VS-ppdsw-ppds\memory\project_extension_review_2026_03_15.md`

- [ ] **Step 1: Create user_preferences.md**

```markdown
---
name: user-preferences
description: How the user prefers to collaborate — discussion-first, long-term approach, honest trade-offs
type: user
---

- Prefers detailed discussion before implementation — conversation, not interrogation
- Wants the "proper long-term approach" over expeditious shortcuts
- Expects honest trade-off analysis with clear recommendations and reasoning
- Uses dev containers for feature work execution sessions
- Uses subagent-driven development for multi-task implementation plans
```

- [ ] **Step 2: Rewrite MEMORY.md**

```markdown
# PPDS Project Memory

## User Preferences

- [user_preferences.md](user_preferences.md) - Interaction style and workflow preferences
```

- [ ] **Step 3: Delete stale files**

Delete:
- `feedback_verify_before_stating.md` — promoted to CLAUDE.md Gotchas
- `feedback_vscode_log_paths.md` — promoted to CLAUDE.md Gotchas
- `project_extension_review_2026_03_15.md` — ephemeral TODO list, partially stale

- [ ] **Step 4: Verify**

Read MEMORY.md — should only reference `user_preferences.md`.

No git commit — memory files are outside the repo.

---

### Task 5: Rewrite /verify Extension Mode

**Files:**
- Modify: `.claude/commands/verify.md`

- [ ] **Step 1: Update prerequisites table**

Replace the extension row:

Old: `| extension | \`acomagu/vscode-as-mcp-server\` installed in VS Code + Playwright MCP for webview |`

New: `| extension | \`src/PPDS.Extension/tools/webview-cdp.mjs\` (uses @playwright/test + @vscode/test-electron, both dev deps) |`

- [ ] **Step 2: Replace Section 5 (Extension Mode) entirely**

Replace everything from `### 5. Extension Mode` through the end of Phase B with:

```markdown
### 5. Extension Mode

**Phase A: Functional Verification (webview-cdp)**

Launch VS Code with the extension and verify panels load:

```bash
# Build and launch (compiles extension + daemon)
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build

# Open Data Explorer and wait for webview
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"

# Screenshot to verify panel rendered correctly
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/data-explorer.png
# LOOK at the screenshot — verify layout, no blank areas, controls visible

# Check for runtime errors
node src/PPDS.Extension/tools/webview-cdp.mjs logs
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"
```

**Phase B: Interaction Verification (if testing interactive features)**

```bash
# Test query execution
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 5 name FROM account")'
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-query.png

# Test Solutions Panel
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Solutions"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/solutions.png
```

**Phase C: Cleanup**

```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

See @webview-cdp skill for full command reference and common patterns.
```

- [ ] **Step 3: Verify no stale references**

Search verify.md for `acomagu`, `vscode-as-mcp-server`, `localhost:5173`, `dev:webview`. None should exist.

- [ ] **Step 4: Commit**

```bash
git add .claude/commands/verify.md
git commit -m "fix(tools): rewrite /verify extension mode to use webview-cdp

Replace removed acomagu/vscode-as-mcp-server and nonexistent Vite
dev server with webview-cdp Playwright workflow."
```

---

### Task 6: Add Extension Mapping to /implement

**Files:**
- Modify: `.claude/commands/implement.md`

- [ ] **Step 1: Add extension to spec mapping in Step 2B**

After the `src/PPDS.Auth/` mapping line, add:

```markdown
  - `src/PPDS.Extension/src/panels/` → `specs/per-panel-environment-scoping.md` (if panels) or relevant spec
  - `src/PPDS.Extension/` → check `specs/README.md` for extension-related specs
```

- [ ] **Step 2: Commit**

```bash
git add .claude/commands/implement.md
git commit -m "fix(tools): add src/PPDS.Extension/ to /implement spec-to-code mapping"
```

---

### Task 7: Add typecheck:all to /gates

**Files:**
- Modify: `.claude/commands/gates.md`

- [ ] **Step 1: Insert Gate 3.5 after Gate 3 (TypeScript Build)**

```markdown
**Gate 3.5: TypeScript Type Check** (if TS/JS files changed)

```bash
npm run typecheck:all --prefix src/PPDS.Extension
```

Pass: 0 errors across both host and webview tsconfigs
Fail: report exact error messages with file:line

Note: `compile` (Gate 3) only runs esbuild which does NOT type-check. This gate runs `tsc --noEmit` against both `tsconfig.json` (host) and `tsconfig.webview.json` (browser) to catch type errors.
```

- [ ] **Step 2: Commit**

```bash
git add .claude/commands/gates.md
git commit -m "fix(tools): add typecheck:all gate to /gates

esbuild does not type-check. Add tsc --noEmit gate for both host
and webview tsconfigs."
```

---

### Task 8: Add Daemon/RPC Patterns to webview-panels Skill

**Files:**
- Modify: `.claude/skills/webview-panels/SKILL.md`

- [ ] **Step 1: Add Daemon Communication section after "What Goes Where" table, before "Adding/Removing a Command"**

```markdown
## Daemon Communication

Host-side panels call the daemon via `this.daemon` (a `DaemonClient` instance passed at construction).

### Calling RPC methods

```typescript
// All daemon methods accept an optional CancellationToken as last arg
const result = await this.daemon.querySql({
    sql, top: 100, environmentUrl: this.environmentUrl
}, token);
```

Available methods are defined in `DaemonClient.ts`. Common ones: `querySql`, `queryFetch`, `queryExplain`, `queryExport`, `queryComplete`, `solutionsList`, `solutionsComponents`, `authWho`, `envList`.

### Handling daemon disconnection

WebviewPanelBase provides a reconnection hook. Subscribe in your constructor and override to auto-refresh:

```typescript
// In constructor:
this.subscribeToDaemonReconnect(daemon);

// Override to handle reconnection:
protected override onDaemonReconnected(): void {
    void this.loadData(); // re-fetch stale data
}
```

The base class sends `{ command: 'daemonReconnected' }` to the webview automatically — add it to your HostToWebview union type and handle it (e.g., show a refresh banner).

### Query cancellation

For long-running operations, use `CancellationTokenSource` from vscode-jsonrpc:

```typescript
private queryCts: CancellationTokenSource | undefined;

async executeQuery(): Promise<void> {
    this.queryCts?.cancel();
    this.queryCts = new CancellationTokenSource();
    const token = this.queryCts.token;

    const result = await this.daemon.querySql(params, token);
    if (token.isCancellationRequested) return;
    // ... use result
}

// Cancel from webview message:
case 'cancelQuery': this.queryCts?.cancel(); break;

// Cleanup in dispose:
this.queryCts?.cancel();
this.queryCts?.dispose();
```

### Panel-scoped environment

Panels can target a specific environment (not the global active one). Store `environmentUrl` as an instance property, pass it to every daemon call, and update it from the environment picker:

```typescript
case 'selectEnvironment':
    this.environmentUrl = message.url;
    await this.loadData(); // re-fetch with new env
    break;
```
```

- [ ] **Step 2: Commit**

```bash
git add .claude/skills/webview-panels/SKILL.md
git commit -m "docs(skills): add daemon/RPC patterns to webview-panels

Document daemon communication, disconnection handling, query
cancellation, and panel-scoped environments."
```

---

### Task 9: Add Error Recovery + CSS Workflow to webview-cdp Skill

**Files:**
- Modify: `.claude/skills/webview-cdp/SKILL.md`

- [ ] **Step 1: Add Error Recovery section before "Gap Protocol"**

```markdown
## Error Recovery

### `launch` fails or hangs

```bash
# Check for stale VS Code processes
node src/PPDS.Extension/tools/webview-cdp.mjs close   # try graceful shutdown first

# If still stuck, the daemon may hold the port
node src/PPDS.Extension/tools/webview-cdp.mjs logs
```

Common causes:
- **Stale process from prior session** — always `close` before `launch`
- **Build failure** with `--build` — run `npm run compile --prefix src/PPDS.Extension` separately to see full error output
- **Daemon won't start** — check `logs --channel "PPDS"` for startup errors

### `wait` times out

Check in order:
1. **Was the command correct?** — `command` names are case-sensitive and must match package.json exactly
2. **Did the extension activate?** — `logs --channel "PPDS"` for activation errors
3. **Is the daemon running?** — extension panels depend on the daemon; check daemon output in logs
4. **Increase timeout** — `wait 30000` for slow machines or first-run scenarios

### Screenshot is blank or wrong panel

- **Blank screenshot** — panel may not have finished rendering. Add `wait` before `screenshot`
- **Wrong panel visible** — use `command` to focus the correct panel, then `screenshot`
- **Stale content** — webview may show cached state. Reopen the panel

### CSS-only changes

CSS changes require `--build` because esbuild bundles CSS files:

```bash
node src/PPDS.Extension/tools/webview-cdp.mjs close
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/css-verify.png
```

You cannot hot-reload CSS in VS Code webviews — a full rebuild + relaunch is required.
```

- [ ] **Step 2: Widen allowed-tools in frontmatter**

Replace:
```
allowed-tools: Bash(node *webview-cdp*), Bash(cd * && node *webview-cdp*)
```

With:
```
allowed-tools: Bash(node *webview-cdp*), Bash(cd * && node *webview-cdp*), Bash(npm run * --prefix src/PPDS.Extension)
```

- [ ] **Step 3: Narrow description in frontmatter**

Replace:
```
description: Interact with VS Code extension webview panels via Playwright Electron. Use when implementing or verifying webview UI — take screenshots, click elements, type text, send keyboard shortcuts, execute VS Code commands, read console logs. Triggers include any task involving VS Code extension webview panels, Data Explorer, plugin traces UI, or any panel built with WebviewPanelBase.
```

With:
```
description: Visual verification of VS Code extension webview panels via Playwright Electron — screenshots, clicks, typing, keyboard shortcuts, VS Code commands, console logs. Use after implementing or modifying any UI-affecting change (CSS, layout, HTML templates, message wiring). For non-visual changes (string constants, config, internal refactors), compile + test is sufficient.
```

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/webview-cdp/SKILL.md
git commit -m "docs(skills): add error recovery and CSS workflow to webview-cdp

Add troubleshooting for launch failures, wait timeouts, blank
screenshots. Document CSS-only change workflow. Widen allowed-tools.
Narrow description to reduce false triggers."
```

---

### Task 10: Create Retrospective Skill

**Files:**
- Create: `.claude/skills/retrospective/SKILL.md`

- [ ] **Step 1: Create the skill file**

```markdown
---
name: retrospective
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured analysis of recent work sessions to identify patterns, assign blame candidly, and recommend improvements.

## When to Use

- End of a feature branch before PR
- After a multi-day sprint
- When the user asks to review recent work quality
- After a particularly rough session (thrashing, rework)

## Process

### 1. Gather Raw Data

```bash
# Get commits for the time period (default: 2 days)
git log --since="2 days ago" --format="COMMIT:%H%nDATE:%ai%nSUBJECT:%s%nBODY:%b%n---" --no-merges
```

Identify session boundaries: gaps of 30+ minutes between commits = new session.

### 2. Deep Dive Each Session (PARALLEL — one agent per session)

For each session, the agent MUST:

**a. Read the actual diffs** for thrashing incidents (2+ fix commits for the same feature):
```bash
git diff <commit1> <commit2> -- <relevant-files>
```

**b. Identify feat-then-fix chains** — a feature commit directly followed by fix commits for the same thing. Count them. This is the primary quality signal.

**c. Identify thrashing** — 3+ commits attempting the same fix. Read diffs to understand what changed between attempts.

**d. Read conversation transcripts** if available (check `.claude/` session logs) to extract direct user corrections and frustrations.

**e. Assign blame per incident:**

| Category | When to assign |
|----------|---------------|
| **AI** | Generated code that was broken on arrival, didn't read docs before implementing, shotgun debugging, shipped without testing |
| **Process** | No verification gate, no review before merge, working 14+ hours, premature documentation |
| **Tooling** | Platform behavior that is genuinely underdocumented or surprising |
| **User** | Flawed requirements, continuing to push during fatigue, not enforcing breaks |

### 3. Audit Skills and Tools (PARALLEL with step 2)

- Read all `.claude/skills/` and `.claude/commands/` files
- Check each for staleness (references to removed tools, wrong paths, outdated patterns)
- Verify descriptions trigger correctly (not too broad, not too narrow)
- Check for conflicts between skills
- Identify repeated manual behaviors that should be skills

### 4. Cross-Reference Memory

- Read all memory files
- Flag any memory entries that contradict the current codebase
- Flag architecture decisions that were reversed but not updated
- Flag ephemeral task tracking masquerading as memory

### 5. Synthesize

Present findings as:

**Aggregate Stats:**
- Total commits, feat/fix ratio, thrashing incidents count
- Blame distribution (AI/Process/Tooling/User percentages)

**Per-Session Analysis:**
- Time range, scope, what went well, what went wrong
- Direct user quotes where available
- Feat-then-fix chains with specific commit hashes

**Skills/Tools Audit:**
- Stale/broken items (P0)
- Missing content (P1)
- Improvements (P2+)

**Recommendations:**
- Specific, actionable items ranked by priority
- Distinguish between "superpowers already covers this" vs "needs a project-specific fix"

## Quality Bar

A retrospective is NOT adequate if it only analyzes commit messages. Commit messages are the AI's self-reported summary — exactly the thing that needs verification. A proper retrospective reads diffs, reads transcripts, and quotes the user directly.

## Output

Save analysis as a discussion document — do NOT save to `docs/plans/`. Present findings to the user for discussion before creating any action plan.
```

- [ ] **Step 2: Commit**

```bash
git add .claude/skills/retrospective/SKILL.md
git commit -m "feat(skills): add retrospective skill for structured session review

Codifies the process for analyzing recent work sessions — git
history analysis, diff reading, blame assignment, skills audit,
memory cross-reference, and synthesis with recommendations."
```

---

## Execution Summary

| Phase | Task | Dependencies | Description |
|-------|------|-------------|-------------|
| 1 | 1 | None | Move src/PPDS.Extension/ to src/PPDS.Extension/ + update all paths |
| 2 | 2 | Task 1 | Add Workflow + Gotchas to repo CLAUDE.md |
| 2 | 3 | None | Add Worktrees to workspace CLAUDE.md |
| 2 | 4 | None | Clean up memory files |
| 2 | 5 | Task 1 | Rewrite /verify extension mode |
| 2 | 6 | Task 1 | Add extension mapping to /implement |
| 2 | 7 | Task 1 | Add typecheck:all to /gates |
| 2 | 8 | None | Add daemon/RPC to webview-panels skill |
| 2 | 9 | Task 1 | Add error recovery to webview-cdp skill |
| 2 | 10 | None | Create retrospective skill |

**Phase 1** is sequential (1 task). **Phase 2** is 9 parallel tasks (Tasks 2-10).

**Validation after all tasks:** Run `/gates` to verify the path changes didn't break anything.
