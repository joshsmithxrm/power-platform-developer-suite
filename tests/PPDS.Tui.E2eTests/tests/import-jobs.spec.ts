import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

const settle = (ms = 150) => new Promise(r => setTimeout(r, ms));

async function navigateToImportJobs(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  terminal.write('\x1bt');  // Alt+T to open Tools menu
  await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
  await settle();
  // Down 2: SQL Query → Solutions → Import Jobs
  for (let i = 0; i < 2; i++) {
    terminal.write('\x1b[B');
    await settle();
  }
  terminal.write('\r');  // Enter to select

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
