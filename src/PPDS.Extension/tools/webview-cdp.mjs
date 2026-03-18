import { readFileSync, writeFileSync, unlinkSync, existsSync, appendFileSync, readdirSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { spawn, execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

// Session-scoped file paths (supports multiple concurrent instances)
function sessionFile(name = 'default') {
  return resolve(__dirname, `.webview-cdp-session-${name}.json`);
}
function logFile(name = 'default') {
  return resolve(__dirname, `.webview-cdp-console-${name}.log`);
}
function profileDir(name = 'default') {
  return resolve(__dirname, `.webview-cdp-profile-${name}`);
}

const VALID_COMMANDS = ['launch', 'close', 'connect', 'command', 'wait',
  'screenshot', 'eval', 'click', 'type', 'select', 'key', 'mouse', 'logs', 'text', 'notebook', 'download'];
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

  // Extract --session from any command
  let session = 'default';
  const filteredRest = [];
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--session' && i + 1 < rest.length) { session = rest[++i]; }
    else { filteredRest.push(rest[i]); }
  }

  // Simple commands with no interaction flags
  if (command === 'launch') {
    let workspace, build = false, vsix;
    for (const arg of filteredRest) {
      if (arg === '--build') build = true;
      else if (arg.startsWith('--vsix')) { /* handled below */ }
      else if (!workspace) workspace = arg;
    }
    // Re-scan for --vsix (needs value)
    for (let i = 0; i < filteredRest.length; i++) {
      if (filteredRest[i] === '--vsix' && i + 1 < filteredRest.length) { vsix = filteredRest[++i]; }
    }
    return { command, workspace, build, vsix, session };
  }
  if (command === 'close') {
    const all = filteredRest.includes('--all');
    return { command, all, session };
  }
  if (command === 'connect') {
    return { command, session };
  }
  if (command === 'download') {
    return { command, args: filteredRest, session };
  }
  if (command === 'command') {
    return { command, args: filteredRest, session };
  }
  if (command === 'wait') {
    let timeout = 30000, ext;
    for (let i = 0; i < filteredRest.length; i++) {
      if (filteredRest[i] === '--ext' && i + 1 < filteredRest.length) { ext = filteredRest[++i]; }
      else { const n = parseInt(filteredRest[i], 10); if (!isNaN(n)) timeout = n; }
    }
    return { command, timeout, ext, session };
  }
  if (command === 'logs') {
    let channel, level;
    for (let i = 0; i < filteredRest.length; i++) {
      if (filteredRest[i] === '--channel' && i + 1 < filteredRest.length) { channel = filteredRest[++i]; }
      else if (filteredRest[i] === '--level' && i + 1 < filteredRest.length) { level = filteredRest[++i]; }
    }
    return { command, channel, level, session };
  }
  if (command === 'notebook') {
    const NOTEBOOK_SUBCOMMANDS = ['run', 'run-all'];
    const subcommand = filteredRest[0];
    if (!subcommand) throw new Error('notebook requires a subcommand: ' + NOTEBOOK_SUBCOMMANDS.join(', '));
    if (!NOTEBOOK_SUBCOMMANDS.includes(subcommand)) throw new Error(`Unknown notebook subcommand: ${subcommand}. Valid: ${NOTEBOOK_SUBCOMMANDS.join(', ')}`);
    return { command, subcommand, session };
  }

  // Interaction commands: click, eval, type, select, screenshot, mouse, key
  let target, ext, right = false, page = false;
  const args = [];
  for (let i = 0; i < filteredRest.length; i++) {
    if (filteredRest[i] === '--target' && i + 1 < filteredRest.length) {
      const val = filteredRest[++i];
      target = val === 'active' ? 'active' : parseInt(val, 10);
    }
    else if (filteredRest[i] === '--ext' && i + 1 < filteredRest.length) { ext = filteredRest[++i]; }
    else if (filteredRest[i] === '--right') { right = true; }
    else if (filteredRest[i] === '--page') { page = true; }
    else { args.push(filteredRest[i]); }
  }

  // key command: only has args and page (no target/ext/right)
  if (command === 'key') {
    return { command, args, page, session };
  }
  // click: has right flag
  if (command === 'click') {
    return { command, args, page, right, target, ext, session };
  }
  // all others: args, page, target, ext
  return { command, args, page, target, ext, session };
}

