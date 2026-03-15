# Retrospective Action Items Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all stale paths, broken references, missing gates, and skill gaps identified by the Mar 13-15 retrospective — prevent the next sprint from repeating the same thrashing patterns.

**Architecture:** Surgical edits to existing `.claude/` files. No new features, no code changes. Every task is a find-and-replace or small insertion.

**Tech Stack:** Markdown, Python, CSS/TypeScript (paths only)

---

## Chunk 1: P0 Fixes — Stale Paths and Broken References

### Task 1: Fix pre-commit hook extension path

The hook silently skips extension lint because it checks `src/PPDS.Extension/` which no longer exists.

**Files:**
- Modify: `.claude/hooks/pre-commit-validate.py:63-64`

- [ ] **Step 1: Fix the path**

Change line 63-64 from:
```python
        # Run extension lint if src/PPDS.Extension/ has changes or exists
        extension_dir = os.path.join(project_dir, "extension")
```
to:
```python
        # Run extension lint if src/PPDS.Extension/ has changes or exists
        extension_dir = os.path.join(project_dir, "src", "extension")
```

- [ ] **Step 2: Verify the hook finds the directory**

Run: `python -c "import os; print(os.path.exists(os.path.join('.', 'src', 'extension', 'package.json')))"`
Expected: `True`

- [ ] **Step 3: Commit**

```bash
git add .claude/hooks/pre-commit-validate.py
git commit -m "fix(hooks): update extension path to src/PPDS.Extension/ in pre-commit hook"
```

### Task 2: Fix /verify detection path and TUI mode

Two issues: line 31 uses old `src/PPDS.Extension/` path, and TUI mode references nonexistent `mcp-tui-test`.

**Files:**
- Modify: `.claude/commands/verify.md:20,31,58-79`

- [ ] **Step 1: Fix extension detection path**

Change line 31 from:
```
- `src/PPDS.Extension/` → Extension mode
```
to:
```
- `src/PPDS.Extension/` → Extension mode
```

- [ ] **Step 2: Mark TUI mode as not yet available**

Change line 20 from:
```
| tui | `mcp-tui-test` configured in Claude Code |
```
to:
```
| tui | Not yet available — TUI snapshot tests only (`npm run tui:test`) |
```

- [ ] **Step 3: Replace TUI interactive section with snapshot-only**

Replace lines 58-79 (the `### 4. TUI Mode` section) with:
```markdown
### 4. TUI Mode

TUI interactive verification via MCP is not yet available. Use snapshot tests:

```bash
npm run tui:test
```

Verify:
- All snapshot tests pass
- No visual regressions in terminal output
```

- [ ] **Step 4: Resolve Rule 2 conflict with webview-cdp**

Change line 161 from:
```
2. **Structured data over screenshots** -- prefer ppds.debug.* JSON over visual inspection.
```
to:
```
2. **Structured data over screenshots** -- when both are available, prefer ppds.debug.* JSON over visual inspection. For webview panels, use @webview-cdp screenshots (see Extension Mode above).
```

- [ ] **Step 5: Commit**

```bash
git add .claude/commands/verify.md
git commit -m "fix(tools): update /verify — fix extension path, stub TUI mode, resolve screenshot conflict"
```

### Task 3: Remove dead ralph hook

The hook has no companion `/ralph-loop` command and is not wired into settings.json's Stop hooks (verified: only `pre-commit-validate.py` is configured under `hooks.PreToolUse`). It's dead code.

**Files:**
- Delete: `.claude/hooks/ralph-hook.py`

- [ ] **Step 1: Delete the file**

```bash
git rm .claude/hooks/ralph-hook.py
```

- [ ] **Step 2: Commit**

```bash
git commit -m "chore: remove dead ralph hook — no companion command, not wired in settings"
```

---

## Chunk 2: P1 Fixes — Missing Gates and Skill Content

### Task 4: Add CSS lint and dead code gates

`/gates` is missing `npm run lint:css` (Stylelint) and `npm run dead-code` (knip).

**Files:**
- Modify: `.claude/commands/gates.md` (insert after Gate 4, before Gate 5)

- [ ] **Step 1: Add Gate 4.5 (CSS Lint) and Gate 4.6 (Dead Code)**

Insert after line 71 (end of Gate 4 section), before `**Gate 5: TypeScript Tests**`:

```markdown

**Gate 4.5: CSS Lint** (if CSS files changed)

```bash
npm run lint:css --prefix src/PPDS.Extension
```

Pass: 0 errors
Fail: report CSS lint violations with file:line

**Gate 4.6: Dead Code Analysis** (if TS/JS files changed)

```bash
npm run dead-code --prefix src/PPDS.Extension
```

Pass: 0 unused exports
Fail: report unused exports/files
```

