import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';

/**
 * Available environment types. These map to the EnvironmentType enum
 * in the C# backend.
 */
const ENVIRONMENT_TYPES = [
    'Unknown',
    'Production',
    'Sandbox',
    'Development',
    'Test',
    'Trial',
] as const;

/**
 * Available environment colors. These map to the EnvironmentColor enum
 * in the C# backend (16-color terminal palette).
 */
const ENVIRONMENT_COLORS = [
    'Red',
    'Green',
    'Yellow',
    'Cyan',
    'Blue',
    'Gray',
    'Brown',
    'White',
    'BrightRed',
    'BrightGreen',
    'BrightYellow',
    'BrightCyan',
    'BrightBlue',
] as const;

/**
 * Registers the environment configuration command.
 *
 * Command: ppds.configureEnvironment
 *
 * Multi-step flow:
 * 1. Get current config from daemon
 * 2. InputBox for Label (free text)
 * 3. QuickPick for Type
 * 4. QuickPick for Color
 * 5. Save via daemon
 * 6. Refresh environment display
 */
export function registerEnvironmentConfigCommand(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
    onConfigChanged: () => void,
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.configureEnvironment', async (environmentUrl?: string) => {
            try {
                // If no URL passed, get from active environment
                if (!environmentUrl) {
                    const who = await daemonClient.authWho();
                    if (!who.environment?.url) {
                        vscode.window.showInformationMessage(
                            'No environment is currently selected. Use "PPDS: Select Environment" first.',
                        );
                        return;
                    }
                    environmentUrl = who.environment.url;
                }

                // Step 1: Get current config
                let currentLabel: string | undefined;
                let currentType: string | undefined;
                let currentColor: string | undefined;
                try {
                    const currentConfig = await daemonClient.envConfigGet(environmentUrl);
                    currentLabel = currentConfig.label ?? undefined;
                    currentType = currentConfig.type ?? undefined;
                    currentColor = currentConfig.color ?? undefined;
                } catch {
                    // Config may not exist yet — proceed with defaults
                }

                // Step 2: Label (free text InputBox)
                const label = await vscode.window.showInputBox({
                    title: 'Configure Environment (1/3): Label',
                    prompt: 'Enter a short label for the status bar (leave empty to use default)',
                    value: currentLabel ?? '',
                    placeHolder: 'e.g., My Dev Org',
                });

                if (label === undefined) {
                    return; // User cancelled
                }

                // Step 3: Type (QuickPick)
                const typeItems: vscode.QuickPickItem[] = ENVIRONMENT_TYPES.map(t => ({
                    label: t,
                    description: t === currentType ? '(current)' : undefined,
                    picked: t === currentType,
                }));

                const typePick = await vscode.window.showQuickPick(typeItems, {
                    title: 'Configure Environment (2/3): Type',
                    placeHolder: currentType
                        ? `Current type: ${currentType}`
                        : 'Select environment type',
                });

                if (typePick === undefined) {
                    return; // User cancelled
                }

                const selectedType = typePick.label;

                // Step 4: Color (QuickPick)
                const colorItems: vscode.QuickPickItem[] = [
                    {
                        label: '$(circle-slash) (Use type default)',
                        description: !currentColor ? '(current)' : undefined,
                    },
                    ...ENVIRONMENT_COLORS.map(c => ({
                        label: `$(circle-filled) ${c}`,
                        description: c === currentColor ? '(current)' : undefined,
                    })),
                ];

                const colorPick = await vscode.window.showQuickPick(colorItems, {
                    title: 'Configure Environment (3/3): Color',
                    placeHolder: currentColor
                        ? `Current color: ${currentColor}`
                        : 'Choose a color for this environment',
                });

                if (colorPick === undefined) {
                    return; // User cancelled
                }

                // Parse color selection
                const selectedColor = colorPick.label.startsWith('$(circle-slash)')
                    ? undefined
                    : colorPick.label.replace('$(circle-filled) ', '');

                // Step 5: Save via daemon
                // TODO: Wire to daemon when env/config/set TS types are finalized
                // For now the daemon client methods are available, so we call them directly.
                await daemonClient.envConfigSet({
                    environmentUrl,
                    label: label || undefined,
                    type: selectedType,
                    color: selectedColor,
                });

                // Step 6: Refresh environment display
                onConfigChanged();

                vscode.window.showInformationMessage(
                    `Environment configuration saved successfully.`,
                );
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to configure environment: ${message}`);
            }
        }),
    );
}
