import { test as base, type Page } from '@playwright/test';
import { downloadAndUnzipVSCode } from '@vscode/test-electron';
import { _electron as electron, type ElectronApplication } from '@playwright/test';
import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs';

/**
 * Custom test fixtures for VS Code extension E2E testing.
 * Downloads VS Code if needed, launches with extension loaded.
 */
export const test = base.extend<{ vscodeApp: ElectronApplication; page: Page }>({
    // eslint-disable-next-line no-empty-pattern
    vscodeApp: async ({}, use) => {
        const extensionPath = path.resolve(__dirname, '..');

        // Download VS Code if not cached
        const vscodeExecutablePath = await downloadAndUnzipVSCode();

        // Create a temporary user data directory for isolation
        const tmpUserDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ppds-e2e-'));

        const electronApp = await electron.launch({
            executablePath: vscodeExecutablePath,
            args: [
                '--extensionDevelopmentPath=' + extensionPath,
                '--no-sandbox',
                '--disable-gpu',
                '--user-data-dir=' + tmpUserDataDir,
            ],
        });

        try {
            await use(electronApp);
        } finally {
            await electronApp.close();
            try {
                fs.rmSync(tmpUserDataDir, { recursive: true, force: true });
            } catch {
                // VS Code may still have file handles open on Windows — ignore cleanup failure
            }
        }
    },

    page: async ({ vscodeApp }, use) => {
        const window = await vscodeApp.firstWindow();
        // Wait for VS Code workbench to be ready before running tests
        await window.waitForSelector('[id="workbench.parts.editor"]', { timeout: 60000 });
        await use(window);
    },
});

export { expect } from '@playwright/test';
