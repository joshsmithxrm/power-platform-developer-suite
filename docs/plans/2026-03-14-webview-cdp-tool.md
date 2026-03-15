# Webview CDP Tool Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a CLI tool that connects to VS Code via CDP and gives AI agents visual access to extension webview panels — screenshots, clicks, typing, keyboard shortcuts, and DOM inspection.

**Architecture:** Single-file Node.js CLI (`src/PPDS.Extension/tools/webview-cdp.mjs`) communicating over raw WebSocket with VS Code's CDP endpoint. Each invocation connects, executes one command, and disconnects. A session file tracks the launched VS Code process between invocations.

**Tech Stack:** Node.js, `ws` (WebSocket), Chrome DevTools Protocol (CDP)

**Spec:** [`specs/vscode-webview-cdp-tool.md`](../../specs/vscode-webview-cdp-tool.md)

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `src/PPDS.Extension/tools/webview-cdp.mjs` | Create | CLI tool — argument parsing, CDP connection, all 10 commands |
| `src/PPDS.Extension/tools/webview-cdp.test.mjs` | Create | Unit tests for pure functions (arg parsing, key combo parsing, session I/O, target filtering) |
| `.agents/skills/webview-cdp/SKILL.md` | Create | Skill file teaching AI agents when/how to use the tool and the gap protocol |
| `src/PPDS.Extension/.gitignore` | Modify | Add `tools/.webview-cdp-session.json` entry |
| `src/PPDS.Extension/package.json` | Modify | Add `ws` dev dependency |
| `src/PPDS.Extension/vitest.config.ts` | Modify | Add `tools/**/*.test.mjs` to include pattern |

---

## Chunk 1: Foundation — CDP Connection, Session Management, and `connect` Command

### Task 1: Project setup

**Files:**
- Modify: `src/PPDS.Extension/package.json`
- Modify: `src/PPDS.Extension/.gitignore`

- [ ] **Step 1: Install `ws` dependency**

Run: `cd src/PPDS.Extension && npm install --save-dev ws`

- [ ] **Step 2: Add session file to gitignore**

In `src/PPDS.Extension/.gitignore`, add at the end:

```
tools/.webview-cdp-session.json
```

- [ ] **Step 3: Update vitest config to include tools tests**

In `src/PPDS.Extension/vitest.config.ts`, update the `include` array to also match tool test files:

```ts
include: ['src/**/*.test.ts', 'tools/**/*.test.mjs'],
```

- [ ] **Step 4: Create tools directory**

Run: `mkdir -p src/PPDS.Extension/tools`

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Extension/package.json extension/package-lock.json src/PPDS.Extension/.gitignore src/PPDS.Extension/vitest.config.ts
git commit -m "chore: add ws dependency, gitignore, and vitest config for webview-cdp tool"
```

---

### Task 2: Unit tests for pure functions

**Files:**
- Create: `src/PPDS.Extension/tools/webview-cdp.test.mjs`

These tests drive the pure utility functions we'll extract. Write them first — they'll fail until Task 3.

- [ ] **Step 1: Write tests for argument parsing**

```js
// src/PPDS.Extension/tools/webview-cdp.test.mjs
import { describe, it, expect } from 'vitest';
import { parseArgs, parseKeyCombo, validatePort, filterWebviewTargets } from './webview-cdp.mjs';

describe('parseArgs', () => {
  it('parses launch with defaults', () => {
    const result = parseArgs(['launch']);
    expect(result).toEqual({ command: 'launch', port: 9223, workspace: undefined, args: [] });
  });

  it('parses launch with port and workspace', () => {
    const result = parseArgs(['launch', '9224', '/my/workspace']);
    expect(result).toEqual({ command: 'launch', port: 9224, workspace: '/my/workspace', args: [] });
  });

  it('parses click with selector', () => {
    const result = parseArgs(['click', '#btn']);
    expect(result).toEqual({ command: 'click', port: 9223, args: ['#btn'], target: undefined, right: false });
  });

  it('parses click with --right flag', () => {
    const result = parseArgs(['click', '#btn', '--right']);
    expect(result).toEqual({ command: 'click', port: 9223, args: ['#btn'], target: undefined, right: true });
  });

  it('parses --target flag', () => {
    const result = parseArgs(['eval', '1+1', '--target', '2']);
    expect(result).toEqual({ command: 'eval', port: 9223, args: ['1+1'], target: 2 });
  });

  it('parses mouse with event and coordinates', () => {
    const result = parseArgs(['mouse', 'mousedown', '150', '200']);
    expect(result).toEqual({ command: 'mouse', port: 9223, args: ['mousedown', '150', '200'], target: undefined });
  });

  it('errors on empty args', () => {
    expect(() => parseArgs([])).toThrow('No command provided');
  });

  it('errors on unknown command', () => {
    expect(() => parseArgs(['foobar'])).toThrow('Unknown command: foobar');
  });
});

