# Webview External Scripts — Extraction & Skill

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract SolutionsPanel inline IIFE to external script (matching the QueryPanel pattern), create a webview-panels skill that establishes external scripts as the standard pattern, and clean up stale dev artifacts.

**Architecture:** VS Code silently drops inline `<script>` tags exceeding ~32KB. The legacy extension avoided this structurally — each panel had its own behavior `.js` file built by a dedicated webpack config (`webpack.webview.config.js`), with zero inline JavaScript. The MVP took a shortcut (inline template literals) that broke when QueryPanel grew past 32KB. This plan aligns all panels with the external script pattern and codifies it in a skill so the problem never recurs.

**Tech Stack:** esbuild (IIFE, browser platform), VS Code Webview API, Vitest

---

## File Structure

| Action | File | Responsibility |
|--------|------|---------------|
| Create | `extension/src/panels/solutions-panel-webview.js` | SolutionsPanel browser-side behavior (extracted from inline IIFE) |
| Modify | `extension/src/panels/SolutionsPanel.ts` | Remove inline IIFE, load external script via `<script src="...">`, remove `getEnvironmentPickerJs` import |
| Modify | `extension/esbuild.js` | Add 5th entry point for solutions-panel-webview |
| Modify | `extension/src/panels/SolutionsPanel.ts:78` | Add `dist/` to `localResourceRoots` |
| Create | `.agents/skills/webview-panels/SKILL.md` | Skill: external script pattern for webview panels |
| Delete or update | `extension/dev/query-panel.html` | Stale dev HTML (still references textarea, not Monaco) |

---

## Task 1: Extract SolutionsPanel IIFE to External Script

**Files:**
- Create: `extension/src/panels/solutions-panel-webview.js`
- Modify: `extension/src/panels/SolutionsPanel.ts`
- Modify: `extension/esbuild.js`

**Context:** SolutionsPanel currently has a 16.7KB inline IIFE (lines 458-832). While under 32KB today, it follows the same growth pattern that broke QueryPanel. Extract it following the exact same pattern used for QueryPanel (commit `cd3407461`).

**Reference:** `extension/src/panels/query-panel-webview.js` — the canonical example of this pattern.

- [ ] **Step 1: Create `solutions-panel-webview.js`**

