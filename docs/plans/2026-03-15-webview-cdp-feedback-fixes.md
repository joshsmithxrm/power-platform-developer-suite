# Webview CDP Tool Feedback Fixes

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 4 pain points discovered during a real logging-verification session with the webview-cdp tool, then update the skill documentation.

**Architecture:** All changes are in `extension/tools/webview-cdp.mjs` (tool code) and `.claude/skills/webview-cdp/SKILL.md` (skill docs). Four code changes: (1) Fix `logs --channel` to filter by filename instead of file content. (2) Add `text` command for quick DOM text reads. (3) Add `--build` flag to `launch` for compiling before start. (4) Add `notebook` command with `run` and `run-all` subcommands. Then one skill update covering all new commands plus panel-obscuring documentation.

**Chunk dependencies:** Chunks 1-3 are independent. **Chunk 4 depends on Chunk 2** (the `VALID_COMMANDS` "Old" value includes `'text'` from Chunk 2). Chunk 5 depends on all code chunks being complete.

**Tech Stack:** JavaScript (Node.js ESM), Vitest, Playwright Electron

---

## File Structure

| Action | File | Responsibility |
|--------|------|---------------|
| Modify | `extension/tools/webview-cdp.mjs:13-14` | Add `text`, `notebook` to VALID_COMMANDS |
| Modify | `extension/tools/webview-cdp.mjs:42-97` | Parse args for `text`, `notebook`, `launch --build` |
| Modify | `extension/tools/webview-cdp.mjs:290-404` | Daemon handlers: fix `logs` filter, add `text` and `notebook` actions |
| Modify | `extension/tools/webview-cdp.mjs:464-495` | `cmdLaunch` — `--build` flag runs `npm run compile` |
| Modify | `extension/tools/webview-cdp.mjs:610-637` | Add `cmdText`, `cmdNotebook`, wire into main dispatch |
| Modify | `extension/tools/webview-cdp.test.mjs` | Tests for new parsing: `text`, `notebook`, `launch --build` |
| Modify | `.claude/skills/webview-cdp/SKILL.md` | Document new commands, notebook workflows, panel tip |

---

## Chunk 1: Fix `logs --channel` Filtering

### Task 1: Fix channel filter to match by filename instead of file content

The current implementation at `webview-cdp.mjs:376-400` reads every `.log` file's entire content and checks if the channel string appears anywhere. This returns megabytes of unrelated logs (file watchers, renderer, etc.) because many files mention "PPDS" in passing.

VS Code's LogOutputChannel writes to files named like `N-ChannelName.log` inside `logs/window1/exthost/output_logging_<date>/`. Filtering by filename is both faster and correct.

**Files:**
- Modify: `extension/tools/webview-cdp.mjs:396-397`
- Modify: `extension/tools/webview-cdp.test.mjs`

- [ ] **Step 1: Write failing test for new logs parsing (no test change needed — parsing is unchanged)**

The `parseArgs` tests for `logs` already exist (line 46-54 of test file). The filtering bug is in the daemon handler, which can't be unit tested without Playwright. Skip to implementation.

- [ ] **Step 2: Fix the daemon logs handler**

In `webview-cdp.mjs`, replace the matching logic at line 396-397:

```javascript
// Old (line 396-397) — matches on file CONTENT (broken: returns entire unrelated files):
const matching = logFiles
  .filter(f => { try { return readFileSync(f, 'utf-8').includes(params.channel); } catch { return false; } });

// New — matches on FILENAME (correct: VS Code names log files as N-ChannelName.log):
const matching = logFiles
  .filter(f => {
    const name = f.split(/[\\/]/).pop();
    return name.toLowerCase().includes(params.channel.toLowerCase());
  });
```

This changes the filter from "read entire file, check if content mentions channel" to "check if filename contains channel name" (case-insensitive). No file I/O during filtering — only reads the matched files afterward (line 399).

- [ ] **Step 3: Run existing tests**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS — existing parseArgs and parseKeyCombo tests still pass (daemon behavior change, not parsing change)

- [ ] **Step 4: Commit**

```bash
git add extension/tools/webview-cdp.mjs
git commit -m "fix(tools): filter logs --channel by filename, not file content

The old approach read every .log file and matched on content, returning
megabytes of unrelated VS Code logs. VS Code names LogOutputChannel files
as N-ChannelName.log — matching on filename is faster and correct."
```

---

## Chunk 2: Add `text` Command

### Task 2: Add a `text` command for quick DOM text reads

Currently reading element text requires: `eval 'document.querySelector("#execution-time")?.textContent'`. The `text` command reduces this to: `text "#execution-time"`.

