import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

const settle = (ms = 150) => new Promise(r => setTimeout(r, ms));

async function navigateToMetadataBrowser(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  terminal.write('\x1bt');  // Alt+T to open Tools menu
  await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
  await settle();
  // Down 7: SQL Query → Solutions → Import Jobs → Connection References → Environment Variables → Plugin Traces → Plugin Registration → Metadata Browser
  for (let i = 0; i < 7; i++) {
    terminal.write('\x1b[B');
    await settle();
  }
  terminal.write('\r');  // Enter to select

  await expect(terminal.getByText('Entities')).toBeVisible();
}

test.describe('Metadata Browser Screen', () => {
  test('navigates to Metadata Browser via Tools menu', async ({ terminal }) => {
    await navigateToMetadataBrowser(terminal);

    await expect(terminal.getByText('Details')).toBeVisible();
    await expect(terminal).toMatchSnapshot();
  });

  test('shows entity list and detail panels', async ({ terminal }) => {
    await navigateToMetadataBrowser(terminal);

    await expect(terminal.getByText('Entities')).toBeVisible();
    await expect(terminal.getByText('Details')).toBeVisible();
  });

  test('shows tab buttons for metadata categories', async ({ terminal }) => {
    await navigateToMetadataBrowser(terminal);

    await expect(terminal.getByText('Attributes')).toBeVisible();
  });

  test('Escape stays on Metadata Browser screen', async ({ terminal }) => {
    await navigateToMetadataBrowser(terminal);

    terminal.write('\x1b');  // Escape
    await settle();

    await expect(terminal.getByText('Entities')).toBeVisible();
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await navigateToMetadataBrowser(terminal);

    await expect(terminal.getByText('Profile:')).toBeVisible();
  });
});

test.describe('Metadata Browser Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await navigateToMetadataBrowser(terminal);

    terminal.write('\t');  // Tab to move focus
    await settle();

    await expect(terminal).toMatchSnapshot();
  });
});
