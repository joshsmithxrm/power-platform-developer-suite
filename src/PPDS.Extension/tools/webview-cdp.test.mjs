// src/PPDS.Extension/tools/webview-cdp.test.mjs
import { describe, it, expect } from 'vitest';
import { parseArgs, parseKeyCombo } from './webview-cdp.mjs';

describe('parseArgs', () => {
  it('parses launch with defaults', () => {
    const result = parseArgs(['launch']);
    expect(result).toMatchObject({ command: 'launch', workspace: undefined, build: false });
  });

  it('parses launch with workspace', () => {
    const result = parseArgs(['launch', '/my/workspace']);
    expect(result).toMatchObject({ command: 'launch', workspace: '/my/workspace', build: false });
  });

  it('parses launch with --build', () => {
    const result = parseArgs(['launch', '--build']);
    expect(result).toMatchObject({ command: 'launch', workspace: undefined, build: true });
  });

  it('parses launch with workspace and --build', () => {
    const result = parseArgs(['launch', '/my/workspace', '--build']);
    expect(result).toMatchObject({ command: 'launch', workspace: '/my/workspace', build: true });
  });

  it('parses close', () => {
    const result = parseArgs(['close']);
    expect(result).toMatchObject({ command: 'close' });
  });

  it('parses connect', () => {
    const result = parseArgs(['connect']);
    expect(result).toMatchObject({ command: 'connect' });
  });

  it('parses command', () => {
    const result = parseArgs(['command', 'ppds.dataExplorer']);
    expect(result).toMatchObject({ command: 'command', args: ['ppds.dataExplorer'] });
  });

  it('parses wait with default timeout', () => {
    const result = parseArgs(['wait']);
    expect(result).toMatchObject({ command: 'wait', timeout: 30000, ext: undefined });
  });

  it('parses wait with timeout', () => {
    const result = parseArgs(['wait', '5000']);
    expect(result).toMatchObject({ command: 'wait', timeout: 5000, ext: undefined });
  });

  it('parses wait with --ext', () => {
    const result = parseArgs(['wait', '--ext', 'ppds']);
    expect(result).toMatchObject({ command: 'wait', timeout: 30000, ext: 'ppds' });
  });

  it('parses logs with no flags', () => {
    const result = parseArgs(['logs']);
    expect(result).toMatchObject({ command: 'logs', channel: undefined, level: undefined });
  });

  it('parses logs with --channel', () => {
    const result = parseArgs(['logs', '--channel', 'PPDS']);
    expect(result).toMatchObject({ command: 'logs', channel: 'PPDS', level: undefined });
  });

  it('parses click with selector', () => {
    const result = parseArgs(['click', '#btn']);
    expect(result).toMatchObject({ command: 'click', args: ['#btn'], page: false, right: false, target: undefined, ext: undefined });
  });

  it('parses click with --right and --page', () => {
    const result = parseArgs(['click', '#btn', '--right', '--page']);
    expect(result).toMatchObject({ command: 'click', args: ['#btn'], page: true, right: true, target: undefined, ext: undefined });
  });

  it('parses eval with --page', () => {
    const result = parseArgs(['eval', 'document.title', '--page']);
    expect(result).toMatchObject({ command: 'eval', args: ['document.title'], page: true, target: undefined, ext: undefined });
  });

  it('parses --target with numeric index', () => {
    const result = parseArgs(['eval', '1+1', '--target', '2']);
    expect(result).toMatchObject({ command: 'eval', args: ['1+1'], page: false, target: 2, ext: undefined });
  });

  it('parses --target active', () => {
    const result = parseArgs(['eval', '1+1', '--target', 'active']);
    expect(result).toMatchObject({ command: 'eval', args: ['1+1'], page: false, target: 'active', ext: undefined });
  });

  it('parses --target active with --ext', () => {
    const result = parseArgs(['click', '#btn', '--target', 'active', '--ext', 'ppds']);
    expect(result).toMatchObject({ command: 'click', args: ['#btn'], page: false, right: false, target: 'active', ext: 'ppds' });
  });

  it('parses --ext flag on interaction commands', () => {
    const result = parseArgs(['eval', '1+1', '--ext', 'ppds']);
    expect(result).toMatchObject({ command: 'eval', args: ['1+1'], page: false, target: undefined, ext: 'ppds' });
  });

  it('parses screenshot with --page', () => {
    const result = parseArgs(['screenshot', '/tmp/shot.png', '--page']);
    expect(result).toMatchObject({ command: 'screenshot', args: ['/tmp/shot.png'], page: true, target: undefined, ext: undefined });
  });

  it('parses mouse with event and coordinates', () => {
    const result = parseArgs(['mouse', 'mousedown', '150', '200']);
    expect(result).toMatchObject({ command: 'mouse', args: ['mousedown', '150', '200'], page: false, target: undefined, ext: undefined });
  });

  it('parses key', () => {
    const result = parseArgs(['key', 'ctrl+shift+p']);
    expect(result).toMatchObject({ command: 'key', args: ['ctrl+shift+p'], page: false });
  });

  it('parses key with --page', () => {
    const result = parseArgs(['key', 'ctrl+shift+p', '--page']);
    expect(result).toMatchObject({ command: 'key', args: ['ctrl+shift+p'], page: true });
  });

  it('parses type with selector and text', () => {
    const result = parseArgs(['type', '#input', 'hello world']);
    expect(result).toMatchObject({ command: 'type', args: ['#input', 'hello world'], page: false, target: undefined, ext: undefined });
  });

  it('parses select with selector and value', () => {
    const result = parseArgs(['select', '#dropdown', 'option1']);
    expect(result).toMatchObject({ command: 'select', args: ['#dropdown', 'option1'], page: false, target: undefined, ext: undefined });
  });

  it('parses text with selector', () => {
    const result = parseArgs(['text', '#status']);
    expect(result).toMatchObject({ command: 'text', args: ['#status'], page: false, target: undefined, ext: undefined });
  });

  it('parses text with --ext', () => {
    const result = parseArgs(['text', '#status', '--ext', 'ppds']);
    expect(result).toMatchObject({ command: 'text', args: ['#status'], page: false, target: undefined, ext: 'ppds' });
  });

  it('errors on empty args', () => {
    expect(() => parseArgs([])).toThrow('No command provided');
  });

  it('errors on unknown command', () => {
    expect(() => parseArgs(['foobar'])).toThrow('Unknown command: foobar');
  });

  it('errors on removed attach command', () => {
    expect(() => parseArgs(['attach'])).toThrow('Unknown command: attach');
  });

  // --session flag
  it('parses --session on launch', () => {
    const result = parseArgs(['launch', '--session', 'legacy', '--build']);
    expect(result).toMatchObject({ command: 'launch', session: 'legacy', build: true });
  });

  it('parses --session on any command', () => {
    const result = parseArgs(['screenshot', '/tmp/shot.png', '--session', 'new']);
    expect(result).toMatchObject({ command: 'screenshot', session: 'new', args: ['/tmp/shot.png'] });
  });

  it('defaults session to default', () => {
    const result = parseArgs(['launch']);
    expect(result.session).toBe('default');
  });

  // --vsix flag
  it('parses --vsix on launch', () => {
    const result = parseArgs(['launch', '--vsix', '/path/to/ext.vsix']);
    expect(result).toMatchObject({ command: 'launch', vsix: '/path/to/ext.vsix' });
  });

  it('parses --vsix with --session', () => {
    const result = parseArgs(['launch', '--vsix', '/ext.vsix', '--session', 'legacy']);
    expect(result).toMatchObject({ command: 'launch', vsix: '/ext.vsix', session: 'legacy' });
  });

  // close --all
  it('parses close --all', () => {
    const result = parseArgs(['close', '--all']);
    expect(result).toMatchObject({ command: 'close', all: true });
  });

  it('parses close without --all', () => {
    const result = parseArgs(['close']);
    expect(result).toMatchObject({ command: 'close', all: false });
  });

  // download command
  it('parses download', () => {
    const result = parseArgs(['download', 'publisher.ext-name', '1.0.0']);
    expect(result).toMatchObject({ command: 'download', args: ['publisher.ext-name', '1.0.0'] });
  });

  it('parses notebook run', () => {
    const result = parseArgs(['notebook', 'run']);
    expect(result).toMatchObject({ command: 'notebook', subcommand: 'run' });
  });

  it('parses notebook run-all', () => {
    const result = parseArgs(['notebook', 'run-all']);
    expect(result).toMatchObject({ command: 'notebook', subcommand: 'run-all' });
  });

  it('errors on notebook with no subcommand', () => {
    expect(() => parseArgs(['notebook'])).toThrow('notebook requires a subcommand');
  });

  it('errors on notebook with unknown subcommand', () => {
    expect(() => parseArgs(['notebook', 'foo'])).toThrow('Unknown notebook subcommand: foo');
  });
});


describe('parseKeyCombo', () => {
  it('parses simple key', () => { expect(parseKeyCombo('Escape')).toMatchObject({ key: 'Escape', modifiers: {} }); });
  it('parses ctrl+key', () => { expect(parseKeyCombo('ctrl+c')).toMatchObject({ key: 'c', modifiers: { ctrl: true } }); });
  it('parses ctrl+shift+key', () => { expect(parseKeyCombo('ctrl+shift+c')).toMatchObject({ key: 'c', modifiers: { ctrl: true, shift: true } }); });
  it('parses ctrl+enter', () => { expect(parseKeyCombo('ctrl+enter')).toMatchObject({ key: 'Enter', modifiers: { ctrl: true } }); });
  it('rejects unknown modifier', () => { expect(() => parseKeyCombo('foo+a')).toThrow("Invalid key combo: unknown modifier 'foo'"); });
  it('rejects empty string', () => { expect(() => parseKeyCombo('')).toThrow('Empty key combo'); });
});
