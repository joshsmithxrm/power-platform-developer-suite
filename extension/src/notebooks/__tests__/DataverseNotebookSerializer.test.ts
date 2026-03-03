import { describe, it, expect, vi, beforeEach } from 'vitest';

// ── Mock: vscode ────────────────────────────────────────────────────────────
// vi.mock is hoisted to the top of the file, so all mock implementations
// must be defined inline within the factory function.

vi.mock('vscode', () => {
    // Matches real VS Code: Markup = 1, Code = 2
    const NotebookCellKind = { Markup: 1, Code: 2 };

    class NotebookCellData {
        kind: number;
        value: string;
        languageId: string;

        constructor(kind: number, value: string, languageId: string) {
            this.kind = kind;
            this.value = value;
            this.languageId = languageId;
        }
    }

    class NotebookData {
        cells: InstanceType<typeof NotebookCellData>[];
        metadata: Record<string, unknown> | undefined;

        constructor(cells: InstanceType<typeof NotebookCellData>[]) {
            this.cells = cells;
        }
    }

    return {
        NotebookCellKind,
        NotebookCellData,
        NotebookData,
        workspace: {
            registerNotebookSerializer: vi.fn(),
        },
    };
});

// ── Import after mocks ─────────────────────────────────────────────────────

import { DataverseNotebookSerializer } from '../DataverseNotebookSerializer.js';

// ── Constants matching vscode.NotebookCellKind ─────────────────────────────

const CellKindCode = 2;
const CellKindMarkup = 1;

// ── Helpers ─────────────────────────────────────────────────────────────────

const token = { isCancellationRequested: false, onCancellationRequested: vi.fn() } as never;

function encode(text: string): Uint8Array {
    return new TextEncoder().encode(text);
}

function decode(data: Uint8Array): string {
    return new TextDecoder().decode(data);
}

