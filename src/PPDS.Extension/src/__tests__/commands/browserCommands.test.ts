import { describe, it, expect, vi, beforeEach } from 'vitest';

// vi.hoisted ensures these are available when the vi.mock factory runs
// (vi.mock is hoisted above all other statements by Vitest)
const {
    mockShowQuickPick,
    mockShowInformationMessage,
    mockShowErrorMessage,
    mockOpenExternal,
    mockUriParse,
} = vi.hoisted(() => ({
    mockShowQuickPick: vi.fn(),
    mockShowInformationMessage: vi.fn(),
    mockShowErrorMessage: vi.fn(),
    mockOpenExternal: vi.fn(),
    mockUriParse: vi.fn((url: string) => ({ toString: () => url })),
}));

vi.mock('vscode', () => ({
    window: {
        showQuickPick: mockShowQuickPick,
        showInformationMessage: mockShowInformationMessage,
        showErrorMessage: mockShowErrorMessage,
    },
    env: {
        openExternal: mockOpenExternal,
    },
    Uri: {
        parse: mockUriParse,
    },
}));

import { buildMakerUrl, buildDynamicsUrl } from '../../commands/browserCommands.js';

describe('browserCommands', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    describe('buildMakerUrl', () => {
        it('returns deep link when environmentId is present', () => {
            const url = buildMakerUrl('abc-123');
            expect(url).toBe('https://make.powerapps.com/environments/abc-123/solutions');
        });

        it('returns base URL when environmentId is null', () => {
            const url = buildMakerUrl(null);
            expect(url).toBe('https://make.powerapps.com');
        });
    });

    describe('buildDynamicsUrl', () => {
        it('returns the environment URL directly', () => {
            const url = buildDynamicsUrl('https://org.crm.dynamics.com');
            expect(url).toBe('https://org.crm.dynamics.com');
        });

        it('strips trailing slash', () => {
            const url = buildDynamicsUrl('https://org.crm.dynamics.com/');
            expect(url).toBe('https://org.crm.dynamics.com');
        });
    });
});
