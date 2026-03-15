# Panel Design Guidance Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add design guidance to the webview-panels skill and implement environment theming CSS so future panel sessions get consistent design patterns automatically.

**Architecture:** Enhance the existing skill file with a new "Design Guidance" section. Implement `data-env-type` toolbar attribute in shared CSS and extend `showEnvironmentPicker` to return environment type. No new files — only modifications to existing ones.

**Tech Stack:** CSS, TypeScript, Markdown (skill file)

---

## Task 1: Add environment theming CSS to shared.css

**Files:**
- Modify: `src/extension/src/panels/styles/shared.css:19-21` (after `.toolbar` rule)

- [ ] **Step 1: Add `[data-env-type]` CSS selectors to shared.css**

Add after the existing `.toolbar` rule block (after line 21):

```css
/* ── Environment type accent ──────────────────────────────────────────────── */

.toolbar[data-env-type="production"]  { border-top: 3px solid var(--vscode-testing-iconFailed, #f14c4c); }
.toolbar[data-env-type="sandbox"]     { border-top: 3px solid var(--vscode-editorWarning-foreground, #cca700); }
.toolbar[data-env-type="development"] { border-top: 3px solid var(--vscode-testing-iconPassed, #73c991); }
.toolbar[data-env-type="test"]        { border-top: 3px solid var(--vscode-editorWarning-foreground, #cca700); }
.toolbar[data-env-type="trial"]       { border-top: 3px solid var(--vscode-editorInfo-foreground, #3794ff); }
```

- [ ] **Step 2: Run CSS lint to verify**

Run: `cd src/extension && npm run lint:css`
Expected: PASS, no new violations

- [ ] **Step 3: Commit**

```bash
git add src/extension/src/panels/styles/shared.css
git commit -m "feat(ext): add environment type accent borders to panel toolbar"
```

---

## Task 2: Extend showEnvironmentPicker to return environment type

**Files:**
- Modify: `src/extension/src/panels/environmentPicker.ts:35,44-49,54-59,95,98`

- [ ] **Step 1: Update return type and map environment type from envList**

In `environmentPicker.ts`:

1. Change return type (line 35) from `Promise<{ url: string; displayName: string } | undefined>` to `Promise<{ url: string; displayName: string; type: string | null } | undefined>`

2. Update `environments` mapping (lines 44-49) to include `type`:
```typescript
environments = result.environments.map(env => ({
    label: env.friendlyName,
    url: env.apiUrl,
    detail: env.region ? `${env.apiUrl} (${env.region})` : env.apiUrl,
    isCurrent: env.apiUrl === currentUrl,
    type: env.type,
}));
```

3. Add `type` to `EnvironmentOption` interface (line 21-26):
```typescript
interface EnvironmentOption {
    label: string;
    url: string;
    detail?: string;
    isCurrent?: boolean;
    type?: string | null;
}
```

4. Add `type` to QuickPick items mapping (lines 54-59):
```typescript
const items = environments.map(env => ({
    label: env.isCurrent ? `$(check) ${env.label}` : env.label,
    description: env.isCurrent ? 'current' : undefined,
    detail: env.detail,
    url: env.url,
    displayName: env.label,
    type: env.type ?? null,
}));
```

5. Update manual entry option (line 63-68) to include `type: null`

6. Update manual return (line 95): `return { url: trimmed, displayName: trimmed, type: null };`

7. Update normal return (line 98): `return { url: selected.url, displayName: selected.displayName, type: selected.type };`

- [ ] **Step 2: Run typecheck to verify**

Run: `cd src/extension && npm run typecheck:all`
Expected: PASS — existing callers destructure `{ url, displayName }` and will ignore the new `type` field without error.

- [ ] **Step 3: Commit**

```bash
git add src/extension/src/panels/environmentPicker.ts
git commit -m "feat(ext): return environment type from showEnvironmentPicker"
```

---

## Task 3: Set data-env-type attribute in panel host code

**Files:**
- Modify: `src/extension/src/panels/QueryPanel.ts` (env selection + init)
- Modify: `src/extension/src/panels/SolutionsPanel.ts` (env selection + init)
- Modify: `src/extension/src/panels/webview/shared/message-types.ts` (add envType to updateEnvironment)
- Modify: `src/extension/src/panels/webview/query-panel.ts` (set attribute on toolbar)
- Modify: `src/extension/src/panels/webview/solutions-panel.ts` (set attribute on toolbar)

The flow: host panel gets env type from `showEnvironmentPicker` or `authWho` → sends it to webview via existing `updateEnvironment` message → webview sets `data-env-type` on toolbar element.