// ── Session file I/O ───────────────────────────────────────────────

function readSession(name = 'default') {
  const f = sessionFile(name);
  if (!existsSync(f))
    throw new Error(`No session '${name}' found. Run \`webview-cdp launch --session ${name}\` first.`);
  return JSON.parse(readFileSync(f, 'utf-8'));
}

function writeSession(data, name = 'default') {
  writeFileSync(sessionFile(name), JSON.stringify(data, null, 2));
}

function deleteSession(name = 'default') {
  const f = sessionFile(name);
  if (existsSync(f)) unlinkSync(f);
}

function listSessions() {
  try {
    const files = readdirSync(__dirname);
    return files
      .filter(f => f.startsWith('.webview-cdp-session-') && f.endsWith('.json'))
      .map(f => f.replace('.webview-cdp-session-', '').replace('.json', ''));
  } catch { return []; }
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

async function runDaemon(workspace, sessionName = 'default', vsixPath = null) {
  const SESSION_PROFILE_DIR = profileDir(sessionName);
  const SESSION_LOG_FILE = logFile(sessionName);

  // Dynamic import — callers don't need Playwright
  const { _electron: electron } = await import('@playwright/test');
  const { downloadAndUnzipVSCode } = await import('@vscode/test-electron');

  const execPath = await downloadAndUnzipVSCode();

  // Build VS Code launch args based on mode
  const launchArgs = [
    '--user-data-dir=' + SESSION_PROFILE_DIR,
    '--no-sandbox',
    '--disable-gpu',
    '--log=trace',
    '--disable-workspace-trust',
    '--skip-release-notes',
    '--disable-telemetry',
    '--disable-crash-reporter',
  ];

  if (vsixPath) {
    // VSIX mode: install extension into a clean extensions dir
    const extDir = resolve(SESSION_PROFILE_DIR, 'extensions');
    const extractDir = resolve(extDir, '_vsix_extract');
    mkdirSync(extractDir, { recursive: true });
    // VSIX files are ZIP archives — extract using platform-appropriate tool
    if (process.platform === 'win32') {
      // bsdtar on Windows can't handle C: drive prefixes — use pwsh
      execSync(`pwsh -NoProfile -Command "Expand-Archive -Path '${vsixPath}' -DestinationPath '${extractDir}' -Force"`, { timeout: 60000 });
    } else {
      execSync(`tar -xf "${vsixPath}" -C "${extractDir}"`, { timeout: 60000 });
    }
    // The VSIX contains an 'extension/' subfolder — rename to VS Code's expected format
    const innerDir = resolve(extractDir, 'extension');
    if (existsSync(innerDir)) {
      const pkgPath = resolve(innerDir, 'package.json');
      if (existsSync(pkgPath)) {
        const pkg = JSON.parse(readFileSync(pkgPath, 'utf-8'));
        const extFolderName = `${pkg.publisher}.${pkg.name}-${pkg.version}`;
        const finalDir = resolve(extDir, extFolderName);
        if (!existsSync(finalDir)) {
          const { renameSync } = await import('node:fs');
          renameSync(innerDir, finalDir);
        }
      }
    }
    launchArgs.push('--extensions-dir=' + extDir);
  } else {
    // Dev mode: load extension from source
    const extensionPath = resolve(__dirname, '..');
    launchArgs.push('--extensionDevelopmentPath=' + extensionPath);
  }

  launchArgs.push(workspace);

  const electronApp = await electron.launch({
    executablePath: execPath,
    args: launchArgs,
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
  if (existsSync(SESSION_LOG_FILE)) unlinkSync(SESSION_LOG_FILE);
  page.on('console', msg => {
    appendFileSync(SESSION_LOG_FILE, JSON.stringify({
      level: msg.type(), message: msg.text(),
      source: 'main', timestamp: new Date().toISOString(),
    }) + '\n');
  });
  page.on('pageerror', err => {
    appendFileSync(SESSION_LOG_FILE, JSON.stringify({
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

    // --target active: find the visible/focused webview
    if (targetIndex === 'active') {
      const visibleFrames = [];
      for (const frame of candidates) {
        try {
          const isVisible = await frame.evaluate(() => document.visibilityState === 'visible');
          if (isVisible) visibleFrames.push(frame);
        } catch { /* skip detached frames */ }
      }
      if (visibleFrames.length === 0) throw new Error('No visible webview found. Focus a webview panel first.');
      if (visibleFrames.length > 1) throw new Error(`Multiple visible webviews found (${visibleFrames.length}). Use --target <index> to disambiguate.`);
      return visibleFrames[0];
    }

    // When no explicit --target is specified and multiple candidates exist,
    // prefer visible frames (sort visible first, then DOM order)
    if (targetIndex == null && candidates.length > 1) {
      const visibility = [];
      for (const frame of candidates) {
        try {
          const isVisible = await frame.evaluate(() => document.visibilityState === 'visible');
          visibility.push(isVisible);
        } catch {
          visibility.push(false);
        }
      }
      // If exactly one is visible, use it; otherwise keep DOM order
      const visibleIndices = visibility.reduce((acc, v, i) => v ? [...acc, i] : acc, []);
      if (visibleIndices.length === 1) {
        return candidates[visibleIndices[0]];
      }
    }

    const idx = typeof targetIndex === 'number' ? targetIndex : 0;
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
          // Check visibility
          let visible = false;
          try {
            visible = await frame.evaluate(() => document.visibilityState === 'visible');
          } catch { /* skip */ }
          // Extract panel title from VS Code tab bar via main page
          let title = null;
          try {
            // webview ID is in the URL: vscode-webview://<id>/...
            const webviewId = new URL(url).hostname;
            title = await page.evaluate((wvId) => {
              // VS Code tab elements have data-resource-name or aria-label with panel title
              const tabs = document.querySelectorAll('.tab');
              for (const tab of tabs) {
                const label = tab.getAttribute('aria-label') || '';
                const resourceUri = tab.querySelector('.label-name')?.getAttribute('data-resource-name') || '';
                // Check if this tab's resource references the webview ID
                if (label.includes(wvId) || resourceUri.includes(wvId)) {
                  return tab.querySelector('.label-name')?.textContent?.trim() || label;
                }
              }
              // Fallback: try matching via active tab's webview container
              return null;
            }, webviewId);
          } catch { /* title extraction is best-effort */ }
          targets.push({
            ext: extMatch?.[1] || 'unknown',
            src: url,
            title: title || '(unknown)',
            visible,
          });
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
      case 'logs': {
        if (params.channel) {
          const logsDir = resolve(SESSION_PROFILE_DIR, 'logs');
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
          // VS Code LogOutputChannel writes to exthost/<extensionId>/ChannelName.log
          const expected = params.channel.toLowerCase() + '.log';
          const matching = logFiles
            .filter(f => f.split(/[\\/]/).pop().toLowerCase() === expected);
          if (matching.length === 0) return { logs: `No logs found for channel: ${params.channel}` };
          const logContent = matching.map(f => readFileSync(f, 'utf-8')).join('\n');
          return { logs: logContent };
        }
        if (!existsSync(SESSION_LOG_FILE)) return { logs: '' };
        return { logs: readFileSync(SESSION_LOG_FILE, 'utf-8') };
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

  writeSession({ daemonPort, daemonPid: process.pid, userDataDir: SESSION_PROFILE_DIR, logFile: SESSION_LOG_FILE }, sessionName);
  console.error(`Daemon '${sessionName}' ready on port ${daemonPort}`);

  async function cleanup() {
    try { await electronApp.close(); } catch {}
    deleteSession(sessionName);
    if (existsSync(SESSION_LOG_FILE)) unlinkSync(SESSION_LOG_FILE);
    server.close();
    process.exit(0);
  }

  process.on('SIGTERM', cleanup);
  process.on('SIGINT', cleanup);
}

// ── Caller command handlers ────────────────────────────────────────

async function cmdLaunch(parsed) {
  const sName = parsed.session;

  if (parsed.build && !parsed.vsix) {
    const extDir = resolve(__dirname, '..');
    const repoRoot = resolve(extDir, '..', '..');
    console.log('Building extension...');
    try {
      execSync('npm run compile', { cwd: extDir, stdio: 'inherit' });
    } catch {
      throw new Error('Extension build failed — fix compilation errors above');
    }
    console.log('Building daemon...');
    try {
      execSync('dotnet build src/PPDS.Cli/PPDS.Cli.csproj -c Debug -v q', { cwd: repoRoot, stdio: 'inherit' });
    } catch {
      throw new Error('Daemon build failed — fix compilation errors above');
    }
    console.log('Build complete');
  }

  const sf = sessionFile(sName);
  if (existsSync(sf)) {
    const session = JSON.parse(readFileSync(sf, 'utf-8'));
    try {
      await fetch(`http://localhost:${session.daemonPort}/health`);
      console.log(`Session '${sName}' is already running. Run \`close --session ${sName}\` first.`);
      return;
    } catch {
      // Stale session — clean up
      try { process.kill(session.daemonPid); } catch {}
      deleteSession(sName);
    }
  }

  if (parsed.vsix && !existsSync(parsed.vsix)) {
    throw new Error(`VSIX file not found: ${parsed.vsix}`);
  }

  const workspace = parsed.workspace || process.cwd();
  const daemonArgs = [__filename, '--daemon', workspace, '--session', sName];
  if (parsed.vsix) daemonArgs.push('--vsix', resolve(parsed.vsix));

  const child = spawn(process.execPath, daemonArgs, {
    detached: true,
    stdio: ['ignore', 'ignore', 'ignore'],
  });
  child.unref();

  const start = Date.now();
  while (Date.now() - start < 90000) {
    if (existsSync(sf)) {
      const session = readSession(sName);
      console.log(`VS Code launched (session '${sName}', daemon PID ${session.daemonPid}, port ${session.daemonPort})`);
      return;
    }
    await new Promise(r => setTimeout(r, 500));
  }
  throw new Error('Timeout: daemon did not start within 90 seconds');
}

async function cmdClose(parsed) {
  if (parsed.all) {
    const sessions = listSessions();
    if (sessions.length === 0) {
      console.log('No active sessions');
      return;
    }
    for (const sName of sessions) {
      await closeSession(sName);
    }
    return;
  }
  await closeSession(parsed.session);
}

async function closeSession(sName) {
  const sf = sessionFile(sName);
  if (!existsSync(sf)) {
    console.log(`No session '${sName}' found`);
    return;
  }
  const session = JSON.parse(readFileSync(sf, 'utf-8'));
  try {
    await fetch(`http://localhost:${session.daemonPort}/shutdown`);
  } catch {
    try { process.kill(session.daemonPid); } catch {}
  }
  const start = Date.now();
  while (Date.now() - start < 10000) {
    if (!existsSync(sf)) {
      console.log(`Session '${sName}' closed`);
      return;
    }
    await new Promise(r => setTimeout(r, 200));
  }
  // Force cleanup
  try { process.kill(session.daemonPid); } catch {}
  deleteSession(sName);
  const lf = logFile(sName);
  if (existsSync(lf)) unlinkSync(lf);
  console.log(`Session '${sName}' closed (forced)`);
}

async function cmdScreenshot(parsed) {
  if (!parsed.args[0]) throw new Error('Usage: screenshot <file>');
  const session = readSession(parsed.session);
  const result = await sendToDaemon(session, 'screenshot', {
    file: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(result.path);
}

async function cmdEval(parsed) {
  if (!parsed.args[0]) throw new Error('Empty expression');
  const session = readSession(parsed.session);
  const result = await sendToDaemon(session, 'eval', {
    expression: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(JSON.stringify(result.value));
}

async function cmdText(parsed) {
  if (!parsed.args[0]) throw new Error('Usage: text <selector>');
  const session = readSession(parsed.session);
  const result = await sendToDaemon(session, 'text', {
    selector: parsed.args[0], page: parsed.page, target: parsed.target, ext: parsed.ext,
  });
  console.log(result.text);
}

async function cmdClick(parsed) {
  if (!parsed.args[0]) throw new Error('Empty selector');
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'click', {
    selector: parsed.args[0], right: parsed.right, page: parsed.page,
    target: parsed.target, ext: parsed.ext,
  });
}

async function cmdType(parsed) {
  if (!parsed.args[0] || parsed.args[1] === undefined) throw new Error('Usage: type <selector> <text>');
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'type', {
    selector: parsed.args[0], text: parsed.args[1], page: parsed.page,
    target: parsed.target, ext: parsed.ext,
  });
}

async function cmdSelect(parsed) {
  if (!parsed.args[0] || parsed.args[1] === undefined) throw new Error('Usage: select <selector> <value>');
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'select', {
    selector: parsed.args[0], value: parsed.args[1], page: parsed.page,
    target: parsed.target, ext: parsed.ext,
  });
}

async function cmdKey(parsed) {
  if (!parsed.args[0]) throw new Error('Empty key combo');
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'key', { combo: parsed.args[0], page: parsed.page });
}

async function cmdMouse(parsed) {
  const event = parsed.args[0];
  const x = parseFloat(parsed.args[1]);
  const y = parseFloat(parsed.args[2]);
  if (!VALID_MOUSE_EVENTS.includes(event)) throw new Error('Invalid mouse event. Must be: mousedown, mousemove, mouseup');
  if (isNaN(x) || isNaN(y) || x < 0 || y < 0) throw new Error('Invalid coordinates');
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'mouse', { event, x, y, page: parsed.page, target: parsed.target, ext: parsed.ext });
}

async function cmdCommand(parsed) {
  if (!parsed.args[0]) throw new Error('Empty command');
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'command', { text: parsed.args[0] });
}

async function cmdWait(parsed) {
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'wait', { timeout: parsed.timeout, ext: parsed.ext, target: parsed.target });
  console.log('Webview ready');
}

async function cmdConnect(parsed) {
  const session = readSession(parsed.session);
  const result = await sendToDaemon(session, 'connect', {});
  if (result.targets.length === 0) {
    throw new Error('No webview found. Is a webview panel open?');
  }
  console.log(`Found ${result.targets.length} webview target(s):`);
  result.targets.forEach((t, i) => {
    const activeMarker = t.visible ? ' *' : '';
    console.log(`  ${i}: ${t.ext} — ${t.title}${activeMarker}`);
  });
}

async function cmdLogs(parsed) {
  const session = readSession(parsed.session);
  const result = await sendToDaemon(session, 'logs', { channel: parsed.channel, level: parsed.level });
  console.log(result.logs);
}

async function cmdNotebook(parsed) {
  const session = readSession(parsed.session);
  await sendToDaemon(session, 'notebook', { subcommand: parsed.subcommand });
  console.log(`notebook ${parsed.subcommand}: done`);
}

// ── Main dispatch ──────────────────────────────────────────────────

async function cmdDownload(parsed) {
  if (parsed.args.length < 2) throw new Error('Usage: download <publisher.extension-name> <version>');
  const extensionId = parsed.args[0];
  const version = parsed.args[1];
  const [publisher, name] = extensionId.includes('.') ? extensionId.split('.', 2) : [null, null];
  if (!publisher || !name) throw new Error('Extension ID must be in format: publisher.extension-name');

  const url = `https://marketplace.visualstudio.com/_apis/public/gallery/publishers/${publisher}/vsextensions/${name}/${version}/vspackage`;
  const outputFile = resolve(`${publisher}.${name}-${version}.vsix`);

  console.log(`Downloading ${extensionId} v${version}...`);
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Download failed (HTTP ${res.status}). Check extension ID and version.`);

  const buffer = Buffer.from(await res.arrayBuffer());
  writeFileSync(outputFile, buffer);
  console.log(`Saved: ${outputFile}`);
}

async function main() {
  // Daemon mode
  if (process.argv.includes('--daemon')) {
    const daemonIdx = process.argv.indexOf('--daemon');
    const workspace = process.argv[daemonIdx + 1] || process.cwd();
    // Parse --session and --vsix from daemon args
    let sessionName = 'default', vsixPath = null;
    for (let i = 0; i < process.argv.length; i++) {
      if (process.argv[i] === '--session' && i + 1 < process.argv.length) sessionName = process.argv[++i];
      if (process.argv[i] === '--vsix' && i + 1 < process.argv.length) vsixPath = process.argv[++i];
    }
    await runDaemon(workspace, sessionName, vsixPath);
    return;
  }

  // Caller mode
  const parsed = parseArgs(process.argv.slice(2));
  switch (parsed.command) {
    case 'launch': await cmdLaunch(parsed); break;
    case 'close': await cmdClose(parsed); break;
    case 'connect': await cmdConnect(parsed); break;
    case 'download': await cmdDownload(parsed); break;
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
    case 'notebook': await cmdNotebook(parsed); break;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === __filename) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
