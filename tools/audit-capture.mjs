#!/usr/bin/env node
// audit-capture — manifest-driven capture runner for PPDS surfaces.
//
// Reads tools/audit-manifests/{surface}.yaml, drives tui-verify / webview-cdp
// to reach each screen, writes PNG + meta.json + manifest.json to $AUDIT_OUT
// conforming to ppds-design-system/AUDIT-SCHEMA.md v1.
//
// See specs/audit-capture.md for the contract this implements.

import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { resolve, join, dirname, isAbsolute, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawnSync } from 'node:child_process';
import YAML from 'yaml';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const REPO_ROOT = resolve(__dirname, '..');

const SCHEMA_VERSION = 1;
const RUNNER_VERSION = '1.0.0';

const SURFACES = ['tui', 'extension'];
const MANIFEST_DIR = join(REPO_ROOT, 'tools', 'audit-manifests');

const TUI_VERIFY = join(REPO_ROOT, 'tests', 'PPDS.Tui.E2eTests', 'tools', 'tui-verify.mjs');
const WEBVIEW_CDP = join(REPO_ROOT, 'src', 'PPDS.Extension', 'tools', 'webview-cdp.mjs');
const WEBVIEW_PROFILE_DIR = join(REPO_ROOT, 'src', 'PPDS.Extension', 'tools', '.webview-cdp-profile');

const EXT_THEME = 'Default Dark+';
const EXT_ID = 'power-platform-developer-suite';

// ── Pure utilities (exported for testing) ───────────────────────────────

export function parseArgs(argv) {
  if (argv.length === 0) throw new Error('Usage: audit-capture <run|validate|list> <surface>');
  const command = argv[0];
  const valid = ['run', 'validate', 'list'];
  if (!valid.includes(command)) throw new Error(`Unknown command: ${command}. Expected one of: ${valid.join(', ')}`);

  const surface = argv[1];
  if (!surface) throw new Error(`Usage: audit-capture ${command} <surface>`);
  const allSurfaces = [...SURFACES, 'all'];
  if (!allSurfaces.includes(surface)) {
    throw new Error(`Unknown surface: ${surface}. Expected one of: ${allSurfaces.join(', ')}`);
  }
  if ((command === 'validate' || command === 'list') && surface === 'all') {
    throw new Error(`${command} does not support surface=all`);
  }
  return { command, surface };
}

export function validateEntryId(id) {
  if (typeof id !== 'string' || id.length === 0) throw new Error('Entry id must be a non-empty string');
  if (!/^[a-z0-9][a-z0-9-]*$/.test(id)) {
    throw new Error(`Invalid entry id '${id}': must be kebab-case ASCII (lowercase, digits, hyphens)`);
  }
}

export function validateScreenshotName(name) {
  if (!/^\d{2}-[a-z0-9][a-z0-9-]*$/.test(name)) {
    throw new Error(`Invalid screenshot name '${name}': must match NN-kebab-name pattern (e.g. 01-empty)`);
  }
}

export function validateManifest(raw, surface) {
  if (!raw || typeof raw !== 'object') throw new Error('Manifest must be a YAML mapping');
  if (raw.surface !== surface) throw new Error(`Manifest surface '${raw.surface}' does not match requested '${surface}'`);
  if (!Array.isArray(raw.entries)) throw new Error('Manifest.entries must be an array');

  const ids = new Set();
  for (let i = 0; i < raw.entries.length; i++) {
    const e = raw.entries[i];
    if (!e || typeof e !== 'object') throw new Error(`entries[${i}]: must be a mapping`);
    validateEntryId(e.id);
    if (ids.has(e.id)) throw new Error(`Duplicate entry id: '${e.id}'`);
    ids.add(e.id);
    if (typeof e.title !== 'string' || !e.title) throw new Error(`entries[${i}].title is required`);
    if (e.requires && !['connected', 'none'].includes(e.requires)) {
      throw new Error(`entries[${i}].requires: must be 'connected' or 'none'`);
    }
    if (!Array.isArray(e.steps)) throw new Error(`entries[${i}].steps: must be an array`);

    const shotNames = new Set();
    for (let j = 0; j < e.steps.length; j++) {
      const step = e.steps[j];
      validateStep(step, surface, `entries[${i}].steps[${j}]`);
      if (step.screenshot) {
        validateScreenshotName(step.screenshot);
        if (shotNames.has(step.screenshot)) {
          throw new Error(`Duplicate screenshot name '${step.screenshot}' in entry '${e.id}'`);
        }
        shotNames.add(step.screenshot);
      }
    }
    if (shotNames.size === 0) {
      throw new Error(`entries[${i}] ('${e.id}') has no screenshot steps — every entry must capture at least one PNG`);
    }

    if (e.masks) validateMasks(e.masks, surface, `entries[${i}].masks`);
  }
  return raw;
}