- [ ] **Step 1: Add `envType` field to updateEnvironment messages in message-types.ts**

The two union types differ — update both:

In `QueryPanelHostToWebview` (line 37), change:
```typescript
| { command: 'updateEnvironment'; name: string; url: string | null }
```
to:
```typescript
| { command: 'updateEnvironment'; name: string; url: string | null; envType: string | null }
```

In `SolutionsPanelHostToWebview` (line 91), change:
```typescript
| { command: 'updateEnvironment'; name: string }
```
to:
```typescript
| { command: 'updateEnvironment'; name: string; envType: string | null }
```

- [ ] **Step 2: Update QueryPanel.ts to pass envType**

Add a private field: `private environmentType: string | null = null;`

In the `requestEnvironmentList` handler (line 212-218), capture type from picker:
```typescript
case 'requestEnvironmentList': {
    const env = await showEnvironmentPicker(this.daemon, this.environmentUrl);
    if (env) {
        this.environmentUrl = env.url;
        this.environmentDisplayName = env.displayName;
        this.environmentType = env.type;
        this.postMessage({ command: 'updateEnvironment', name: env.displayName, url: env.url, envType: env.type });
        this.updateTitle();
    }
    break;
}
```

In `initEnvironment()` (line 243), capture type from `authWho` response:
- After line 249 (`this.environmentUrl = who.environment.url;`), add: `this.environmentType = who.environment.type ?? null;`
- Update the postMessage at line 255 to include `envType: this.environmentType`

- [ ] **Step 3: Update SolutionsPanel.ts to pass envType**

Add a private field: `private environmentType: string | null = null;`

In `handleEnvironmentPicker()` (line 153-161), capture type from picker:
- After line 156 (`this.environmentUrl = result.url;`), add: `this.environmentType = result.type;`
- Update postMessage at line 160 to: `this.postMessage({ command: 'updateEnvironment', name: result.displayName, envType: result.type });`

In `initialize()` (line 130), capture type from `authWho`:
- After line 136 (`this.environmentUrl = who.environment.url;`), add: `this.environmentType = who.environment.type ?? null;`
- Update postMessage at line 145 to include `envType: this.environmentType`

- [ ] **Step 4: Update query-panel.ts webview to set data-env-type on toolbar**

In the `updateEnvironment` handler (line 747-750), after `currentEnvironmentUrl = msg.url || null;`, add:
```typescript
const toolbar = document.querySelector('.toolbar');
if (toolbar) {
    if (msg.envType) {
        toolbar.setAttribute('data-env-type', msg.envType.toLowerCase());
    } else {
        toolbar.removeAttribute('data-env-type');
    }
}
```

- [ ] **Step 5: Update solutions-panel.ts webview to set data-env-type on toolbar**

In the `updateEnvironment` handler (line 179-181), after `updateEnvironmentDisplay(msg.name);`, add the same toolbar attribute logic as Step 4.

- [ ] **Step 6: Run typecheck and lint**

Run: `cd src/extension && npm run typecheck:all && npm run lint`
Expected: PASS — assertNever will catch any missed cases.

- [ ] **Step 7: Commit**

```bash
git add src/extension/src/panels/
git commit -m "feat(ext): wire environment type through to toolbar data attribute"
```

---

## Task 4: Add Design Guidance section to webview-panels skill

**Files:**
- Modify: `.claude/skills/webview-panels/SKILL.md` (append after "Reference Implementations" section)

- [ ] **Step 1: Append Design Guidance section**

Add the following after the "Reference Implementations" section at the end of the skill file:

