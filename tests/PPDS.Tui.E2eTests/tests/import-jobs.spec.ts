import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath, openToolsMenuItem, settle } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

async function navigateToImportJobs(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  await openToolsMenuItem(terminal, 'Import Jobs');

  // Wait for tab bar to show the screen (not the menu item text)
  await expect(terminal.getByText('1: Import Jobs')).toBeVisible();
}

test.describe('Import Jobs Screen', () => {
  test('navigates to Import Jobs via Tools menu', async ({ terminal }) => {
    await navigateToImportJobs(terminal);

    await expect(terminal).toMatchSnapshot();
  });

  test('Escape stays on Import Jobs screen', async ({ terminal }) => {
    await navigateToImportJobs(terminal);

    terminal.write('\x1b');  // Escape
    await settle();

    await expect(terminal.getByText('1: Import Jobs')).toBeVisible();
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await navigateToImportJobs(terminal);

    await expect(terminal.getByText('Profile:')).toBeVisible();
  });
});

test.describe('Import Jobs Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await navigateToImportJobs(terminal);

    terminal.write('\t');  // Tab to move focus
    await settle();

    await expect(terminal).toMatchSnapshot();
  });
});
