import { describe, it, expect, vi, beforeEach } from 'vitest';

// ── Mock: vscode ─────────────────────────────────────────────────────────────

vi.mock('vscode', () => ({
    ThemeIcon: class ThemeIcon {
        id: string;
        constructor(id: string) { this.id = id; }
    },
    window: {
        createQuickPick: vi.fn(),
        showInformationMessage: vi.fn(),
        showWarningMessage: vi.fn(),
        showErrorMessage: vi.fn(),
    },
    env: {
        clipboard: { writeText: vi.fn() },
    },
}));

// ── Import after mocks ────────────────────────────────────────────────────────

import {
    RUN_BUTTON,
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
        expect(RUN_BUTTON.tooltip).toBe('Run this query');
    });

    it('COPY_BUTTON has copy icon and tooltip', () => {
        expect((COPY_BUTTON.iconPath as any).id).toBe('copy');
        expect(COPY_BUTTON.tooltip).toBe('Copy SQL');
    });

    it('DELETE_BUTTON has trash icon and tooltip', () => {
        expect((DELETE_BUTTON.iconPath as any).id).toBe('trash');
        expect(DELETE_BUTTON.tooltip).toBe('Delete');
    });

    it('button constants are stable references (same object across imports)', async () => {
        // Re-importing the module should return the same cached exports
        const mod1 = await import('../../commands/queryHistoryCommand.js');
        const mod2 = await import('../../commands/queryHistoryCommand.js');
        expect(mod1.RUN_BUTTON).toBe(mod2.RUN_BUTTON);
        expect(mod1.COPY_BUTTON).toBe(mod2.COPY_BUTTON);
        expect(mod1.DELETE_BUTTON).toBe(mod2.DELETE_BUTTON);
    });
});

describe('buildHistoryItem', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('builds label with formatted date and SQL preview', () => {
        const entry = makeEntry({ executedAt: '2026-03-03T10:30:00Z', sql: 'SELECT name FROM account' });
        const item = buildHistoryItem(entry);
        // Label should contain a locale-formatted date (format varies by environment locale)
        // and the SQL preview — just verify it starts with '[' and contains the SQL
        expect(item.label).toMatch(/^\[.+\]/);
        expect(item.label).toContain('SELECT name FROM account');
    });

    it('truncates long SQL with ellipsis', () => {
        const longSql = 'SELECT accountid, name, createdon, modifiedon, statecode FROM account WHERE statecode = 0';
        const entry = makeEntry({ sql: longSql });
        const item = buildHistoryItem(entry);
        expect(item.label).toContain('...');
    });

    it('does not truncate short SQL', () => {
        const entry = makeEntry({ sql: 'SELECT * FROM lead' });
        const item = buildHistoryItem(entry);
        expect(item.label).not.toContain('...');
    });

    it('sets description to row count when rowCount is set', () => {
        const entry = makeEntry({ rowCount: 1234 });
        const item = buildHistoryItem(entry);
        expect(item.description).toMatch(/\(1.?234 rows\)/);
    });

    it('sets description to empty string when rowCount is null', () => {
        const entry = makeEntry({ rowCount: null });
        const item = buildHistoryItem(entry);
        expect(item.description).toBe('');
    });

    it('sets detail to full SQL', () => {
        const entry = makeEntry({ sql: 'SELECT * FROM contact' });
        const item = buildHistoryItem(entry);
        expect(item.detail).toBe('SELECT * FROM contact');
    });

    it('attaches all three buttons', () => {
        const entry = makeEntry();
        const item = buildHistoryItem(entry);
        expect(item.buttons).toHaveLength(3);
        expect(item.buttons![0]).toBe(RUN_BUTTON);
        expect(item.buttons![1]).toBe(COPY_BUTTON);
        expect(item.buttons![2]).toBe(DELETE_BUTTON);
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
