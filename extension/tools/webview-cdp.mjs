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

// ── Command dispatch ───────────────────────────────────────────────

async function main() {
  const parsed = parseArgs(process.argv.slice(2));

  switch (parsed.command) {
    case 'connect':
      await cmdConnect(parsed);
      break;
    default:
      throw new Error(`Command '${parsed.command}' not yet implemented`);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === __filename) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
