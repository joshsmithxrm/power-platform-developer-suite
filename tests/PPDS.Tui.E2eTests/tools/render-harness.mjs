// Headless Chromium + xterm.js render harness for tui-verify.
// Takes tui-test's serialize() output and produces a PNG that matches what a
// user would see in a 120×30 terminal with PPDS's default color scheme.
//
// Dep: @playwright/test (dev dep already, for Electron E2E), xterm (new, ~200 KB).

import { fileURLToPath } from 'node:url';
import { resolve, dirname, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

export const COLS = 120;
export const ROWS = 30;
export const DPR = 2.0;
export const FONT_SIZE = 16;

// Palette — sourced from ppds-design-system/colors_and_type.css.
// Dark-mode-first; 16 ANSI entries mapped to the design system's locked values.
// Background and foreground set to panel defaults; cursor matches foreground.
export const PPDS_THEME = {
  background: '#1e1e1e',
  foreground: '#f0f0f0',
  cursor: '#f0f0f0',
  cursorAccent: '#1e1e1e',
  selectionBackground: '#00cccc',
  selectionForeground: '#000000',
  black: '#1e1e1e',
  red: '#c00000',
  green: '#00a300',
  yellow: '#c5b000',
  blue: '#005aad',
  magenta: '#b23cc0',
  cyan: '#00cccc',
  white: '#c8c8c8',
  brightBlack: '#5c5c5c',
  brightRed: '#ff5555',
  brightGreen: '#33d955',
  brightYellow: '#f5e94a',
  brightBlue: '#4aa8ff',
  brightMagenta: '#e070ff',
  brightCyan: '#33ffff',
  brightWhite: '#ffffff',
};

// ── shifts → ANSI (exported for unit-testability) ───────────────────────

/**
 * Convert tui-test's serialize() output ({view, shifts}) into an SGR-annotated
 * replay stream suitable for term.write() on a fresh xterm.js instance.
 *
 * Algorithm: iterate row×col, emit an SGR escape only when the attribute set
 * differs from the previous cell. Reset + newline between rows. No cursor
 * positioning — we write rows linearly into a cleared terminal.
 */
export function shiftsToAnsi(view, shifts) {
  const rows = view.split('\n');
  const get = (r, c) => {
    if (shifts instanceof Map) return shifts.get(`${r},${c}`);
    return shifts[`${r},${c}`];
  };

  let out = '\x1b[0m';
  let prevSgr = '';
  for (let r = 0; r < rows.length; r++) {
    const line = rows[r];
    for (let c = 0; c < line.length; c++) {
      const attrs = get(r, c);
      const sgr = attrsToSgr(attrs);
      if (sgr !== prevSgr) {
        out += '\x1b[0m' + sgr;
        prevSgr = sgr;
      }
      out += line[c];
    }
    out += '\x1b[0m\r\n';
    prevSgr = '';
  }
  return out;
}

/**
 * Map a single tui-test CellShift object to an SGR escape sequence.
 * Empty shift or null/undefined → '' (default attributes).
 */
export function attrsToSgr(attrs) {
  if (!attrs) return '';
  const codes = [];
  if (attrs.bold) codes.push(1);
  if (attrs.dim) codes.push(2);
  if (attrs.italic) codes.push(3);
  if (attrs.underline) codes.push(4);
  if (attrs.blink) codes.push(5);
  if (attrs.inverse) codes.push(7);
  if (attrs.invisible) codes.push(8);
  if (attrs.strike) codes.push(9);
  if (attrs.overline) codes.push(53);

  // Foreground
  if (attrs.fgColorMode === 1) {
    // 16-color base: fgColor is 0-7 (normal) or 8-15 (bright)
    const fg = attrs.fgColor ?? 0;
    if (fg < 8) codes.push(30 + fg);
    else codes.push(90 + (fg - 8));
  } else if (attrs.fgColorMode === 2) {
    codes.push(38, 5, attrs.fgColor ?? 0);
  } else if (attrs.fgColorMode === 3) {
    const rgb = attrs.fgColor ?? 0;
    codes.push(38, 2, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
  }

  // Background
  if (attrs.bgColorMode === 1) {
    const bg = attrs.bgColor ?? 0;
    if (bg < 8) codes.push(40 + bg);
    else codes.push(100 + (bg - 8));
  } else if (attrs.bgColorMode === 2) {
    codes.push(48, 5, attrs.bgColor ?? 0);
  } else if (attrs.bgColorMode === 3) {
    const rgb = attrs.bgColor ?? 0;
    codes.push(48, 2, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
  }

  if (codes.length === 0) return '';
  return `\x1b[${codes.join(';')}m`;
}

// ── HTML shell served to the render page ────────────────────────────────

function findPackage(startDir, pkgName) {
  let dir = startDir;
  while (dir) {
    const candidate = join(dir, 'node_modules', pkgName);
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
  return null;
}

function loadXtermAssets() {
  const pkgDir = findPackage(__dirname, 'xterm');
  if (!pkgDir) {
    throw new Error('xterm package not installed. Run `npm install` in tests/PPDS.Tui.E2eTests first.');
  }
  const js = readFileSync(join(pkgDir, 'lib', 'xterm.js'), 'utf8');
  const css = readFileSync(join(pkgDir, 'css', 'xterm.css'), 'utf8');
  return { js, css };
}

function buildShellHtml() {
  const { js, css } = loadXtermAssets();
  return `<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>${css}</style>
<style>
  :root { color-scheme: dark; }
  html, body { margin: 0; padding: 0; background: ${PPDS_THEME.background}; }
  body { display: flex; align-items: flex-start; justify-content: flex-start; }
  #term { display: inline-block; }
  /* Remove padding/scrollbar artifacts */
  .xterm .xterm-viewport { overflow: hidden !important; }
  .xterm-scrollable-element { overflow: hidden !important; }
</style>
<script>${js}</script>
</head>
<body>
<div id="term"></div>
<script>
  const theme = ${JSON.stringify(PPDS_THEME)};
  const term = new Terminal({
    rows: ${ROWS},
    cols: ${COLS},
    fontFamily: 'Cascadia Mono, Cascadia Code, Consolas, "DejaVu Sans Mono", "Courier New", monospace',
    fontSize: ${FONT_SIZE},
    lineHeight: 1.2,
    theme,
    allowTransparency: false,
    cursorBlink: false,
    disableStdin: true,
    scrollback: 0,
    convertEol: false,
  });
  term.open(document.getElementById('term'));
  window.__writeBuffer = (stream) => new Promise((resolve) => {
    term.reset();
    term.write(stream, () => {
      // One more tick for the renderer to paint
      requestAnimationFrame(() => requestAnimationFrame(resolve));
    });
  });
  window.__measureCell = () => {
    const el = document.querySelector('#term .xterm-screen');
    const r = el.getBoundingClientRect();
    return { width: r.width, height: r.height };
  };
  window.__ready = true;
</script>
</body>
</html>`;
}

// ── RenderSession — encapsulates browser + page lifecycle ────────────────

export class RenderSession {
  constructor() {
    this._browser = null;
    this._context = null;
    this._page = null;
    this._chromium = null;
  }

  async ensure() {
    if (this._page) return;
    const { chromium } = await import('@playwright/test');
    this._chromium = chromium;
    this._browser = await chromium.launch({ headless: true });
    this._context = await this._browser.newContext({
      deviceScaleFactor: DPR,
      viewport: { width: COLS * FONT_SIZE, height: Math.ceil(ROWS * FONT_SIZE * 1.2) },
    });
    this._page = await this._context.newPage();
    await this._page.setContent(buildShellHtml(), { waitUntil: 'networkidle' });
    await this._page.waitForFunction(() => window.__ready === true, { timeout: 10000 });
  }

  async writeAndScreenshot(serialized, outFile) {
    await this.ensure();
    const stream = shiftsToAnsi(serialized.view, serialized.shifts ?? {});
    await this._page.evaluate((s) => window.__writeBuffer(s), stream);
    const locator = this._page.locator('#term .xterm-screen');
    await locator.screenshot({ path: outFile, omitBackground: false });
    return outFile;
  }

  async close() {
    try { await this._page?.close(); } catch {}
    try { await this._context?.close(); } catch {}
    try { await this._browser?.close(); } catch {}
    this._page = null;
    this._context = null;
    this._browser = null;
  }
}
