# TUI Verify Tool Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a CLI tool (`tui-verify.mjs`) that lets AI agents launch, interact with, and inspect the PPDS TUI in a PTY — mirroring webview-cdp's daemon pattern with text-based verification.

**Architecture:** Single-file Node.js tool with caller/daemon dual mode. Caller sends HTTP requests to a persistent daemon that holds a `@microsoft/tui-test` Terminal instance. Text-based primary workflow — `getBuffer()` for reading, `write()`/named methods for input, `serialize()` for debugging dumps.

**Tech Stack:** Node.js, `@microsoft/tui-test@0.0.1-rc.5` (PTY via node-pty, terminal emulation via @xterm/headless)

**Spec:** [`specs/tui-verify-tool.md`](../../specs/tui-verify-tool.md)

**Reference Implementation:** [`src/PPDS.Extension/tools/webview-cdp.mjs`](../../src/PPDS.Extension/tools/webview-cdp.mjs)

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` | Create | CLI tool — caller mode + daemon mode (single file) |
| `tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs` | Create | Unit tests for pure functions (arg parsing, key mapping) |
| `.claude/skills/tui-verify/SKILL.md` | Create | Skill file teaching agents how to use the tool |
| `.claude/commands/verify.md` | Modify | Replace TUI Mode section with tui-verify steps |
| `.claude/commands/qa.md` | Modify | Add TUI Mode blind verifier prompt |
| `.gitignore` | Modify | Add `.tui-verify-session.json` pattern |

---

## Chunk 1: Core Tool — Pure Functions and Daemon Infrastructure

### Prerequisites

**Test runner:** Use Node.js built-in `node:test` and `node:assert` — zero additional dependencies. The `tests/PPDS.Tui.E2eTests/package.json` only has `@microsoft/tui-test` and `@playwright/test` as devDependencies; adding vitest is unnecessary for a handful of unit tests.

**Windows path comparison:** The `main()` entry guard must use case-insensitive comparison: `resolve(process.argv[1]).toLowerCase() === __filename.toLowerCase()` since Windows paths may differ in casing.

### Task 1: Scaffolding and Pure Functions

**Files:**
- Create: `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`
- Create: `tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`

**Context:** The tool has two categories of code: pure functions (argument parsing, key mapping) that can be unit tested, and side-effectful daemon/caller code that requires integration testing. Start with the pure functions.

**Key reference:** `src/PPDS.Extension/tools/webview-cdp.mjs:21-109` — `parseKeyCombo()` and `parseArgs()` are the pure functions exported for testing. Mirror this pattern.

- [ ] **Step 1: Write failing tests for `parseArgs()`**

Create `tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`:

```js
import { describe, it } from 'node:test';
import { deepStrictEqual, throws } from 'node:assert';
import { parseArgs, parseKeyCombo } from './tui-verify.mjs';

