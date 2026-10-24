import { expect, test } from './fixtures.js';

const PPDS_ACTIVITY_NAME = 'Power Platform Developer Suite';

test.beforeEach(async ({ page }) => {
    await page.keyboard.press('Escape');
});

test('VS Code workbench opens with the PPDS development extension', async ({ page }) => {
    await expect(page).toHaveTitle(/Extension Development Host/);
    await expect(page.getByRole('tab', { name: PPDS_ACTIVITY_NAME, exact: true })).toBeVisible();
});

test('PPDS activity view activates the extension and registers its tree views', async ({ page }) => {
    await page.getByRole('tab', { name: PPDS_ACTIVITY_NAME, exact: true }).click();

    await expect(page.getByText('Tools', { exact: true })).toBeVisible();
    await expect(page.getByText('Profiles', { exact: true })).toBeVisible();
});

test('PPDS commands are available from the command palette', async ({ page }) => {
    await page.keyboard.press('Control+KeyP');
    const commandInput = page.locator('.quick-input-widget input');
    await expect(commandInput).toBeVisible();
    await commandInput.fill('>PPDS: Open Data Explorer');

    await expect(page.getByText('PPDS: Open Data Explorer', { exact: true }).first()).toBeVisible();
});
