import * as vscode from 'vscode';

/** JSON format for .ppdsnb notebook files. */
interface NotebookFileData {
    metadata?: NotebookMetadata;
    cells: NotebookCellFileData[];
}

interface NotebookMetadata {
    environmentId?: string;
    environmentName?: string;
    environmentUrl?: string;
}

interface NotebookCellFileData {
    kind: 'sql' | 'fetchxml' | 'markdown';
    source: string;
}

export class DataverseNotebookSerializer implements vscode.NotebookSerializer {

    deserializeNotebook(content: Uint8Array, _token: vscode.CancellationToken): Promise<vscode.NotebookData> {
        const text = new TextDecoder().decode(content);

        if (!text.trim()) {
            return Promise.resolve(this.createEmptyNotebook());
        }

        try {
            const data: unknown = JSON.parse(text);
            if (!data || typeof data !== 'object' || !('cells' in data) || !Array.isArray((data as NotebookFileData).cells)) {
                vscode.window.showWarningMessage(
                    'Could not parse notebook file. Starting with empty notebook.'
                );
                return Promise.resolve(this.createEmptyNotebook());
            }
            return Promise.resolve(this.parseNotebookData(data as NotebookFileData));
        } catch {
            vscode.window.showWarningMessage(
                'Could not parse notebook file. Starting with empty notebook.'
            );
            return Promise.resolve(this.createEmptyNotebook());
        }
    }

    serializeNotebook(data: vscode.NotebookData, _token: vscode.CancellationToken): Promise<Uint8Array> {
        const notebookData: NotebookFileData = {
            metadata: this.extractMetadata(data),
            cells: data.cells.map(cell => this.serializeCell(cell)),
        };
        return Promise.resolve(new TextEncoder().encode(JSON.stringify(notebookData, null, 2)));
    }

    private createEmptyNotebook(): vscode.NotebookData {
        const cell = new vscode.NotebookCellData(
            vscode.NotebookCellKind.Code,
            '-- Write your SQL query here\nSELECT TOP 10 * FROM account',
            'sql'
        );
        const notebookData = new vscode.NotebookData([cell]);
        notebookData.metadata = { environmentId: undefined, environmentName: undefined };
        return notebookData;
    }

    private parseNotebookData(data: NotebookFileData): vscode.NotebookData {
        const cells = data.cells.map(cellData => {
            const kind = cellData.kind === 'markdown'
                ? vscode.NotebookCellKind.Markup
                : vscode.NotebookCellKind.Code;

            const language = cellData.kind === 'markdown' ? 'markdown'
                : cellData.kind === 'fetchxml' ? 'fetchxml'
                : 'sql';

            return new vscode.NotebookCellData(kind, cellData.source, language);
        });

        if (cells.length === 0) {
            cells.push(new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code,
                '-- Write your SQL query here\nSELECT TOP 10 * FROM account',
                'sql'
            ));
        }

        const notebookData = new vscode.NotebookData(cells);
        notebookData.metadata = this.safeMetadata(data.metadata);
        return notebookData;
    }

    private extractMetadata(data: vscode.NotebookData): NotebookMetadata {
        return this.safeMetadata(data.metadata);
    }

    private safeMetadata(metadata: unknown): NotebookMetadata {
        const values = metadata && typeof metadata === 'object' ? metadata as Record<string, unknown> : {};
        return {
            environmentId: typeof values.environmentId === 'string' ? values.environmentId : undefined,
            environmentName: typeof values.environmentName === 'string' ? values.environmentName : undefined,
            environmentUrl: typeof values.environmentUrl === 'string' ? values.environmentUrl : undefined,
        };
    }

    private serializeCell(cell: vscode.NotebookCellData): NotebookCellFileData {
        let kind: NotebookCellFileData['kind'];
        if (cell.kind === vscode.NotebookCellKind.Markup) {
            kind = 'markdown';
        } else if (cell.languageId === 'fetchxml' || cell.languageId === 'xml') {
            kind = 'fetchxml';
        } else {
            kind = 'sql';
        }
        return { kind, source: cell.value };
    }
}
