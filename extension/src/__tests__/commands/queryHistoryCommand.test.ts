import { describe, it, expect, vi, beforeEach } from 'vitest';

// ── Mock: vscode ─────────────────────────────────────────────────────────────

vi.mock('vscode', () => ({
    ThemeIcon: class ThemeIcon {
        id: string;
        constructor(id: string) { this.id = id; }
    },
    QuickPickItemKind: { Separator: -1 },
    window: {
        createQuickPick: vi.fn(),
        showInformationMessage: vi.fn(),
        showWarningMessage: vi.fn(),
        showErrorMessage: vi.fn(),
    },
    env: {
        clipboard: { writeText: vi.fn() },
    },
    commands: {
        executeCommand: vi.fn(),
    },
}));

// ── Import after mocks ────────────────────────────────────────────────────────

import {
    RUN_BUTTON,
    NOTEBOOK_BUTTON,
    COPY_BUTTON,
    DELETE_BUTTON,
    buildHistoryItem,
} from '../../commands/queryHistoryCommand.js';
import type { QueryHistoryEntryDto } from '../../types.js';

// ── Test data ─────────────────────────────────────────────────────────────────

function makeEntry(overrides: Partial<QueryHistoryEntryDto> = {}): QueryHistoryEntryDto {
    return {
        id: 'h-1',
        sql: 'SELECT TOP 10 * FROM account',
        rowCount: 42,
        executionTimeMs: 150,
        environmentUrl: 'https://org.crm.dynamics.com',
        executedAt: '2026-03-03T10:30:00Z',
        ...overrides,
    };
}

// ── Tests ─────────────────────────────────────────────────────────────────────

describe('Button constants', () => {
    it('RUN_BUTTON, COPY_BUTTON, DELETE_BUTTON are distinct objects', () => {
        expect(RUN_BUTTON).not.toBe(COPY_BUTTON);
        expect(RUN_BUTTON).not.toBe(DELETE_BUTTON);
        expect(COPY_BUTTON).not.toBe(DELETE_BUTTON);
    });

    it('RUN_BUTTON has play icon and tooltip', () => {
        expect((RUN_BUTTON.iconPath as any).id).toBe('play');
        expect(RUN_BUTTON.tooltip).toContain('Run');
    });

    it('NOTEBOOK_BUTTON has notebook icon and tooltip', () => {
        expect((NOTEBOOK_BUTTON.iconPath as any).id).toBe('notebook');
        expect(NOTEBOOK_BUTTON.tooltip).toContain('Notebook');
    });

    it('COPY_BUTTON has copy icon and tooltip', () => {
        expect((COPY_BUTTON.iconPath as any).id).toBe('copy');
        expect(COPY_BUTTON.tooltip).toContain('Copy');
    });

    it('DELETE_BUTTON has trash icon and tooltip', () => {
        expect((DELETE_BUTTON.iconPath as any).id).toBe('trash');
        expect(DELETE_BUTTON.tooltip).toContain('Delete');
    });

    it('button constants are stable references (same object across imports)', async () => {
        // Re-importing the module should return the same cached exports
        const mod1 = await import('../../commands/queryHistoryCommand.js');
        const mod2 = await import('../../commands/queryHistoryCommand.js');
        expect(mod1.RUN_BUTTON).toBe(mod2.RUN_BUTTON);
        expect(mod1.NOTEBOOK_BUTTON).toBe(mod2.NOTEBOOK_BUTTON);
        expect(mod1.COPY_BUTTON).toBe(mod2.COPY_BUTTON);
        expect(mod1.DELETE_BUTTON).toBe(mod2.DELETE_BUTTON);
    });
});

describe('buildHistoryItem', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('builds label with icon prefix and SQL preview', () => {
        const entry = makeEntry({ executedAt: '2026-03-03T10:30:00Z', sql: 'SELECT name FROM account' });
        const item = buildHistoryItem(entry);
        // Label starts with codicon prefix and contains the SQL preview
        expect(item.label).toContain('$(history)');
        expect(item.label).toContain('SELECT name FROM account');
    });

    it('truncates long SQL with ellipsis', () => {
        const longSql = 'SELECT accountid, name, createdon, modifiedon, statecode, statuscode, ownerid FROM account WHERE statecode = 0 AND statuscode = 1';
        const entry = makeEntry({ sql: longSql });
        const item = buildHistoryItem(entry);
        expect(item.label).toContain('...');
    });

    it('does not truncate short SQL', () => {
        const entry = makeEntry({ sql: 'SELECT * FROM lead' });
        const item = buildHistoryItem(entry);
        expect(item.label).not.toContain('...');
    });

    it('sets description with row count and execution time', () => {
        const entry = makeEntry({ rowCount: 1234, executionTimeMs: 150 });
        const item = buildHistoryItem(entry);
        expect(item.description).toContain('1,234 rows');
        expect(item.description).toContain('150ms');
    });

    it('sets description to empty string when both rowCount and executionTimeMs are null', () => {
        const entry = makeEntry({ rowCount: null, executionTimeMs: null });
        const item = buildHistoryItem(entry);
        expect(item.description).toBe('');
    });

    it('sets detail with date and full SQL', () => {
        const entry = makeEntry({ sql: 'SELECT * FROM contact' });
        const item = buildHistoryItem(entry);
        expect(item.detail).toContain('SELECT * FROM contact');
    });

    it('attaches all four buttons', () => {
        const entry = makeEntry();
        const item = buildHistoryItem(entry);
        expect(item.buttons).toHaveLength(4);
        expect(item.buttons![0]).toBe(RUN_BUTTON);
        expect(item.buttons![1]).toBe(NOTEBOOK_BUTTON);
        expect(item.buttons![2]).toBe(COPY_BUTTON);
        expect(item.buttons![3]).toBe(DELETE_BUTTON);
    });

    it('attaches the original entry to the item', () => {
        const entry = makeEntry({ id: 'h-99', sql: 'SELECT * FROM systemuser' });
        const item = buildHistoryItem(entry);
        expect(item.entry).toBe(entry);
    });

    it('normalises whitespace in SQL preview', () => {
        const entry = makeEntry({ sql: 'SELECT\n    name\nFROM\n    account' });
        const item = buildHistoryItem(entry);
        // The preview part should not have newlines
        expect(item.label).not.toMatch(/\n/);
    });
});
