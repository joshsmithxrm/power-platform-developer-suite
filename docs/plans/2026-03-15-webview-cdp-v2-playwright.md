# Webview CDP Tool v2: Playwright Electron Rewrite

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite the webview-cdp CLI tool from raw CDP to Playwright Electron, adding daemon lifecycle management, command palette execution, console log capture, and output channel reading.

**Architecture:** Single-file CLI (`extension/tools/webview-cdp.mjs`) with dual mode: caller (short-lived per command) and daemon (long-lived, holds VS Code alive). Daemon uses `_electron.launch()` + console capture. Callers reconnect via `chromium.connectOverCDP(wsEndpoint)`. Webview content accessed via Playwright's `contentFrame()` chain.

**Tech Stack:** `@playwright/test` (Electron module), `@vscode/test-electron`, Node.js `child_process.fork()`

**Spec:** [`specs/vscode-webview-cdp-tool.md`](../../specs/vscode-webview-cdp-tool.md) (v2.0)

**Reference code:** Archived E2E helpers at `C:\VS\ppdsw\ppds-extension-archived\e2e\helpers\` — `VSCodeLauncher.ts`, `CommandPaletteHelper.ts`, `WebviewHelper.ts`. These contain proven patterns for Playwright Electron + VS Code interaction.

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `extension/tools/webview-cdp.mjs` | **Rewrite** | Complete rewrite. Dual-mode: caller CLI + daemon. Playwright Electron engine replaces raw CDP. |
| `extension/tools/webview-cdp.test.mjs` | **Update** | Keep pure function tests (parseArgs, validatePort, parseKeyCombo). Add tests for new parseArgs shapes (no port on launch, --ext flag, `command`/`wait`/`logs` commands). Remove `filterWebviewTargets` tests (function removed). |
| `.agents/skills/webview-cdp/SKILL.md` | **Rewrite** | Complete rewrite for v2. Remove attach, add command/wait/logs, update workflow, add screenshot temp dir guidance, remove "Known Limitations" that are now fixed. |
| `extension/package.json` | **Modify** | Remove `ws` dev dependency. |
| `extension/.gitignore` | **Already done** | Console log file already gitignored. No changes needed. |

---

## Chunk 1: Setup and Pure Functions

### Task 1: Remove `ws` dependency

**Files:**
- Modify: `extension/package.json`

- [ ] **Step 1: Uninstall ws**

Run: `cd extension && npm uninstall ws`

- [ ] **Step 2: Commit**

```bash
git add extension/package.json extension/package-lock.json
git commit -m "chore: remove ws dependency — Playwright handles WebSocket communication"
```

---

### Task 2: Update unit tests for v2 argument parsing

**Files:**
- Modify: `extension/tools/webview-cdp.test.mjs`

The v2 CLI changes:
- `launch` takes `[workspace]` not `[port] [workspace]`
- New commands: `command`, `wait`, `logs`
- `attach` removed
- `--page` flag stays, `--ext` flag added
- `filterWebviewTargets` no longer exported (webview discovery done via Playwright frames)

- [ ] **Step 1: Rewrite the test file**

```js
// extension/tools/webview-cdp.test.mjs
import { describe, it, expect } from 'vitest';
import { parseArgs, parseKeyCombo, validatePort } from './webview-cdp.mjs';

