import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { QueryHistoryEntryDto } from '../types.js';

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

    interface HistoryQuickPickItem extends vscode.QuickPickItem {
        entry: QueryHistoryEntryDto;
    }

    const items: HistoryQuickPickItem[] = result.entries.map(entry => {
        const date = new Date(entry.executedAt);
        const dateStr = `${String(date.getMonth() + 1).padStart(2, '0')}/${String(date.getDate()).padStart(2, '0')} ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
        const sqlPreview = entry.sql.replace(/\s+/g, ' ').trim().substring(0, 50);
        const rowInfo = entry.rowCount !== null ? `(${entry.rowCount.toLocaleString()} rows)` : '';

        return {
            label: `[${dateStr}] ${sqlPreview}${entry.sql.length > 50 ? '...' : ''}`,
            description: rowInfo,
            detail: entry.sql,
            entry,
            buttons: [
                { iconPath: new vscode.ThemeIcon('play'), tooltip: 'Run this query' },
                { iconPath: new vscode.ThemeIcon('copy'), tooltip: 'Copy SQL' },
                { iconPath: new vscode.ThemeIcon('trash'), tooltip: 'Delete' },
            ],
        };
    });

    return new Promise<string | undefined>((resolve) => {
        const quickPick = vscode.window.createQuickPick<HistoryQuickPickItem>();
        quickPick.title = 'Query History';
        quickPick.placeholder = 'Select a query to load';
        quickPick.items = items;
        quickPick.matchOnDetail = true;

        const disposables: vscode.Disposable[] = [];

        disposables.push(quickPick.onDidTriggerItemButton(async (e) => {
            const entry = e.item.entry;
            const buttonTooltip = (e.button as vscode.QuickInputButton & { tooltip: string }).tooltip;

            if (buttonTooltip === 'Copy SQL') {
                await vscode.env.clipboard.writeText(entry.sql);
                vscode.window.showInformationMessage('SQL copied to clipboard');
            } else if (buttonTooltip === 'Delete') {
                const confirm = await vscode.window.showWarningMessage(
                    'Delete this history entry?', { modal: true }, 'Delete'
                );
                if (confirm === 'Delete') {
                    try {
                        await daemon.queryHistoryDelete(entry.id);
                        const refreshed = await daemon.queryHistoryList(undefined, 50);
                        quickPick.items = refreshed.entries.map(e2 => {
                            const d = new Date(e2.executedAt);
                            const ds = `${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
                            const sp = e2.sql.replace(/\s+/g, ' ').trim().substring(0, 50);
                            return {
                                label: `[${ds}] ${sp}${e2.sql.length > 50 ? '...' : ''}`,
                                description: e2.rowCount !== null ? `(${e2.rowCount.toLocaleString()} rows)` : '',
                                detail: e2.sql,
                                entry: e2,
                                buttons: [
                                    { iconPath: new vscode.ThemeIcon('play'), tooltip: 'Run this query' },
                                    { iconPath: new vscode.ThemeIcon('copy'), tooltip: 'Copy SQL' },
                                    { iconPath: new vscode.ThemeIcon('trash'), tooltip: 'Delete' },
                                ],
                            };
                        });
                    } catch (err) {
                        const msg = err instanceof Error ? err.message : String(err);
                        vscode.window.showErrorMessage(`Failed to delete history entry: ${msg}`);
                    }
                }
            } else if (buttonTooltip === 'Run this query') {
                resolve(entry.sql);
                quickPick.hide();
            }
        }));

        disposables.push(quickPick.onDidAccept(() => {
            const selected = quickPick.selectedItems[0];
            resolve(selected?.entry.sql);
            quickPick.hide();
        }));

        disposables.push(quickPick.onDidHide(() => {
            disposables.forEach(d => d.dispose());
            quickPick.dispose();
            resolve(undefined);
        }));

        quickPick.show();
    });
}