describe('parseArgs', () => {
  it('parses launch with no flags', () => {
    deepStrictEqual(parseArgs(['launch']), { command: 'launch', build: false });
  });

  it('parses launch --build', () => {
    deepStrictEqual(parseArgs(['launch', '--build']), { command: 'launch', build: true });
  });

  it('parses close', () => {
    deepStrictEqual(parseArgs(['close']), { command: 'close' });
  });

  it('parses text with row number', () => {
    deepStrictEqual(parseArgs(['text', '5']), { command: 'text', row: 5 });
  });

  it('rejects text with out-of-range row', () => {
    throws(() => parseArgs(['text', '50']), /Row must be 0-29/);
  });

  it('rejects text with negative row', () => {
    throws(() => parseArgs(['text', '-1']), /Row must be 0-29/);
  });

  it('rejects text without row', () => {
    throws(() => parseArgs(['text']), /Usage: text <row>/);
  });

  it('parses key with combo', () => {
    deepStrictEqual(parseArgs(['key', 'ctrl+t']), { command: 'key', combo: 'ctrl+t' });
  });

  it('rejects key without combo', () => {
    throws(() => parseArgs(['key']), /Usage: key <combo>/);
  });

  it('parses type with text', () => {
    deepStrictEqual(parseArgs(['type', 'hello']), { command: 'type', text: 'hello' });
  });

  it('rejects type without text', () => {
    throws(() => parseArgs(['type']), /Usage: type <text>/);
  });

  it('parses wait with text and default timeout', () => {
    deepStrictEqual(parseArgs(['wait', 'PPDS']), { command: 'wait', text: 'PPDS', timeout: 10000 });
  });

  it('parses wait with text and custom timeout', () => {
    deepStrictEqual(parseArgs(['wait', 'PPDS', '5000']), { command: 'wait', text: 'PPDS', timeout: 5000 });
  });

  it('rejects wait with zero timeout', () => {
    throws(() => parseArgs(['wait', 'PPDS', '0']), /Invalid timeout/);
  });

  it('rejects wait without text', () => {
    throws(() => parseArgs(['wait']), /Usage: wait/);
  });

  it('parses screenshot with file', () => {
    deepStrictEqual(parseArgs(['screenshot', '/tmp/out.json']), { command: 'screenshot', file: '/tmp/out.json' });
  });

  it('rejects screenshot without file', () => {
    throws(() => parseArgs(['screenshot']), /Usage: screenshot <file>/);
  });

  it('parses rows', () => {
    deepStrictEqual(parseArgs(['rows']), { command: 'rows' });
  });

  it('rejects unknown command', () => {
    throws(() => parseArgs(['bogus']), /Unknown command: bogus/);
  });

  it('rejects empty args', () => {
    throws(() => parseArgs([]), /No command provided/);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `node --test tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`
Expected: FAIL — module not found or functions not exported

- [ ] **Step 3: Implement `parseArgs()` in tui-verify.mjs**

Create `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` with the pure functions:

```js
import { readFileSync, writeFileSync, unlinkSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const SESSION_FILE = resolve(__dirname, '.tui-verify-session.json');
const LOG_FILE = resolve(__dirname, '.tui-verify-daemon.log');
const VALID_COMMANDS = ['launch', 'close', 'screenshot', 'key', 'type', 'text', 'wait', 'rows'];
const VALID_MODIFIERS = ['ctrl', 'alt', 'shift'];
const ROWS = 30;
const COLS = 120;

// ── Pure functions (exported for testing) ──────────────────────────

export function parseArgs(argv) {
  if (!argv.length) throw new Error('No command provided');
  const command = argv[0];
  if (!VALID_COMMANDS.includes(command)) throw new Error(`Unknown command: ${command}`);

  if (command === 'launch') {
    return { command, build: argv.includes('--build') };
  }
  if (command === 'close' || command === 'rows') {
    return { command };
  }
  if (command === 'text') {
    if (!argv[1]) throw new Error('Usage: text <row>');
    const row = parseInt(argv[1], 10);
    if (isNaN(row) || row < 0 || row >= ROWS) throw new Error(`Row must be 0-${ROWS - 1} (terminal has ${ROWS} rows)`);
    return { command, row };
  }
  if (command === 'key') {
    if (!argv[1]) throw new Error('Usage: key <combo>');
    return { command, combo: argv[1] };
  }
  if (command === 'type') {
    if (!argv[1]) throw new Error('Usage: type <text>');
    return { command, text: argv[1] };
  }
  if (command === 'wait') {
    if (!argv[1]) throw new Error('Usage: wait <text> [timeout]');
    const timeout = argv[2] ? parseInt(argv[2], 10) : 10000;
    if (isNaN(timeout) || timeout <= 0) throw new Error('Invalid timeout');
    return { command, text: argv[1], timeout };
  }
  if (command === 'screenshot') {
    if (!argv[1]) throw new Error('Usage: screenshot <file>');
    return { command, file: argv[1] };
  }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`
Expected: All parseArgs tests PASS

- [ ] **Step 5: Commit**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs
git commit -m "feat(tui-verify): add parseArgs with tests"
```

### Task 2: Key Combo Parsing

**Files:**
- Modify: `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`
- Modify: `tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`

**Context:** The key mapper translates human-readable combos like `"ctrl+t"`, `"tab"`, `"F1"` into objects the daemon can use to dispatch to the correct tui-test Terminal method. The daemon side (Task 4) will consume these parsed results.

**Key reference:** `src/PPDS.Extension/tools/webview-cdp.mjs:21-40` — `parseKeyCombo()` in webview-cdp. Our version is different because we target terminal escape sequences instead of Playwright key names, but the parsing structure is similar.

- [ ] **Step 1: Write failing tests for `parseKeyCombo()`**

Add to `tui-verify.test.mjs`:

```js
describe('parseKeyCombo', () => {
  it('parses single named key: enter', () => {
    deepStrictEqual(parseKeyCombo('enter'), { key: 'enter', modifiers: {} });
  });

  it('parses single named key: tab', () => {
    deepStrictEqual(parseKeyCombo('tab'), { key: 'tab', modifiers: {} });
  });

  it('parses single named key: escape', () => {
    deepStrictEqual(parseKeyCombo('escape'), { key: 'escape', modifiers: {} });
  });

  it('parses arrow keys', () => {
    deepStrictEqual(parseKeyCombo('up'), { key: 'up', modifiers: {} });
    deepStrictEqual(parseKeyCombo('down'), { key: 'down', modifiers: {} });
  });

  it('parses function keys', () => {
    deepStrictEqual(parseKeyCombo('F1'), { key: 'F1', modifiers: {} });
    deepStrictEqual(parseKeyCombo('F12'), { key: 'F12', modifiers: {} });
  });

  it('parses ctrl+letter', () => {
    deepStrictEqual(parseKeyCombo('ctrl+c'), { key: 'c', modifiers: { ctrl: true } });
  });

  it('parses alt+letter', () => {
    deepStrictEqual(parseKeyCombo('alt+t'), { key: 't', modifiers: { alt: true } });
  });

  it('parses ctrl+shift+letter', () => {
    deepStrictEqual(parseKeyCombo('ctrl+shift+t'), { key: 't', modifiers: { ctrl: true, shift: true } });
  });

  it('rejects empty combo', () => {
    throws(() => parseKeyCombo(''), /Empty key combo/);
  });

  it('rejects unknown modifier', () => {
    throws(() => parseKeyCombo('super+t'), /unknown modifier 'super'/);
  });

  it('handles single character key', () => {
    deepStrictEqual(parseKeyCombo('a'), { key: 'a', modifiers: {} });
  });
});
```

- [ ] **Step 2: Run tests to verify parseKeyCombo tests fail**

Run: `node --test tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`
Expected: parseKeyCombo tests FAIL

- [ ] **Step 3: Implement `parseKeyCombo()`**

Add to `tui-verify.mjs` in the pure functions section:

```js
export function parseKeyCombo(combo) {
  if (!combo) throw new Error('Empty key combo');
  const parts = combo.split('+');
  const key = parts.pop();
  const modifiers = {};
  for (const mod of parts) {
    const m = mod.toLowerCase();
    if (!VALID_MODIFIERS.includes(m)) {
      throw new Error(`Invalid key combo: unknown modifier '${m}'`);
    }
    modifiers[m] = true;
  }
  return { key, modifiers };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs
git commit -m "feat(tui-verify): add parseKeyCombo with tests"
```

### Task 3: Session File I/O and HTTP Client

**Files:**
- Modify: `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`

**Context:** These are the shared utilities used by both caller commands and the daemon. Session file read/write/delete, and the HTTP client that callers use to talk to the daemon.

**Key reference:** `src/PPDS.Extension/tools/webview-cdp.mjs:112-139` — `readSession()`, `writeSession()`, `deleteSession()`, `sendToDaemon()`.

- [ ] **Step 1: Add session I/O and HTTP client functions**

Add to `tui-verify.mjs` after the pure functions:

```js
// ── Session file I/O ───────────────────────────────────────────────

function readSession() {
  if (!existsSync(SESSION_FILE))
    throw new Error('No session found. Run `tui-verify launch` first.');
  return JSON.parse(readFileSync(SESSION_FILE, 'utf-8'));
}

function writeSession(data) {
  writeFileSync(SESSION_FILE, JSON.stringify(data, null, 2));
}

function deleteSession() {
  if (existsSync(SESSION_FILE)) unlinkSync(SESSION_FILE);
}

// ── HTTP Client (caller mode) ──────────────────────────────────────

async function sendToDaemon(session, action, params = {}) {
  const body = JSON.stringify({ action, ...params });
  const res = await fetch(`http://localhost:${session.daemonPort}/execute`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body,
  });
  const result = await res.json();
  if (!result.success) throw new Error(result.error);
  return result;
}
```

- [ ] **Step 2: Commit**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs
git commit -m "feat(tui-verify): add session I/O and HTTP client"
```

### Task 4: Daemon Mode — PTY Spawn, Action Handler, HTTP Server

**Files:**
- Modify: `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`

**Context:** This is the core of the tool. The daemon spawns the TUI in a PTY via tui-test, exposes an HTTP server, and handles actions against the Terminal instance. The daemon runs as a detached child process forked by the `launch` command.

**Key reference:** `src/PPDS.Extension/tools/webview-cdp.mjs:143-505` — `runDaemon()` is the daemon entry point. Study its structure: launch app → setup → HTTP server → action handler → cleanup.

**Important tui-test API details (from `lib/terminal/term.d.ts`):**
- `spawn(options, trace, traceEmitter)` — options: `{ program: { file, args }, rows, columns, shell }`
- `terminal.getBuffer(): string[][]`
- `terminal.getViewableBuffer(): string[][]`
- `terminal.write(data: string): void`
- `terminal.submit(data?: string): void`
- `terminal.keyUp/Down/Left/Right/Escape/Delete/Backspace/CtrlC/CtrlD(count?): void`
- `terminal.serialize(): { view: string, shifts: Map<string, CellShift> }`
- `terminal.kill(): void`
- `terminal.onExit: IEvent<{ exitCode, signal }>`
- Shell enum: `{ Bash, Powershell, Cmd, ... }`

- [ ] **Step 1: Add the `sendKey()` helper that maps parsed combos to terminal methods**

This is the bridge between `parseKeyCombo()` output and the tui-test Terminal API.

```js
// ── Key dispatch ───────────────────────────────────────────────────

// Function key escape sequences (VT220 standard)
const FN_KEYS = {
  F1: '\x1bOP', F2: '\x1bOQ', F3: '\x1bOR', F4: '\x1bOS',
  F5: '\x1b[15~', F6: '\x1b[17~', F7: '\x1b[18~', F8: '\x1b[19~',
  F9: '\x1b[20~', F10: '\x1b[21~', F11: '\x1b[23~', F12: '\x1b[24~',
};

function sendKey(terminal, parsed) {
  const { key, modifiers } = parsed;
  const hasCtrl = modifiers.ctrl;
  const hasAlt = modifiers.alt;
  const hasShift = modifiers.shift;
  const lk = key.toLowerCase();

  // No modifiers — use named methods or raw sequences
  if (!hasCtrl && !hasAlt && !hasShift) {
    if (lk === 'enter') { terminal.submit(); return; }
    if (lk === 'tab') { terminal.write('\t'); return; }
    if (lk === 'escape') { terminal.keyEscape(); return; }
    if (lk === 'up') { terminal.keyUp(); return; }
    if (lk === 'down') { terminal.keyDown(); return; }
    if (lk === 'left') { terminal.keyLeft(); return; }
    if (lk === 'right') { terminal.keyRight(); return; }
    if (lk === 'backspace') { terminal.keyBackspace(); return; }
    if (lk === 'delete') { terminal.keyDelete(); return; }
    if (lk === 'space') { terminal.write(' '); return; }
    if (FN_KEYS[key]) { terminal.write(FN_KEYS[key]); return; }
    // Single character
    terminal.write(key);
    return;
  }

  // Ctrl+letter — send control character
  if (hasCtrl && !hasAlt && key.length === 1) {
    const code = key.toUpperCase().charCodeAt(0);
    if (code >= 65 && code <= 90) {
      // Ctrl+C and Ctrl+D have dedicated methods
      if (lk === 'c') { terminal.keyCtrlC(); return; }
      if (lk === 'd') { terminal.keyCtrlD(); return; }
      terminal.write(String.fromCharCode(code - 64));
      return;
    }
  }

  // Alt+letter — send ESC + letter
  if (hasAlt && !hasCtrl && key.length === 1) {
    terminal.write('\x1b' + key);
    return;
  }

  // Ctrl+Shift+letter — send ESC [ <code> ; 6 ~ (CSI modifier pattern)
  // This is platform-dependent; best effort for Terminal.Gui
  if (hasCtrl && hasShift && key.length === 1) {
    const code = key.toUpperCase().charCodeAt(0);
    terminal.write(String.fromCharCode(code - 64));
    return;
  }

  // Fallback — try writing the key directly
  terminal.write(key);
}
```

- [ ] **Step 2: Add the daemon entry point**

```js
// ── Daemon Mode ────────────────────────────────────────────────────

async function runDaemon() {
  // Dynamic import — callers don't need tui-test
  const tuiTest = await import('@microsoft/tui-test');
  const { EventEmitter } = await import('node:events');

  const repoRoot = resolve(__dirname, '..', '..', '..');
  const exe = resolve(repoRoot, 'src/PPDS.Cli/bin/Debug/net10.0/ppds.exe');

  if (!existsSync(exe)) {
    console.error(`Binary not found: ${exe}`);
    console.error('Run with --build or build manually first.');
    process.exit(1);
  }

  const traceEmitter = new EventEmitter();
  const terminal = await tuiTest.spawn(
    { program: { file: exe, args: ['tui'] }, rows: ROWS, columns: COLS },
    false,
    traceEmitter,
  );

  let exited = false;
  terminal.onExit(() => { exited = true; });

  // Wait for Terminal.Gui to render (non-empty buffer)
  const readyStart = Date.now();
  while (Date.now() - readyStart < 30000) {
    try {
      const buf = terminal.getViewableBuffer();
      const hasContent = buf.some(row => row.some(cell => cell.trim() !== ''));
      if (hasContent) break;
    } catch { /* buffer not ready yet */ }
    await new Promise(r => setTimeout(r, 250));
  }

  // Action handler
  async function handleAction(action, params) {
    if (exited) throw new Error('TUI process exited. Run `launch` again.');

    switch (action) {
      case 'text': {
        const { row } = params;
        if (row < 0 || row >= ROWS) throw new Error(`Row must be 0-${ROWS - 1} (terminal has ${ROWS} rows)`);
        const buffer = terminal.getViewableBuffer();
        const text = buffer[row] ? buffer[row].join('').trimEnd() : '';
        return { text };
      }
      case 'key': {
        const parsed = parseKeyCombo(params.combo);
        sendKey(terminal, parsed);
        await new Promise(r => setTimeout(r, 100)); // settle time
        return {};
      }
      case 'type': {
        for (const ch of params.text) {
          terminal.write(ch);
          await new Promise(r => setTimeout(r, 30)); // per-character delay
        }
        return {};
      }
      case 'wait': {
        const timeout = params.timeout || 10000;
        const start = Date.now();
        while (Date.now() - start < timeout) {
          if (exited) throw new Error('TUI process exited. Run `launch` again.');
          const buffer = terminal.getViewableBuffer();
          for (const row of buffer) {
            const line = row.join('');
            if (line.includes(params.text)) return {};
          }
          await new Promise(r => setTimeout(r, 250));
        }
        throw new Error(`Timeout: '${params.text}' not found within ${timeout}ms`);
      }
      case 'screenshot': {
        const { view, shifts } = terminal.serialize();
        // Convert Map to plain object for JSON serialization
        const shiftsObj = {};
        if (shifts && typeof shifts.forEach === 'function') {
          shifts.forEach((value, key) => { shiftsObj[key] = value; });
        }
        writeFileSync(resolve(params.file), JSON.stringify({ view, shifts: shiftsObj }, null, 2));
        return { path: resolve(params.file) };
      }
      case 'rows': {
        return { dimensions: `${COLS}x${ROWS}` };
      }
      default:
        throw new Error(`Unknown action: ${action}`);
    }
  }

  // HTTP server
  function readBody(req) {
    return new Promise((resolveBody) => {
      let data = '';
      req.on('data', chunk => { data += chunk; });
      req.on('end', () => resolveBody(data));
    });
  }

  const server = createServer(async (req, res) => {
    try {
      if (req.url === '/health') {
        res.writeHead(200); res.end('ok'); return;
      }
      if (req.url === '/shutdown') {
        res.writeHead(200); res.end('ok');
        await cleanup();
        return;
      }
      if (req.url === '/execute' && req.method === 'POST') {
        const body = await readBody(req);
        const { action, ...params } = JSON.parse(body);
        const result = await handleAction(action, params);
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ success: true, ...result }));
        return;
      }
      res.writeHead(404); res.end('Not found');
    } catch (err) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ success: false, error: err.message }));
    }
  });

  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const daemonPort = server.address().port;

  writeSession({ daemonPort, daemonPid: process.pid });
  console.error(`Daemon ready on port ${daemonPort}`);

  async function cleanup() {
    try { terminal.kill(); } catch {}
    deleteSession();
    if (existsSync(LOG_FILE)) unlinkSync(LOG_FILE);
    server.close();
    process.exit(0);
  }

  process.on('SIGTERM', cleanup);
  process.on('SIGINT', cleanup);
}
```

- [ ] **Step 3: Commit**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs
git commit -m "feat(tui-verify): add daemon mode — PTY spawn, action handler, HTTP server"
```

### Task 5: Caller Commands and Main Dispatch

**Files:**
- Modify: `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`

**Context:** The caller-side functions that handle each CLI command. `launch` forks the daemon, `close` shuts it down, and everything else sends HTTP requests via `sendToDaemon()`.

**Key reference:** `src/PPDS.Extension/tools/webview-cdp.mjs:509-725` — `cmdLaunch()`, `cmdClose()`, and the main dispatch switch.

- [ ] **Step 1: Add caller command handlers**

```js
// ── Caller command handlers ────────────────────────────────────────

async function cmdLaunch(parsed) {
  if (parsed.build) {
    const repoRoot = resolve(__dirname, '..', '..', '..');
    console.error('Building PPDS CLI...');
    try {
      execFileSync('dotnet', ['build', resolve(repoRoot, 'src/PPDS.Cli/PPDS.Cli.csproj'), '-f', 'net10.0', '-v', 'q'], {
        cwd: repoRoot,
        stdio: 'inherit',
      });
    } catch {
      throw new Error('Build failed — fix compilation errors above');
    }
    console.error('Build complete');
  }

  if (existsSync(SESSION_FILE)) {
    const session = JSON.parse(readFileSync(SESSION_FILE, 'utf-8'));
    try {
      await fetch(`http://localhost:${session.daemonPort}/health`);
      console.error('TUI already running. Run `close` first.');
      return;
    } catch {
      // Stale session — clean up
      try { process.kill(session.daemonPid); } catch {}
      deleteSession();
    }
  }

  // Redirect daemon stderr to a log file for diagnostics
  const { openSync } = await import('node:fs');
  const logFd = openSync(LOG_FILE, 'w');
  const child = spawn(process.execPath, [__filename, '--daemon'], {
    detached: true,
    stdio: ['ignore', 'ignore', logFd],
  });
  child.unref();

  const start = Date.now();
  while (Date.now() - start < 30000) {
    if (existsSync(SESSION_FILE)) {
      const session = readSession();
      console.error(`TUI launched (daemon PID ${session.daemonPid}, port ${session.daemonPort})`);
      return;
    }
    await new Promise(r => setTimeout(r, 500));
  }
  // On timeout, show daemon log for diagnostics
  if (existsSync(LOG_FILE)) {
    const log = readFileSync(LOG_FILE, 'utf-8').split('\n').slice(-20).join('\n');
    if (log.trim()) console.error('Daemon log:\n' + log);
  }
  throw new Error('Timeout: daemon did not start within 30 seconds');
}

async function cmdClose() {
  if (!existsSync(SESSION_FILE)) {
    console.error('No session found. Nothing to close.');
    return;
  }
  const session = readSession();
  try {
    await fetch(`http://localhost:${session.daemonPort}/shutdown`);
  } catch {
    try { process.kill(session.daemonPid); } catch {}
  }
  const start = Date.now();
  while (Date.now() - start < 10000) {
    if (!existsSync(SESSION_FILE)) {
      console.error('TUI closed');
      return;
    }
    await new Promise(r => setTimeout(r, 200));
  }
  // Force cleanup
  try { process.kill(session.daemonPid); } catch {}
  deleteSession();
  if (existsSync(LOG_FILE)) unlinkSync(LOG_FILE);
  console.error('TUI closed (forced)');
}

async function cmdText(parsed) {
  const session = readSession();
  const result = await sendToDaemon(session, 'text', { row: parsed.row });
  console.log(result.text);
}

async function cmdKey(parsed) {
  const session = readSession();
  await sendToDaemon(session, 'key', { combo: parsed.combo });
}

async function cmdType(parsed) {
  const session = readSession();
  await sendToDaemon(session, 'type', { text: parsed.text });
}

async function cmdWait(parsed) {
  const session = readSession();
  await sendToDaemon(session, 'wait', { text: parsed.text, timeout: parsed.timeout });
  console.log(`Found: ${parsed.text}`);
}

async function cmdScreenshot(parsed) {
  const session = readSession();
  const result = await sendToDaemon(session, 'screenshot', { file: parsed.file });
  console.log(result.path);
}

async function cmdRows() {
  const session = readSession();
  const result = await sendToDaemon(session, 'rows', {});
  console.log(result.dimensions);
}
```

- [ ] **Step 2: Add main dispatch**

```js
// ── Main dispatch ──────────────────────────────────────────────────

async function main() {
  // Daemon mode
  if (process.argv.includes('--daemon')) {
    await runDaemon();
    return;
  }

  // Caller mode
  const parsed = parseArgs(process.argv.slice(2));
  switch (parsed.command) {
    case 'launch': await cmdLaunch(parsed); break;
    case 'close': await cmdClose(); break;
    case 'text': await cmdText(parsed); break;
    case 'key': await cmdKey(parsed); break;
    case 'type': await cmdType(parsed); break;
    case 'wait': await cmdWait(parsed); break;
    case 'screenshot': await cmdScreenshot(parsed); break;
    case 'rows': await cmdRows(); break;
  }
}

if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === __filename.toLowerCase()) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
```

- [ ] **Step 3: Run unit tests to confirm nothing broke**

Run: `node --test tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs
git commit -m "feat(tui-verify): add caller commands and main dispatch"
```

### Task 6: Gitignore

**Files:**
- Modify: `.gitignore`

- [ ] **Step 1: Add session and log files to .gitignore**

Add `.tui-verify-session.json` and `.tui-verify-daemon.log` to the `.gitignore` file, near any existing webview-cdp session patterns.

- [ ] **Step 2: Commit**

```bash
git add .gitignore
git commit -m "chore: add tui-verify session file to gitignore"
```

---

## Chunk 2: Integration Testing (Eat Your Own Dog Food)

### Task 7: Smoke Test — Launch, Read, Close

**Files:** None (manual verification using the tool itself)

**Context:** The tool is now code-complete. Before writing integration docs, verify it actually works by using it. This is the "eat your own dog food" step from the issue. Must work on Windows.

- [ ] **Step 1: Build the TUI**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0 -v q`
Expected: Build succeeds

- [ ] **Step 2: Launch with --build**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build`
Expected: stderr shows "Build complete" then "TUI launched (daemon PID X, port Y)"

- [ ] **Step 3: Verify session file was created**

Check: `tests/PPDS.Tui.E2eTests/tools/.tui-verify-session.json` exists with `daemonPort` and `daemonPid`

- [ ] **Step 4: Wait for TUI to render**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000`
Expected: stdout shows "Found: PPDS"

- [ ] **Step 5: Read the title bar**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0`
Expected: stdout shows the top row of the TUI (should contain "PPDS" title text)

- [ ] **Step 6: Read the status bar**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29`
Expected: stdout shows the bottom row (status bar content)

- [ ] **Step 7: Check dimensions**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs rows`
Expected: stdout shows "120x30"

- [ ] **Step 8: Send Tab keystroke**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"`
Expected: No error. Then read a row to verify focus moved:
Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2`

- [ ] **Step 9: Dump terminal state**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-debug.json`
Expected: File written, contains `view` and `shifts` keys. Read the file to inspect.

- [ ] **Step 10: Test error handling**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 50`
Expected: stderr "Row must be 0-29 (terminal has 30 rows)", exit 1

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "NONEXISTENT" 2000`
Expected: stderr "Timeout: 'NONEXISTENT' not found within 2000ms", exit 1

- [ ] **Step 11: Close**

Run: `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close`
Expected: stderr "TUI closed". Session file deleted. No orphaned processes.

- [ ] **Step 12: Fix any issues found during smoke testing**

If any command doesn't work as expected, fix the implementation. Common issues:
- PTY binary path wrong → adjust path resolution in `runDaemon()`
- Buffer not populated → increase ready-wait polling or check Terminal.Gui startup
- Key combos not recognized by Terminal.Gui → adjust escape sequences in `sendKey()`
- Windows-specific PTY behavior → check node-pty Windows handling

- [ ] **Step 13: Commit any fixes**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs
git commit -m "fix(tui-verify): fixes from smoke testing"
```

---

## Chunk 3: Integration — Skill, Commands, Gitignore

### Task 8: Create @tui-verify Skill

**Files:**
- Create: `.claude/skills/tui-verify/SKILL.md`

**Context:** This skill teaches AI agents when and how to use tui-verify.mjs. It mirrors `.claude/skills/webview-cdp/SKILL.md` in structure. The spec's Integration section defines what must be included.

**Key reference:** `.claude/skills/webview-cdp/SKILL.md` — the webview-cdp skill file. Mirror its structure: frontmatter, when to use, command table, common patterns, tips, gap protocol.

- [ ] **Step 1: Create the skill file**

Create `.claude/skills/tui-verify/SKILL.md` with:

```markdown
---
name: tui-verify
description: Interactive TUI verification via PTY — launch, navigate, read text, send keystrokes. Use after implementing or modifying any TUI-affecting change (screens, widgets, keyboard handlers, status bar). For non-visual changes (service logic, data access), compile + test is sufficient.
allowed-tools: Bash(node *tui-verify*), Bash(cd * && node *tui-verify*)
---

# TUI Verify Tool

Interact with the PPDS TUI running in a PTY. Read terminal text to verify what's rendered, send keystrokes to navigate, wait for specific content to appear. Text-based verification — the AI reads terminal rows directly instead of interpreting screenshots.

## When to Use

- After implementing or modifying any TUI screen, widget, or keyboard handler
- When debugging TUI rendering — read specific rows to see what's there
- When testing keyboard navigation — send keystrokes and verify focus moved
- When verifying status bar content after an operation

## Setup

The tool is at `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs`. Uses `@microsoft/tui-test` (already a dev dependency). Requires the TUI to be built first.

## Core Workflow

```bash
# 1. Build and launch TUI in PTY (120x30 terminal)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# 2. Wait for it to render
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# 3. Read specific rows to verify content
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0   # title bar
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29  # status bar

# 4. Navigate with keystrokes
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# 5. Verify expected content appeared
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000

# 6. When done
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [--build]` | `launch --build` | Start TUI in PTY. `--build` compiles first |
| `close` | `close` | Kill PTY, clean up (idempotent) |
| `text <row>` | `text 0` | Read terminal row content (0-based, 0-29) |
| `key "<combo>"` | `key "ctrl+t"` | Send keystroke |
| `type "<text>"` | `type "SELECT"` | Type text character by character |
| `wait "<text>" [ms]` | `wait "PPDS" 5000` | Poll until text appears (default 10s) |
| `screenshot <file>` | `screenshot $TEMP/debug.json` | Dump serialize() as JSON (text + colors) |
| `rows` | `rows` | Show terminal dimensions (120x30) |

## Key Combos

| Combo | What it does in Terminal.Gui |
|-------|------------------------------|
| `tab` | Cycle focus to next widget |
| `shift+tab` | Cycle focus to previous widget |
| `enter` | Activate focused button/menu item |
| `escape` | Close dialog/cancel operation |
| `alt+<letter>` | Menu bar shortcut (e.g., `alt+f` for File) |
| `ctrl+q` | Quit (standard Terminal.Gui quit) |
| `up`/`down` | Navigate lists, menus, tree views |
| `F1`-`F12` | Function key shortcuts |
| `ctrl+t` | Common PPDS shortcut for TDS toggle |

## Common Patterns

### Verify a screen loaded
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0  # check title
```

### Navigate to a specific screen
```bash
# Use Alt+letter for menu shortcuts, then arrow keys and Enter
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "alt+s"  # Solutions menu
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "Solutions" 5000
```

### Check status bar after operation
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29  # bottom row
```

### Type into a text field
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"  # focus the input
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT TOP 10 * FROM account"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"
```

### Debug rendering issues
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-state.json
# Read the file to see full terminal state with colors
```

### Read multiple rows
```bash
# Scan visible area for content
for i in 0 1 2 3 4 5; do
  node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text $i
done
```

## Screenshots

The `screenshot` command dumps the terminal's `serialize()` output as JSON — text with color metadata. Not a PNG image. Use `$TEMP` for the output path:

```bash
# Good
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/debug.json

# BAD — creates file in repo
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot debug.json
```

## Verification Before Completion

**Interactive verification required** — any change affecting TUI rendering:
- Screen layout, widget placement, borders
- Keyboard handlers, focus order
- Status bar content, menu items
- Dialog boxes, error messages

**Compile + test sufficient** — no visual impact:
- Application Service logic (business rules)
- Dataverse queries, data transformation
- CLI command behavior (not TUI)

## Error Recovery

### `launch` fails
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close  # clean up stale session
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
```

### `wait` times out
1. Check if TUI is alive: `text 0` — if error, TUI crashed. `close` then `launch --build`
2. Check if you're on the right screen: read several rows with `text`
3. Increase timeout: `wait "text" 30000`

### TUI crashes mid-session
```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close   # clean up
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
```

## Gap Protocol

If you encounter a TUI interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need
3. **Propose an enhancement** — a new command or flag
4. **Ask the user** — implement now or defer
```

- [ ] **Step 2: Commit**

```bash
git add .claude/skills/tui-verify/SKILL.md
git commit -m "feat(tui-verify): add @tui-verify skill file"
```

### Task 9: Update /verify Command

**Files:**
- Modify: `.claude/commands/verify.md`

**Context:** Replace Section 4 ("TUI Mode") which currently says "not yet available" with interactive tui-verify steps. The spec's Integration section provides the exact replacement content.

**Key reference:** `.claude/commands/verify.md:56-68` — the current TUI Mode section to replace. Also look at Section 5 (Extension Mode) for the Phase A/B/C structure to mirror.

- [ ] **Step 1: Read the current verify.md to find exact lines to replace**

Read `.claude/commands/verify.md` and locate the TUI Mode section (around lines 56-68).

- [ ] **Step 2: Replace the TUI Mode section**

Replace the "TUI interactive verification via MCP is not yet available" block with:

```markdown
### 4. TUI Mode

**Phase A: Build and Launch**

```bash
# Build and launch TUI in PTY
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# Wait for TUI to render
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# Read the title bar to confirm it loaded
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0
```

**Phase B: Interactive Verification (MANDATORY for TUI rendering/interaction changes)**

If changed files touch `src/PPDS.Cli/Tui/`, Phase B is NOT optional.

```bash
# Navigate to the relevant screen
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2

# Verify status bar content
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 29

# Wait for expected screen title
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000

# Dump terminal state for debugging if needed
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-verify.json
```

**Phase C: Cleanup**

```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

See @tui-verify skill for full command reference and Terminal.Gui keyboard patterns.
```

- [ ] **Step 3: Commit**

```bash
git add .claude/commands/verify.md
git commit -m "feat(tui-verify): update /verify tui with interactive verification"
```

### Task 10: Update /qa Command

**Files:**
- Modify: `.claude/commands/qa.md`

**Context:** Add a "TUI Mode — Verifier Prompt" section to qa.md, and update the mode detection so `src/PPDS.Cli/Tui/` maps to "TUI mode (tui-verify)" instead of "snapshot tests — no blind verification yet". The spec provides the complete verifier prompt.

**Key reference:** `.claude/commands/qa.md` — look at the "Extension Mode — Verifier Prompt" section for the pattern, and the "Step 2: Detect Mode" section for the mapping to update.

- [ ] **Step 1: Read qa.md and find the mode detection and extension verifier sections**

Read `.claude/commands/qa.md`, locate:
- The Step 2 mode detection mapping (around line 40) where `src/PPDS.Cli/Tui/` is mapped
- The Extension Mode verifier prompt section (to mirror its structure)

- [ ] **Step 2: Update mode detection**

Change `src/PPDS.Cli/Tui/` mapping from:
```
- `src/PPDS.Cli/Tui/` → TUI mode (snapshot tests — no blind verification yet)
```
to:
```
- `src/PPDS.Cli/Tui/` → TUI mode (tui-verify)
```

- [ ] **Step 3: Add TUI Mode blind verifier prompt**

Add the following section after the Extension Mode verifier prompt:

````markdown
#### TUI Mode — Verifier Prompt

```
You are a QA tester. You have NEVER seen the source code for this TUI
application. You don't know how anything is implemented. You only know
what the product SHOULD do.

## Your Tools

You may ONLY use these tools:
- Bash: ONLY for running `node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` commands
- Read: ONLY for viewing screenshot JSON files (in $TEMP)

You MUST NOT use Read/Grep/Glob on source code files. You cannot look
at .cs, .ts, .json, or any file under src/. You are blind to the
implementation.

## Verification Protocol

For EACH check below:
1. Perform the action using tui-verify commands
2. Read the relevant terminal row(s): `text <row>`
3. Optionally dump full state: `screenshot $TEMP/qa-tui-{check-number}.json`
4. Compare actual text to expected text
5. Report PASS or FAIL with the actual text content as evidence

If a check FAILS:
- Show exactly what text you SAW vs what was EXPECTED
- Include the row number and full row content
- Do NOT speculate about why it failed

## Setup

```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close  # clean up prior
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000
```

## Checks

{paste generated checklist here}

## Teardown

```bash
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

## Report Format

| # | Check | Status | Evidence |
|---|-------|--------|----------|
| 1 | description | PASS/FAIL | row N: "actual text content" |

### Verdict: PASS (all green) / FAIL (N issues found)
```
````

- [ ] **Step 4: Commit**

```bash
git add .claude/commands/qa.md
git commit -m "feat(tui-verify): add TUI Mode blind verifier to /qa"
```

---

## Chunk 4: Final Verification

### Task 11: End-to-End Verification

- [ ] **Step 1: Run the full verification sequence from the spec**

Execute AC-01 through AC-17 from the spec's acceptance criteria:

```bash
# AC-01: launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build

# AC-10: wait finds existing text
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# AC-04: text reads row content
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0

# AC-14: rows returns dimensions
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs rows

# AC-06: tab moves focus
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2

# AC-07: enter activates
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "enter"

# AC-09: escape closes dialog
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "escape"

# AC-08: ctrl+q sends Ctrl+Q
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "ctrl+q"
# Then dismiss any quit dialog:
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "escape"

# AC-12: type writes characters
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs type "SELECT"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 5

# AC-13: screenshot dumps serialize()
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-verify-final.json

# AC-15: stale session detection
# Read the session file to get daemon PID, then kill the daemon
# without running `close` — this leaves a stale session file behind
cat tests/PPDS.Tui.E2eTests/tools/.tui-verify-session.json
# Note the daemonPid, then:
# Windows: taskkill /F /PID <pid>   Linux: kill -9 <pid>
# Verify session file still exists (stale), then launch should detect and clean up:
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
# Expected: launch detects stale session, cleans up, starts fresh

# AC-02: launch without --build (uses existing binary)
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000

# AC-05: out-of-range row error
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 50

# AC-11: wait timeout
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "NONEXISTENT" 2000

# AC-03, AC-16: close cleans up
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

- [ ] **Step 2: Fix any remaining issues**

If any acceptance criteria fail, fix and re-test.

- [ ] **Step 3: Run unit tests one final time**

Run: `node --test tests/PPDS.Tui.E2eTests/tools/tui-verify.test.mjs`
Expected: All PASS

- [ ] **Step 4: Final commit if any fixes were needed**

```bash
git add tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs
git commit -m "fix(tui-verify): final verification fixes"
```

### Task 12: Run Quality Gates

- [ ] **Step 1: Run /gates**

Invoke `/automated-quality-gates` to verify no build/lint/test regressions.

- [ ] **Step 2: Fix any gate failures**

If gates fail, fix and re-run.

- [ ] **Step 3: Commit fixes**

Stage only the files you changed, then commit:

```bash
git add <specific-files-that-were-fixed>
git commit -m "fix: quality gate fixes"
```