describe('parseArgs', () => {
  it('parses launch with defaults', () => {
    const result = parseArgs(['launch']);
    expect(result).toEqual({ command: 'launch', workspace: undefined });
  });

  it('parses launch with workspace', () => {
    const result = parseArgs(['launch', '/my/workspace']);
    expect(result).toEqual({ command: 'launch', workspace: '/my/workspace' });
  });

  it('parses close', () => {
    const result = parseArgs(['close']);
    expect(result).toEqual({ command: 'close' });
  });

  it('parses connect', () => {
    const result = parseArgs(['connect']);
    expect(result).toEqual({ command: 'connect' });
  });

  it('parses command', () => {
    const result = parseArgs(['command', 'ppds.dataExplorer']);
    expect(result).toEqual({ command: 'command', args: ['ppds.dataExplorer'], page: false });
  });

  it('parses wait with default timeout', () => {
    const result = parseArgs(['wait']);
    expect(result).toEqual({ command: 'wait', timeout: 30000, ext: undefined });
  });

  it('parses wait with timeout', () => {
    const result = parseArgs(['wait', '5000']);
    expect(result).toEqual({ command: 'wait', timeout: 5000, ext: undefined });
  });

  it('parses wait with --ext', () => {
    const result = parseArgs(['wait', '--ext', 'ppds']);
    expect(result).toEqual({ command: 'wait', timeout: 30000, ext: 'ppds' });
  });

  it('parses logs with no flags', () => {
    const result = parseArgs(['logs']);
    expect(result).toEqual({ command: 'logs', channel: undefined, level: undefined });
  });

  it('parses logs with --channel', () => {
    const result = parseArgs(['logs', '--channel', 'PPDS']);
    expect(result).toEqual({ command: 'logs', channel: 'PPDS', level: undefined });
  });

  it('parses click with selector', () => {
    const result = parseArgs(['click', '#btn']);
    expect(result).toEqual({ command: 'click', args: ['#btn'], page: false, right: false, target: undefined, ext: undefined });
  });

  it('parses click with --right and --page', () => {
    const result = parseArgs(['click', '#btn', '--right', '--page']);
    expect(result).toEqual({ command: 'click', args: ['#btn'], page: true, right: true, target: undefined, ext: undefined });
  });

  it('parses eval with --page', () => {
    const result = parseArgs(['eval', 'document.title', '--page']);
    expect(result).toEqual({ command: 'eval', args: ['document.title'], page: true, target: undefined, ext: undefined });
  });

  it('parses --target flag', () => {
    const result = parseArgs(['eval', '1+1', '--target', '2']);
    expect(result).toEqual({ command: 'eval', args: ['1+1'], page: false, target: 2, ext: undefined });
  });

  it('parses --ext flag on interaction commands', () => {
    const result = parseArgs(['eval', '1+1', '--ext', 'ppds']);
    expect(result).toEqual({ command: 'eval', args: ['1+1'], page: false, target: undefined, ext: 'ppds' });
  });

  it('parses screenshot with --page', () => {
    const result = parseArgs(['screenshot', '/tmp/shot.png', '--page']);
    expect(result).toEqual({ command: 'screenshot', args: ['/tmp/shot.png'], page: true, target: undefined, ext: undefined });
  });

  it('parses mouse with event and coordinates', () => {
    const result = parseArgs(['mouse', 'mousedown', '150', '200']);
    expect(result).toEqual({ command: 'mouse', args: ['mousedown', '150', '200'], page: false, target: undefined, ext: undefined });
  });

  it('parses key', () => {
    const result = parseArgs(['key', 'ctrl+shift+p']);
    expect(result).toEqual({ command: 'key', args: ['ctrl+shift+p'], page: false });
  });

  it('parses key with --page', () => {
    const result = parseArgs(['key', 'ctrl+shift+p', '--page']);
    expect(result).toEqual({ command: 'key', args: ['ctrl+shift+p'], page: true });
  });

  it('errors on empty args', () => {
    expect(() => parseArgs([])).toThrow('No command provided');
  });

  it('errors on unknown command', () => {
    expect(() => parseArgs(['foobar'])).toThrow('Unknown command: foobar');
  });

  it('errors on removed attach command', () => {
    expect(() => parseArgs(['attach'])).toThrow('Unknown command: attach');
  });
});

describe('validatePort', () => {
  it('accepts valid port', () => { expect(validatePort(9223)).toBe(9223); });
  it('rejects port below 1024', () => { expect(() => validatePort(80)).toThrow('Invalid port: must be 1024-65535'); });
  it('rejects port above 65535', () => { expect(() => validatePort(70000)).toThrow('Invalid port: must be 1024-65535'); });
  it('rejects non-integer', () => { expect(() => validatePort(NaN)).toThrow('Invalid port: must be 1024-65535'); });
});