```markdown
## Design Guidance

### Panel Anatomy

Every panel follows the same three-zone layout defined in `shared.css`:

```
┌─────────────────────────────────────────┐
│ Toolbar    [env picker] [actions]  [...] │  ← .toolbar (flex, 8px gap, border-bottom)
├─────────────────────────────────────────┤
│                                         │
│              Content area               │  ← .content (flex: 1, overflow: auto)
│         (table / tree / detail)         │
│                                         │
├─────────────────────────────────────────┤
│ Status bar: record count, timing, etc.  │  ← .status-bar (border-top, 12px font)
└─────────────────────────────────────────┘
```

**Rules:**
- Toolbar always contains the environment picker (via `environmentPicker.ts`)
- Content area gets `flex: 1` and handles its own scrolling
- Status bar shows contextual counts/timing — never empty, show "Ready" as default
- Empty state (`.empty-state`), error state (`.error-state`), and loading state (`.loading-state`) are in `shared.css` — use them, don't reinvent

### Reusable CSS Patterns

Before writing panel-specific CSS, check what already exists. Each pattern has a reference implementation — read it before building yours.

| Pattern | CSS Source | Reference Panel | Use When |
|---------|-----------|----------------|----------|
| Data table (sticky header, sort, selection) | `query-panel.css` `.results-table` | QueryPanel | Tabular data with columns |
| Tree/list (chevron expand, nested indent) | `solutions-panel.css` `.solution-list` | SolutionsPanel | Hierarchical browsing |
| Detail card (standalone, label/value grid) | `solutions-panel.css` `.detail-card` | SolutionsPanel | Inline record details |
| Detail card (nested, inside list items) | `solutions-panel.css` `.component-detail-card` | SolutionsPanel | Expandable item details |
| Filter bar (debounced input, count badge) | `query-panel.css` `.filter-bar` | QueryPanel | Filtering loaded results |
| Dropdown menu | `query-panel.css` `.dropdown-menu` | QueryPanel | Export, overflow actions |
| Context menu | `query-panel.css` `.context-menu` | QueryPanel | Right-click actions |

**Rules:**
- `@import './shared.css'` as your first line — every panel gets toolbar, status bar, states for free
- Copy the CSS pattern from the reference, don't `@import` panel-specific files into other panels
- Use `var(--vscode-*)` tokens for all colors — never hardcode hex values
- Spacing: 4/6/8/12/16/40px scale (match existing panels, don't introduce new values)
- Font sizes: 11px (labels/badges), 12px (secondary text, detail cards), 13px (body/inputs)
- Border radius: 2px (inputs, badges), 4px (menus, dropdowns)

### Environment Theming

Panels display a colored top-border accent on the toolbar based on environment type. This maps to the TUI's `StatusBar_Production/Sandbox/Development/Test/Trial` color schemes.

The CSS rules are in `shared.css` using `[data-env-type]` attribute selectors:
- Production → red, Sandbox → yellow, Development → green, Test → yellow, Trial → blue
- Unknown/null → no attribute, no accent (natural default)

**Implementation:** When the environment is selected (via picker or `authWho` on init), the host panel sends `envType` in the `updateEnvironment` message. The webview script sets `data-env-type` on the `.toolbar` element. See QueryPanel and SolutionsPanel for reference.

### Keyboard Shortcuts

All panels should support a standard set of keyboard shortcuts. Register in the webview script via `document.addEventListener('keydown', ...)`. Check `event.metaKey || event.ctrlKey` for cross-platform support.

| Shortcut | Action | Notes |
|----------|--------|-------|
| `Ctrl/Cmd+R` | Refresh data | Re-fetch from daemon |
| `Ctrl/Cmd+F` | Focus filter bar | If panel has filtering |
| `Escape` | Close filter / deselect | Context-dependent |
| `Ctrl/Cmd+Shift+E` | Export visible data | CSV or JSON, match query panel pattern |
| `Ctrl/Cmd+C` | Copy selection | Tables: selected cells as TSV |

### TUI Functional Parity

When designing an extension panel, verify the equivalent TUI screen exposes the same capabilities. This is a functional check, not a visual one — the interfaces look different but should offer equivalent data and actions.

**Before marking a panel complete, confirm:**
- [ ] Same data fields visible (columns in table, fields in detail view)
- [ ] Same filter/search capabilities
- [ ] Same sort options
- [ ] Same export formats available
- [ ] Same drill-down / navigation paths (e.g., solution → components)
- [ ] Same refresh behavior
- [ ] Environment scoping works equivalently

If the TUI screen doesn't exist yet, file or reference the corresponding wave:2 issue. Panels and screens can be built in parallel but should converge on the same RPC methods and data shapes.
```

- [ ] **Step 2: Verify skill renders correctly**

Read back the skill file and confirm markdown formatting is intact, code blocks render, table aligns.

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/webview-panels/SKILL.md
git commit -m "feat(skill): add design guidance section to webview-panels skill (#307)"
```

---

## Task 5: Quality gates and close issue

**Files:** None (verification only)

- [ ] **Step 1: Run all quality gates**

Run: `cd src/extension && npm run typecheck:all && npm run lint && npm run lint:css && npm run test`
Expected: All PASS

- [ ] **Step 2: Close GitHub issue #307**

```bash
gh issue close 307 -c "Implemented: Design guidance added to webview-panels skill covering panel anatomy, reusable CSS patterns, environment theming (with working CSS implementation), keyboard shortcuts, and TUI functional parity checklist."
```

- [ ] **Step 3: Push branch**

```bash
git push
```
