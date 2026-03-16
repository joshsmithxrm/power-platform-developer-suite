import { describe, it } from 'node:test';
import { strict as assert } from 'node:assert';
import { parseArgs, parseKeyCombo, ROWS, COLS } from './tui-verify.mjs';

// ── parseArgs ────────────────────────────────────────────────────────

describe('parseArgs', () => {
  // launch
  it('launch without --build', () => {
    const result = parseArgs(['launch']);
    assert.deepStrictEqual(result, { command: 'launch', build: false });
  });

  it('launch with --build', () => {
    const result = parseArgs(['launch', '--build']);
    assert.deepStrictEqual(result, { command: 'launch', build: true });
  });

  // close
  it('close', () => {
    const result = parseArgs(['close']);
    assert.deepStrictEqual(result, { command: 'close' });
  });

  // rows
  it('rows', () => {
    const result = parseArgs(['rows']);
    assert.deepStrictEqual(result, { command: 'rows' });
  });

  // text
  it('text with valid row', () => {
    const result = parseArgs(['text', '5']);
    assert.deepStrictEqual(result, { command: 'text', row: 5 });
  });

  it('text with row 0', () => {
    const result = parseArgs(['text', '0']);
    assert.deepStrictEqual(result, { command: 'text', row: 0 });
  });

  it('text with row 29 (max)', () => {
    const result = parseArgs(['text', '29']);
    assert.deepStrictEqual(result, { command: 'text', row: 29 });
  });

  it('text with out-of-range row throws', () => {
    assert.throws(() => parseArgs(['text', '30']), /Row must be 0-29/);
  });

  it('text with negative row throws', () => {
    assert.throws(() => parseArgs(['text', '-1']), /Row must be 0-29/);
  });

  it('text with missing row throws', () => {
    assert.throws(() => parseArgs(['text']), /Usage: text <row>/);
  });

  // key
  it('key with combo', () => {
    const result = parseArgs(['key', 'ctrl+c']);
    assert.deepStrictEqual(result, { command: 'key', combo: 'ctrl+c' });
  });

  it('key with missing combo throws', () => {
    assert.throws(() => parseArgs(['key']), /Usage: key <combo>/);
  });

  // type
  it('type with text', () => {
    const result = parseArgs(['type', 'hello world']);
    assert.deepStrictEqual(result, { command: 'type', text: 'hello world' });
  });

  it('type with missing text throws', () => {
    assert.throws(() => parseArgs(['type']), /Usage: type <text>/);
  });

  // wait
  it('wait with text and default timeout', () => {
    const result = parseArgs(['wait', 'Loading']);
    assert.deepStrictEqual(result, { command: 'wait', text: 'Loading', timeout: 10000 });
  });

  it('wait with text and custom timeout', () => {
    const result = parseArgs(['wait', 'Loading', '5000']);
    assert.deepStrictEqual(result, { command: 'wait', text: 'Loading', timeout: 5000 });
  });

  it('wait with zero timeout throws', () => {
    assert.throws(() => parseArgs(['wait', 'Loading', '0']), /Invalid timeout/);
  });

  it('wait with negative timeout throws', () => {
    assert.throws(() => parseArgs(['wait', 'Loading', '-1']), /Invalid timeout/);
  });

  it('wait with missing text throws', () => {
    assert.throws(() => parseArgs(['wait']), /Usage: wait <text> \[timeout\]/);
  });

  // screenshot
  it('screenshot with file', () => {
    const result = parseArgs(['screenshot', 'shot.json']);
    assert.deepStrictEqual(result, { command: 'screenshot', file: 'shot.json' });
  });

  it('screenshot with missing file throws', () => {
    assert.throws(() => parseArgs(['screenshot']), /Usage: screenshot <file>/);
  });

  // errors
  it('unknown command throws', () => {
    assert.throws(() => parseArgs(['explode']), /Unknown command: explode/);
  });

  it('empty args throws', () => {
    assert.throws(() => parseArgs([]), /No command provided/);
  });
});

// ── parseKeyCombo ────────────────────────────────────────────────────

describe('parseKeyCombo', () => {
  // Single named keys
  it('enter', () => {
    const result = parseKeyCombo('enter');
    assert.deepStrictEqual(result, { key: 'enter', modifiers: {} });
  });

  it('tab', () => {
    const result = parseKeyCombo('tab');
    assert.deepStrictEqual(result, { key: 'tab', modifiers: {} });
  });

  it('escape', () => {
    const result = parseKeyCombo('escape');
    assert.deepStrictEqual(result, { key: 'escape', modifiers: {} });
  });

  it('up', () => {
    const result = parseKeyCombo('up');
    assert.deepStrictEqual(result, { key: 'up', modifiers: {} });
  });

  it('down', () => {
    const result = parseKeyCombo('down');
    assert.deepStrictEqual(result, { key: 'down', modifiers: {} });
  });

  it('F1', () => {
    const result = parseKeyCombo('F1');
    assert.deepStrictEqual(result, { key: 'F1', modifiers: {} });
  });

  it('f12 (case-insensitive)', () => {
    const result = parseKeyCombo('f12');
    assert.deepStrictEqual(result, { key: 'f12', modifiers: {} });
  });

  // Modifier combos
  it('ctrl+letter', () => {
    const result = parseKeyCombo('ctrl+c');
    assert.deepStrictEqual(result, { key: 'c', modifiers: { ctrl: true } });
  });

  it('alt+letter', () => {
    const result = parseKeyCombo('alt+x');
    assert.deepStrictEqual(result, { key: 'x', modifiers: { alt: true } });
  });

  it('ctrl+shift+letter', () => {
    const result = parseKeyCombo('ctrl+shift+a');
    assert.deepStrictEqual(result, { key: 'a', modifiers: { ctrl: true, shift: true } });
  });

  // Errors
  it('empty combo throws', () => {
    assert.throws(() => parseKeyCombo(''), /Empty key combo/);
  });

  it('unknown modifier throws', () => {
    assert.throws(() => parseKeyCombo('super+a'), /unknown modifier 'super'/);
  });

  // Single character
  it('single character', () => {
    const result = parseKeyCombo('a');
    assert.deepStrictEqual(result, { key: 'a', modifiers: {} });
  });
});

// ── Constants ────────────────────────────────────────────────────────

describe('constants', () => {
  it('ROWS is 30', () => {
    assert.strictEqual(ROWS, 30);
  });

  it('COLS is 120', () => {
    assert.strictEqual(COLS, 120);
  });
});
