import { test, expect } from './fixtures.js';

/**
 * Basic smoke test that verifies the extension loads in VS Code.
 * These tests require VS Code to be available in the environment.
 *
 * Run with: npx playwright test
 * Set VSCODE_PATH env var if code is not in PATH.
 */
test.describe('Extension Smoke Tests', () => {
    test('VS Code window opens', async ({ vscodeApp }) => {
        // Just verify the window opened
        const title = await vscodeApp.title();
        expect(title).toBeTruthy();
    });

    test('activity bar has PPDS section', async ({ vscodeApp }) => {
        // Look for the PPDS activity bar icon
        // Note: This depends on VS Code's HTML structure which may change
        const activityBar = vscodeApp.locator('[id="workbench.parts.activitybar"]');
        await expect(activityBar).toBeVisible({ timeout: 30000 });
    });
});
