import { readFileSync, writeFileSync, unlinkSync, existsSync, openSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { spawn as spawnProcess, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:http';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const SESSION_FILE = resolve(__dirname, '.tui-verify-session.json');
const LOG_FILE = resolve(__dirname, '.tui-verify-daemon.log');

export const ROWS = 30;
export const COLS = 120;

const VALID_COMMANDS = ['launch', 'close', 'screenshot', 'key', 'type', 'text', 'wait', 'rows'];
const VALID_MODIFIERS = ['ctrl', 'alt', 'shift'];

// ── Pure functions (exported for testing) ────────────────────────────

export function parseArgs(argv) {
  if (!argv.length) throw new Error('No command provided');
  const command = argv[0];
  if (!VALID_COMMANDS.includes(command)) throw new Error(`Unknown command: ${command}`);

  if (command === 'launch') {
    let build = false;
    for (const arg of argv.slice(1)) {
      if (arg === '--build') build = true;
    }
    return { command, build };
  }

  if (command === 'close' || command === 'rows') {
    return { command };
  }

  if (command === 'text') {
    const rowStr = argv[1];
    if (rowStr === undefined) throw new Error('text requires a row number');
    const row = parseInt(rowStr, 10);
    if (isNaN(row) || row < 0 || row >= ROWS) throw new Error(`Row ${rowStr} out of range (0-${ROWS - 1})`);
    return { command, row };
  }

  if (command === 'key') {
    const combo = argv[1];
    if (!combo) throw new Error('key requires a key combo');
    return { command, combo };
  }

  if (command === 'type') {
    const text = argv[1];
    if (text === undefined) throw new Error('type requires text');
    return { command, text };
  }

  if (command === 'wait') {
    const text = argv[1];
    if (text === undefined) throw new Error('wait requires text');
    const timeout = argv[2] !== undefined ? parseInt(argv[2], 10) : 10000;
    if (timeout <= 0) throw new Error('Invalid timeout');
    return { command, text, timeout };
  }

  if (command === 'screenshot') {
    const file = argv[1];
    if (!file) throw new Error('screenshot requires a file path');
    return { command, file };
  }

  return { command };
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
  return { key, modifiers };
}

// ── Session file I/O ─────────────────────────────────────────────────

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

// ── HTTP Client (caller mode) ────────────────────────────────────────

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

// ── Daemon Mode ──────────────────────────────────────────────────────

async function runDaemon() {
  const { EventEmitter } = await import('node:events');
  const traceEmitter = new EventEmitter();

  // Dynamic import — callers don't need tui-test
  const tuiTest = await import('@microsoft/tui-test/lib/terminal/term.js');

  const repoRoot = resolve(__dirname, '..', '..', '..');
  const exe = resolve(repoRoot, 'src/PPDS.Cli/bin/Debug/net10.0/ppds.exe');

  const terminal = await tuiTest.spawn(
    { rows: ROWS, cols: COLS, program: { file: exe, args: ['tui'] } },
    false,
    traceEmitter,
  );

  // Track PTY exit
  let exited = false;
  terminal.onExit(({ exitCode, signal }) => {
    exited = true;
    console.error(`PTY exited: code=${exitCode} signal=${signal}`);
  });

  // Wait for terminal to render (poll for non-empty content, 30s timeout)
  const renderStart = Date.now();
  while (Date.now() - renderStart < 30000) {
    const buf = terminal.getViewableBuffer();
    const hasContent = buf.some(row => row.some(cell => cell.trim() !== ''));
    if (hasContent) break;
    await new Promise(r => setTimeout(r, 250));
  }

  // ── sendKey helper ───────────────────────────────────────────────

  function sendKey(term, parsed) {
    const { key, modifiers } = parsed;
    const lk = key.toLowerCase();

    // ctrl combos
    if (modifiers.ctrl) {
      if (lk === 'c') { term.keyCtrlC(); return; }
      if (lk === 'd') { term.keyCtrlD(); return; }
      // ctrl+letter → write control character
      const code = lk.charCodeAt(0);
      if (code >= 97 && code <= 122) {
        term.write(String.fromCharCode(code - 96));
        return;
      }
    }

    // alt combos
    if (modifiers.alt) {
      term.write('\x1b' + key);
      return;
    }

    // Named keys
    const namedKeys = {
      enter: () => term.submit(),
      tab: () => term.write('\t'),
      escape: () => term.keyEscape(),
      up: () => term.keyUp(),
      down: () => term.keyDown(),
      left: () => term.keyLeft(),
      right: () => term.keyRight(),
      backspace: () => term.keyBackspace(),
      delete: () => term.keyDelete(),
      space: () => term.write(' '),
    };

    if (namedKeys[lk]) {
      namedKeys[lk]();
      return;
    }

    // Function keys F1-F12 (VT220 escape sequences)
    const fMatch = key.match(/^[Ff](\d+)$/);
    if (fMatch) {
      const fNum = parseInt(fMatch[1], 10);
      const fSeqs = {
        1: '\x1bOP', 2: '\x1bOQ', 3: '\x1bOR', 4: '\x1bOS',
        5: '\x1b[15~', 6: '\x1b[17~', 7: '\x1b[18~', 8: '\x1b[19~',
        9: '\x1b[20~', 10: '\x1b[21~', 11: '\x1b[23~', 12: '\x1b[24~',
      };
      if (fSeqs[fNum]) {
        term.write(fSeqs[fNum]);
        return;
      }
    }

    // Single character fallback
    term.write(key);
  }

  // ── Action handler ───────────────────────────────────────────────

  async function handleAction(action, params) {
    if (exited) throw new Error('Terminal has exited');

    switch (action) {
      case 'text': {
        const row = params.row;
        const buf = terminal.getViewableBuffer();
        if (row < 0 || row >= buf.length) throw new Error(`Row ${row} out of range`);
        const text = buf[row].join('').trimEnd();
        return { text };
      }
      case 'key': {
        const parsed = parseKeyCombo(params.combo);
        sendKey(terminal, parsed);
        await new Promise(r => setTimeout(r, 100)); // 100ms settle
        return {};
      }
      case 'type': {
        const chars = params.text.split('');
        for (const ch of chars) {
          terminal.write(ch);
          await new Promise(r => setTimeout(r, 30)); // 30ms per char
        }
        return {};
      }
      case 'wait': {
        const timeout = params.timeout ?? 10000;
        const start = Date.now();
        while (Date.now() - start < timeout) {
          const buf = terminal.getViewableBuffer();
          for (const row of buf) {
            const line = row.join('');
            if (line.includes(params.text)) return {};
          }
          await new Promise(r => setTimeout(r, 250));
        }
        throw new Error(`Timeout: '${params.text}' not found within ${(params.timeout ?? 10000) / 1000}s`);
      }
      case 'screenshot': {
        const snapshot = terminal.serialize();
        // Convert Map to plain object for JSON serialization
        const shifts = {};
        if (snapshot.shifts instanceof Map) {
          for (const [k, v] of snapshot.shifts) {
            shifts[k] = v;
          }
        } else {
          Object.assign(shifts, snapshot.shifts);
        }
        const data = { view: snapshot.view, shifts };
        writeFileSync(resolve(params.file), JSON.stringify(data, null, 2));
        return { path: resolve(params.file) };
      }
      case 'rows': {
        return { dimensions: `${COLS}x${ROWS}` };
      }
      default:
        throw new Error(`Unknown action: ${action}`);
    }
  }

  // ── HTTP server ──────────────────────────────────────────────────

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

  writeSession({ daemonPort, daemonPid: process.pid, logFile: LOG_FILE });
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

// ── Caller command handlers ──────────────────────────────────────────

async function cmdLaunch(parsed) {
  if (parsed.build) {
    const repoRoot = resolve(__dirname, '..', '..', '..');
    console.error('Building PPDS CLI...');
    try {
      execFileSync('dotnet', ['build', 'src/PPDS.Cli/PPDS.Cli.csproj', '-f', 'net10.0', '-v', 'q'], {
        cwd: repoRoot,
        stdio: 'inherit',
      });
    } catch {
      throw new Error('Build failed — fix compilation errors above');
    }
    console.error('Build complete');
  }

  // Check for stale session
  if (existsSync(SESSION_FILE)) {
    const session = JSON.parse(readFileSync(SESSION_FILE, 'utf-8'));
    try {
      await fetch(`http://localhost:${session.daemonPort}/health`);
      console.error('TUI is already running. Run `close` first.');
      return;
    } catch {
      // Stale session — clean up
      try { process.kill(session.daemonPid); } catch {}
      deleteSession();
    }
  }

  // Fork daemon with stderr redirected to log file
  const logFd = openSync(LOG_FILE, 'w');
  const child = spawnProcess(process.execPath, [__filename, '--daemon'], {
    detached: true,
    stdio: ['ignore', 'ignore', logFd],
  });
  child.unref();

  // Poll for session file (30s timeout)
  const start = Date.now();
  while (Date.now() - start < 30000) {
    if (existsSync(SESSION_FILE)) {
      const session = readSession();
      console.error(`TUI launched (daemon PID ${session.daemonPid}, port ${session.daemonPort})`);
      return;
    }
    await new Promise(r => setTimeout(r, 500));
  }

  // Timeout — print last 20 lines of daemon log
  if (existsSync(LOG_FILE)) {
    const logContent = readFileSync(LOG_FILE, 'utf-8');
    const lines = logContent.split('\n');
    const tail = lines.slice(-20).join('\n');
    console.error('Daemon log (last 20 lines):\n' + tail);
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

  // Wait for session file deletion
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
  console.log('Found');
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

// ── Main dispatch ────────────────────────────────────────────────────

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

// Entry guard: Windows case-insensitive path comparison
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === __filename.toLowerCase()) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