function validateStep(step, surface, path) {
  if (!step || typeof step !== 'object') throw new Error(`${path}: must be a mapping`);
  const keys = Object.keys(step);
  if (keys.length === 0) throw new Error(`${path}: empty step`);

  // Exactly one primary key per step
  const primaryKeys = ['key', 'type', 'wait', 'screenshot', 'sleep', 'click', 'eval', 'command'];
  const primary = keys.filter(k => primaryKeys.includes(k));
  if (primary.length !== 1) {
    throw new Error(`${path}: expected exactly one of ${primaryKeys.join(', ')}, got ${primary.length}`);
  }
  const k = primary[0];

  if (surface === 'tui' && ['click', 'eval', 'command'].includes(k)) {
    throw new Error(`${path}: step '${k}' is not supported on TUI surface`);
  }

  switch (k) {
    case 'key':
      if (typeof step.key !== 'string' || !step.key) throw new Error(`${path}.key: must be a non-empty string`);
      break;
    case 'type':
      if (typeof step.type !== 'string') throw new Error(`${path}.type: must be a string`);
      break;
    case 'wait':
      if (!step.wait || typeof step.wait !== 'object') throw new Error(`${path}.wait: must be a mapping`);
      if (surface === 'tui') {
        if (typeof step.wait.text !== 'string' || !step.wait.text) throw new Error(`${path}.wait.text: required on TUI`);
      } else {
        if (step.wait.text && typeof step.wait.text !== 'string') throw new Error(`${path}.wait.text must be a string`);
        if (step.wait.ext && typeof step.wait.ext !== 'string') throw new Error(`${path}.wait.ext must be a string`);
      }
      if (step.wait.timeout !== undefined && (typeof step.wait.timeout !== 'number' || step.wait.timeout <= 0)) {
        throw new Error(`${path}.wait.timeout: must be a positive number`);
      }
      break;
    case 'screenshot':
      if (typeof step.screenshot !== 'string') throw new Error(`${path}.screenshot: must be a string`);
      break;
    case 'sleep':
      if (typeof step.sleep !== 'number' || step.sleep <= 0) throw new Error(`${path}.sleep: must be a positive number`);
      break;
    case 'click':
      if (typeof step.click !== 'string' || !step.click) throw new Error(`${path}.click: must be a non-empty selector`);
      break;
    case 'eval':
      if (typeof step.eval !== 'string' || !step.eval) throw new Error(`${path}.eval: must be a non-empty expression`);
      break;
    case 'command':
      if (typeof step.command !== 'string' || !step.command) throw new Error(`${path}.command: must be a non-empty string`);
      break;
  }
}

function validateMasks(masks, surface, path) {
  if (!Array.isArray(masks)) throw new Error(`${path}: must be an array`);
  for (let i = 0; i < masks.length; i++) {
    const m = masks[i];
    if (!m || typeof m !== 'object') throw new Error(`${path}[${i}]: must be a mapping`);
    if (typeof m.reason !== 'string' || !m.reason) throw new Error(`${path}[${i}].reason: required`);
    if (surface === 'tui') {
      if (!Number.isInteger(m.row) || m.row < 0 || m.row > 29) throw new Error(`${path}[${i}].row: 0-29 required`);
      if (!Number.isInteger(m.colStart) || m.colStart < 0 || m.colStart > 120) throw new Error(`${path}[${i}].colStart: 0-120`);
      if (!Number.isInteger(m.colEnd) || m.colEnd < m.colStart || m.colEnd > 120) throw new Error(`${path}[${i}].colEnd: >= colStart and <= 120`);
    } else {
      for (const f of ['x', 'y', 'width', 'height']) {
        if (!Number.isInteger(m[f]) || m[f] < 0) throw new Error(`${path}[${i}].${f}: non-negative integer required`);
      }
    }
  }
}

export function validateAuditOut(auditOut, repoRoot) {
  if (!auditOut) throw new Error('AUDIT_OUT env var is required');
  if (!isAbsolute(auditOut)) throw new Error(`AUDIT_OUT must be absolute: ${auditOut}`);
  const rel = relative(repoRoot, auditOut);
  // relative("/repo", "/repo") => ""           (same)    → reject
  // relative("/repo", "/repo/out") => "out"    (inside)  → reject
  // relative("/repo", "/tmp/out") => "../tmp/out" (outside) → accept
  if (!rel.startsWith('..')) {
    throw new Error(`AUDIT_OUT must be outside the repo working tree: ${auditOut}`);
  }
}

