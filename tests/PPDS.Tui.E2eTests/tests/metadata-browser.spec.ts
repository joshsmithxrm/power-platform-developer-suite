import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath, openToolsMenuItem, settle } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

async function navigateToMetadataBrowser(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  await openToolsMenuItem(terminal, 'Metadata Browser');

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
    await expect(terminal.getByText('Relationships')).toBeVisible();
    await expect(terminal.getByText('Keys')).toBeVisible();
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
