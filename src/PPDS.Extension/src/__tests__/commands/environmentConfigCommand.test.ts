import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ResponseError } from 'vscode-jsonrpc/node';

// ── Mock: vscode ─────────────────────────────────────────────────────────────

vi.mock('vscode', () => ({
    window: {
        showWarningMessage: vi.fn(),
        showInformationMessage: vi.fn(),
        showErrorMessage: vi.fn(),
    },
    commands: { registerCommand: vi.fn() },
}));

// ── Import after mocks ────────────────────────────────────────────────────────

import * as vscode from 'vscode';
import {
    LOWER_PROTECTION_BUTTON,
    PROTECTION_LOWERING_CONFIRMATION_REQUIRED,
    confirmLowerProtection,
    isProtectionLoweringConfirmationRequired,
    saveEnvironmentConfig,
} from '../../commands/environmentConfigCommand.js';

const URL = 'https://org.crm.dynamics.com';
const SERVER_MESSAGE = `Changing the type of '${URL}' from Production to Development lowers write protection.`;

function loweringRefusal(): ResponseError<unknown> {
    return new ResponseError(-32000, SERVER_MESSAGE, {
        code: PROTECTION_LOWERING_CONFIRMATION_REQUIRED,
        message: SERVER_MESSAGE,
    });
}

describe('isProtectionLoweringConfirmationRequired', () => {
    it('recognises the daemon refusal by its structured code', () => {
        expect(isProtectionLoweringConfirmationRequired(loweringRefusal())).toBe(true);
    });

    it('ignores other daemon errors and non-RPC errors', () => {
        expect(isProtectionLoweringConfirmationRequired(
            new ResponseError(-32000, 'nope', { code: 'Environment.ProtectionLoweringRefused' }))).toBe(false);
        expect(isProtectionLoweringConfirmationRequired(new Error(PROTECTION_LOWERING_CONFIRMATION_REQUIRED))).toBe(false);
        expect(isProtectionLoweringConfirmationRequired(undefined)).toBe(false);
    });
});

describe('saveEnvironmentConfig', () => {
    const params = { environmentUrl: URL, label: 'Contoso', type: 'Development', color: undefined };

    it('saves once, without an acknowledgement, when the daemon accepts the change', async () => {
        const envConfigSet = vi.fn().mockResolvedValue({ saved: true });
        const confirm = vi.fn();

        await expect(saveEnvironmentConfig({ envConfigSet }, params, confirm)).resolves.toBe('saved');

        expect(envConfigSet).toHaveBeenCalledTimes(1);
        expect(envConfigSet.mock.calls[0][0]).not.toHaveProperty('confirmLowerProtection');
        expect(confirm).not.toHaveBeenCalled();
    });

    it('asks with the daemon message, then resends with confirmLowerProtection naming the environment', async () => {
        const envConfigSet = vi.fn()
            .mockRejectedValueOnce(loweringRefusal())
            .mockResolvedValueOnce({ saved: true });
        const confirm = vi.fn().mockResolvedValue(true);

        await expect(saveEnvironmentConfig({ envConfigSet }, params, confirm)).resolves.toBe('saved');

        expect(confirm).toHaveBeenCalledWith(SERVER_MESSAGE);
        expect(envConfigSet).toHaveBeenCalledTimes(2);
        expect(envConfigSet.mock.calls[1][0]).toEqual({ ...params, confirmLowerProtection: URL });
    });

    it('sends nothing more when the user declines', async () => {
        const envConfigSet = vi.fn().mockRejectedValueOnce(loweringRefusal());
        const confirm = vi.fn().mockResolvedValue(false);

        await expect(saveEnvironmentConfig({ envConfigSet }, params, confirm)).resolves.toBe('cancelled');

        expect(envConfigSet).toHaveBeenCalledTimes(1);
    });

    it('rethrows any other error without asking', async () => {
        const other = new ResponseError(-32000, 'boom', { code: 'Environment.ProtectionLoweringRefused' });
        const envConfigSet = vi.fn().mockRejectedValueOnce(other);
        const confirm = vi.fn();

        await expect(saveEnvironmentConfig({ envConfigSet }, params, confirm)).rejects.toBe(other);

        expect(confirm).not.toHaveBeenCalled();
    });
});

describe('confirmLowerProtection', () => {
    beforeEach(() => vi.mocked(vscode.window.showWarningMessage).mockReset());

    it('shows a modal with the daemon message and confirms only on the explicit button', async () => {
        vi.mocked(vscode.window.showWarningMessage).mockResolvedValue(LOWER_PROTECTION_BUTTON as never);

        await expect(confirmLowerProtection(SERVER_MESSAGE)).resolves.toBe(true);

        expect(vscode.window.showWarningMessage).toHaveBeenCalledWith(
            expect.any(String), { modal: true, detail: SERVER_MESSAGE }, LOWER_PROTECTION_BUTTON);
    });

    it('treats dismissing the modal as no', async () => {
        vi.mocked(vscode.window.showWarningMessage).mockResolvedValue(undefined);

        await expect(confirmLowerProtection(SERVER_MESSAGE)).resolves.toBe(false);
    });
});