describe('validatePort', () => {
  it('accepts valid port', () => {
    expect(validatePort(9223)).toBe(9223);
  });

  it('rejects port below 1024', () => {
    expect(() => validatePort(80)).toThrow('Invalid port: must be 1024-65535');
  });

  it('rejects port above 65535', () => {
    expect(() => validatePort(70000)).toThrow('Invalid port: must be 1024-65535');
  });

  it('rejects non-integer', () => {
    expect(() => validatePort(NaN)).toThrow('Invalid port: must be 1024-65535');
  });
});

describe('parseKeyCombo', () => {
  it('parses simple key', () => {
    expect(parseKeyCombo('Escape')).toEqual({ key: 'Escape', modifiers: {} });
  });

  it('parses ctrl+key', () => {
    expect(parseKeyCombo('ctrl+c')).toEqual({ key: 'c', modifiers: { ctrl: true } });
  });

  it('parses ctrl+shift+key', () => {
    expect(parseKeyCombo('ctrl+shift+c')).toEqual({ key: 'c', modifiers: { ctrl: true, shift: true } });
  });

  it('parses ctrl+enter', () => {
    expect(parseKeyCombo('ctrl+enter')).toEqual({ key: 'Enter', modifiers: { ctrl: true } });
  });

  it('rejects unknown modifier', () => {
    expect(() => parseKeyCombo('foo+a')).toThrow("Invalid key combo: unknown modifier 'foo'");
  });

  it('rejects empty string', () => {
    expect(() => parseKeyCombo('')).toThrow('Empty key combo');
  });
});

describe('filterWebviewTargets', () => {
  const targets = [
    { id: '1', type: 'page', url: 'file:///vscode/workbench.html', title: 'VS Code' },
    { id: '2', type: 'iframe', url: 'vscode-webview://abc123/index.html?extensionId=test', title: 'webview' },
    { id: '3', type: 'worker', url: 'worker.js', title: 'TextMateWorker' },
    { id: '4', type: 'iframe', url: 'vscode-webview://def456/index.html?extensionId=other', title: 'webview2' },
  ];

  it('filters to iframe targets with vscode-webview:// URLs', () => {
    const result = filterWebviewTargets(targets);
    expect(result).toHaveLength(2);
    expect(result[0].id).toBe('2');
    expect(result[1].id).toBe('4');
  });

  it('returns empty array when no webview targets', () => {
    const result = filterWebviewTargets([targets[0], targets[2]]);
    expect(result).toEqual([]);
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/PPDS.Extension && npx vitest run tools/webview-cdp.test.mjs`
Expected: FAIL — module `./webview-cdp.mjs` does not exist

- [ ] **Step 3: Commit failing tests**

```bash
git add src/PPDS.Extension/tools/webview-cdp.test.mjs
git commit -m "test: add failing unit tests for webview-cdp pure functions"
```

---

### Task 3: Core implementation — argument parsing, session I/O, CDP connection, `connect` command

**Files:**
- Create: `src/PPDS.Extension/tools/webview-cdp.mjs`

This is the main implementation. Build the foundation that all commands share, plus the `connect` command as the first working command.

- [ ] **Step 1: Implement the CLI tool with pure functions and connect command**

```js
// src/PPDS.Extension/tools/webview-cdp.mjs
import { readFileSync, writeFileSync, unlinkSync, existsSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { spawn, execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const SESSION_FILE = resolve(__dirname, '.webview-cdp-session.json');
const VALID_COMMANDS = ['launch', 'close', 'connect', 'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse'];
const VALID_MODIFIERS = ['ctrl', 'shift', 'alt', 'meta'];
const VALID_MOUSE_EVENTS = ['mousedown', 'mousemove', 'mouseup'];

// ── Pure functions (exported for testing) ──────────────────────────

export function validatePort(port) {
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    throw new Error('Invalid port: must be 1024-65535');
  }
  return port;
}

export function parseKeyCombo(combo) {
  if (!combo) throw new Error('Empty key combo');
  const parts = combo.split('+');
  const key = parts.pop();
  const modifiers = {};
  for (const mod of parts) {
    const m = mod.toLowerCase();
    if (!VALID_MODIFIERS.includes(m)) {
      throw new Error(`Invalid key combo: unknown modifier '${mod}'`);
    }
    modifiers[m] = true;
  }
  // Normalize common key names
  const keyMap = { enter: 'Enter', escape: 'Escape', tab: 'Tab', backspace: 'Backspace',
    delete: 'Delete', arrowup: 'ArrowUp', arrowdown: 'ArrowDown',
    arrowleft: 'ArrowLeft', arrowright: 'ArrowRight', space: ' ' };
  const normalizedKey = keyMap[key.toLowerCase()] || key;
  return { key: normalizedKey, modifiers };
}

export function filterWebviewTargets(targets) {
  return targets.filter(t => t.type === 'iframe' && t.url && t.url.startsWith('vscode-webview://'));
}

export function parseArgs(argv) {
  if (!argv.length) throw new Error('No command provided');
  const command = argv[0];
  if (!VALID_COMMANDS.includes(command)) throw new Error(`Unknown command: ${command}`);

  const rest = argv.slice(1);
  let target, right = false, port = 9223;
  const args = [];

  // Extract flags
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--target' && i + 1 < rest.length) {
      target = parseInt(rest[++i], 10);
    } else if (rest[i] === '--right') {
      right = true;
    } else if (rest[i] === '--port' && i + 1 < rest.length) {
      port = validatePort(parseInt(rest[++i], 10));
    } else {
      args.push(rest[i]);
    }
  }

  if (command === 'launch') {
    const launchPort = args[0] ? validatePort(parseInt(args[0], 10)) : 9223;
    return { command, port: launchPort, workspace: args[1], args: [] };
  }

  const result = { command, port, args };
  if (target !== undefined) result.target = target;
  if (command === 'click') result.right = right;
  return result;
}

// ── Session file I/O ───────────────────────────────────────────────

function readSession() {
  if (!existsSync(SESSION_FILE)) {
    throw new Error('No session found. Run `webview-cdp launch` first.');
  }
  return JSON.parse(readFileSync(SESSION_FILE, 'utf-8'));
}

function writeSession(pid, port) {
  writeFileSync(SESSION_FILE, JSON.stringify({ pid, port }, null, 2));
}

function deleteSession() {
  if (existsSync(SESSION_FILE)) unlinkSync(SESSION_FILE);
}

// ── CDP helpers ────────────────────────────────────────────────────

async function fetchTargets(port) {
  const res = await fetch(`http://localhost:${port}/json/list`);
  if (!res.ok) throw new Error(`CDP endpoint returned ${res.status}`);
  return res.json();
}

async function connectWebSocket(url, timeoutMs = 5000) {
  const { default: WebSocket } = await import('ws');
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('WebSocket connection timeout')), timeoutMs);
    const ws = new WebSocket(url);
    ws.on('open', () => { clearTimeout(timer); resolve(ws); });
    ws.on('error', (err) => { clearTimeout(timer); reject(err); });
  });
}

