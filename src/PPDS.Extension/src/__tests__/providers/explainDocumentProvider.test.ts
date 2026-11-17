import { describe, expect, it, vi } from 'vitest';

const host = vi.hoisted(() => ({
    openTextDocument: vi.fn(async (uri: any) => ({ uri })),
    setTextDocumentLanguage: vi.fn(async () => undefined),
    showTextDocument: vi.fn(async () => undefined),
}));

vi.mock('vscode', () => ({
    ViewColumn: { One: 1, Beside: 2 },
    EventEmitter: class { event = vi.fn(); fire = vi.fn(); dispose = vi.fn(); },
    Uri: { parse: (value: string) => ({ value, toString: () => value }) },
    workspace: {
        onDidCloseTextDocument: () => ({ dispose: vi.fn() }),
        openTextDocument: (...args: unknown[]) => host.openTextDocument(...(args as [any])),
    },
    languages: { setTextDocumentLanguage: (...args: unknown[]) => host.setTextDocumentLanguage(...(args as [])) },
    window: { showTextDocument: (...args: unknown[]) => host.showTextDocument(...(args as [])) },
}));

import { ExplainDocumentProvider } from '../../providers/explainDocumentProvider.js';

describe('ExplainDocumentProvider (read-only DML statement view)', () => {
    it('serves the exact statement as a virtual, read-only .sql document', async () => {
        const provider = new ExplainDocumentProvider();
        const statement = 'DELETE FROM account\r\nWHERE name = 1';
        await provider.show(statement, 'sql', 'DML to confirm');
        const uri = host.openTextDocument.mock.calls.at(-1)![0];
        expect(String(uri)).toBe(`${ExplainDocumentProvider.scheme}:DML to confirm 1.sql`);
        expect(provider.provideTextDocumentContent(uri)).toBe(statement);
        expect(host.showTextDocument).toHaveBeenCalled();
        provider.dispose();
    });

    it('still shows the statement when the SQL language is not available', async () => {
        const provider = new ExplainDocumentProvider();
        host.setTextDocumentLanguage.mockRejectedValueOnce(new Error('Unknown language id: sql'));
        await expect(provider.show('UPDATE account SET name = 1 WHERE accountid = 1', 'sql', 'DML to confirm')).resolves.toBeUndefined();
        expect(host.showTextDocument).toHaveBeenCalled();
        provider.dispose();
    });
});