export function sourceInfo() {
  const env = process.env;
  const repo = env.AUDIT_SOURCE_REPO || detectRepoFromRemote();
  const ref = env.AUDIT_SOURCE_REF || detectRef();
  const commit = env.AUDIT_SOURCE_COMMIT || detectCommit();
  return { repo, ref, commit, runner: `audit-capture@${RUNNER_VERSION}` };
}

function gitSync(...args) {
  const res = spawnSync('git', args, { cwd: REPO_ROOT, encoding: 'utf8' });
  if (res.status !== 0) return '';
  return res.stdout.trim();
}

function detectRepoFromRemote() {
  const url = gitSync('config', '--get', 'remote.origin.url');
  if (!url) return 'unknown/unknown';
  const m = url.match(/[:/]([^/]+)\/([^/]+?)(?:\.git)?$/);
  return m ? `${m[1]}/${m[2]}` : url;
}

function detectRef() { return gitSync('symbolic-ref', 'HEAD') || 'refs/heads/HEAD'; }
function detectCommit() { return gitSync('rev-parse', 'HEAD'); }

// ── Manifest loader ─────────────────────────────────────────────────────

function loadManifest(surface) {
  const path = join(MANIFEST_DIR, `${surface}.yaml`);
  if (!existsSync(path)) throw new Error(`Manifest not found: ${path}`);
  let raw;
  try {
    raw = YAML.parse(readFileSync(path, 'utf8'));
  } catch (err) {
    throw new Error(`Manifest parse error (${path}): ${err.message}`);
  }
  return validateManifest(raw, surface);
}

// ── Verify-tool shelling ────────────────────────────────────────────────

function runVerify(tool, args, opts = {}) {
  const res = spawnSync(process.execPath, [tool, ...args], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    timeout: opts.timeout || 120000,
  });
  return {
    status: res.status,
    stdout: res.stdout || '',
    stderr: res.stderr || '',
  };
}

function tuiCmd(args, opts) { return runVerify(TUI_VERIFY, args, opts); }
function extCmd(args, opts) { return runVerify(WEBVIEW_CDP, args, opts); }

// ── Surface: TUI ─────────────────────────────────────────────────────────

async function runTui(manifest, auditOut, cfg) {
  const surfaceDir = join(auditOut, 'tui');
  // Clean any prior captures, then recreate fresh.
  rmSync(surfaceDir, { recursive: true, force: true });
  mkdirSync(surfaceDir, { recursive: true });

  // Ensure fresh daemon
  tuiCmd(['close']);
  const launch = tuiCmd(['launch', '--build'], { timeout: 180000 });
  if (launch.status !== 0) {
    throw new Error(`tui-verify launch failed: ${launch.stderr}`);
  }
  const splash = tuiCmd(['wait', 'PPDS', '15000']);
  if (splash.status !== 0) {
    tuiCmd(['close']);
    throw new Error(`TUI did not reach splash: ${splash.stderr}`);
  }

  const entries = [];
  let exitCode = 0;

  for (const entry of manifest.entries) {
    const entryDir = join(surfaceDir, entry.id);
    const skipForRequires = entry.requires === 'connected' && !(cfg.profile && cfg.env);
    if (skipForRequires) {
      entries.push({
        id: entry.id,
        title: entry.title,
        state: 'skipped',
        screenshots: [],
        skipReason: `requires: connected (PPDS_PROFILE=${cfg.profile || 'unset'}, PPDS_ENV=${cfg.env || 'unset'})`,
      });
      continue;
    }

    try {
      mkdirSync(entryDir, { recursive: true });
      await navigateToTuiBaseline();
      const result = await runTuiEntry(entry, entryDir);
      entries.push(result);
    } catch (err) {
      exitCode = 1;
      entries.push({
        id: entry.id,
        title: entry.title,
        state: 'error',
        screenshots: [],
        error: err.message,
        stderr: (err.stderr || '').slice(0, 4096),
      });
      // Attempt recovery for next entry
      tuiCmd(['close']);
      const r = tuiCmd(['launch'], { timeout: 60000 });
      if (r.status !== 0) break; // give up on TUI surface
      tuiCmd(['wait', 'PPDS', '15000']);
    }
  }

  tuiCmd(['close']);
  return { entries, exitCode };
}