function sendCDP(ws, method, params = {}, timeoutMs = 10000) {
  return new Promise((resolve, reject) => {
    const id = Math.floor(Math.random() * 1e9);
    const timer = setTimeout(() => {
      ws.off('message', handler);
      reject(new Error(`CDP timeout (${method}): no response within ${timeoutMs}ms`));
    }, timeoutMs);
    const handler = (data) => {
      const msg = JSON.parse(data.toString());
      if (msg.id === id) {
        clearTimeout(timer);
        ws.off('message', handler);
        if (msg.error) reject(new Error(`CDP error (${method}): ${msg.error.message}`));
        else resolve(msg.result);
      }
    };
    ws.on('message', handler);
    ws.send(JSON.stringify({ id, method, params }));
  });
}

async function discoverContext(ws) {
  const contexts = [];
  const contextHandler = (data) => {
    const msg = JSON.parse(data.toString());
    if (msg.method === 'Runtime.executionContextCreated') {
      contexts.push(msg.params.context);
    }
  };
  ws.on('message', contextHandler);
  await sendCDP(ws, 'Runtime.enable');
  // Brief wait for context events to arrive
  await new Promise(r => setTimeout(r, 500));
  ws.off('message', contextHandler);

  // Try to find the active-frame context by probing
  for (const ctx of contexts) {
    try {
      const result = await sendCDP(ws, 'Runtime.evaluate', {
        expression: 'document.body !== null',
        contextId: ctx.id,
        returnByValue: true,
      });
      if (result.result && result.result.value === true) {
        // Verify it's the inner frame (has real content, not just the wrapper)
        const probe = await sendCDP(ws, 'Runtime.evaluate', {
          expression: 'document.body.children.length',
          contextId: ctx.id,
          returnByValue: true,
        });
        if (probe.result && probe.result.value > 0) {
          return ctx.id;
        }
      }
    } catch {
      // Context may not be valid, skip
    }
  }
  throw new Error('Could not find webview execution context. Webview may still be loading.');
}

// ── Shared command infrastructure ──────────────────────────────────