- [ ] **Step 2: Commit**

```bash
git add .claude/commands/gates.md
git commit -m "fix(tools): add CSS lint and dead code gates to /gates"
```

### Task 5: Update webview-panels skill architecture diagram

The skill is missing `filter-bar.ts`, `monacoUtils.ts`, `querySelectionUtils.ts`, `webviewUtils.ts`, `monaco-entry.ts`, `monaco-worker.ts`, and has wrong case for `DaemonClient.ts`.

**Files:**
- Modify: `.claude/skills/webview-panels/SKILL.md:18-46,199-209,224`

- [ ] **Step 1: Update the architecture diagram**

Replace the `src/panels/` tree (lines 18-46) with:

```
src/panels/
  QueryPanel.ts                    ← host-side panel (extends WebviewPanelBase<TIn, TOut>)
  SolutionsPanel.ts                ← host-side panel
  WebviewPanelBase.ts              ← abstract base with typed postMessage + AbortSignal
  environmentPicker.ts             ← shared HTML generator + QuickPick helper
  monacoUtils.ts                   ← detectLanguage, mapCompletionKind, mapCompletionItems
  querySelectionUtils.ts           ← getSelectionRect, isSingleCell, sanitizeValue, buildTsv
  webviewUtils.ts                  ← shared webview helper utilities
  monaco-entry.ts                  ← Monaco editor browser entry (IIFE bundle)
  monaco-worker.ts                 ← Monaco editor worker entry

  webview/                         ← browser-side TypeScript (tsconfig.webview.json)
    query-panel.ts                 ← Query Panel webview entry point
    solutions-panel.ts             ← Solutions Panel webview entry point
    shared/
      message-types.ts             ← discriminated unions for ALL panel messages
      dom-utils.ts                 ← escapeHtml, escapeAttr, cssEscape, formatDate, sanitizeValue
      filter-bar.ts                ← generic FilterBar<T> — debounced text filtering with count
      vscode-api.ts                ← typed getVsCodeApi<T>() wrapper
      assert-never.ts              ← exhaustive switch helper

  styles/                          ← CSS files (esbuild bundles @import)
    shared.css                     ← common styles (toolbar, status bar, spinner, env picker)
    query-panel.css                ← @import './shared.css' + Query Panel specific
    solutions-panel.css            ← @import './shared.css' + Solutions Panel specific

esbuild.js                         ← builds host + webview TS + CSS entry points
dist/
  query-panel.js                   ← built webview bundle (IIFE)
  query-panel.css                  ← built CSS bundle
  solutions-panel.js / .css        ← same pattern
  monaco-editor.js / .css          ← Monaco bundle
  editor.worker.js                 ← Monaco worker
```

- [ ] **Step 2: Add missing entries to "What Goes Where" table**

Add after line 208 (Shared HTML generators row):

```markdown
| Debounced text filtering | `webview/shared/filter-bar.ts` | Generic, shared across panels |
| Monaco utilities (detect lang, map completions) | `monacoUtils.ts` | Pure functions, host-side |
| Selection/copy utilities | `querySelectionUtils.ts` | Pure functions, testable |
```

- [ ] **Step 3: Fix DaemonClient.ts case**

Change line 224 from:
```
Available methods are defined in `DaemonClient.ts`.
```
to:
```
Available methods are defined in `daemonClient.ts`.
```

- [ ] **Step 4: Commit**

```bash
git add .claude/skills/webview-panels/SKILL.md
git commit -m "docs(skills): update webview-panels architecture for new utilities and fix filename case"
```

---

## Chunk 3: Memory Cleanup

### Task 6: Clean up misplaced memory entries

Remove content from memory that duplicates CLAUDE.md.

**Files:**
- Modify: `C:\Users\josh_\.claude\projects\C--VS-ppdsw-ppds\memory\user_preferences.md`

- [ ] **Step 1: Remove duplicated workflow items, keep actual preferences**

Replace file contents with:
```markdown
---
name: user-preferences
description: How the user prefers to collaborate — discussion-first, long-term approach, honest trade-offs
type: user
---

- Prefers detailed discussion before implementation — conversation, not interrogation
- Wants the "proper long-term approach" over expeditious shortcuts
- Expects honest trade-off analysis with clear recommendations and reasoning
```

(Removed "Uses dev containers" — infrastructure fact, not preference. Removed "Uses subagent-driven development" — already in CLAUDE.md workflow section.)

- [ ] **Step 2: No commit needed** (memory files are outside the repo)
