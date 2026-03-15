// extension/tools/webview-cdp.test.mjs
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
  it('accepts valid port', () => { expect(validatePort(9223)).toBe(9223); });
  it('rejects port below 1024', () => { expect(() => validatePort(80)).toThrow('Invalid port: must be 1024-65535'); });
  it('rejects port above 65535', () => { expect(() => validatePort(70000)).toThrow('Invalid port: must be 1024-65535'); });
  it('rejects non-integer', () => { expect(() => validatePort(NaN)).toThrow('Invalid port: must be 1024-65535'); });
});

describe('parseKeyCombo', () => {
  it('parses simple key', () => { expect(parseKeyCombo('Escape')).toEqual({ key: 'Escape', modifiers: {} }); });
  it('parses ctrl+key', () => { expect(parseKeyCombo('ctrl+c')).toEqual({ key: 'c', modifiers: { ctrl: true } }); });
  it('parses ctrl+shift+key', () => { expect(parseKeyCombo('ctrl+shift+c')).toEqual({ key: 'c', modifiers: { ctrl: true, shift: true } }); });
  it('parses ctrl+enter', () => { expect(parseKeyCombo('ctrl+enter')).toEqual({ key: 'Enter', modifiers: { ctrl: true } }); });
  it('rejects unknown modifier', () => { expect(() => parseKeyCombo('foo+a')).toThrow("Invalid key combo: unknown modifier 'foo'"); });
  it('rejects empty string', () => { expect(() => parseKeyCombo('')).toThrow('Empty key combo'); });
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