describe('parseKeyCombo', () => {
  it('parses simple key', () => { expect(parseKeyCombo('Escape')).toEqual({ key: 'Escape', modifiers: {} }); });
  it('parses ctrl+key', () => { expect(parseKeyCombo('ctrl+c')).toEqual({ key: 'c', modifiers: { ctrl: true } }); });
  it('parses ctrl+shift+key', () => { expect(parseKeyCombo('ctrl+shift+c')).toEqual({ key: 'c', modifiers: { ctrl: true, shift: true } }); });
  it('parses ctrl+enter', () => { expect(parseKeyCombo('ctrl+enter')).toEqual({ key: 'Enter', modifiers: { ctrl: true } }); });
  it('rejects unknown modifier', () => { expect(() => parseKeyCombo('foo+a')).toThrow("Invalid key combo: unknown modifier 'foo'"); });
  it('rejects empty string', () => { expect(() => parseKeyCombo('')).toThrow('Empty key combo'); });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd extension && npx vitest run tools/webview-cdp.test.mjs`
Expected: FAIL — parseArgs returns v1 shapes, import of `filterWebviewTargets` may fail

- [ ] **Step 3: Commit failing tests**

```bash
git add extension/tools/webview-cdp.test.mjs
git commit -m "test: update webview-cdp tests for v2 argument parsing"
```

---

## Chunk 2: Core Rewrite — Daemon, Launch, Close

### Task 3: Rewrite webview-cdp.mjs — pure functions, daemon, launch, close

**Files:**
- Rewrite: `extension/tools/webview-cdp.mjs`

This is the core rewrite. The file is completely replaced. All v1 CDP code is removed. The new file has two modes:
- **Caller mode** (default): parses args, sends HTTP request to daemon, prints result
- **Daemon mode** (`--daemon` flag): launches VS Code, holds it alive, runs HTTP server for commands, captures console logs

**CRITICAL ARCHITECTURE NOTE:** CLI commands do NOT use `connectOverCDP`. All Playwright interaction happens inside the daemon process, which holds the Electron-mode `Page`. This is required because Playwright's keyboard shortcuts (`page.keyboard.press`) only trigger VS Code keybindings from the Electron-mode Page, not from a CDP-reconnected Page. The daemon exposes a local HTTP server; CLI commands are thin HTTP clients.

**Important references to read before implementing:**
- Archived `VSCodeLauncher.ts` at `C:\VS\ppdsw\ppds-extension-archived\e2e\helpers\VSCodeLauncher.ts` — launch pattern, console capture, extension log reading
- Archived `CommandPaletteHelper.ts` at `C:\VS\ppdsw\ppds-extension-archived\e2e\helpers\CommandPaletteHelper.ts` — command palette interaction pattern
- Archived `WebviewHelper.ts` at `C:\VS\ppdsw\ppds-extension-archived\e2e\helpers\WebviewHelper.ts` — double-nested iframe traversal
- Current E2E fixtures at `extension/e2e/fixtures.ts` — simplified Electron launch pattern

The implementer MUST read these reference files and adapt the proven patterns. Do not invent new approaches — use what's already working.

- [ ] **Step 1: Write the complete v2 implementation**

The file structure should be:

```
// ── Imports ──────────────────────────────────────────────────────
import { readFileSync, writeFileSync, unlinkSync, existsSync, appendFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fork, execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';

// ── Constants ────────────────────────────────────────────────────
// SESSION_FILE, LOG_FILE, PROFILE_DIR
// VALID_COMMANDS: ['launch', 'close', 'connect', 'command', 'wait',
//   'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs']
// VALID_MODIFIERS, VALID_MOUSE_EVENTS

// ── Pure functions (exported for testing) ────────────────────────
// validatePort (keep from v1)
// parseKeyCombo (keep from v1 — output used by daemon to map to Playwright's format)
// parseArgs (rewrite for v2 command shapes — see test file for expected return shapes)

// ── Session file I/O ─────────────────────────────────────────────
// readSession() → { daemonPort, daemonPid, userDataDir, logFile }
// writeSession(daemonPort, daemonPid, userDataDir, logFile)
// deleteSession()

// ── HTTP client (used by caller mode) ────────────────────────────
// async function sendToDaemon(session, action, params) → result
//   POST http://localhost:<session.daemonPort>/execute
//   Body: JSON { action, ...params }
//   Returns: parsed JSON response
//   Throws on connection refused: "VS Code may have closed. Run launch again"
//
// Example:
//   const result = await sendToDaemon(session, 'click', { selector: '#btn', right: false, page: false });
//   const result = await sendToDaemon(session, 'screenshot', { file: '/tmp/shot.png', page: true });
//   const result = await sendToDaemon(session, 'eval', { expression: '1+1', page: false });

// ── Daemon mode ──────────────────────────────────────────────────
// async function runDaemon(workspace)
//
// Key Playwright API code snippets:
//
// Launch:
//   import { _electron as electron } from '@playwright/test';
//   import { downloadAndUnzipVSCode } from '@vscode/test-electron';
//   const execPath = await downloadAndUnzipVSCode();
//   const electronApp = await electron.launch({
//     executablePath: execPath,
//     args: [
//       '--extensionDevelopmentPath=' + extensionPath,
//       '--user-data-dir=' + profileDir,
//       '--no-sandbox',
//       '--disable-gpu',
//       '--log=trace',
//     ],
//   });
//   const page = await electronApp.firstWindow();
//   await page.waitForSelector('[id="workbench.parts.editor"]', { timeout: 60000 });
//
// Dialog interception:
//   try {
//     await electronApp.evaluate(({ dialog }) => {
//       dialog.showMessageBox = async () => ({ response: 0, checkboxChecked: false });
//       dialog.showOpenDialog = async () => ({ canceled: true, filePaths: [] });
//       dialog.showSaveDialog = async () => ({ canceled: true, filePath: undefined });
//     });
//     console.error('Dialog hooks installed');
//   } catch { console.error('Dialog hooks not available'); }
//
// Console capture (write to JSONL file continuously):
//   page.on('console', msg => {
//     appendFileSync(logFile, JSON.stringify({
//       level: msg.type(), message: msg.text(),
//       source: 'main', timestamp: new Date().toISOString()
//     }) + '\n');
//   });
//   page.on('pageerror', err => {
//     appendFileSync(logFile, JSON.stringify({
//       level: 'error', message: 'Uncaught: ' + err.message,
//       source: 'main', timestamp: new Date().toISOString()
//     }) + '\n');
//   });
//
// Webview frame traversal (from archived WebviewHelper.ts):
//   async function resolveWebviewFrame(page, targetIndex, extFilter) {
//     const iframes = await page.$$('iframe[class*="webview"]');
//     // filter by extFilter if provided (check iframe src URL for extensionId)
//     const iframe = iframes[targetIndex ?? 0];
//     if (!iframe) throw new Error('No webview found');
//     const outerFrame = await iframe.contentFrame();
//     const innerIframe = await outerFrame.waitForSelector('iframe', { timeout: 5000 });
//     const innerFrame = await innerIframe.contentFrame();
//     return innerFrame;
//   }
//
// Command palette (from archived CommandPaletteHelper.ts):
//   // NOTE: Playwright Electron uses 'Control+Shift+P' not 'ctrl+shift+p'
//   await page.keyboard.press('Control+Shift+P');
//   await page.waitForSelector('.quick-input-widget', { state: 'visible', timeout: 5000 });
//   await page.keyboard.type(commandText, { delay: 50 });
//   // Wait for result or "no matching"
//   const result = await Promise.race([
//     page.waitForSelector('.quick-input-list .quick-input-list-entry', { timeout: 5000 }),
//     page.waitForSelector('.quick-input-message', { timeout: 5000 }),
//   ]);
//   if (await result.evaluate(el => el.classList.contains('quick-input-message'))) {
//     await page.keyboard.press('Escape');
//     throw new Error('Command not found: ' + commandText);
//   }
//   await page.keyboard.press('Enter');
//
// Key combo mapping (parseKeyCombo returns {key, modifiers} — map to Playwright format):
//   // parseKeyCombo('ctrl+shift+p') → { key: 'p', modifiers: { ctrl: true, shift: true } }
//   // Playwright wants: 'Control+Shift+p'
//   function toPlaywrightCombo(parsed) {
//     const parts = [];
//     if (parsed.modifiers.ctrl) parts.push('Control');
//     if (parsed.modifiers.shift) parts.push('Shift');
//     if (parsed.modifiers.alt) parts.push('Alt');
//     if (parsed.modifiers.meta) parts.push('Meta');
//     parts.push(parsed.key);
//     return parts.join('+');
//   }
//
// HTTP server (handles commands from CLI callers):
//   const server = createServer(async (req, res) => {
//     if (req.url === '/health') { res.end('ok'); return; }
//     if (req.url === '/shutdown') { /* clean shutdown */ }
//     if (req.url === '/execute') {
//       const body = await readBody(req);
//       const { action, ...params } = JSON.parse(body);
//       try {
//         const result = await handleAction(page, electronApp, action, params);
//         res.writeHead(200); res.end(JSON.stringify({ success: true, ...result }));
//       } catch (err) {
//         res.writeHead(200); res.end(JSON.stringify({ success: false, error: err.message }));
//       }
//     }
//   });
//   server.listen(0); // random port
//   const daemonPort = server.address().port;
//
// handleAction dispatches to the right Playwright API:
//   async function handleAction(page, electronApp, action, params) {
//     switch (action) {
//       case 'screenshot': { await target.screenshot({ path: params.file }); return { path: params.file }; }
//       case 'eval': { const v = await target.evaluate(params.expression); return { value: v }; }
//       case 'click': {
//         if (params.right) await target.click(params.selector, { button: 'right' });
//         else await target.click(params.selector);
//         return {};
//       }
//       case 'type': { await target.fill(params.selector, params.text); return {}; }
//       case 'select': { await target.selectOption(params.selector, params.value); return {}; }
//       case 'key': { await page.keyboard.press(toPlaywrightCombo(params.combo)); return {}; }
//       case 'mouse': { await page.mouse[params.event](params.x, params.y); return {}; }
//       case 'command': { await executeCommand(page, params.text); return {}; }
//       case 'connect': { /* list webview frames */ }
//       case 'wait': { /* poll for webview frame */ }
//       case 'logs': { /* read log file or channel */ }
//     }
//   }

// ── Caller command handlers ──────────────────────────────────────
// Each is thin: validate args, read session, POST to daemon, print result.
//
// async function cmdClick(parsed) {
//   const session = readSession();
//   const result = await sendToDaemon(session, 'click', {
//     selector: parsed.args[0], right: parsed.right, page: parsed.page,
//     target: parsed.target, ext: parsed.ext,
//   });
//   if (!result.success) throw new Error(result.error);
// }

// ── Main dispatch ────────────────────────────────────────────────
// if process.argv includes '--daemon': runDaemon(workspace)
// else: parseArgs → switch → handler → exit
```

Key implementation notes:

**No `connectOverCDP` anywhere.** All Playwright interaction happens inside the daemon. CLI commands are HTTP clients only. This is non-negotiable — CDP-reconnected Pages cannot trigger VS Code keybindings.

**Daemon fork:** Use `child_process.spawn('node', [__filename, '--daemon', workspace], { detached: true, stdio: ['ignore', 'ignore', 'ignore'] })` and `child.unref()`. Note: `fork()` requires IPC channel; `spawn()` with explicit `node` path is simpler for full detachment.

**Console capture JSONL format:** Each `page.on('console')` event appends one line to the log file.

**`close` flow:** POST to `/shutdown` endpoint. The daemon handles cleanup. If the daemon doesn't respond (already dead), the caller force-kills the daemon PID and cleans up session/log files.

**Key combo format:** `parseKeyCombo('ctrl+shift+p')` returns `{ key: 'p', modifiers: { ctrl: true, shift: true } }`. The daemon maps this to Playwright's format: `'Control+Shift+p'`. This mapping happens in the daemon, not the CLI.

**Mouse event mapping:** Playwright's `page.mouse` API uses `page.mouse.down()`, `page.mouse.move()`, `page.mouse.up()` — not `mousedown`, `mousemove`, `mouseup`. The daemon maps the CLI event names to Playwright method names.

**`--disable-extensions` is intentionally NOT passed** to VS Code launch args. The tool needs `--extensionDevelopmentPath` to load our extension, which requires the extension host to be active.

- [ ] **Step 2: Run unit tests**

Run: `cd extension && npx vitest run tools/webview-cdp.test.mjs`
Expected: All tests pass (parseArgs, validatePort, parseKeyCombo)

- [ ] **Step 3: Commit**

```bash
git add extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp v2 — Playwright Electron rewrite with daemon model"
```

---

## Chunk 3: Skill File Rewrite

### Task 4: Rewrite the skill file for v2

**Files:**
- Rewrite: `.agents/skills/webview-cdp/SKILL.md`

The v2 skill file must:
- Remove all `attach` references
- Remove "Known Limitations" about keyboard shortcuts not working (they work now)
- Change `launch [port]` to `launch [workspace]`
- Add `command`, `wait`, `logs` commands
- Update workflow to show `command "PPDS: Data Explorer"` instead of `click --page`
- Add screenshot temp directory guidance
- Add bash quoting guidance for eval
- Keep gap protocol

- [ ] **Step 1: Write the complete v2 skill file**

Key sections:
- **When to Use** — same as v1
- **Core Workflow** — launch → command → wait → screenshot → interact → logs → close
- **Commands** — full table with all 13 commands
- **Two Targets: --page vs default** — same concept, updated examples
- **Common Patterns** — open panel, verify click, test keyboard shortcut, test context menu, drag selection, check DOM state, check logs
- **Screenshots** — "Always save to temp directory. NEVER save to repo working tree." with example using `$TMPDIR` or `/tmp/`
- **Bash Quoting** — "Use single quotes for eval expressions containing template literals or $. Example: `eval 'document.querySelector(\"td\").textContent'`"
- **Gap Protocol** — same as v1
- **Important** — updated rules (no more attach warnings, no more "keyboard shortcuts don't work")

- [ ] **Step 2: Commit**

```bash
git add .agents/skills/webview-cdp/SKILL.md
git commit -m "feat(tools): webview-cdp v2 skill file — command palette, logs, daemon workflow"
```

---

## Chunk 4: Integration Test

### Task 5: Run integration test sequence

This verifies the complete v2 tool against a live VS Code instance. Covers all 23 acceptance criteria.

**Prerequisites:** Extension must be compiled: `cd extension && npm run compile`

Note: On Windows in Git Bash, `/tmp/` maps to a temp directory. All screenshots go there — never to the repo.

- [ ] **Step 1: Launch VS Code (AC-01)**

Run: `node extension/tools/webview-cdp.mjs launch`
Expected: "VS Code launched" message with daemon PID
Verify: VS Code window appears, session file exists at `extension/tools/.webview-cdp-session.json`
Check daemon logs: verify "Dialog hooks installed" or "Dialog hooks not available" appears (AC-23)

- [ ] **Step 2: Execute a command to open Data Explorer (AC-04)**

Run: `node extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"`
Expected: No error, exits cleanly

- [ ] **Step 3: Wait for webview (AC-05)**

Run: `node extension/tools/webview-cdp.mjs wait`
Expected: Returns when webview frame is found

- [ ] **Step 4: List webview frames (AC-03)**

Run: `node extension/tools/webview-cdp.mjs connect`
Expected: Lists webview frames with extension ID identification

- [ ] **Step 5: Take screenshots (AC-06, AC-07)**

Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-webview.png`
Expected: PNG shows Data Explorer webview content (AC-07)

Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-page.png --page`
Expected: PNG shows full VS Code window (AC-06)

- [ ] **Step 6: Test eval in both contexts (AC-08, AC-09)**

Run: `node extension/tools/webview-cdp.mjs eval "1 + 1"`
Expected: `2` (AC-08)

Run: `node extension/tools/webview-cdp.mjs eval --page "document.title"`
Expected: String containing "Visual Studio Code" (AC-09)

- [ ] **Step 7: Test type and click in webview (AC-10, AC-13)**

Run: `node extension/tools/webview-cdp.mjs type "#sql-editor" "SELECT 1"`
Run: `node extension/tools/webview-cdp.mjs eval "document.querySelector('#sql-editor').value"`
Expected: `"SELECT 1"` (AC-13)

Run: `node extension/tools/webview-cdp.mjs click "#execute-btn"`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-after-click.png`
Expected: Screenshot shows button was activated (AC-10)

- [ ] **Step 8: Test click --page on VS Code native UI (AC-11)**

First discover a clickable sidebar element:
Run: `node extension/tools/webview-cdp.mjs eval --page "document.querySelector('[aria-label=\"Data Explorer\"]')?.tagName || 'not found'"`

Then click it (or another known sidebar element):
Run: `node extension/tools/webview-cdp.mjs click --page "[aria-label='Data Explorer']"`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-page-click.png`
Expected: Screenshot shows the sidebar item was activated (AC-11)

- [ ] **Step 9: Test right-click context menu (AC-12)**

Run: `node extension/tools/webview-cdp.mjs click "#sql-editor" --right`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-rightclick.png`
Expected: Screenshot may show context menu (AC-12)

- [ ] **Step 10: Test keyboard shortcut in VS Code native UI (AC-14)**

Run: `node extension/tools/webview-cdp.mjs key "ctrl+shift+p"`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-palette.png`
Expected: Screenshot shows command palette open (AC-14)

Run: `node extension/tools/webview-cdp.mjs key "Escape"`

- [ ] **Step 11: Test keyboard shortcut inside webview (AC-15)**

Focus the webview first by clicking the SQL editor:
Run: `node extension/tools/webview-cdp.mjs click "#sql-editor"`
Run: `node extension/tools/webview-cdp.mjs key "ctrl+a"`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-webview-key.png`
Expected: Text in SQL editor is selected (AC-15)

- [ ] **Step 12: Test mouse events (AC-16)**

Get coordinates of an element:
Run: `node extension/tools/webview-cdp.mjs eval "JSON.stringify(document.querySelector('#sql-editor').getBoundingClientRect())"`

Dispatch a mouse sequence:
Run: `node extension/tools/webview-cdp.mjs mouse mousedown <x> <y>` (use coordinates from above)
Run: `node extension/tools/webview-cdp.mjs mouse mouseup <x> <y>`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-mouse.png`
Expected: No errors, screenshot shows state change (AC-16)

- [ ] **Step 13: Test select dropdown (AC-17)**

If the Data Explorer has a dropdown (e.g., environment picker or TDS toggle), test it:
Run: `node extension/tools/webview-cdp.mjs eval "document.querySelector('select')?.id || 'no select element'"`
If a select element exists, run: `node extension/tools/webview-cdp.mjs select "<selector>" "<value>"`
If no select element, document: "AC-17 deferred — no native select elements in current Data Explorer UI. Will be tested when plugin traces tool adds dropdown filters."

- [ ] **Step 14: Test logs (AC-18)**

Run: `node extension/tools/webview-cdp.mjs logs`
Expected: Console output from the session (AC-18)

- [ ] **Step 15: Test logs --channel (AC-19)**

Run: `node extension/tools/webview-cdp.mjs logs --channel "PPDS"`
Expected: PPDS extension log output (AC-19). If empty, verify the extension's log channel name is correct.

- [ ] **Step 16: Open second panel and test --target (AC-20)**

Run: `node extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"`
Run: `node extension/tools/webview-cdp.mjs wait`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-target0.png --target 0`
Run: `node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-target1.png --target 1`
Expected: Two different screenshots showing different panel instances (AC-20)

- [ ] **Step 17: Verify sequential reconnection (AC-21)**

Run 5 commands in quick succession:
```bash
node extension/tools/webview-cdp.mjs connect
node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-seq1.png
node extension/tools/webview-cdp.mjs eval "1+1"
node extension/tools/webview-cdp.mjs click "#execute-btn"
node extension/tools/webview-cdp.mjs screenshot /tmp/webview-cdp-test-seq2.png
```
Expected: All succeed independently (AC-21)

- [ ] **Step 18: Close and verify no orphans (AC-02, AC-22)**

Run: `node extension/tools/webview-cdp.mjs close`
Expected: "VS Code closed" message (AC-02)
Verify: VS Code window closes, session file deleted
Run: `tasklist | findstr -i "code electron ppds"` (Windows) to verify no orphaned processes (AC-22)

- [ ] **Step 19: Clean up test screenshots**

```bash
rm -f /tmp/webview-cdp-test-*.png
```

- [ ] **Step 20: Run full test suite**

Run: `cd extension && npm run test`
Expected: All tests pass

- [ ] **Step 21: Commit if any fixes were needed**

```bash
git add extension/tools/webview-cdp.mjs extension/tools/webview-cdp.test.mjs .agents/skills/webview-cdp/SKILL.md
git commit -m "fix(tools): address issues found during v2 integration testing"
```
