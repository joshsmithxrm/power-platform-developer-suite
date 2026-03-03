import { test as base } from '@playwright/test';
import { downloadAndUnzipVSCode, resolveCliArgsFromVSCodeExecutablePath } from '@vscode/test-electron';
import { _electron as electron } from '@playwright/test';
import * as path from 'path';

/**
 * Custom test fixtures for VS Code extension E2E testing.
 * Downloads VS Code if needed, launches with extension loaded.
 */
export const test = base.extend<{ vscodeApp: any }>({
    // eslint-disable-next-line no-empty-pattern
    vscodeApp: async ({}, use) => {
        const extensionPath = path.resolve(__dirname, '..');

        // Download VS Code if not cached
        const vscodeExecutablePath = await downloadAndUnzipVSCode();
        const [cliPath] = resolveCliArgsFromVSCodeExecutablePath(vscodeExecutablePath);

        // Resolve actual Electron binary (not the CLI wrapper)
        const electronPath = process.platform === 'win32'
            ? path.resolve(path.dirname(vscodeExecutablePath), 'Code.exe')
            : vscodeExecutablePath;

        const electronApp = await electron.launch({
            executablePath: electronPath,
            args: [
                '--extensionDevelopmentPath=' + extensionPath,
                '--disable-extensions',
                '--no-sandbox',
            ],
        });
        const window = await electronApp.firstWindow();
        await use(window);
        await electronApp.close();
    },
});

export { expect } from '@playwright/test';