Extract lines 459-831 from `SolutionsPanel.ts` into a new file. Key changes from the template literal:
- Remove `${getEnvironmentPickerJs()}` interpolation — inline the environment picker code directly (same as query-panel-webview.js lines 431-441)
- No double-escaped sequences exist in this IIFE (unlike QueryPanel's), so no escape conversion is needed
- Add `/* global acquireVsCodeApi */` comment at top
- Add header comment explaining why this is external

The file should start with the `(function() {` IIFE and end with `vscode.postMessage({ command: 'ready' });` then `})();`.

- [ ] **Step 2: Add esbuild entry point**

In `extension/esbuild.js`, add a 5th build context after `queryPanelCtx`:

```javascript
// Build 5: Solutions panel webview script (browser, IIFE)
const solutionsPanelCtx = await esbuild.context({
    entryPoints: ['src/panels/solutions-panel-webview.js'],
    bundle: true,
    format: 'iife',
    minify: production,
    sourcemap: !production,
    sourcesContent: false,
    platform: 'browser',
    outfile: 'dist/solutions-panel.js',
    logLevel: 'warning',
});
```

Add `solutionsPanelCtx` to the watch and rebuild `Promise.all` arrays.

- [ ] **Step 3: Update SolutionsPanel.ts**

Three changes:

1. Remove `getEnvironmentPickerJs` from the import (keep `getEnvironmentPickerCss`, `getEnvironmentPickerHtml`, `showEnvironmentPicker`). Also delete the `getEnvironmentPickerJs()` function from `environmentPicker.ts` — it now has zero callers.

2. Add `dist/` to `localResourceRoots` (line 78 — currently only has `node_modules`):
```typescript
localResourceRoots: [
    vscode.Uri.joinPath(extensionUri, 'node_modules'),
    vscode.Uri.joinPath(extensionUri, 'dist'),
],
```

3. In `getHtmlContent()`:
   - Add webview URI for the solutions panel script:
     ```typescript
     const solutionsPanelJsUri = webview.asWebviewUri(
         vscode.Uri.joinPath(this.extensionUri, 'dist', 'solutions-panel.js')
     );
     ```
   - Replace the entire inline `<script nonce="${nonce}">...</script>` block (lines 458-832) with:
     ```html
     <script nonce="${nonce}" src="${solutionsPanelJsUri}"></script>
     ```

- [ ] **Step 4: Build and verify**

Run from repo root:
```bash
cd extension && npx tsc --noEmit && npm run compile && ls -la dist/solutions-panel.js
```

Expected: clean TypeScript, clean build, `dist/solutions-panel.js` exists.

- [ ] **Step 5: Run tests**

```bash
npm run ext:test
```

Expected: 170 tests pass (SolutionsPanel tests should still pass since behavior is identical).

- [ ] **Step 6: Commit**

```bash
git add extension/src/panels/solutions-panel-webview.js extension/src/panels/SolutionsPanel.ts extension/esbuild.js
git commit -m "refactor(extension): extract SolutionsPanel IIFE to external script

Same pattern as QueryPanel (cd3407461) — move browser-side behavior to
a dedicated .js file built by esbuild as IIFE. Prevents future 32KB
inline script limit issues as the panel grows."
```

---

## Task 2: Create Webview Panels Skill

**Files:**
- Create: `.agents/skills/webview-panels/SKILL.md`

**Context:** This skill should fire whenever an agent is creating or modifying a webview panel. It establishes the external script pattern as the standard and explains *why*. The format follows the existing `webview-cdp` skill as a reference.

- [ ] **Step 1: Create the skill file**

Create `.agents/skills/webview-panels/SKILL.md` with the following content:

```markdown
---
name: webview-panels
description: Building or modifying VS Code webview panels. Use when creating new panels, adding features to existing panels, or modifying getHtmlContent(). Establishes the external script pattern — all webview JavaScript must live in separate files built by esbuild, never inline in HTML template literals.
---

# Webview Panel Development

## The Rule

**All webview JavaScript lives in external `.js` files, never inline in HTML templates.**

VS Code silently drops inline `<script>` tags exceeding ~32KB. There is no error, no CSP violation, no console message — the script simply doesn't exist in the rendered DOM. External scripts loaded via `<script src="...">` have no size limit.

## Architecture

```
src/panels/
  QueryPanel.ts              ← host-side panel (getHtmlContent returns HTML + <script src>)
  query-panel-webview.js     ← browser-side behavior (IIFE, all DOM/Monaco/message logic)
  SolutionsPanel.ts          ← host-side panel
  solutions-panel-webview.js ← browser-side behavior
  WebviewPanelBase.ts        ← abstract base class
  environmentPicker.ts       ← shared HTML/CSS generators (no JS — inlined in webview scripts)

esbuild.js                   ← builds each webview script as IIFE for browser platform
dist/
  query-panel.js             ← built webview bundle
  solutions-panel.js         ← built webview bundle
  monaco-editor.js           ← Monaco bundle
  editor.worker.js           ← Monaco worker
```

## Creating a New Panel

1. **Create the host-side panel** (`src/panels/FooPanel.ts`):
   - Extend `WebviewPanelBase`
   - `getHtmlContent()` returns HTML + CSS + `<script src="${fooJsUri}">`
   - Handle `postMessage` events from webview
   - Include `dist/` in `localResourceRoots`

2. **Create the webview script** (`src/panels/foo-panel-webview.js`):
   - Plain JavaScript (not TypeScript) — avoids strict mode issues with DOM globals
   - Wrapped in IIFE: `(function() { ... })();`
   - Calls `acquireVsCodeApi()` at the top
   - Ends with `vscode.postMessage({ command: 'ready' });`
   - Include environment picker code directly if needed (copy from existing panel)

3. **Add esbuild entry point** in `esbuild.js`:
   ```javascript
   const fooPanelCtx = await esbuild.context({
       entryPoints: ['src/panels/foo-panel-webview.js'],
       bundle: true,
       format: 'iife',
       minify: production,
       sourcemap: !production,
       sourcesContent: false,
       platform: 'browser',
       outfile: 'dist/foo-panel.js',
       logLevel: 'warning',
   });
   ```
   Add to both `watch()` and `rebuild()`/`dispose()` arrays.

4. **Load in HTML** with nonce:
   ```typescript
   const fooJsUri = webview.asWebviewUri(
       vscode.Uri.joinPath(this.extensionUri, 'dist', 'foo-panel.js')
   );
   // In the HTML template:
   // <script nonce="${nonce}" src="${fooJsUri}"></script>
   ```

## What Goes Where

| Content | Location | Why |
|---------|----------|-----|
| DOM manipulation, event handlers, message handling | `*-webview.js` | Browser-side, must be external |
| CSS styles | Inline `<style>` in `getHtmlContent()` | Small, no size limit on style tags |
| HTML structure | Inline in `getHtmlContent()` | Template literal, small |
| RPC calls, VS Code API, business logic | `*Panel.ts` | Host-side, Node.js context |
| Shared HTML/CSS generators | `environmentPicker.ts` | Reused across panels |

## Exceptions

- **Tiny config scripts** (<200 bytes) that set globals before external scripts load are OK inline:
  ```html
  <script nonce="${nonce}">self.__MONACO_WORKER_URL__ = '${workerUri}';</script>
  ```
- **Notebook cell output scripts** (`virtualScrollScript.ts`) are generated per-cell, tiny (~75 lines), and don't accumulate features. These are fine inline.

## Reference

- QueryPanel: `src/panels/QueryPanel.ts` + `src/panels/query-panel-webview.js`
- SolutionsPanel: `src/panels/SolutionsPanel.ts` + `src/panels/solutions-panel-webview.js`
- Legacy extension: `ppds-extension-archived/webpack.webview.config.js` — had 14 separate behavior entry points, zero inline JS
```

- [ ] **Step 2: Commit**

```bash
git add .agents/skills/webview-panels/SKILL.md
git commit -m "feat(skills): add webview-panels skill for external script pattern

Codifies the architectural pattern established in cd3407461: webview
JavaScript must live in external files built by esbuild, never inline
in HTML template literals. VS Code silently drops inline scripts over
~32KB. Includes full guide for creating new panels."
```

---

## Task 3: Clean Up Stale Dev HTML

**Files:**
- Delete: `extension/dev/query-panel.html`

**Context:** This file is a standalone dev-mode HTML for testing the Data Explorer panel. It still references a `<textarea>` instead of Monaco, predating the Monaco integration (commit `3946de4c7`). It no longer represents the actual panel and would mislead anyone using it as a reference.

- [ ] **Step 1: Delete the file**

```bash
git rm extension/dev/query-panel.html
```

- [ ] **Step 2: Commit**

```bash
git commit -m "chore(extension): remove stale dev-mode query panel HTML

This file still referenced a textarea instead of Monaco and predates
the external script extraction. No longer useful as a dev reference."
```

---

## Verification Checklist

After all tasks complete:

- [ ] `npm run compile` — builds 5 esbuild entry points (extension, monaco, worker, query-panel, solutions-panel)
- [ ] `npx tsc --noEmit` — clean
- [ ] `npm run ext:test` — 170 tests pass
- [ ] Open Data Explorer → Monaco editor renders, environment picker loads
- [ ] Open Solutions panel → solutions list loads, expand/collapse works
- [ ] No inline `<script>` tags in any panel exceed 200 bytes (only config setters)
- [ ] `.agents/skills/webview-panels/SKILL.md` exists and follows skill format
