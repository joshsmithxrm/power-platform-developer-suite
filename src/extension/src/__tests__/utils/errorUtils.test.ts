import { describe, it, expect, vi, beforeEach } from 'vitest';

const { mockShowErrorMessage } = vi.hoisted(() => ({
    mockShowErrorMessage: vi.fn(),
}));

vi.mock('vscode', () => ({
    window: {
        showErrorMessage: mockShowErrorMessage,
    },
}));

vi.mock('vscode-jsonrpc/node', () => ({
    ResponseError: class ResponseError extends Error {
        data: unknown;
        constructor(code: number, message: string, data?: unknown) {
            super(message);
            this.data = data;
        }
    },
}));

import { handleAuthError } from '../../utils/errorUtils.js';

describe('handleAuthError', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('returns false for non-auth errors', async () => {
        const error = new Error('Network timeout');
        const daemon = { authWho: vi.fn(), profilesInvalidate: vi.fn() } as any;
        const retry = vi.fn();
        const handled = await handleAuthError(daemon, error, false, retry);
        expect(handled).toBe(false);
        expect(mockShowErrorMessage).not.toHaveBeenCalled();
    });

    it('returns false when isRetry is true (prevents infinite loop)', async () => {
        const error = new Error('unauthorized');
        const daemon = { authWho: vi.fn(), profilesInvalidate: vi.fn() } as any;
        const retry = vi.fn();
        const handled = await handleAuthError(daemon, error, true, retry);
        expect(handled).toBe(false);
        expect(mockShowErrorMessage).not.toHaveBeenCalled();
    });

    it('shows re-auth prompt and retries on success', async () => {
        const error = new Error('unauthorized');
        const daemon = {
            authWho: vi.fn().mockResolvedValue({ name: 'dev', index: 0 }),
            profilesInvalidate: vi.fn().mockResolvedValue(undefined),
        } as any;
        const retry = vi.fn().mockResolvedValue(undefined);
        mockShowErrorMessage.mockResolvedValue('Re-authenticate');
        const handled = await handleAuthError(daemon, error, false, retry);
        expect(handled).toBe(true);
        expect(daemon.profilesInvalidate).toHaveBeenCalledWith('dev');
        expect(retry).toHaveBeenCalled();
    });

    it('returns false when user cancels re-auth prompt', async () => {
        const error = new Error('unauthorized');
        const daemon = { authWho: vi.fn(), profilesInvalidate: vi.fn() } as any;
        const retry = vi.fn();
        mockShowErrorMessage.mockResolvedValue('Cancel');
        const handled = await handleAuthError(daemon, error, false, retry);
        expect(handled).toBe(false);
        expect(retry).not.toHaveBeenCalled();
    });

    it('returns false when retry throws', async () => {
        const error = new Error('unauthorized');
        const daemon = {
            authWho: vi.fn().mockResolvedValue({ name: 'dev', index: 0 }),
            profilesInvalidate: vi.fn().mockResolvedValue(undefined),
        } as any;
        const retry = vi.fn().mockRejectedValue(new Error('still failing'));
        mockShowErrorMessage.mockResolvedValue('Re-authenticate');
        const handled = await handleAuthError(daemon, error, false, retry);
        expect(handled).toBe(false);
    });

    it('uses profile index when name is null', async () => {
        const error = new Error('token expired');
        const daemon = {
            authWho: vi.fn().mockResolvedValue({ name: null, index: 2 }),
            profilesInvalidate: vi.fn().mockResolvedValue(undefined),
        } as any;
        const retry = vi.fn().mockResolvedValue(undefined);
        mockShowErrorMessage.mockResolvedValue('Re-authenticate');
        await handleAuthError(daemon, error, false, retry);
        expect(daemon.profilesInvalidate).toHaveBeenCalledWith('2');
    });
});
