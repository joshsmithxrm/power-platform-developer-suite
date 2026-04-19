import { describe, it, expect, vi } from 'vitest';

vi.mock('vscode', () => ({}));

import { logCommand, newClientCorrelationId } from '../utils/structuredLog.js';

/**
 * Minimal LogOutputChannel stub — only the methods logCommand uses need to exist.
 * We spy on each so tests can assert which channel method fires for each event type.
 */
function createChannel() {
    return {
        debug: vi.fn(),
        info: vi.fn(),
        warn: vi.fn(),
        error: vi.fn(),
    };
}

describe('logCommand', () => {
    it('emits command.start via debug', () => {
        const channel = createChannel();
        logCommand(channel as never, {
            event: 'command.start',
            command: 'ppds.test',
            correlationId: 'abc-123',
        });
        expect(channel.debug).toHaveBeenCalledOnce();
        const line = channel.debug.mock.calls[0][0] as string;
        expect(line).toContain('command.start ppds.test');
        expect(line).toContain('abc-123');
    });

    it('emits command.end with success outcome via info', () => {
        const channel = createChannel();
        logCommand(channel as never, {
            event: 'command.end',
            command: 'ppds.test',
            correlationId: 'abc-123',
            durationMs: 42,
            outcome: 'success',
        });
        expect(channel.info).toHaveBeenCalledOnce();
        const line = channel.info.mock.calls[0][0] as string;
        expect(line).toContain('command.end ppds.test');
        expect(line).toContain('"durationMs":42');
        expect(line).toContain('"outcome":"success"');
    });

    it('emits command.end with failure outcome via warn', () => {
        const channel = createChannel();
        logCommand(channel as never, {
            event: 'command.end',
            command: 'ppds.test',
            outcome: 'failure',
        });
        expect(channel.warn).toHaveBeenCalledOnce();
    });

    it('emits command.error via error', () => {
        const channel = createChannel();
        logCommand(channel as never, {
            event: 'command.error',
            command: 'ppds.test',
            correlationId: 'abc-123',
            durationMs: 42,
            outcome: 'failure',
            errorMessage: 'boom',
        });
        expect(channel.error).toHaveBeenCalledOnce();
        const line = channel.error.mock.calls[0][0] as string;
        expect(line).toContain('command.error ppds.test');
        expect(line).toContain('"errorMessage":"boom"');
    });

    it('is safe with undefined channel (no-op)', () => {
        expect(() => logCommand(undefined, {
            event: 'command.start',
            command: 'ppds.test',
        })).not.toThrow();
    });

    it('includes extra fields in the JSON payload', () => {
        const channel = createChannel();
        logCommand(channel as never, {
            event: 'command.start',
            command: 'ppds.test',
            extra: { panel: 'Solutions', envUrl: 'https://dev.crm.dynamics.com' },
        });
        const line = channel.debug.mock.calls[0][0] as string;
        expect(line).toContain('"panel":"Solutions"');
        expect(line).toContain('"envUrl":"https://dev.crm.dynamics.com"');
    });
});

describe('newClientCorrelationId', () => {
    it('returns a non-empty string', () => {
        const id = newClientCorrelationId();
        expect(id).toBeTruthy();
        expect(typeof id).toBe('string');
    });

    it('returns unique ids on successive calls', () => {
        const ids = new Set([
            newClientCorrelationId(),
            newClientCorrelationId(),
            newClientCorrelationId(),
        ]);
        expect(ids.size).toBe(3);
    });
});
