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
  await new Promise(r => setTimeout(r, 500));
  ws.off('message', contextHandler);

  for (const ctx of contexts) {
    try {
      const result = await sendCDP(ws, 'Runtime.evaluate', {
        expression: 'document.body !== null',
        contextId: ctx.id,
        returnByValue: true,
      });
      if (result.result && result.result.value === true) {
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

// ── Command: launch ────────────────────────────────────────────────

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
  const extensionPath = resolve(__dirname, '..');  // extension/ directory

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

  // Poll for CDP readiness
  const startTime = Date.now();
  const timeout = 15000;
  while (Date.now() - startTime < timeout) {
    try {
      const verRes = await fetch(`http://localhost:${port}/json/version`);
      await verRes.json();
      const pidRes = await fetch(`http://localhost:${port}/json/list`);
      await pidRes.json();

      writeSession(codeProcess.pid, port);
      console.log(`VS Code launched on port ${port} (PID ${codeProcess.pid})`);
      return;
    } catch {
      await new Promise(r => setTimeout(r, 500));
    }
  }
  throw new Error(`Timeout: VS Code did not start within ${timeout / 1000} seconds.`);
}

// ── Command: close ─────────────────────────────────────────────────

async function cmdClose() {
  const session = readSession();

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
      if (process.platform === 'win32') {
        execSync(`taskkill /PID ${session.pid} /T /F`, { stdio: 'ignore' });
      } else {
        process.kill(-session.pid, 'SIGTERM');
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

// ── Command: connect ───────────────────────────────────────────────

async function cmdConnect(parsed) {
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
    const extMatch = t.url.match(/extensionId=([^&]+)/);
    const ext = extMatch ? extMatch[1] : 'unknown';
    console.log(`  ${i}: ${ext} — ${t.title || t.url}`);
  });
}

// ── Command: screenshot ────────────────────────────────────────────

async function cmdScreenshot(parsed) {
  const filePath = parsed.args[0];
  if (!filePath) throw new Error('Usage: screenshot <file>');

  const dir = dirname(resolve(filePath));
  if (!existsSync(dir)) throw new Error(`Cannot write to: ${filePath}`);

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
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

    const allTargets = await fetchTargets(session.port);
    const pageTargets = allTargets.filter(t => t.type === 'page' && t.title?.includes('Visual Studio Code'));
    if (pageTargets.length === 0) throw new Error('Cannot capture screenshot: no page target found');

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

// ── Command: eval ──────────────────────────────────────────────────

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

// ── Command: click ─────────────────────────────────────────────────

async function cmdClick(parsed) {
  const selector = parsed.args[0];
  if (!selector) throw new Error('Empty selector');

  const session = readSession();
  await withWebview(session.port, parsed.target, async (ws, contextId) => {
    const check = await sendCDP(ws, 'Runtime.evaluate', {
      expression: `document.querySelector(${JSON.stringify(selector)}) !== null`,
      contextId,
      returnByValue: true,
    });
    if (!check.result.value) {
      throw new Error(`Element not found: ${selector}`);
    }

    if (parsed.right) {
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
      await sendCDP(ws, 'Runtime.evaluate', {
        expression: `document.querySelector(${JSON.stringify(selector)}).click()`,
        contextId,
        returnByValue: true,
      });
    }
  });
}

// ── Command: type ──────────────────────────────────────────────────

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

// ── Command: select ────────────────────────────────────────────────

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

// ── Command: key ───────────────────────────────────────────────────

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

    if (key.length === 1) {
      dispatchParams.text = key;
    }

    await sendCDP(ws, 'Input.dispatchKeyEvent', dispatchParams);
    const { text, ...keyUpParams } = dispatchParams;
    await sendCDP(ws, 'Input.dispatchKeyEvent', { ...keyUpParams, type: 'keyUp' });
  });
}

// ── Command: mouse ─────────────────────────────────────────────────

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

// ── Command dispatch ───────────────────────────────────────────────

async function main() {
  const parsed = parseArgs(process.argv.slice(2));

  switch (parsed.command) {
    case 'launch':
      await cmdLaunch(parsed);
      break;
    case 'close':
      await cmdClose();
      break;
    case 'connect':
      await cmdConnect(parsed);
      break;
    case 'screenshot':
      await cmdScreenshot(parsed);
      break;
    case 'eval':
      await cmdEval(parsed);
      break;
    case 'click':
      await cmdClick(parsed);
      break;
    case 'type':
      await cmdType(parsed);
      break;
    case 'select':
      await cmdSelect(parsed);
      break;
    case 'key':
      await cmdKey(parsed);
      break;
    case 'mouse':
      await cmdMouse(parsed);
      break;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === __filename) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
