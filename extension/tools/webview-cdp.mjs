import { readFileSync, writeFileSync, unlinkSync, existsSync, appendFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { spawn, execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const SESSION_FILE = resolve(__dirname, '.webview-cdp-session.json');
const LOG_FILE = resolve(__dirname, '.webview-cdp-console.log');
const PROFILE_DIR = resolve(__dirname, '.webview-cdp-profile');
const VALID_COMMANDS = ['launch', 'close', 'connect', 'command', 'wait',
  'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs', 'text'];
const VALID_MODIFIERS = ['ctrl', 'shift', 'alt', 'meta'];
const VALID_MOUSE_EVENTS = ['mousedown', 'mousemove', 'mouseup'];

// ── Pure functions (exported for testing) ──────────────────────────


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
  const keyMap = {
    enter: 'Enter', escape: 'Escape', tab: 'Tab', backspace: 'Backspace',
    delete: 'Delete', arrowup: 'ArrowUp', arrowdown: 'ArrowDown',
    arrowleft: 'ArrowLeft', arrowright: 'ArrowRight', space: ' ',
  };
  const normalizedKey = keyMap[key.toLowerCase()] || key;
  return { key: normalizedKey, modifiers };
}

export function parseArgs(argv) {
  if (!argv.length) throw new Error('No command provided');
  const command = argv[0];
  if (!VALID_COMMANDS.includes(command)) throw new Error(`Unknown command: ${command}`);

  const rest = argv.slice(1);

  // Simple commands with no interaction flags
  if (command === 'launch') {
    let workspace, build = false;
    for (const arg of rest) {
      if (arg === '--build') build = true;
      else if (!workspace) workspace = arg;
    }
    return { command, workspace, build };
  }
  if (command === 'close' || command === 'connect') {
    return { command };
  }
  if (command === 'command') {
    return { command, args: rest };
  }
  if (command === 'wait') {
    let timeout = 30000, ext;
    for (let i = 0; i < rest.length; i++) {
      if (rest[i] === '--ext' && i + 1 < rest.length) { ext = rest[++i]; }
      else { const n = parseInt(rest[i], 10); if (!isNaN(n)) timeout = n; }
    }
    return { command, timeout, ext };
  }
  if (command === 'logs') {
    let channel, level;
    for (let i = 0; i < rest.length; i++) {
      if (rest[i] === '--channel' && i + 1 < rest.length) { channel = rest[++i]; }
      else if (rest[i] === '--level' && i + 1 < rest.length) { level = rest[++i]; }
    }
    return { command, channel, level };
  }

  // Interaction commands: click, eval, type, select, screenshot, mouse, key
  let target, ext, right = false, page = false;
  const args = [];
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--target' && i + 1 < rest.length) { target = parseInt(rest[++i], 10); }
    else if (rest[i] === '--ext' && i + 1 < rest.length) { ext = rest[++i]; }
    else if (rest[i] === '--right') { right = true; }
    else if (rest[i] === '--page') { page = true; }
    else { args.push(rest[i]); }
  }

  // key command: only has args and page (no target/ext/right)
  if (command === 'key') {
    return { command, args, page };
  }
  // click: has right flag
  if (command === 'click') {
    return { command, args, page, right, target, ext };
  }
  // all others: args, page, target, ext
  return { command, args, page, target, ext };
}

// ── Session file I/O ───────────────────────────────────────────────

