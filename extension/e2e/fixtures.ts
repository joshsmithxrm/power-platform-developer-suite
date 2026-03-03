import { test as base, _electron as electron } from '@playwright/test';
import * as path from 'path';

/**
 * Custom test fixtures for VS Code extension E2E testing.
 * Launches VS Code with the extension loaded via --extensionDevelopmentPath.
 */
export const test = base.extend<{ vscodeApp: any }>({
    // eslint-disable-next-line no-empty-pattern
    vscodeApp: async ({}, use) => {
        const extensionPath = path.resolve(__dirname, '..');
        const electronApp = await electron.launch({
            executablePath: process.env.VSCODE_PATH || 'code',
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