**Files:**
- Modify: `extension/tools/webview-cdp.mjs:13` (VALID_COMMANDS)
- Modify: `extension/tools/webview-cdp.mjs:42-97` (parseArgs)
- Modify: `extension/tools/webview-cdp.mjs:290-404` (daemon handler)
- Modify: `extension/tools/webview-cdp.mjs:610-637` (cmdText + dispatch)
- Modify: `extension/tools/webview-cdp.test.mjs`

- [ ] **Step 1: Write failing tests for `text` command parsing**

Append to `extension/tools/webview-cdp.test.mjs`, inside the `parseArgs` describe block (after the `select` test at line 109):

```javascript
  it('parses text with selector', () => {
    const result = parseArgs(['text', '#status']);
    expect(result).toEqual({ command: 'text', args: ['#status'], page: false, target: undefined, ext: undefined });
  });

  it('parses text with --ext', () => {
    const result = parseArgs(['text', '#status', '--ext', 'ppds']);
    expect(result).toEqual({ command: 'text', args: ['#status'], page: false, target: undefined, ext: 'ppds' });
  });
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: FAIL — `Unknown command: text`

- [ ] **Step 3: Add `text` to VALID_COMMANDS**

In `webview-cdp.mjs`, line 13:

```javascript
// Old:
const VALID_COMMANDS = ['launch', 'close', 'connect', 'command', 'wait',
  'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs'];

// New:
const VALID_COMMANDS = ['launch', 'close', 'connect', 'command', 'wait',
  'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs', 'text'];
```

No changes needed in `parseArgs` — `text` falls through to the interaction commands block (line 76-96), which already handles `--page`, `--target`, `--ext`, and positional args. This gives `text "#selector"` the same parsed shape as `eval "expression"`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS — `text` now recognized, falls through to interaction command parsing

- [ ] **Step 5: Add daemon handler for `text`**

In `webview-cdp.mjs`, add a new case in the `handleAction` switch, after the `eval` case (after line 305):

```javascript
      case 'text': {
        const target = await resolveTarget(params);
        const text = await target.evaluate(
          (sel) => document.querySelector(sel)?.textContent ?? '',
          params.selector
        );
        return { text };
      }
