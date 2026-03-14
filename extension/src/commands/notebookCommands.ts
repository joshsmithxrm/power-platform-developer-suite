import * as vscode from 'vscode';
import type { DataverseNotebookController } from '../notebooks/DataverseNotebookController.js';
import type { DaemonClient } from '../daemonClient.js';

/**
 * Creates a new .ppdsnb notebook with example cells.
 */
export async function createNewNotebook(): Promise<void> {
    const notebook = await vscode.workspace.openNotebookDocument(
        'ppdsnb',
        new vscode.NotebookData([
            new vscode.NotebookCellData(
                vscode.NotebookCellKind.Markup,
                '# Power Platform Developer Suite Notebook\n\nSelect an environment using the status bar picker, then write SQL or FetchXML queries.\n\n**Tip:** Use the toggle button in the cell toolbar to convert between SQL and FetchXML.',
                'markdown'
            ),
            new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code,
                '-- SQL Example\nSELECT TOP 10\n    accountid,\n    name,\n    createdon\nFROM account\nORDER BY createdon DESC',
                'sql'
            ),
            new vscode.NotebookCellData(
                vscode.NotebookCellKind.Code,
                '<!-- FetchXML Example -->\n<fetch top="10">\n  <entity name="account">\n    <attribute name="accountid" />\n    <attribute name="name" />\n    <attribute name="createdon" />\n    <order attribute="createdon" descending="true" />\n  </entity>\n</fetch>',
                'fetchxml'
            ),
        ])
    );
    await vscode.window.showNotebookDocument(notebook);
}

/**
 * Toggles active cell between SQL and FetchXML.
 * SQL -> FetchXML: calls query/sql with showFetchXml=true to transpile.
 * FetchXML -> SQL: switches language only (no server-side transpiler for this direction yet).
 */
export async function toggleCellLanguage(daemon: DaemonClient): Promise<void> {
    const editor = vscode.window.activeNotebookEditor;
    if (editor?.notebook.notebookType !== 'ppdsnb') return;

    const selections = editor.selections;
    const firstSelection = selections[0];
    if (!firstSelection) return;

    const cell = editor.notebook.cellAt(firstSelection.start);
    if (cell.kind !== vscode.NotebookCellKind.Code) return;

    const currentLanguage = cell.document.languageId;
    const content = cell.document.getText().trim();

    if (!content) {
        const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
        await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
        return;
    }

    try {
        if (currentLanguage !== 'fetchxml' && !content.startsWith('<')) {
            const result = await daemon.queryExplain({ sql: content });
            if (result.plan) {
                const edit = new vscode.WorkspaceEdit();
                const fullRange = new vscode.Range(
                    cell.document.positionAt(0),
                    cell.document.positionAt(cell.document.getText().length)
                );
                edit.replace(cell.document.uri, fullRange, result.plan);
                await vscode.workspace.applyEdit(edit);
                await vscode.languages.setTextDocumentLanguage(cell.document, 'fetchxml');
                return;
            }
        }
        const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
        await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
    } catch {
        vscode.window.showWarningMessage('Could not transpile. Language toggled without conversion.');
        const newLang = currentLanguage === 'fetchxml' ? 'sql' : 'fetchxml';
        await vscode.languages.setTextDocumentLanguage(cell.document, newLang);
    }
}

/**
 * Opens a query from a notebook cell in the Data Explorer webview panel.
 */
export function openCellInDataExplorer(openQueryPanel: (sql: string) => void): void {
    const editor = vscode.window.activeNotebookEditor;
    if (editor?.notebook.notebookType !== 'ppdsnb') return;

    const selections = editor.selections;
    const firstSelection = selections[0];
    if (!firstSelection) return;

    const cell = editor.notebook.cellAt(firstSelection.start);
    if (cell.kind !== vscode.NotebookCellKind.Code) return;

    openQueryPanel(cell.document.getText());
}

/**
 * Exports cell results to CSV or JSON file.
 */
export async function exportCellResults(
    controller: DataverseNotebookController,
    format: 'csv' | 'json'
): Promise<void> {
    const editor = vscode.window.activeNotebookEditor;
    if (!editor || editor.notebook.notebookType !== 'ppdsnb') {
        vscode.window.showWarningMessage('This command is only available for Dataverse notebooks.');
        return;
    }

    const firstSelection = editor.selections[0];
    if (!firstSelection) { vscode.window.showWarningMessage('No cell selected.'); return; }

    const cell = editor.notebook.cellAt(firstSelection.start);
    if (cell.kind !== vscode.NotebookCellKind.Code) { vscode.window.showWarningMessage('Select a code cell.'); return; }

    const cellUri = cell.document.uri.toString();
    const results = controller.getCellResults(cellUri);
    if (!results) { vscode.window.showWarningMessage('No results to export. Execute the cell first.'); return; }
    if (results.records.length === 0) { vscode.window.showWarningMessage('Query returned no results.'); return; }

    const entityName = results.entityName ?? 'query_results';
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    let content: string;
    let filename: string;
    let filterName: string;

    if (format === 'csv') {
        const headers = results.columns.map(c => c.alias ?? c.logicalName);
        const rows = results.records.map(record =>
            results.columns.map(col => {
                const key = col.alias ?? col.logicalName;
                const val = record[key];
                if (val === null || val === undefined) return '';
                if (typeof val === 'object' && val !== null && 'entityId' in val) {
                    const lookup = val as { formatted?: string; value?: string };
                    return String(lookup.formatted ?? lookup.value ?? '');
                }
                if (typeof val === 'object' && 'formatted' in val) return String((val as { formatted: string }).formatted ?? '');
                return String(val);
            })
        );
        content = [headers, ...rows].map(row => row.map(cell => `"${cell.replace(/"/g, '""')}"`).join(',')).join('\n');
        filename = `${entityName}_${timestamp}.csv`;
        filterName = 'CSV Files';
    } else {
        const jsonArray = results.records.map(record => {
            const obj: Record<string, unknown> = {};
            for (const col of results.columns) {
                obj[col.alias ?? col.logicalName] = record[col.alias ?? col.logicalName];
            }
            return obj;
        });
        content = JSON.stringify(jsonArray, null, 2);
        filename = `${entityName}_${timestamp}.json`;
        filterName = 'JSON Files';
    }

    const uri = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.file(filename),
        filters: { [filterName]: [format] },
    });

    if (uri) {
        await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(content));
        vscode.window.showInformationMessage(`Exported ${results.records.length} rows to ${uri.fsPath}`);
    }
}

/**
 * Opens a query in a new notebook (called from Data Explorer "Open in Notebook").
 */
export async function openQueryInNotebook(
    sql: string,
    environmentName?: string,
    environmentUrl?: string
): Promise<void> {
    const isFetchXml = sql.trim().startsWith('<');
    const language = isFetchXml ? 'fetchxml' : 'sql';

    const notebookData = new vscode.NotebookData([
        new vscode.NotebookCellData(
            vscode.NotebookCellKind.Markup,
            `# Query Notebook\n\n**Environment:** ${environmentName ?? 'Not selected'}`,
            'markdown'
        ),
        new vscode.NotebookCellData(vscode.NotebookCellKind.Code, sql.trim(), language),
    ]);

    notebookData.metadata = { environmentName, environmentUrl };

    const notebook = await vscode.workspace.openNotebookDocument('ppdsnb', notebookData);
    await vscode.window.showNotebookDocument(notebook);
}
