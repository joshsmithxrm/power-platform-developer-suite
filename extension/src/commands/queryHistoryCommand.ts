import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { QueryHistoryEntryDto } from '../types.js';

// ── Button constants (identity-comparable) ───────────────────────────────────

export const RUN_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('play'),
    tooltip: 'Run this query in Data Explorer',
};
export const NOTEBOOK_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('notebook'),
    tooltip: 'Open in Notebook',
};
export const COPY_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('copy'),
    tooltip: 'Copy SQL to clipboard',
};
export const DELETE_BUTTON: vscode.QuickInputButton = {
    iconPath: new vscode.ThemeIcon('trash'),
    tooltip: 'Delete from history',
};

interface HistoryQuickPickItem extends vscode.QuickPickItem {
    entry: QueryHistoryEntryDto;
}

/**
 * Returns a relative date label for grouping history entries.
 */
function getRelativeDateLabel(date: Date): string {
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
    if (diffDays === 0) return 'Today';
    if (diffDays === 1) return 'Yesterday';
    if (diffDays < 7) return 'This Week';
    return 'Older';
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
    const sqlPreview = entry.sql.replace(/\s+/g, ' ').trim().substring(0, 80);
    const parts: string[] = [];
    if (entry.rowCount !== null) parts.push(`${entry.rowCount.toLocaleString()} rows`);
    if (entry.executionTimeMs !== null && entry.executionTimeMs !== undefined) parts.push(`${entry.executionTimeMs}ms`);

    return {
        label: `$(history) ${sqlPreview}${entry.sql.length > 80 ? '...' : ''}`,
        description: parts.length > 0 ? parts.join(' · ') : '',
        detail: `${dateStr} — ${entry.sql}`,
        entry,
        buttons: [RUN_BUTTON, NOTEBOOK_BUTTON, COPY_BUTTON, DELETE_BUTTON],
    };
}

/**
 * Shows the query history as a QuickPick with run, copy, notebook, and delete actions.
 * Returns the selected SQL string (or undefined if cancelled).
 *
 * Entries are grouped by relative date (Today, Yesterday, This Week, Older)
 * using VS Code's separator feature for visual clarity.
 */
export async function showQueryHistory(daemon: DaemonClient): Promise<string | undefined> {
    const result = await daemon.queryHistoryList(undefined, 100);

    if (result.entries.length === 0) {
        vscode.window.showInformationMessage('No query history found.');
        return undefined;
    }

    // Build items with date-group separators
    const rawItems = result.entries.map(buildHistoryItem);
    const items: (HistoryQuickPickItem | vscode.QuickPickItem)[] = [];
    let lastGroup = '';
    for (const item of rawItems) {
        const group = getRelativeDateLabel(new Date(item.entry.executedAt));
        if (group !== lastGroup) {
            items.push({ label: group, kind: vscode.QuickPickItemKind.Separator });
            lastGroup = group;
        }
        items.push(item);
    }

    return new Promise<string | undefined>((resolve) => {
        const quickPick = vscode.window.createQuickPick<HistoryQuickPickItem>();
        quickPick.title = `Query History (${result.entries.length} entries)`;
        quickPick.placeholder = 'Search queries — Enter to load, or use buttons';
        quickPick.items = items as HistoryQuickPickItem[];
        quickPick.matchOnDetail = true;

        let resolved = false;
        const disposables: vscode.Disposable[] = [];

        disposables.push(quickPick.onDidTriggerItemButton(async (e) => {
            const entry = e.item.entry;

            if (e.button === COPY_BUTTON) {
                await vscode.env.clipboard.writeText(entry.sql);
                vscode.window.showInformationMessage('SQL copied to clipboard');
            } else if (e.button === NOTEBOOK_BUTTON) {
                if (!resolved) { resolved = true; resolve(undefined); }
                quickPick.hide();
                await vscode.commands.executeCommand('ppds.openQueryInNotebook', entry.sql);
            } else if (e.button === DELETE_BUTTON) {
                const confirm = await vscode.window.showWarningMessage(
                    'Delete this history entry?', { modal: true }, 'Delete'
                );
                if (confirm === 'Delete') {
                    try {
                        await daemon.queryHistoryDelete(entry.id);
                        const refreshed = await daemon.queryHistoryList(undefined, 100);
                        const newItems: (HistoryQuickPickItem | vscode.QuickPickItem)[] = [];
                        let lg = '';
                        for (const ri of refreshed.entries.map(buildHistoryItem)) {
                            const g = getRelativeDateLabel(new Date(ri.entry.executedAt));
                            if (g !== lg) {
                                newItems.push({ label: g, kind: vscode.QuickPickItemKind.Separator });
                                lg = g;
                            }
                            newItems.push(ri);
                        }
                        quickPick.items = newItems as HistoryQuickPickItem[];
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
