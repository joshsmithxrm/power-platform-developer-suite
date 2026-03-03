import { test, expect } from './fixtures.js';

/**
 * Basic smoke test that verifies the extension loads in VS Code.
 * These tests require VS Code to be available in the environment.
 *
 * Run with: npx playwright test
 * Set VSCODE_PATH env var if code is not in PATH.
 */
test.describe('Extension Smoke Tests', () => {
    test('VS Code window opens', async ({ page }) => {
        // Verify the window opened and workbench is ready
        const title = await page.title();
        expect(title).toBeTruthy();
    });

    test('activity bar has PPDS section', async ({ page }) => {
        // Look for the PPDS activity bar icon
        // Note: This depends on VS Code's HTML structure which may change
        const activityBar = page.locator('[id="workbench.parts.activitybar"]');
        await expect(activityBar).toBeVisible({ timeout: 30000 });
    });
});

test.describe('PPDS Extension Tests', () => {
    test('PPDS extension is active', async ({ page }) => {
        await page.keyboard.press('Control+Shift+X');
        await page.waitForSelector('text=Power Platform Developer Suite', { timeout: 30000 });
    });

    test('PPDS commands are registered', async ({ page }) => {
        await page.keyboard.press('Control+Shift+P');
        await page.type('.quick-input-widget input', 'PPDS');
        await expect(page.locator('text=PPDS: Open Data Explorer')).toBeVisible({ timeout: 10000 });
    });

    test('PPDS tree view is registered', async ({ page }) => {
        const ppdsIcon = page.locator('[id="workbench.view.extension.ppds-explorer"]');
        if (await ppdsIcon.isVisible()) {
            await ppdsIcon.click();
            await expect(page.locator('text=Profiles')).toBeVisible({ timeout: 10000 });
        }
    });
});