function readSession() {
  if (!existsSync(SESSION_FILE))
    throw new Error('No session found. Run `webview-cdp launch` first.');
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

// ── Daemon Mode ────────────────────────────────────────────────────

async function runDaemon(workspace) {
  // Dynamic import — callers don't need Playwright
  const { _electron: electron } = await import('@playwright/test');
  const { downloadAndUnzipVSCode } = await import('@vscode/test-electron');

  const execPath = await downloadAndUnzipVSCode();
  const extensionPath = resolve(__dirname, '..');

  const electronApp = await electron.launch({
    executablePath: execPath,
    args: [
      '--extensionDevelopmentPath=' + extensionPath,
      '--user-data-dir=' + PROFILE_DIR,
      '--no-sandbox',
      '--disable-gpu',
      '--log=trace',
      '--disable-workspace-trust',
      '--skip-release-notes',
      '--disable-telemetry',
      '--disable-crash-reporter',
      workspace,
    ],
  });

  const page = await electronApp.firstWindow();
  await page.waitForSelector('[id="workbench.parts.editor"]', { timeout: 60000 });

  // Try dialog interception
  try {
    await electronApp.evaluate(({ dialog }) => {
      dialog.showMessageBox = async () => ({ response: 0, checkboxChecked: false });
      dialog.showOpenDialog = async () => ({ canceled: true, filePaths: [] });
      dialog.showSaveDialog = async () => ({ canceled: true, filePath: undefined });
    });
    console.error('Dialog hooks installed');
  } catch {
    console.error('Dialog hooks not available — native dialogs may block');
  }

  // Console capture
  if (existsSync(LOG_FILE)) unlinkSync(LOG_FILE);
  page.on('console', msg => {
    appendFileSync(LOG_FILE, JSON.stringify({
      level: msg.type(), message: msg.text(),
      source: 'main', timestamp: new Date().toISOString(),
    }) + '\n');
  });
  page.on('pageerror', err => {
    appendFileSync(LOG_FILE, JSON.stringify({
      level: 'error', message: 'Uncaught: ' + err.message,
      source: 'main', timestamp: new Date().toISOString(),
    }) + '\n');
  });

  // Webview frame resolver
  // Use page.frames() instead of DOM traversal — avoids stale ElementHandle issues
  // when VS Code re-renders webview containers between commands
  async function resolveWebviewFrame(targetIndex, extFilter) {
    const allFrames = page.frames();

    // VS Code webview structure: each webview panel has two frames:
    //   - index.html (wrapper — bootstrap code with extensionId in URL)
    //   - fake.html? (inner active-frame — extension's actual DOM, NO extensionId)
    // We want the inner frame. Strategy:
    //   1. Find all fake.html frames (inner frames)
    //   2. For --ext filter, check the PARENT frame's URL for the extensionId
    //   3. Fall back to any webview frame if no fake.html frames found
    const innerFrames = [];

    for (const frame of allFrames) {
      const url = frame.url();
      if (!url.startsWith('vscode-webview://')) continue;
      if (!url.includes('fake.html')) continue; // only inner frames

      // For --ext filter, check parent's URL (that's where extensionId lives)
      if (extFilter) {
        const parentUrl = frame.parentFrame()?.url() || '';
        if (!parentUrl.includes(extFilter)) continue;
      }

      innerFrames.push(frame);
    }

    // Fall back to any webview frame with content if no inner frames found
    let candidates = innerFrames;
    if (candidates.length === 0) {
      for (const frame of allFrames) {
        const url = frame.url();
        if (!url.startsWith('vscode-webview://')) continue;
        if (extFilter && !url.includes(extFilter)) continue;
        try {
          const hasContent = await frame.evaluate(() => document.body && document.body.children.length > 0);
          if (hasContent) candidates.push(frame);
        } catch { /* skip */ }
      }
    }

    if (candidates.length === 0) throw new Error('No webview found. Open a panel first.');

    const idx = targetIndex ?? 0;
    if (idx < 0 || idx >= candidates.length)
      throw new Error(`Target index ${idx} out of range (found ${candidates.length} webviews)`);

    return candidates[idx];
  }

  async function resolveTarget(params) {
    if (params.page) return page;
    return resolveWebviewFrame(params.target, params.ext);
  }

  // Command palette helper
  async function executeCommand(commandText) {
    await page.keyboard.press('Control+Shift+P');
    await page.waitForSelector('.quick-input-widget', { state: 'visible', timeout: 5000 });
    await page.keyboard.type(commandText, { delay: 50 });

    try {
      const firstResult = await page.waitForSelector(
        '.quick-input-list .quick-input-list-entry',
        { timeout: 5000 }
      );
      if (firstResult) {
        await page.keyboard.press('Enter');
        await page.waitForTimeout(500);
        return;
      }
    } catch {
      await page.keyboard.press('Escape');
      throw new Error(`Command not found: ${commandText}`);
    }
  }

  // Key combo → Playwright format
  function toPlaywrightCombo(parsed) {
    const parts = [];
    if (parsed.modifiers.ctrl) parts.push('Control');
    if (parsed.modifiers.shift) parts.push('Shift');
    if (parsed.modifiers.alt) parts.push('Alt');
    if (parsed.modifiers.meta) parts.push('Meta');
    parts.push(parsed.key);
    return parts.join('+');
  }

  // Action handler
  // Retry wrapper for commands that may fail due to transient frame detachment
  async function withRetry(fn, maxRetries = 2) {
    for (let attempt = 0; attempt <= maxRetries; attempt++) {
      try {
        return await fn();
      } catch (err) {
        const isTransient = err.message?.includes('detached') || err.message?.includes('navigating')
          || err.message?.includes('closed') || err.message?.includes('Target page, context or browser has been closed');
        if (!isTransient || attempt === maxRetries) throw err;
        await new Promise(r => setTimeout(r, 300));
      }
    }
  }

  async function handleAction(action, params) {
    switch (action) {
      case 'screenshot': {
        // Always use page.screenshot() — it's reliable and fast.
        // frame.locator('body').screenshot() times out on complex webview content.
        // --page captures the full window; default captures full window too
        // (agents can crop mentally — a full window screenshot with the webview
        // visible is more useful than a timeout).
        await page.screenshot({ path: resolve(params.file) });
        return { path: resolve(params.file) };
      }
      case 'eval': {
        const target = await resolveTarget(params);
        const value = await target.evaluate(params.expression);
        return { value };
      }
      case 'text': {
        const target = await resolveTarget(params);
        const text = await target.evaluate(
          (sel) => document.querySelector(sel)?.textContent ?? '',
          params.selector
        );
        return { text };
      }
      case 'click': {
        const target = await resolveTarget(params);
        const opts = {};
        if (params.right) opts.button = 'right';
        await target.click(params.selector, opts);
        return {};
      }
      case 'type': {
        const target = await resolveTarget(params);
        // Try fill() first (works on input/textarea/select/contenteditable)
        // Fall back to click + keyboard.type() for custom elements (e.g., Monaco editor)
        try {
          await target.fill(params.selector, params.text);
        } catch {
          await target.click(params.selector);
          await page.keyboard.press('Control+A'); // select all existing text
          await page.keyboard.type(params.text, { delay: 10 });
        }
        return {};
      }
      case 'select': {
        const target = await resolveTarget(params);
        await target.selectOption(params.selector, params.value);
        return {};
      }
      case 'key': {
        const combo = parseKeyCombo(params.combo);
        const pwCombo = toPlaywrightCombo(combo);
        await page.keyboard.press(pwCombo);
        return {};
      }
      case 'mouse': {
        const methodMap = { mousedown: 'down', mousemove: 'move', mouseup: 'up' };
        const method = methodMap[params.event];
        if (!method) throw new Error('Invalid mouse event');
        await page.mouse[method](params.x, params.y);
        return {};
      }
      case 'command': {
        await executeCommand(params.text);
        return {};
      }
      case 'connect': {
        // List only inner frames (fake.html) — same logic as resolveWebviewFrame
        const allFrames = page.frames();
        const targets = [];
        for (const frame of allFrames) {
          const url = frame.url();
          if (!url.startsWith('vscode-webview://')) continue;
          if (!url.includes('fake.html')) continue;
          // Extension ID is in the parent (wrapper) frame's URL
          const parentUrl = frame.parentFrame()?.url() || '';
          const extMatch = parentUrl.match(/extensionId=([^&]*)/);
          targets.push({ ext: extMatch?.[1] || 'unknown', src: url });
        }
        return { targets };
      }
      case 'wait': {
        const start = Date.now();
        const timeout = params.timeout || 30000;
        while (Date.now() - start < timeout) {
          try {
            await resolveWebviewFrame(params.target, params.ext);
            return {};
          } catch {
            await new Promise(r => setTimeout(r, 500));
          }
        }
        throw new Error(`Timeout: no webview found within ${timeout / 1000} seconds`);
      }
      case 'logs': {
        if (params.channel) {
          const logsDir = resolve(PROFILE_DIR, 'logs');
          if (!existsSync(logsDir)) return { logs: 'No log directory found' };
          // Search log files using pure Node.js (no shell — CONSTITUTION S2)
          const { readdirSync, statSync } = await import('node:fs');
          function findLogFiles(dir) {
            let files = [];
            try {
              for (const entry of readdirSync(dir)) {
                const full = resolve(dir, entry);
                try {
                  if (statSync(full).isDirectory()) files.push(...findLogFiles(full));
                  else if (entry.endsWith('.log')) files.push(full);
                } catch { /* skip inaccessible */ }
              }
            } catch { /* skip inaccessible */ }
            return files;
          }
          const logFiles = findLogFiles(logsDir);
          const matching = logFiles
            .filter(f => {
              const name = f.split(/[\\/]/).pop();
              return name.toLowerCase().includes(params.channel.toLowerCase());
            });
          if (matching.length === 0) return { logs: `No logs found for channel: ${params.channel}` };
          const logContent = matching.map(f => readFileSync(f, 'utf-8')).join('\n');
          return { logs: logContent };
        }
        if (!existsSync(LOG_FILE)) return { logs: '' };
        return { logs: readFileSync(LOG_FILE, 'utf-8') };
      }
      default:
        throw new Error(`Unknown action: ${action}`);
    }
  }

  // HTTP server helpers
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
        const result = await withRetry(() => handleAction(action, params));
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

  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const daemonPort = server.address().port;

  writeSession({ daemonPort, daemonPid: process.pid, userDataDir: PROFILE_DIR, logFile: LOG_FILE });
  console.error(`Daemon ready on port ${daemonPort}`);

  async function cleanup() {
    try { await electronApp.close(); } catch {}
    deleteSession();
    if (existsSync(LOG_FILE)) unlinkSync(LOG_FILE);
    server.close();
    process.exit(0);
  }

  process.on('SIGTERM', cleanup);
  process.on('SIGINT', cleanup);
}

// ── Caller command handlers ────────────────────────────────────────

async function cmdLaunch(parsed) {
  if (parsed.build) {
    const extDir = resolve(__dirname, '..');
    console.log('Building extension...');
    execSync('npm run compile', { cwd: extDir, stdio: 'inherit' });
    console.log('Build complete');
  }

  if (existsSync(SESSION_FILE)) {
    const session = JSON.parse(readFileSync(SESSION_FILE, 'utf-8'));
    try {
      await fetch(`http://localhost:${session.daemonPort}/health`);
      console.log('VS Code is already running. Run `close` first.');
      return;
    } catch {
      // Stale session — clean up
      try { process.kill(session.daemonPid); } catch {}
      deleteSession();
    }
  }

  const workspace = parsed.workspace || process.cwd();
  const child = spawn(process.execPath, [__filename, '--daemon', workspace], {
    detached: true,
    stdio: ['ignore', 'ignore', 'ignore'],
  });
  child.unref();

  const start = Date.now();
  while (Date.now() - start < 90000) {
    if (existsSync(SESSION_FILE)) {
      const session = readSession();
      console.log(`VS Code launched (daemon PID ${session.daemonPid}, port ${session.daemonPort})`);
      return;
    }
    await new Promise(r => setTimeout(r, 500));
  }
  throw new Error('Timeout: daemon did not start within 90 seconds');
}

async function cmdClose() {
  const session = readSession();
  try {
    await fetch(`http://localhost:${session.daemonPort}/shutdown`);
  } catch {
    try { process.kill(session.daemonPid); } catch {}
  }
  const start = Date.now();
  while (Date.now() - start < 10000) {
    if (!existsSync(SESSION_FILE)) {
      console.log('VS Code closed');
      return;
    }
    await new Promise(r => setTimeout(r, 200));
  }
  // Force cleanup
  try { process.kill(session.daemonPid); } catch {}
  deleteSession();
  if (existsSync(LOG_FILE)) unlinkSync(LOG_FILE);
  console.log('VS Code closed (forced)');
}

async function cmdScreenshot(parsed) {
  if (!parsed.args[0]) throw new Error('Usage: screenshot <file>');
  const session = readSession();
  const result = await sendToDaemon(session, 'screenshot', {
    file: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(result.path);
}

async function cmdEval(parsed) {
  if (!parsed.args[0]) throw new Error('Empty expression');
  const session = readSession();
  const result = await sendToDaemon(session, 'eval', {
    expression: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(JSON.stringify(result.value));
}

async function cmdText(parsed) {
  if (!parsed.args[0]) throw new Error('Usage: text <selector>');
  const session = readSession();
  const result = await sendToDaemon(session, 'text', {
    selector: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(result.text);
}

async function cmdClick(parsed) {
  if (!parsed.args[0]) throw new Error('Empty selector');
  const session = readSession();
  await sendToDaemon(session, 'click', {
    selector: parsed.args[0], right: parsed.right, page: parsed.page,
    target: parsed.target, ext: parsed.ext,
  });
}

async function cmdType(parsed) {
  if (!parsed.args[0] || parsed.args[1] === undefined) throw new Error('Usage: type <selector> <text>');
  const session = readSession();
  await sendToDaemon(session, 'type', {
    selector: parsed.args[0], text: parsed.args[1], page: parsed.page,
    target: parsed.target, ext: parsed.ext,
  });
}

async function cmdSelect(parsed) {
  if (!parsed.args[0] || parsed.args[1] === undefined) throw new Error('Usage: select <selector> <value>');
  const session = readSession();
  await sendToDaemon(session, 'select', {
    selector: parsed.args[0], value: parsed.args[1], page: parsed.page,
    target: parsed.target, ext: parsed.ext,
  });
}

async function cmdKey(parsed) {
  if (!parsed.args[0]) throw new Error('Empty key combo');
  const session = readSession();
  await sendToDaemon(session, 'key', { combo: parsed.args[0], page: parsed.page });
}

async function cmdMouse(parsed) {
  const event = parsed.args[0];
  const x = parseFloat(parsed.args[1]);
  const y = parseFloat(parsed.args[2]);
  if (!VALID_MOUSE_EVENTS.includes(event)) throw new Error('Invalid mouse event. Must be: mousedown, mousemove, mouseup');
  if (isNaN(x) || isNaN(y) || x < 0 || y < 0) throw new Error('Invalid coordinates');
  const session = readSession();
  await sendToDaemon(session, 'mouse', { event, x, y, page: parsed.page, target: parsed.target, ext: parsed.ext });
}

async function cmdCommand(parsed) {
  if (!parsed.args[0]) throw new Error('Empty command');
  const session = readSession();
  await sendToDaemon(session, 'command', { text: parsed.args[0] });
}

async function cmdWait(parsed) {
  const session = readSession();
  await sendToDaemon(session, 'wait', { timeout: parsed.timeout, ext: parsed.ext, target: parsed.target });
  console.log('Webview ready');
}

async function cmdConnect() {
  const session = readSession();
  const result = await sendToDaemon(session, 'connect', {});
  if (result.targets.length === 0) {
    throw new Error('No webview found. Is a webview panel open?');
  }
  console.log(`Found ${result.targets.length} webview target(s):`);
  result.targets.forEach((t, i) => {
    console.log(`  ${i}: ${t.ext} — ${t.src.substring(0, 80)}`);
  });
}

async function cmdLogs(parsed) {
  const session = readSession();
  const result = await sendToDaemon(session, 'logs', { channel: parsed.channel, level: parsed.level });
  console.log(result.logs);
}

// ── Main dispatch ──────────────────────────────────────────────────

async function main() {
  // Daemon mode
  if (process.argv.includes('--daemon')) {
    const daemonIdx = process.argv.indexOf('--daemon');
    const workspace = process.argv[daemonIdx + 1] || process.cwd();
    await runDaemon(workspace);
    return;
  }

  // Caller mode
  const parsed = parseArgs(process.argv.slice(2));
  switch (parsed.command) {
    case 'launch': await cmdLaunch(parsed); break;
    case 'close': await cmdClose(); break;
    case 'connect': await cmdConnect(parsed); break;
    case 'command': await cmdCommand(parsed); break;
    case 'wait': await cmdWait(parsed); break;
    case 'screenshot': await cmdScreenshot(parsed); break;
    case 'eval': await cmdEval(parsed); break;
    case 'text': await cmdText(parsed); break;
    case 'click': await cmdClick(parsed); break;
    case 'type': await cmdType(parsed); break;
    case 'select': await cmdSelect(parsed); break;
    case 'key': await cmdKey(parsed); break;
    case 'mouse': await cmdMouse(parsed); break;
    case 'logs': await cmdLogs(parsed); break;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === __filename) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
