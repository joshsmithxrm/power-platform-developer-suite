import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

const settle = (ms = 150) => new Promise(r => setTimeout(r, ms));

async function navigateToPluginRegistration(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  terminal.write('\x1bt');  // Alt+T to open Tools menu
  await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
  await settle();
  // Down 6: SQL Query → Solutions → Import Jobs → Connection References → Environment Variables → Plugin Traces → Plugin Registration
  for (let i = 0; i < 6; i++) {
    terminal.write('\x1b[B');
    await settle();
  }
  terminal.write('\r');  // Enter to select

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
