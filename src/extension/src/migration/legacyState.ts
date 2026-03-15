import * as vscode from 'vscode';

/**
 * Migrates state from the archived Power Platform Dev Suite extension.
 * Clears globalState keys and workspaceState keys from the old extension
 * that would otherwise persist in VS Code's storage forever.
 *
 * Runs once, gated by the ppds.legacyStateCleaned flag.
 */
export function migrateLegacyState(context: vscode.ExtensionContext): void {
    const FLAG_KEY = 'ppds.legacyStateCleaned';

    if (context.globalState.get<boolean>(FLAG_KEY)) {
        return; // Already migrated
    }

    // Clear old extension's environment storage
    void context.globalState.update('power-platform-dev-suite-environments', undefined);

    // Clear old panel state keys from workspace state
    // VS Code doesn't expose a way to list all keys, so we clear known patterns
    const knownPanelStateKeys = [
        'panel-state-dataExplorer',
        'panel-state-solutions',
        'panel-state-importJobs',
        'panel-state-connectionRefs',
        'panel-state-envVars',
        'panel-state-pluginTraces',
        'panel-state-metadataBrowser',
        'panel-state-webResources',
        'panel-state-settings',
    ];

    for (const key of knownPanelStateKeys) {
        void context.workspaceState.update(key, undefined);
    }

    // Mark migration as complete
    void context.globalState.update(FLAG_KEY, true);
}
