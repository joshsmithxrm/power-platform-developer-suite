import { describe, it, expect, vi } from 'vitest';

vi.mock('vscode', () => ({
    window: { showErrorMessage: vi.fn() },
    EventEmitter: class { event = vi.fn(); fire = vi.fn(); dispose = vi.fn(); },
}));

vi.mock('vscode-jsonrpc/node', () => ({
    createMessageConnection: vi.fn(),
    StreamMessageReader: vi.fn(),
    StreamMessageWriter: vi.fn(),
    RequestType: class { constructor(public method: string, public paramStructures?: unknown) {} },
    ParameterStructures: { byPosition: 1, byName: 2, auto: 0 },
}));

import { parseDaemonLogLevel } from '../daemonClient.js';

describe('parseDaemonLogLevel', () => {
    it('parses INF as info', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [INF] [Category] message')).toBe('info');
    });

    it('parses DBG as debug', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [DBG] [Category] message')).toBe('debug');
    });

    it('parses TRC as trace', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [TRC] [Category] message')).toBe('trace');
    });

    it('parses WRN as warn', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [WRN] [Category] message')).toBe('warn');
    });

    it('parses ERR as error', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [ERR] [Category] error message')).toBe('error');
    });

    it('parses CRT as error', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [CRT] [Category] critical')).toBe('error');
    });

    it('defaults to warn for unrecognized format', () => {
        expect(parseDaemonLogLevel('some unstructured stderr output')).toBe('warn');
    });

    it('defaults to warn for empty string', () => {
        expect(parseDaemonLogLevel('')).toBe('warn');
    });
});
