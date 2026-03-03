import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock vscode module
vi.mock('vscode', () => {
    return {
        NotebookCellKind: {
            Code: 2,
            Markup: 1,
        },
        NotebookCellData: class {
            kind: number;
            value: string;
            languageId: string;
            constructor(kind: number, value: string, languageId: string) {
                this.kind = kind;
                this.value = value;
                this.languageId = languageId;
            }
        },
        NotebookData: class {
            cells: any[];
            metadata: any;
            constructor(cells: any[]) {
                this.cells = cells;
                this.metadata = {};
            }
        },
    };
});

import { DataverseNotebookSerializer } from '../DataverseNotebookSerializer.js';

// Helper to create a cancellation token stub
const token = { isCancellationRequested: false, onCancellationRequested: vi.fn() } as any;

describe('DataverseNotebookSerializer', () => {
    let serializer: DataverseNotebookSerializer;

    beforeEach(() => {
        serializer = new DataverseNotebookSerializer();
    });

    it('round-trips SQL cells with metadata', async () => {
        const { NotebookData, NotebookCellData, NotebookCellKind } = await import('vscode');
        const original = new NotebookData([
            new NotebookCellData(NotebookCellKind.Code, 'SELECT * FROM account', 'sql'),
        ]);
        original.metadata = { environmentId: 'env-1', environmentName: 'Dev', environmentUrl: 'https://dev.crm.dynamics.com' };

        const bytes = await serializer.serializeNotebook(original, token);
        const deserialized = await serializer.deserializeNotebook(bytes, token);

        expect(deserialized.cells).toHaveLength(1);
        expect(deserialized.cells[0].value).toBe('SELECT * FROM account');
        expect(deserialized.cells[0].languageId).toBe('sql');
        expect(deserialized.metadata?.environmentId).toBe('env-1');
        expect(deserialized.metadata?.environmentUrl).toBe('https://dev.crm.dynamics.com');
    });

    it('round-trips FetchXML cells', async () => {
        const { NotebookData, NotebookCellData, NotebookCellKind } = await import('vscode');
        const fetchXml = '<fetch><entity name="account"><attribute name="name"/></entity></fetch>';
        const original = new NotebookData([
            new NotebookCellData(NotebookCellKind.Code, fetchXml, 'fetchxml'),
        ]);
        original.metadata = {};

        const bytes = await serializer.serializeNotebook(original, token);
        const deserialized = await serializer.deserializeNotebook(bytes, token);

        expect(deserialized.cells).toHaveLength(1);
        expect(deserialized.cells[0].value).toBe(fetchXml);
        expect(deserialized.cells[0].languageId).toBe('fetchxml');
    });

    it('round-trips markdown cells', async () => {
        const { NotebookData, NotebookCellData, NotebookCellKind } = await import('vscode');
        const original = new NotebookData([
            new NotebookCellData(NotebookCellKind.Markup, '# Hello World\n\nThis is a **test**.', 'markdown'),
        ]);
        original.metadata = {};

        const bytes = await serializer.serializeNotebook(original, token);
        const deserialized = await serializer.deserializeNotebook(bytes, token);

        expect(deserialized.cells).toHaveLength(1);
        expect(deserialized.cells[0].value).toBe('# Hello World\n\nThis is a **test**.');
        expect(deserialized.cells[0].languageId).toBe('markdown');
    });

    it('round-trips mixed cells', async () => {
        const { NotebookData, NotebookCellData, NotebookCellKind } = await import('vscode');
        const original = new NotebookData([
            new NotebookCellData(NotebookCellKind.Markup, '# Query accounts', 'markdown'),
            new NotebookCellData(NotebookCellKind.Code, 'SELECT name FROM account', 'sql'),
            new NotebookCellData(NotebookCellKind.Code, '<fetch><entity name="contact"/></fetch>', 'fetchxml'),
        ]);
        original.metadata = { environmentName: 'Test' };

        const bytes = await serializer.serializeNotebook(original, token);
        const deserialized = await serializer.deserializeNotebook(bytes, token);

        expect(deserialized.cells).toHaveLength(3);
        expect(deserialized.cells[0].languageId).toBe('markdown');
        expect(deserialized.cells[1].languageId).toBe('sql');
        expect(deserialized.cells[2].languageId).toBe('fetchxml');
    });

    it('handles empty files gracefully', async () => {
        const emptyBytes = new TextEncoder().encode('');
        const result = await serializer.deserializeNotebook(emptyBytes, token);

        expect(result.cells).toHaveLength(1);
        expect(result.cells[0].languageId).toBe('sql');
        expect(result.cells[0].value).toContain('SELECT TOP 10');
    });

    it('handles corrupt JSON gracefully', async () => {
        const badJson = new TextEncoder().encode('{ this is not valid json }');
        const result = await serializer.deserializeNotebook(badJson, token);

        expect(result.cells).toHaveLength(1);
        expect(result.cells[0].languageId).toBe('sql');
    });

    it('ensures at least one cell exists when cells array is empty', async () => {
        const emptyNotebook = JSON.stringify({ metadata: {}, cells: [] });
        const bytes = new TextEncoder().encode(emptyNotebook);
        const result = await serializer.deserializeNotebook(bytes, token);

        expect(result.cells).toHaveLength(1);
        expect(result.cells[0].languageId).toBe('sql');
    });

    it('serializes notebook to readable JSON', async () => {
        const { NotebookData, NotebookCellData, NotebookCellKind } = await import('vscode');
        const original = new NotebookData([
            new NotebookCellData(NotebookCellKind.Code, 'SELECT 1', 'sql'),
        ]);
        original.metadata = { environmentName: 'Dev' };

        const bytes = await serializer.serializeNotebook(original, token);
        const text = new TextDecoder().decode(bytes);
        const parsed = JSON.parse(text);

        expect(parsed.metadata.environmentName).toBe('Dev');
        expect(parsed.cells).toHaveLength(1);
        expect(parsed.cells[0].kind).toBe('sql');
        expect(parsed.cells[0].source).toBe('SELECT 1');
    });
});
