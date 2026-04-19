import { describe, it, expect, vi, beforeEach } from 'vitest';

const {
    mockShowErrorMessage,
    mockOpenExternal,
    mockUriParse,
} = vi.hoisted(() => ({
    mockShowErrorMessage: vi.fn(),
    mockOpenExternal: vi.fn(),
    mockUriParse: vi.fn((s: string) => ({ toString: () => s })),
}));

vi.mock('vscode', () => ({
    window: {
        showErrorMessage: mockShowErrorMessage,
    },
    env: {
        openExternal: mockOpenExternal,
    },
    Uri: {
        parse: mockUriParse,
    },
}));

import {
    showErrorWithReport,
    REPORT_ISSUE_URL,
    REPORT_ISSUE_ACTION,
} from '../../utils/errorNotify.js';

describe('showErrorWithReport', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('passes the message and a Report Issue action to showErrorMessage', async () => {
        mockShowErrorMessage.mockResolvedValue(undefined);

        await showErrorWithReport('Something failed');

        expect(mockShowErrorMessage).toHaveBeenCalledTimes(1);
        const [message, ...actions] = mockShowErrorMessage.mock.calls[0] as [
            string,
            ...Array<{ title: string }>,
        ];
        expect(message).toBe('Something failed');
        expect(actions).toEqual([{ title: REPORT_ISSUE_ACTION }]);
    });

    it('opens the GitHub new-issue URL when the user clicks Report Issue', async () => {
        mockShowErrorMessage.mockResolvedValue({ title: REPORT_ISSUE_ACTION });

        const result = await showErrorWithReport('Boom');

        expect(mockUriParse).toHaveBeenCalledWith(REPORT_ISSUE_URL);
        expect(mockOpenExternal).toHaveBeenCalledTimes(1);
        expect(result).toBe(REPORT_ISSUE_ACTION);
    });

    it('does not open the browser when the user dismisses the notification', async () => {
        mockShowErrorMessage.mockResolvedValue(undefined);

        const result = await showErrorWithReport('Nope');

        expect(mockOpenExternal).not.toHaveBeenCalled();
        expect(result).toBeUndefined();
    });

    it('uses the /new path so the user lands on the pre-filled issue form', () => {
        expect(REPORT_ISSUE_URL).toBe(
            'https://github.com/joshsmithxrm/power-platform-developer-suite/issues/new',
        );
    });

    it('uses an https scheme (guard for the A4 BrowserHelper scheme check)', () => {
        expect(REPORT_ISSUE_URL).toMatch(/^https:\/\//);
    });
});