async function navigateToTuiBaseline() {
  // Best-effort reset: repeated Escape + Ctrl+Home to collapse dialogs/menus.
  tuiCmd(['key', 'escape']);
  tuiCmd(['key', 'escape']);
}

async function runTuiEntry(entry, entryDir) {
  const stepsLog = [];
  const screenshots = [];

  for (const step of entry.steps) {
    if (step.key !== undefined) {
      const r = tuiCmd(['key', step.key]);
      if (r.status !== 0) throw withStderr(new Error(`key '${step.key}' failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ key: step.key });
    } else if (step.type !== undefined) {
      const r = tuiCmd(['type', step.type]);
      if (r.status !== 0) throw withStderr(new Error(`type failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ type: step.type });
    } else if (step.wait !== undefined) {
      const timeout = step.wait.timeout || 10000;
      const r = tuiCmd(['wait', step.wait.text, String(timeout)]);
      if (r.status !== 0) throw withStderr(new Error(`wait '${step.wait.text}' failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ wait: { text: step.wait.text, timeout } });
    } else if (step.sleep !== undefined) {
      await new Promise(r => setTimeout(r, step.sleep));
      stepsLog.push({ sleep: step.sleep });
    } else if (step.screenshot !== undefined) {
      const outFile = join(entryDir, `${step.screenshot}.png`);
      const r = tuiCmd(['render', outFile], { timeout: 60000 });
      if (r.status !== 0) throw withStderr(new Error(`render failed: ${r.stderr}`), r.stderr);
      // render now returns { path, serialize: { view, shifts } } as JSON on stdout —
      // reuse that instead of a second `screenshot` shell-out.
      let serialize = null;
      try { serialize = JSON.parse(r.stdout).serialize ?? null; } catch {}
      if (entry.masks && entry.masks.length > 0) {
        await applyTuiMasks(outFile, entry.masks);
      }
      const { PNG } = await import('pngjs');
      const png = PNG.sync.read(readFileSync(outFile));
      screenshots.push({
        name: step.screenshot,
        path: `tui/${entry.id}/${step.screenshot}.png`,
        dimensions: { width: png.width, height: png.height },
        dpr: 2.0,
        theme: 'dark',
        _serialize: serialize,
      });
      stepsLog.push({ screenshot: step.screenshot });
    }
  }

  // Per-entry meta.json — include last serialize dump (richest state).
  const lastSerialize = screenshots.length > 0 ? screenshots[screenshots.length - 1]._serialize : null;
  const meta = {
    id: entry.id,
    surface: 'tui',
    title: entry.title,
    capturedAt: new Date().toISOString(),
    steps: stepsLog,
    masks: entry.masks || [],
    surfaceSpecific: {
      rows: 30,
      cols: 120,
      font: 'Cascadia Mono',
      fontSize: 16,
      theme: 'ppds-dark',
      serialize: lastSerialize,
    },
  };
  writeFileSync(join(entryDir, 'meta.json'), JSON.stringify(meta, null, 2));

  // Strip internal _serialize from the screenshots payload before returning.
  return {
    id: entry.id,
    title: entry.title,
    state: 'ok',
    screenshots: screenshots.map(({ _serialize, ...s }) => s),
    metaPath: `tui/${entry.id}/meta.json`,
  };
}

async function applyTuiMasks(pngPath, masks) {
  const { PNG } = await import('pngjs');
  const buf = readFileSync(pngPath);
  const png = PNG.sync.read(buf);
  // TUI cell-grid masking: convert cells → pixel rects.
  // Image is 120 cols × 30 rows cells at DPR 2.0; we can derive cell dims from image size.
  const cellW = png.width / 120;
  const cellH = png.height / 30;
  for (const m of masks) {
    const px = Math.round(m.colStart * cellW);
    const py = Math.round(m.row * cellH);
    const pw = Math.round((m.colEnd - m.colStart) * cellW);
    const ph = Math.round(cellH);
    fillRect(png, px, py, pw, ph, [0x1e, 0x1e, 0x1e, 0xff]);
  }
  writeFileSync(pngPath, PNG.sync.write(png));
}

function fillRect(png, x, y, w, h, [r, g, b, a]) {
  for (let yy = y; yy < y + h && yy < png.height; yy++) {
    for (let xx = x; xx < x + w && xx < png.width; xx++) {
      const idx = (png.width * yy + xx) << 2;
      png.data[idx] = r;
      png.data[idx + 1] = g;
      png.data[idx + 2] = b;
      png.data[idx + 3] = a;
    }
  }
}

function withStderr(err, stderr) { err.stderr = stderr; return err; }

// ── Surface: Extension ───────────────────────────────────────────────────

async function runExtension(manifest, auditOut, cfg) {
  const surfaceDir = join(auditOut, 'extension');
  // Clean any prior captures, then recreate fresh.
  rmSync(surfaceDir, { recursive: true, force: true });
  mkdirSync(surfaceDir, { recursive: true });

  // Pin VS Code theme via profile settings.json
  ensureThemePin(WEBVIEW_PROFILE_DIR, EXT_THEME);

  extCmd(['close']);
  const launch = extCmd(['launch', '--build'], { timeout: 300000 });
  if (launch.status !== 0) {
    throw new Error(`webview-cdp launch failed: ${launch.stderr}`);
  }

  const entries = [];
  let exitCode = 0;

  for (const entry of manifest.entries) {
    const entryDir = join(surfaceDir, entry.id);
    const skipForRequires = entry.requires === 'connected' && !(cfg.profile && cfg.env);
    if (skipForRequires) {
      entries.push({
        id: entry.id,
        title: entry.title,
        state: 'skipped',
        screenshots: [],
        skipReason: `requires: connected (PPDS_PROFILE=${cfg.profile || 'unset'}, PPDS_ENV=${cfg.env || 'unset'})`,
      });
      continue;
    }

    try {
      mkdirSync(entryDir, { recursive: true });
      const result = await runExtensionEntry(entry, entryDir);
      entries.push(result);
    } catch (err) {
      exitCode = 1;
      entries.push({
        id: entry.id,
        title: entry.title,
        state: 'error',
        screenshots: [],
        error: err.message,
        stderr: (err.stderr || '').slice(0, 4096),
      });
    }
  }

  extCmd(['close']);
  return { entries, exitCode };
}

function ensureThemePin(profileDir, themeId) {
  const userDir = join(profileDir, 'User');
  mkdirSync(userDir, { recursive: true });
  const settingsPath = join(userDir, 'settings.json');
  let current = {};
  if (existsSync(settingsPath)) {
    try { current = JSON.parse(readFileSync(settingsPath, 'utf8')); } catch { current = {}; }
  }
  current['workbench.colorTheme'] = themeId;
  writeFileSync(settingsPath, JSON.stringify(current, null, 2));
}

async function runExtensionEntry(entry, entryDir) {
  const stepsLog = [];
  const screenshots = [];
  let commandInvoked = null;

  for (const step of entry.steps) {
    if (step.command !== undefined) {
      const r = extCmd(['command', step.command], { timeout: 30000 });
      if (r.status !== 0) throw withStderr(new Error(`command '${step.command}' failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ command: step.command });
      if (!commandInvoked) commandInvoked = step.command;
    } else if (step.wait !== undefined) {
      const args = ['wait'];
      if (step.wait.timeout) args.push(String(step.wait.timeout));
      if (step.wait.ext) args.push('--ext', step.wait.ext);
      const r = extCmd(args, { timeout: (step.wait.timeout || 30000) + 10000 });
      if (r.status !== 0) throw withStderr(new Error(`wait failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ wait: step.wait });
    } else if (step.click !== undefined) {
      const args = ['click', step.click];
      if (step.ext) args.push('--ext', step.ext);
      const r = extCmd(args);
      if (r.status !== 0) throw withStderr(new Error(`click '${step.click}' failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ click: step.click, ext: step.ext });
    } else if (step.eval !== undefined) {
      const r = extCmd(['eval', step.eval]);
      if (r.status !== 0) throw withStderr(new Error(`eval failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ eval: step.eval });
    } else if (step.key !== undefined) {
      const r = extCmd(['key', step.key, '--page']);
      if (r.status !== 0) throw withStderr(new Error(`key '${step.key}' failed: ${r.stderr}`), r.stderr);
      stepsLog.push({ key: step.key });
    } else if (step.sleep !== undefined) {
      await new Promise(r => setTimeout(r, step.sleep));
      stepsLog.push({ sleep: step.sleep });
    } else if (step.screenshot !== undefined) {
      const outFile = join(entryDir, `${step.screenshot}.png`);
      const r = extCmd(['screenshot', outFile], { timeout: 30000 });
      if (r.status !== 0) throw withStderr(new Error(`screenshot failed: ${r.stderr}`), r.stderr);
      if (entry.masks && entry.masks.length > 0) await applyExtMasks(outFile, entry.masks);
      const { PNG } = await import('pngjs');
      const png = PNG.sync.read(readFileSync(outFile));
      screenshots.push({
        name: step.screenshot,
        path: `extension/${entry.id}/${step.screenshot}.png`,
        dimensions: { width: png.width, height: png.height },
        dpr: 2.0,
        theme: 'dark',
      });
      stepsLog.push({ screenshot: step.screenshot });
    }
  }

  const meta = {
    id: entry.id,
    surface: 'extension',
    title: entry.title,
    capturedAt: new Date().toISOString(),
    steps: stepsLog,
    masks: entry.masks || [],
    surfaceSpecific: {
      vscodeTheme: EXT_THEME,
      extensionId: EXT_ID,
      panel: commandInvoked,
      commandInvoked,
    },
  };
  writeFileSync(join(entryDir, 'meta.json'), JSON.stringify(meta, null, 2));

  return {
    id: entry.id,
    title: entry.title,
    state: 'ok',
    screenshots,
    metaPath: `extension/${entry.id}/meta.json`,
  };
}

async function applyExtMasks(pngPath, masks) {
  const { PNG } = await import('pngjs');
  const png = PNG.sync.read(readFileSync(pngPath));
  for (const m of masks) {
    fillRect(png, m.x, m.y, m.width, m.height, [0x1e, 0x1e, 0x1e, 0xff]);
  }
  writeFileSync(pngPath, PNG.sync.write(png));
}

// ── Validate (dry-run) ──────────────────────────────────────────────────

async function validateSurface(surface) {
  const manifest = loadManifest(surface);
  console.log(`Manifest parsed: ${manifest.entries.length} entries`);
  for (const e of manifest.entries) {
    console.log(`  ${e.id} — ${e.title}${e.requires === 'connected' ? '  [requires connected]' : ''}`);
  }
  console.log('OK');
}

async function listSurface(surface) {
  const manifest = loadManifest(surface);
  for (const e of manifest.entries) {
    console.log(`${e.id}\t${e.title}`);
  }
}

// ── Run orchestration ──────────────────────────────────────────────────

async function doRun(surface) {
  const auditOut = process.env.AUDIT_OUT;
  validateAuditOut(auditOut, REPO_ROOT);
  mkdirSync(auditOut, { recursive: true });

  const cfg = {
    profile: process.env.PPDS_PROFILE || '',
    env: process.env.PPDS_ENV || '',
    redact: process.env.AUDIT_REDACT !== 'false',
  };

  const surfacesToRun = surface === 'all' ? SURFACES : [surface];
  const surfaceResults = {};
  let overallExit = 0;

  for (const s of surfacesToRun) {
    const manifest = loadManifest(s);
    console.error(`[${s}] capturing ${manifest.entries.length} entries...`);
    const handler = s === 'tui' ? runTui : runExtension;
    const res = await handler(manifest, auditOut, cfg);
    surfaceResults[s] = res.entries;
    if (res.exitCode !== 0) overallExit = res.exitCode;
  }

  // Write unified manifest LAST, after all entry dirs are flushed.
  const summary = { total: 0, ok: 0, error: 0, skipped: 0 };
  const surfaces = {};
  for (const [s, entries] of Object.entries(surfaceResults)) {
    surfaces[s] = { entries };
    for (const e of entries) {
      summary.total++;
      summary[e.state]++;
    }
  }

  const manifestObj = {
    schemaVersion: SCHEMA_VERSION,
    generatedAt: new Date().toISOString(),
    source: sourceInfo(),
    surfaces,
    summary,
  };
  writeFileSync(join(auditOut, 'manifest.json'), JSON.stringify(manifestObj, null, 2));
  console.error(`[done] ${summary.ok} ok, ${summary.error} error, ${summary.skipped} skipped → ${auditOut}`);

  if (summary.error > 0) overallExit = 1;
  return overallExit;
}

// ── Main dispatch ──────────────────────────────────────────────────────

async function main() {
  const parsed = parseArgs(process.argv.slice(2));

  switch (parsed.command) {
    case 'validate':
      await validateSurface(parsed.surface);
      break;
    case 'list':
      await listSurface(parsed.surface);
      break;
    case 'run': {
      const code = await doRun(parsed.surface);
      process.exit(code);
    }
  }
}

if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === __filename.toLowerCase()) {
  main().catch(err => {
    process.stderr.write(err.message + '\n');
    process.exit(1);
  });
}