async function withWebview(port, targetIndex, fn) {
  let allTargets;
  try {
    allTargets = await fetchTargets(port);
  } catch {
    throw new Error('Connection refused. VS Code may have closed.');
  }

  const webviews = filterWebviewTargets(allTargets);
  if (webviews.length === 0) {
    throw new Error('No webview found. Is a webview panel open?');
  }

  const idx = targetIndex ?? 0;
  if (idx < 0 || idx >= webviews.length) {
    throw new Error(`Target index ${idx} out of range (found ${webviews.length} webviews)`);
  }

  const target = webviews[idx];
  const ws = await connectWebSocket(target.webSocketDebuggerUrl);
  try {
    const contextId = await discoverContext(ws);
    return await fn(ws, contextId, target);
  } finally {
    ws.close();
  }
}

// ── Command: connect ───────────────────────────────────────────────

async function cmdConnect(parsed) {
  // Use port from: positional arg > session file > default
  let port = parsed.port;
  if (parsed.args[0]) {
    port = validatePort(parseInt(parsed.args[0], 10));
  } else if (existsSync(SESSION_FILE)) {
    port = readSession().port;
  }
  let allTargets;
  try {
    allTargets = await fetchTargets(port);
  } catch {
    throw new Error('Connection refused. VS Code may have closed.');
  }

  const webviews = filterWebviewTargets(allTargets);
  if (webviews.length === 0) {
    throw new Error('No webview found. Is a webview panel open?');
  }

  console.log(`Found ${webviews.length} webview target(s):`);
  webviews.forEach((t, i) => {
    // Extract extension ID from URL if available
    const extMatch = t.url.match(/extensionId=([^&]+)/);
    const ext = extMatch ? extMatch[1] : 'unknown';
    console.log(`  ${i}: ${ext} — ${t.title || t.url}`);
  });
}

// ── Command dispatch ───────────────────────────────────────────────

async function main() {
  const parsed = parseArgs(process.argv.slice(2));

  switch (parsed.command) {
    case 'connect':
      await cmdConnect(parsed);
      break;

    // Placeholder for other commands — implemented in subsequent tasks
    default:
      throw new Error(`Command '${parsed.command}' not yet implemented`);
  }
}

// Only run main when executed directly (not imported for tests)
if (process.argv[1] && resolve(process.argv[1]) === __filename) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
```

- [ ] **Step 2: Run unit tests to verify pure functions pass**

Run: `cd src/PPDS.Extension && npx vitest run tools/webview-cdp.test.mjs`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp core — arg parsing, CDP connection, connect command"
```

---

## Chunk 2: Lifecycle Commands — `launch` and `close`

### Task 4: Implement `launch` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add launch command implementation**

Add this function above the command dispatch switch in `webview-cdp.mjs`:

```js
async function cmdLaunch(parsed) {
  const port = parsed.port;

  // Check if port is in use
  try {
    await fetch(`http://localhost:${port}/json/version`);
    throw new Error(`Port ${port} already in use.`);
  } catch (err) {
    if (err.message.includes('already in use')) throw err;
    // Connection refused = port is free, continue
  }

  // Resolve extension path (relative to tool location, not cwd)
  const workspace = parsed.workspace || process.cwd();
  const extensionPath = resolve(__dirname, '..');  // src/PPDS.Extension/ directory

  // Spawn VS Code
  const codeProcess = spawn('code', [
    '--extensionDevelopmentPath', extensionPath,
    '--remote-debugging-port', String(port),
    '--wait',  // keeps the wrapper process alive, so its PID stays valid
    workspace,
  ], {
    shell: true, // Required: 'code' is a .cmd batch file on Windows
    detached: true,
    stdio: 'ignore',
  });
  codeProcess.unref();

  // Poll for CDP readiness, then resolve the actual Electron PID from CDP
  const startTime = Date.now();
  const timeout = 15000;
  while (Date.now() - startTime < timeout) {
    try {
      // Get the browser version info which includes the PID of the Electron process
      const verRes = await fetch(`http://localhost:${port}/json/version`);
      const verInfo = await verRes.json();
      // The /json/version endpoint includes webSocketDebuggerUrl — extract PID
      // from the browser process that owns the CDP endpoint
      const pidRes = await fetch(`http://localhost:${port}/json/list`);
      await pidRes.json(); // just verify it works

      // Store the wrapper PID — close will kill the process tree
      writeSession(codeProcess.pid, port);
      console.log(`VS Code launched on port ${port} (PID ${codeProcess.pid})`);
      return;
    } catch {
      await new Promise(r => setTimeout(r, 500));
    }
  }
  throw new Error(`Timeout: VS Code did not start within ${timeout / 1000} seconds.`);
}
```

- [ ] **Step 2: Wire launch into the command dispatch switch**

Add to the switch statement:

```js
    case 'launch':
      await cmdLaunch(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp launch command — spawns VS Code with extension"
```

---

### Task 5: Implement `close` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add close command implementation**

```js
async function cmdClose() {
  const session = readSession();

  // Verify PID belongs to a VS Code / Electron process before killing
  let isVSCode = false;
  try {
    if (process.platform === 'win32') {
      const output = execSync(`tasklist /FI "PID eq ${session.pid}" /FO CSV /NH`, { encoding: 'utf-8' });
      isVSCode = /code|electron/i.test(output);
    } else {
      const output = execSync(`ps -p ${session.pid} -o comm=`, { encoding: 'utf-8' });
      isVSCode = /code|electron/i.test(output);
    }
  } catch {
    // Process may already be dead
  }

  if (isVSCode) {
    try {
      // Kill the process tree — the stored PID is the wrapper, which spawned Electron
      if (process.platform === 'win32') {
        execSync(`taskkill /PID ${session.pid} /T /F`, { stdio: 'ignore' });
      } else {
        process.kill(-session.pid, 'SIGTERM'); // negative PID kills the process group
      }
    } catch {
      // Process already dead, that's fine
    }
    console.log(`VS Code closed (PID ${session.pid})`);
  } else {
    console.log(`Warning: PID ${session.pid} is not a VS Code process. Session file cleaned up.`);
  }

  deleteSession();
}
```

- [ ] **Step 2: Wire close into the command dispatch switch**

```js
    case 'close':
      await cmdClose();
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp close command — kills managed VS Code instance"
```

---

## Chunk 3: Core Interaction Commands — `screenshot`, `eval`, `click`

### Task 6: Implement `screenshot` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add screenshot command**

```js
async function cmdScreenshot(parsed) {
  const filePath = parsed.args[0];
  if (!filePath) throw new Error('Usage: screenshot <file>');

  // Verify parent directory exists
  const dir = dirname(resolve(filePath));
  if (!existsSync(dir)) throw new Error(`Cannot write to: ${filePath}`);

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    // Try Page.captureScreenshot first (works on some CDP targets)
    try {
      await sendCDP(ws, 'Page.enable');
      const result = await sendCDP(ws, 'Page.captureScreenshot', { format: 'png' });
      const buffer = Buffer.from(result.data, 'base64');
      writeFileSync(resolve(filePath), buffer);
      console.log(resolve(filePath));
      return;
    } catch {
      // Page domain may not be available on iframe targets — fall back
    }

    // Fallback: capture via the parent page target
    // Find the parent page target and screenshot from there
    const allTargets = await fetchTargets(session.port);
    const pageTargets = allTargets.filter(t => t.type === 'page' && t.title?.includes('Visual Studio Code'));
    if (pageTargets.length === 0) throw new Error('Cannot capture screenshot: no page target found');

    // Connect to the parent page target for screenshot
    const pageWs = await connectWebSocket(pageTargets[0].webSocketDebuggerUrl);
    try {
      await sendCDP(pageWs, 'Page.enable');
      const result = await sendCDP(pageWs, 'Page.captureScreenshot', { format: 'png' });
      const buffer = Buffer.from(result.data, 'base64');
      writeFileSync(resolve(filePath), buffer);
      console.log(resolve(filePath));
    } finally {
      pageWs.close();
    }
  });
}
```

> **Note:** `Page.captureScreenshot` may not work on iframe CDP targets. The implementation tries the iframe target first. If it fails, it falls back to capturing from the parent page target (which shows the full VS Code window including the webview panel). During the integration test (Task 14), verify which approach produces usable output and remove the other path.

- [ ] **Step 2: Wire into dispatch**

```js
    case 'screenshot':
      await cmdScreenshot(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp screenshot command — captures webview as PNG"
```

---

### Task 7: Implement `eval` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add eval command**

```js
async function cmdEval(parsed) {
  const expression = parsed.args[0];
  if (!expression) throw new Error('Empty expression');

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    const result = await sendCDP(ws, 'Runtime.evaluate', {
      expression,
      contextId,
      returnByValue: true,
      awaitPromise: true,
    });

    if (result.exceptionDetails) {
      const msg = result.exceptionDetails.exception?.description
        || result.exceptionDetails.text
        || 'Unknown eval error';
      throw new Error(`Eval error: ${msg}`);
    }

    const value = result.result.value;
    console.log(JSON.stringify(value));
  });
}
```

- [ ] **Step 2: Wire into dispatch**

```js
    case 'eval':
      await cmdEval(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp eval command — runs JS in webview context"
```

---

### Task 8: Implement `click` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add click command**

```js
async function cmdClick(parsed) {
  const selector = parsed.args[0];
  if (!selector) throw new Error('Empty selector');

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    // Check element exists
    const check = await sendCDP(ws, 'Runtime.evaluate', {
      expression: `document.querySelector(${JSON.stringify(selector)}) !== null`,
      contextId,
      returnByValue: true,
    });
    if (!check.result.value) {
      throw new Error(`Element not found: ${selector}`);
    }

    if (parsed.right) {
      // Right-click: dispatch contextmenu event
      await sendCDP(ws, 'Runtime.evaluate', {
        expression: `(() => {
          const el = document.querySelector(${JSON.stringify(selector)});
          const rect = el.getBoundingClientRect();
          const x = rect.left + rect.width / 2;
          const y = rect.top + rect.height / 2;
          el.dispatchEvent(new MouseEvent('contextmenu', {
            bubbles: true, cancelable: true, clientX: x, clientY: y, button: 2
          }));
        })()`,
        contextId,
        returnByValue: true,
        awaitPromise: true,
      });
    } else {
      // Left-click
      await sendCDP(ws, 'Runtime.evaluate', {
        expression: `document.querySelector(${JSON.stringify(selector)}).click()`,
        contextId,
        returnByValue: true,
      });
    }
  });
}
```

- [ ] **Step 2: Wire into dispatch**

```js
    case 'click':
      await cmdClick(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp click command — left and right click via CSS selector"
```

---

## Chunk 4: Input Commands — `type`, `select`, `key`, `mouse`

### Task 9: Implement `type` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add type command**

```js
async function cmdType(parsed) {
  const selector = parsed.args[0];
  const text = parsed.args[1];
  if (!selector || text === undefined) throw new Error('Usage: type <selector> <text>');

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    const check = await sendCDP(ws, 'Runtime.evaluate', {
      expression: `document.querySelector(${JSON.stringify(selector)}) !== null`,
      contextId,
      returnByValue: true,
    });
    if (!check.result.value) throw new Error(`Element not found: ${selector}`);

    await sendCDP(ws, 'Runtime.evaluate', {
      expression: `(() => {
        const el = document.querySelector(${JSON.stringify(selector)});
        el.focus();
        el.value = ${JSON.stringify(text)};
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      })()`,
      contextId,
      returnByValue: true,
      awaitPromise: true,
    });
  });
}
```

- [ ] **Step 2: Wire into dispatch**

```js
    case 'type':
      await cmdType(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp type command — types text into inputs"
```

---

### Task 10: Implement `select` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add select command**

```js
async function cmdSelect(parsed) {
  const selector = parsed.args[0];
  const value = parsed.args[1];
  if (!selector || value === undefined) throw new Error('Usage: select <selector> <value>');

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    const result = await sendCDP(ws, 'Runtime.evaluate', {
      expression: `(() => {
        const el = document.querySelector(${JSON.stringify(selector)});
        if (!el) return { error: 'not_found' };
        const options = Array.from(el.options || el.querySelectorAll('option'));
        // Try matching by value first, then by text content
        let option = options.find(o => o.value === ${JSON.stringify(value)});
        if (!option) option = options.find(o => o.textContent.trim() === ${JSON.stringify(value)});
        if (!option) return { error: 'option_not_found' };
        el.value = option.value;
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { success: true, selected: option.value };
      })()`,
      contextId,
      returnByValue: true,
      awaitPromise: true,
    });

    const val = result.result.value;
    if (val?.error === 'not_found') throw new Error(`Element not found: ${selector}`);
    if (val?.error === 'option_not_found') throw new Error(`Option not found: ${value}`);
  });
}
```

- [ ] **Step 2: Wire into dispatch**

```js
    case 'select':
      await cmdSelect(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp select command — selects dropdown options"
```

---

### Task 11: Implement `key` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add key command**

```js
async function cmdKey(parsed) {
  const combo = parsed.args[0];
  if (!combo) throw new Error('Empty key combo');
  const { key, modifiers } = parseKeyCombo(combo);

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    const dispatchParams = {
      type: 'keyDown',
      key,
      modifiers: (modifiers.alt ? 1 : 0) | (modifiers.ctrl ? 2 : 0)
        | (modifiers.meta ? 4 : 0) | (modifiers.shift ? 8 : 0),
    };

    // For printable characters, include text
    if (key.length === 1) {
      dispatchParams.text = key;
    }

    await sendCDP(ws, 'Input.dispatchKeyEvent', dispatchParams);
    // keyUp should not include 'text' — text input happens on keyDown only
    const { text, ...keyUpParams } = dispatchParams;
    await sendCDP(ws, 'Input.dispatchKeyEvent', { ...keyUpParams, type: 'keyUp' });
  });
}
```

- [ ] **Step 2: Wire into dispatch**

```js
    case 'key':
      await cmdKey(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp key command — dispatches keyboard shortcuts"
```

---

### Task 12: Implement `mouse` command

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs`

- [ ] **Step 1: Add mouse command**

```js
async function cmdMouse(parsed) {
  const event = parsed.args[0];
  const x = parseFloat(parsed.args[1]);
  const y = parseFloat(parsed.args[2]);

  if (!VALID_MOUSE_EVENTS.includes(event)) {
    throw new Error('Invalid mouse event. Must be: mousedown, mousemove, mouseup');
  }
  if (isNaN(x) || isNaN(y) || x < 0 || y < 0) {
    throw new Error('Invalid coordinates');
  }

  // Map our event names to CDP event types
  const cdpTypeMap = {
    mousedown: 'mousePressed',
    mousemove: 'mouseMoved',
    mouseup: 'mouseReleased',
  };

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    await sendCDP(ws, 'Input.dispatchMouseEvent', {
      type: cdpTypeMap[event],
      x, y,
      button: event === 'mousemove' ? 'none' : 'left',
      clickCount: event === 'mousedown' ? 1 : 0,
    });
  });
}
```

- [ ] **Step 2: Wire into dispatch**

```js
    case 'mouse':
      await cmdMouse(parsed);
      break;
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "feat(tools): webview-cdp mouse command — raw mouse events for drag interactions"
```

---

## Chunk 5: Skill File, Gitignore, and Integration Test

### Task 13: Create the skill file

**Files:**
- Create: `.agents/skills/webview-cdp/SKILL.md`

- [ ] **Step 1: Write the skill file**

```markdown
---
name: webview-cdp
description: Interact with VS Code extension webview panels via Chrome DevTools Protocol. Use when implementing or verifying webview UI — take screenshots to see your work, click elements, type text, send keyboard shortcuts, inspect DOM state. Triggers include any task involving VS Code extension webview panels, Data Explorer, plugin traces UI, or any panel built with WebviewPanelBase.
allowed-tools: Bash(node *webview-cdp*), Bash(cd * && node *webview-cdp*)
---

# VS Code Webview CDP Tool

Interact with extension webview panels running inside VS Code. Take screenshots to see what you've built, click buttons, type text, test keyboard shortcuts, right-click context menus, and inspect DOM state.

## When to Use

- After implementing or modifying any webview panel UI
- Before claiming UI work is complete — always screenshot to verify
- When debugging visual issues — screenshot to see the current state
- When testing interactions — clicks, keyboard shortcuts, context menus, dropdowns

## Setup

The tool is at `src/PPDS.Extension/tools/webview-cdp.mjs`. No additional installation needed.

## Workflow

```bash
# 1. Start a VS Code instance with the extension loaded
node src/PPDS.Extension/tools/webview-cdp.mjs launch 9223

# 2. After compiling (npm run compile), verify your work
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot current-state.png
# IMPORTANT: Actually look at the screenshot to verify the UI

# 3. Interact and verify
node src/PPDS.Extension/tools/webview-cdp.mjs click "#my-button"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot after-click.png

# 4. When done
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

## Commands

| Command | Example | Purpose |
|---------|---------|---------|
| `launch [port] [workspace]` | `launch 9223` | Start VS Code with extension |
| `close` | `close` | Kill VS Code instance |
| `connect [port]` | `connect 9223` | Test connectivity, list webview targets |
| `screenshot <file>` | `screenshot result.png` | Capture webview as PNG |
| `eval "<js>"` | `eval "document.title"` | Run JS in webview, print result |
| `click "<selector>" [--right]` | `click "#btn"` | Left or right click element |
| `type "<selector>" "<text>"` | `type "#input" "hello"` | Type text into element |
| `select "<selector>" "<value>"` | `select "#dropdown" "opt1"` | Select dropdown option |
| `key "<combo>"` | `key "ctrl+enter"` | Send keyboard shortcut |
| `mouse <event> <x> <y>` | `mouse mousedown 150 200` | Raw mouse event at coordinates |

All commands except `launch` and `close` accept `--target N` to select a specific webview when multiple panels are open.

## Common Patterns

### Verify a button click
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot after-execute.png
```

### Test a keyboard shortcut
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs key "ctrl+enter"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot after-shortcut.png
```

### Test a context menu
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs click "td[data-row='1']" --right
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot context-menu.png
node src/PPDS.Extension/tools/webview-cdp.mjs click ".context-menu [data-action='copy']"
```

### Test drag selection
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs eval "JSON.stringify(document.querySelector('td[data-row=\"2\"]').getBoundingClientRect())"
node src/PPDS.Extension/tools/webview-cdp.mjs mouse mousedown 150 200
node src/PPDS.Extension/tools/webview-cdp.mjs mouse mousemove 300 250
node src/PPDS.Extension/tools/webview-cdp.mjs mouse mouseup 300 250
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot after-drag.png
```

### Check DOM state
```bash
node src/PPDS.Extension/tools/webview-cdp.mjs eval "document.querySelector('.cell-selected') !== null"
node src/PPDS.Extension/tools/webview-cdp.mjs eval "document.querySelector('#status-text').textContent"
```

## Gap Protocol

If you encounter a webview interaction that this tool cannot handle:

1. **STOP** — do not work around it silently
2. **Describe the gap** — what interaction you need and why the current commands don't cover it
3. **Propose an enhancement** — a new command, flag, or behavior that would close the gap
4. **Ask the user** — whether to implement the enhancement now or defer it

This ensures the tool evolves based on real needs.

## Important

- **Always screenshot after changes** — don't assume your code works, verify visually
- **Do NOT use agent-browser for VS Code webviews** — it cannot reach webview iframe targets
- **Do NOT use Playwright MCP for webview testing** — same limitation
- **Do NOT skip visual verification** — the whole point of this tool is seeing your work
- **Avoid eval expressions that read secrets** — auth tokens, cookies, etc. will appear in logs
```

- [ ] **Step 2: Commit**

```bash
git add .agents/skills/webview-cdp/SKILL.md
git commit -m "feat(tools): webview-cdp skill file — teaches AI agents the tool workflow"
```

---

### Task 14: Run the integration test sequence

This is the proving test from the spec. Run it manually to verify all commands work end-to-end.

**Prerequisites:** The extension must be compiled (`cd src/PPDS.Extension && npm run compile`).

- [ ] **Step 1: Launch VS Code**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs launch 9223`
Expected: "VS Code launched on port 9223 (PID NNNNN)"
Verify: VS Code window opens, session file exists at `src/PPDS.Extension/tools/.webview-cdp-session.json`

- [ ] **Step 2: Wait for VS Code to fully load, then test connect**

Wait ~10 seconds for the extension to activate and a webview to open (may need to open one manually via command palette), then:

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs connect`
Expected: "Found N webview target(s):" with at least one listed

- [ ] **Step 3: Take a screenshot**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs screenshot test-initial.png`
Expected: PNG file created showing webview content. **Open the file and verify** it shows the actual webview panel.

- [ ] **Step 4: Test eval**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs eval "1 + 1"`
Expected: `2`

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs eval "document.body.children.length"`
Expected: A positive integer

- [ ] **Step 5: Test click**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn"`
Run: `node src/PPDS.Extension/tools/webview-cdp.mjs screenshot test-after-click.png`
Expected: Screenshot shows the Execute button was activated (loading state or error message since no daemon is running)

- [ ] **Step 6: Test type**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs type "#sql-editor" "SELECT 1"`
Run: `node src/PPDS.Extension/tools/webview-cdp.mjs eval "document.querySelector('#sql-editor').value"`
Expected: `"SELECT 1"`

- [ ] **Step 7: Test key**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs key "Escape"`
Expected: No error (exits cleanly)

- [ ] **Step 8: Test right-click**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs click "#sql-editor" --right`
Run: `node src/PPDS.Extension/tools/webview-cdp.mjs screenshot test-right-click.png`
Expected: Screenshot may show context menu (depends on whether the element has a contextmenu handler)

- [ ] **Step 9: Clean up**

Run: `node src/PPDS.Extension/tools/webview-cdp.mjs close`
Expected: "VS Code closed (PID NNNNN)"
Verify: VS Code window closes, session file deleted

- [ ] **Step 10: Clean up test screenshots and commit**

```bash
rm -f test-initial.png test-after-click.png test-right-click.png
git add src/PPDS.Extension/tools/webview-cdp.mjs .agents/skills/webview-cdp/SKILL.md
git commit -m "feat(tools): webview-cdp integration verified — all commands working"
```

---

## Chunk 6: Final Cleanup

### Task 15: Run all unit tests

- [ ] **Step 1: Run vitest**

Run: `cd src/PPDS.Extension && npx vitest run tools/webview-cdp.test.mjs`
Expected: All tests pass

- [ ] **Step 2: Run existing extension tests to check for regressions**

Run: `cd src/PPDS.Extension && npm run test`
Expected: All existing tests still pass

- [ ] **Step 3: Final commit if any fixes were needed**

```bash
git add -A
git commit -m "fix(tools): address issues found during integration testing"
```
