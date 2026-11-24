import * as vscode from 'vscode';
import { ResponseError } from 'vscode-jsonrpc/node';

import type { DaemonClient } from '../daemonClient.js';
import { showErrorWithReport } from '../utils/errorNotify.js';

/** Daemon error code for a type change that lowers write protection and was not confirmed (#1829). */
export const PROTECTION_LOWERING_CONFIRMATION_REQUIRED = 'Environment.ProtectionLoweringConfirmationRequired';

/** The modal's confirming button. Anything else, including dismissing the modal, cancels. */
export const LOWER_PROTECTION_BUTTON = 'Lower protection';

type EnvConfigSetParams = Parameters<DaemonClient['envConfigSet']>[0];

/** True when the daemon refused a change because it lowers protection and was not confirmed. */
export function isProtectionLoweringConfirmationRequired(error: unknown): boolean {
    if (!(error instanceof ResponseError)) return false;
    const data: unknown = error.data;
    return !!data && typeof data === 'object'
        && (data as { code?: unknown }).code === PROTECTION_LOWERING_CONFIRMATION_REQUIRED;
}

/** Shows the modal warning; resolves true only when the user chose {@link LOWER_PROTECTION_BUTTON}. */
export async function confirmLowerProtection(serverMessage: string): Promise<boolean> {
    const choice = await vscode.window.showWarningMessage(
        'Lower this environment\'s write protection?',
        { modal: true, detail: serverMessage },
        LOWER_PROTECTION_BUTTON,
    );
    return choice === LOWER_PROTECTION_BUTTON;
}

/**
 * Saves the configuration. When the daemon answers that the change lowers write protection, asks the
 * user in a modal and resends with `confirmLowerProtection` only on an explicit yes. The daemon decides
 * what lowers protection; the extension never sends the acknowledgement unasked.
 */
export async function saveEnvironmentConfig(
    daemonClient: Pick<DaemonClient, 'envConfigSet'>,
    params: Omit<EnvConfigSetParams, 'confirmLowerProtection'>,
    confirm: (serverMessage: string) => Promise<boolean> = confirmLowerProtection,
): Promise<'saved' | 'cancelled'> {
    try {
        await daemonClient.envConfigSet(params);
        return 'saved';
    } catch (error) {
        if (!isProtectionLoweringConfirmationRequired(error)) throw error;
        const message = error instanceof Error ? error.message : String(error);
        if (!await confirm(message)) return 'cancelled';
        await daemonClient.envConfigSet({ ...params, confirmLowerProtection: params.environmentUrl });
        return 'saved';
    }
}

/**
 * Available environment types. These map to the EnvironmentType enum
 * in the C# backend.
 */
const ENVIRONMENT_TYPES = [
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
 * 5. Save via daemon; a type change that lowers write protection asks in a modal first
 * 6. Refresh environment display
 */
export function registerEnvironmentConfigCommand(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
    onConfigChanged: () => void,
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.configureEnvironment', async (itemOrUrl?: unknown) => {
            try {
                // Accept either a string URL or an EnvironmentTreeItem from context menu
                let environmentUrl: string | undefined;
                if (typeof itemOrUrl === 'string') {
                    environmentUrl = itemOrUrl;
                } else if (itemOrUrl && typeof itemOrUrl === 'object' && 'envUrl' in itemOrUrl) {
                    environmentUrl = (itemOrUrl as { envUrl: string }).envUrl;
                }

                // If no URL resolved, get from active environment
                if (!environmentUrl) {
                    const who = await daemonClient.authWho();
                    if (!who.environment?.url) {
                        vscode.window.showInformationMessage(
                            'No environment is currently selected. Select an environment first.',
                        );
                        return;
                    }
                    environmentUrl = who.environment.url;
                }

                // Step 1: Get current config
                let currentLabel: string | undefined;
                let currentType: string | undefined;
                let currentColor: string | undefined;
                let resolvedType: string | undefined;
                try {
                    const currentConfig = await daemonClient.envConfigGet(environmentUrl);
                    currentLabel = currentConfig.label ?? undefined;
                    currentType = currentConfig.type ?? undefined;
                    currentColor = currentConfig.color ?? undefined;
                    resolvedType = currentConfig.resolvedType ?? undefined;
                } catch {
                    // Config may not exist yet — proceed with defaults
                }

                // Step 2: Label (free text InputBox)
                const label = await vscode.window.showInputBox({
                    title: 'Configure Environment (1/3): Label',
                    prompt: 'Enter a short label for the status bar (leave empty to use default)',
                    value: currentLabel ?? '',
                    placeHolder: 'e.g., My Dev Org',
                    ignoreFocusOut: true,
                });

                if (label === undefined) {
                    return; // User cancelled
                }

                // Step 3: Type (QuickPick)
                // Pre-select: user's explicit type if set, otherwise the auto-detected type.
                // showQuickPick auto-focuses the first item, so move the current selection to the top.
                const effectiveType = currentType ?? resolvedType;
                const typeItems: vscode.QuickPickItem[] = ENVIRONMENT_TYPES.map(t => ({
                    label: t,
                    description: t === currentType ? '(current)' :
                        (!currentType && t === resolvedType) ? '(detected)' : undefined,
                }));
                // Move the active item to the top so VS Code auto-highlights it
                if (effectiveType) {
                    const activeIdx = typeItems.findIndex(t => t.label === effectiveType);
                    if (activeIdx > 0) {
                        const [active] = typeItems.splice(activeIdx, 1);
                        typeItems.unshift(active);
                    }
                }

                const typePick = await vscode.window.showQuickPick(typeItems, {
                    title: 'Configure Environment (2/3): Type',
                    placeHolder: currentType
                        ? `Current type: ${currentType}`
                        : 'Select environment type (or auto-detect)',
                    ignoreFocusOut: true,
                });

                if (typePick === undefined) {
                    return; // User cancelled
                }

                const selectedType = typePick.label;

                // Step 4: Color (QuickPick)
                // Build list with current selection moved to top for auto-highlight
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
                if (currentColor) {
                    const activeIdx = colorItems.findIndex(c => c.label.includes(currentColor));
                    if (activeIdx > 0) {
                        const [active] = colorItems.splice(activeIdx, 1);
                        colorItems.unshift(active);
                    }
                }

                const colorPick = await vscode.window.showQuickPick(colorItems, {
                    title: 'Configure Environment (3/3): Color',
                    placeHolder: currentColor
                        ? `Current color: ${currentColor}`
                        : 'Choose a color for this environment',
                    ignoreFocusOut: true,
                });

                if (colorPick === undefined) {
                    return; // User cancelled
                }

                // Parse color selection
                const selectedColor = colorPick.label.startsWith('$(circle-slash)')
                    ? undefined
                    : colorPick.label.replace('$(circle-filled) ', '');

                // Step 5: Save via daemon (asks first if the change lowers write protection)
                const outcome = await saveEnvironmentConfig(daemonClient, {
                    environmentUrl,
                    label: label || undefined,
                    type: selectedType,
                    color: selectedColor,
                });
                if (outcome === 'cancelled') {
                    vscode.window.showInformationMessage('Environment configuration not changed.');
                    return;
                }

                // Step 6: Refresh environment display
                onConfigChanged();

                vscode.window.showInformationMessage(
                    `Environment configuration saved successfully.`,
                );
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                void showErrorWithReport(`Failed to configure environment: ${message}`);
            }
        }),
    );
}