describe('DataverseNotebookSerializer', () => {
    let serializer: DataverseNotebookSerializer;

    beforeEach(() => {
        serializer = new DataverseNotebookSerializer();
    });

    it('round-trips SQL cells with metadata', async () => {
        const input = JSON.stringify({
            metadata: {
                environmentId: 'env-123',
                environmentName: 'Production',
                environmentUrl: 'https://org.crm.dynamics.com',
            },
            cells: [
                { kind: 'sql', source: 'SELECT name FROM account' },
            ],
        });

        const notebook = await serializer.deserializeNotebook(encode(input), token);
        const output = await serializer.serializeNotebook(notebook as never, token);
        const parsed = JSON.parse(decode(output));

        expect(parsed.metadata.environmentId).toBe('env-123');
        expect(parsed.metadata.environmentName).toBe('Production');
        expect(parsed.metadata.environmentUrl).toBe('https://org.crm.dynamics.com');
        expect(parsed.cells).toHaveLength(1);
        expect(parsed.cells[0].kind).toBe('sql');
        expect(parsed.cells[0].source).toBe('SELECT name FROM account');
    });

    it('round-trips FetchXML cells', async () => {
        const fetchXml = '<fetch><entity name="account"><attribute name="name" /></entity></fetch>';
        const input = JSON.stringify({
            metadata: {},
            cells: [
                { kind: 'fetchxml', source: fetchXml },
            ],
        });

        const notebook = await serializer.deserializeNotebook(encode(input), token);
        expect(notebook.cells[0].languageId).toBe('fetchxml');
        expect(notebook.cells[0].kind).toBe(CellKindCode);

        const output = await serializer.serializeNotebook(notebook as never, token);
        const parsed = JSON.parse(decode(output));

        expect(parsed.cells).toHaveLength(1);
        expect(parsed.cells[0].kind).toBe('fetchxml');
        expect(parsed.cells[0].source).toBe(fetchXml);
    });

    it('round-trips markdown cells', async () => {
        const input = JSON.stringify({
            metadata: {},
            cells: [
                { kind: 'markdown', source: '# My Notebook\nThis is a test.' },
            ],
        });

        const notebook = await serializer.deserializeNotebook(encode(input), token);
        expect(notebook.cells[0].kind).toBe(CellKindMarkup);
        expect(notebook.cells[0].languageId).toBe('markdown');

        const output = await serializer.serializeNotebook(notebook as never, token);
        const parsed = JSON.parse(decode(output));

        expect(parsed.cells).toHaveLength(1);
        expect(parsed.cells[0].kind).toBe('markdown');
        expect(parsed.cells[0].source).toBe('# My Notebook\nThis is a test.');
    });

    it('round-trips mixed cell types', async () => {
        const input = JSON.stringify({
            metadata: { environmentName: 'Dev' },
            cells: [
                { kind: 'markdown', source: '# Query accounts' },
                { kind: 'sql', source: 'SELECT TOP 5 name FROM account' },
                { kind: 'fetchxml', source: '<fetch><entity name="contact" /></fetch>' },
                { kind: 'markdown', source: '## Done' },
            ],
        });

        const notebook = await serializer.deserializeNotebook(encode(input), token);
        expect(notebook.cells).toHaveLength(4);
        expect(notebook.cells[0].kind).toBe(CellKindMarkup);
        expect(notebook.cells[1].kind).toBe(CellKindCode);
        expect(notebook.cells[2].kind).toBe(CellKindCode);
        expect(notebook.cells[3].kind).toBe(CellKindMarkup);

        const output = await serializer.serializeNotebook(notebook as never, token);
        const parsed = JSON.parse(decode(output));

        expect(parsed.cells).toHaveLength(4);
        expect(parsed.cells[0].kind).toBe('markdown');
        expect(parsed.cells[1].kind).toBe('sql');
        expect(parsed.cells[2].kind).toBe('fetchxml');
        expect(parsed.cells[3].kind).toBe('markdown');
    });

    it('handles empty files gracefully', async () => {
        const notebook = await serializer.deserializeNotebook(encode(''), token);

        expect(notebook.cells).toHaveLength(1);
        expect(notebook.cells[0].kind).toBe(CellKindCode);
        expect(notebook.cells[0].languageId).toBe('sql');
        expect(notebook.cells[0].value).toContain('SELECT TOP 10 * FROM account');
    });

    it('handles corrupt JSON gracefully', async () => {
        const notebook = await serializer.deserializeNotebook(encode('{ broken json !!!'), token);

        expect(notebook.cells).toHaveLength(1);
        expect(notebook.cells[0].kind).toBe(CellKindCode);
        expect(notebook.cells[0].languageId).toBe('sql');
        expect(notebook.cells[0].value).toContain('SELECT TOP 10 * FROM account');
    });

    it('ensures at least one cell exists when cells array is empty', async () => {
        const input = JSON.stringify({
            metadata: { environmentId: 'env-456' },
            cells: [],
        });

        const notebook = await serializer.deserializeNotebook(encode(input), token);

        expect(notebook.cells).toHaveLength(1);
        expect(notebook.cells[0].kind).toBe(CellKindCode);
        expect(notebook.cells[0].languageId).toBe('sql');
        expect(notebook.cells[0].value).toContain('SELECT TOP 10 * FROM account');
        // Metadata should still be preserved even when cells are empty
        expect(notebook.metadata?.environmentId).toBe('env-456');
    });

    it('preserves environment metadata through round-trip', async () => {
        const input = JSON.stringify({
            metadata: {
                environmentId: 'env-789',
                environmentName: 'Staging',
                environmentUrl: 'https://staging.crm.dynamics.com',
            },
            cells: [
                { kind: 'sql', source: 'SELECT 1' },
            ],
        });

        const notebook = await serializer.deserializeNotebook(encode(input), token);

        // Verify metadata on deserialized notebook
        expect(notebook.metadata?.environmentId).toBe('env-789');
        expect(notebook.metadata?.environmentName).toBe('Staging');
        expect(notebook.metadata?.environmentUrl).toBe('https://staging.crm.dynamics.com');

        // Serialize and verify metadata is preserved
        const output = await serializer.serializeNotebook(notebook as never, token);
        const parsed = JSON.parse(decode(output));

        expect(parsed.metadata.environmentId).toBe('env-789');
        expect(parsed.metadata.environmentName).toBe('Staging');
        expect(parsed.metadata.environmentUrl).toBe('https://staging.crm.dynamics.com');
    });
});
