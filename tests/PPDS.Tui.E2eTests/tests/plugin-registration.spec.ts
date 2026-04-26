import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath, openToolsMenuItem, settle } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

async function navigateToPluginRegistration(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  await openToolsMenuItem(terminal, 'Plugin Registration');

  await expect(terminal.getByText('Registrations')).toBeVisible();
}

test.describe('Plugin Registration Screen', () => {
  test('navigates to Plugin Registration via Tools menu', async ({ terminal }) => {
    await navigateToPluginRegistration(terminal);

    await expect(terminal.getByText('Details')).toBeVisible();
    await expect(terminal).toMatchSnapshot();
  });

  test('shows tree view and detail panels', async ({ terminal }) => {
    await navigateToPluginRegistration(terminal);

    await expect(terminal.getByText('Registrations')).toBeVisible();
    await expect(terminal.getByText('Details')).toBeVisible();
  });

  test('Escape stays on Plugin Registration screen', async ({ terminal }) => {
    await navigateToPluginRegistration(terminal);

    terminal.write('\x1b');  // Escape
    await settle();

    await expect(terminal.getByText('Registrations')).toBeVisible();
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await navigateToPluginRegistration(terminal);

    await expect(terminal.getByText('Profile:')).toBeVisible();
  });
});

test.describe('Plugin Registration Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await navigateToPluginRegistration(terminal);

    terminal.write('\t');  // Tab to move focus
    await settle();

    await expect(terminal).toMatchSnapshot();
  });
});
