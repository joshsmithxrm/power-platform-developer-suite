import * as vscode from 'vscode';

/**
 * Typed accessors for PPDS extension configuration.
 * Centralizes all `vscode.workspace.getConfiguration('ppds')` reads
 * so new panels don't scatter inline config reads across the codebase.
 *
 * Each function reads fresh from workspace config (no caching) so
 * configuration changes take effect immediately.
 */

function cfg(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration('ppds');
}

/** Default TOP clause for queries (default: 100, range: 1-5000). */
export function queryDefaultTop(): number {
    return cfg().get<number>('queryDefaultTop', 100);
}