```

- [ ] **Step 6: Add client function and wire dispatch**

In `webview-cdp.mjs`, add after `cmdEval` (after line 535):

```javascript
async function cmdText(parsed) {
  if (!parsed.args[0]) throw new Error('Usage: text <selector>');
  const session = readSession();
  const result = await sendToDaemon(session, 'text', {
    selector: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(result.text);
}
```

Then add to the main dispatch switch (after the `eval` case around line 630):

```javascript
    case 'text': await cmdText(parsed); break;
```

- [ ] **Step 7: Run all tests**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS — all existing + new tests green

- [ ] **Step 8: Commit**

```bash
git add extension/tools/webview-cdp.mjs extension/tools/webview-cdp.test.mjs
git commit -m "feat(tools): add text command for quick DOM element reads

text \"#selector\" returns the textContent of the matched element.
Shorthand for eval 'document.querySelector(\"#sel\")?.textContent'.
Returns empty string if element not found."
```

---

## Chunk 3: Add `--build` Flag to `launch`

### Task 3: Add `--build` flag that compiles the extension before launching VS Code

The first VS Code launch after code changes uses stale compiled output. Adding `--build` runs `npm run compile` before spawning VS Code.

**Files:**
- Modify: `extension/tools/webview-cdp.mjs:50-51` (parseArgs for launch)
- Modify: `extension/tools/webview-cdp.mjs:464-495` (cmdLaunch)
- Modify: `extension/tools/webview-cdp.test.mjs`

- [ ] **Step 1: Update existing launch tests and add new ones for `--build`**

Update the existing launch tests at lines 7-13 to include the `build` field, and add new tests for `--build`. All in one step so tests and implementation change together:

```javascript
  it('parses launch with defaults', () => {
    const result = parseArgs(['launch']);
    expect(result).toEqual({ command: 'launch', workspace: undefined, build: false });
  });

  it('parses launch with workspace', () => {
    const result = parseArgs(['launch', '/my/workspace']);
    expect(result).toEqual({ command: 'launch', workspace: '/my/workspace', build: false });
  });

  it('parses launch with --build', () => {
    const result = parseArgs(['launch', '--build']);
    expect(result).toEqual({ command: 'launch', workspace: undefined, build: true });
  });

  it('parses launch with workspace and --build', () => {
    const result = parseArgs(['launch', '/my/workspace', '--build']);
    expect(result).toEqual({ command: 'launch', workspace: '/my/workspace', build: true });
  });
```

- [ ] **Step 2: Update parseArgs for launch**

In `webview-cdp.mjs`, replace the launch parsing (lines 50-51):

```javascript
// Old:
  if (command === 'launch') {
    return { command, workspace: rest[0] };
  }

// New:
  if (command === 'launch') {
    let workspace, build = false;
    for (const arg of rest) {
      if (arg === '--build') build = true;
      else if (!workspace) workspace = arg;
    }
    return { command, workspace, build };
  }
```

- [ ] **Step 3: Run tests to verify they pass**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS

- [ ] **Step 4: Add build step to cmdLaunch**

In `webview-cdp.mjs`, add build logic at the top of `cmdLaunch` (after line 464, before the session check):

```javascript
async function cmdLaunch(parsed) {
  if (parsed.build) {
    const extDir = resolve(__dirname, '..');
    console.log('Building extension...');
    execSync('npm run compile', { cwd: extDir, stdio: 'inherit' });
    console.log('Build complete');
  }

  if (existsSync(SESSION_FILE)) {
    // ... rest unchanged ...
```

- [ ] **Step 5: Run all tests**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add extension/tools/webview-cdp.mjs extension/tools/webview-cdp.test.mjs
git commit -m "feat(tools): add --build flag to launch for fresh compiles

launch --build runs npm run compile before spawning VS Code,
preventing stale binary issues on first launch after code changes."
```

---

## Chunk 4: Add `notebook` Command

**Depends on:** Chunk 2 (VALID_COMMANDS already includes `'text'`)

### Task 4: Add `notebook run` and `notebook run-all` composite commands

`command "Notebook: Execute Cell"` fails because opening the command palette steals focus from the cell. `notebook run` clicks the cell's run button in the DOM instead — focus-independent and reliable.

**Files:**
- Modify: `extension/tools/webview-cdp.mjs:13` (VALID_COMMANDS — already includes `'text'` from Chunk 2)
- Modify: `extension/tools/webview-cdp.mjs:42-97` (parseArgs)
- Modify: `extension/tools/webview-cdp.mjs:290-404` (daemon handler)
- Modify: `extension/tools/webview-cdp.mjs:610-637` (cmdNotebook + dispatch)
- Modify: `extension/tools/webview-cdp.test.mjs`

- [ ] **Step 1: Write failing tests for `notebook` command parsing**

Append to `extension/tools/webview-cdp.test.mjs`, inside the `parseArgs` describe block:

```javascript
  it('parses notebook run', () => {
    const result = parseArgs(['notebook', 'run']);
    expect(result).toEqual({ command: 'notebook', subcommand: 'run' });
  });

  it('parses notebook run-all', () => {
    const result = parseArgs(['notebook', 'run-all']);
    expect(result).toEqual({ command: 'notebook', subcommand: 'run-all' });
  });

  it('errors on notebook with no subcommand', () => {
    expect(() => parseArgs(['notebook'])).toThrow('notebook requires a subcommand');
  });

  it('errors on notebook with unknown subcommand', () => {
    expect(() => parseArgs(['notebook', 'foo'])).toThrow('Unknown notebook subcommand: foo');
  });
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: FAIL — `Unknown command: notebook`

- [ ] **Step 3: Add `notebook` to VALID_COMMANDS and parseArgs**

In `webview-cdp.mjs`, update VALID_COMMANDS (line 13):

```javascript
// Old:
const VALID_COMMANDS = ['launch', 'close', 'connect', 'command', 'wait',
  'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs', 'text'];

// New:
const VALID_COMMANDS = ['launch', 'close', 'connect', 'command', 'wait',
  'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs', 'text', 'notebook'];
```

Add notebook parsing in `parseArgs`, after the `logs` block (after line 74):

```javascript
  if (command === 'notebook') {
    const NOTEBOOK_SUBCOMMANDS = ['run', 'run-all'];
    const subcommand = rest[0];
    if (!subcommand) throw new Error('notebook requires a subcommand: ' + NOTEBOOK_SUBCOMMANDS.join(', '));
    if (!NOTEBOOK_SUBCOMMANDS.includes(subcommand)) throw new Error(`Unknown notebook subcommand: ${subcommand}. Valid: ${NOTEBOOK_SUBCOMMANDS.join(', ')}`);
    return { command, subcommand };
  }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS

- [ ] **Step 5: Add daemon handler for `notebook`**

In `webview-cdp.mjs`, add a new case in the `handleAction` switch, before the `logs` case:

```javascript
      case 'notebook': {
        if (params.subcommand === 'run') {
          // Click the run button on the focused/selected cell.
          // This is more reliable than command palette (which steals focus)
          // or Ctrl+Enter (which may trigger executeAndInsertBelow).
          // VS Code notebook cells have a run button in the cell toolbar
          // with the codicon-notebook-execute icon.
          const runBtn = page.locator('.notebook-cell-list .cell-focus-indicator-top + .cell-inner-container .run-button-container button, .notebook-cell-list .focused .run-button-container button, .notebook-cell-list .cell-selected .run-button-container button').first();
          try {
            await runBtn.click({ timeout: 3000 });
          } catch {
            // Fallback: use command palette — works when button not visible
            await executeCommand('Notebook: Run Cell');
          }
          await page.waitForTimeout(500);
          return {};
        }
        if (params.subcommand === 'run-all') {
          await executeCommand('Notebook: Run All');
          return {};
        }
        throw new Error(`Unknown notebook subcommand: ${params.subcommand}`);
      }
```

- [ ] **Step 6: Add client function and wire dispatch**

In `webview-cdp.mjs`, add after `cmdLogs` (around line 608):

```javascript
async function cmdNotebook(parsed) {
  const session = readSession();
  await sendToDaemon(session, 'notebook', { subcommand: parsed.subcommand });
  console.log(`notebook ${parsed.subcommand}: done`);
}
```

Then add to the main dispatch switch:

```javascript
    case 'notebook': await cmdNotebook(parsed); break;
```

- [ ] **Step 7: Run all tests**

Run: `npx vitest run extension/tools/webview-cdp.test.mjs`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add extension/tools/webview-cdp.mjs extension/tools/webview-cdp.test.mjs
git commit -m "feat(tools): add notebook run/run-all commands

notebook run clicks the cell's run button (focus-independent) instead of
using the command palette which steals focus and breaks cell execution.
notebook run-all uses command palette since Notebook: Run All doesn't
need cell focus."
```

---

## Chunk 5: Update Skill Documentation

### Task 5: Update the webview-cdp skill to document all new commands

**Files:**
- Modify: `.claude/skills/webview-cdp/SKILL.md`

- [ ] **Step 1: Add `text` and `notebook` to the Commands table**

In the Commands table, add these rows:

```markdown
| `text "<selector>" [--page]` | `text "#status"` | Read textContent of element |
| `notebook run` | `notebook run` | Execute focused cell (clicks run button) |
| `notebook run-all` | `notebook run-all` | Execute all cells |
```

- [ ] **Step 2: Update the `launch` row in the Commands table**

```markdown
| `launch [workspace] [--build]` | `launch` or `launch --build` | Start VS Code with extension (--build compiles first) |
```

- [ ] **Step 3: Add a "Notebook Workflows" section**

Add after the "Check for errors" section in Common Patterns:

```markdown
### Execute notebook cells
```bash
# Run the focused cell — clicks the run button (reliable, focus-independent)
node extension/tools/webview-cdp.mjs notebook run
node extension/tools/webview-cdp.mjs screenshot $TEMP/after-run.png

# Run all cells
node extension/tools/webview-cdp.mjs notebook run-all
node extension/tools/webview-cdp.mjs screenshot $TEMP/all-cells.png
```

**Avoid** using `command "Notebook: Execute Cell"` — opening the command palette steals focus from the cell, causing the command to silently fail. Similarly, `key "ctrl+enter"` may trigger `executeAndInsertBelow` (creates a duplicate cell) depending on VS Code's keybinding context.

### Hide panel for notebook screenshots
```bash
# The output panel takes vertical space — hide it to see cell output clearly
node extension/tools/webview-cdp.mjs command "View: Toggle Panel Visibility"
node extension/tools/webview-cdp.mjs screenshot $TEMP/notebook-clean.png
```
```

- [ ] **Step 4: Add a "Quick DOM reads" pattern**

Add after the "Check DOM state" section:

```markdown
### Read element text
```bash
# Quick read of an element's text content
node extension/tools/webview-cdp.mjs text "#execution-time" --ext "power-platform-developer-suite"
# Returns: "in 298ms via Dataverse"

# Equivalent eval (more verbose, same result):
node extension/tools/webview-cdp.mjs eval 'document.querySelector("#execution-time")?.textContent'
```
```

- [ ] **Step 5: Update the "Core Workflow" example**

Change step 1 to use `--build`:

```markdown
# 1. Launch VS Code with the extension (--build ensures fresh compilation)
node extension/tools/webview-cdp.mjs launch --build
```

- [ ] **Step 6: Commit**

```bash
git add .claude/skills/webview-cdp/SKILL.md
git commit -m "docs(tools): update webview-cdp skill for text, notebook, --build commands

Documents new text command, notebook run/run-all, launch --build.
Adds notebook workflow patterns and panel-hiding tip."
```

---

## Verification

After all tasks are complete:

- [ ] **Final: Run full test suite**

```bash
npx vitest run extension/tools/webview-cdp.test.mjs
```

Expected: All tests pass.
