import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { QueryHistoryEntryDto } from '../types.js';

// ── Button constants (identity-comparable) ───────────────────────────────────

export const RUN_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('play'),
    tooltip: 'Run this query',
};
export const COPY_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('copy'),
    tooltip: 'Copy SQL',
};
export const DELETE_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('trash'),
    tooltip: 'Delete',
};

interface HistoryQuickPickItem extends vscode.QuickPickItem {
    entry: QueryHistoryEntryDto;
}

/**
 * Builds a QuickPickItem for a single history entry.
 */
export function buildHistoryItem(entry: QueryHistoryEntryDto): HistoryQuickPickItem {
    const date = new Date(entry.executedAt);
    const dateStr = date.toLocaleString(undefined, {
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
    });
    const sqlPreview = entry.sql.replace(/\s+/g, ' ').trim().substring(0, 50);
    const rowInfo = entry.rowCount !== null ? `(${entry.rowCount.toLocaleString()} rows)` : '';

    return {
        label: `[${dateStr}] ${sqlPreview}${entry.sql.length > 50 ? '...' : ''}`,
        description: rowInfo,
        detail: entry.sql,
        entry,
        buttons: [RUN_BUTTON, COPY_BUTTON, DELETE_BUTTON],
    };
}

/**
 * Shows the query history as a QuickPick with run, copy, and delete actions.
 * Returns the selected SQL string (or undefined if cancelled).
 */
export async function showQueryHistory(daemon: DaemonClient): Promise<string | undefined> {
    const result = await daemon.queryHistoryList(undefined, 50);

    if (result.entries.length === 0) {
        vscode.window.showInformationMessage('No query history found.');
        return undefined;
    }

    const items: HistoryQuickPickItem[] = result.entries.map(buildHistoryItem);

    return new Promise<string | undefined>((resolve) => {
        const quickPick = vscode.window.createQuickPick<HistoryQuickPickItem>();
        quickPick.title = 'Query History';
        quickPick.placeholder = 'Select a query to load';
        quickPick.items = items;
        quickPick.matchOnDetail = true;

        let resolved = false;
        const disposables: vscode.Disposable[] = [];

        disposables.push(quickPick.onDidTriggerItemButton(async (e) => {
            const entry = e.item.entry;

            if (e.button === COPY_BUTTON) {
                await vscode.env.clipboard.writeText(entry.sql);
                vscode.window.showInformationMessage('SQL copied to clipboard');
            } else if (e.button === DELETE_BUTTON) {
                const confirm = await vscode.window.showWarningMessage(
                    'Delete this history entry?', { modal: true }, 'Delete'
                );
                if (confirm === 'Delete') {
                    try {
                        await daemon.queryHistoryDelete(entry.id);
                        const refreshed = await daemon.queryHistoryList(undefined, 50);
                        quickPick.items = refreshed.entries.map(buildHistoryItem);
                    } catch (err) {
                        const msg = err instanceof Error ? err.message : String(err);
                        vscode.window.showErrorMessage(`Failed to delete history entry: ${msg}`);
                    }
                }
            } else if (e.button === RUN_BUTTON) {
                if (!resolved) { resolved = true; resolve(entry.sql); }
                quickPick.hide();
            }
        }));

        disposables.push(quickPick.onDidAccept(() => {
            const selected = quickPick.selectedItems[0];
            if (!resolved) { resolved = true; resolve(selected?.entry.sql); }
            quickPick.hide();
        }));

        disposables.push(quickPick.onDidHide(() => {
            disposables.forEach(d => d.dispose());
            quickPick.dispose();
            if (!resolved) { resolved = true; resolve(undefined); }
        }));

        quickPick.show();
    });
}
