import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath, openToolsMenuItem, settle } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

async function navigateToPluginTraces(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  await openToolsMenuItem(terminal, 'Plugin Traces');

  // Wait for tab bar to show the screen (not the menu item text)
  await expect(terminal.getByText('1: Plugin Traces')).toBeVisible();
}

test.describe('Plugin Traces Screen', () => {
  test('navigates to Plugin Traces via Tools menu', async ({ terminal }) => {
    await navigateToPluginTraces(terminal);

    await expect(terminal).toMatchSnapshot();
  });

  test('Escape stays on Plugin Traces screen', async ({ terminal }) => {
    await navigateToPluginTraces(terminal);

    terminal.write('\x1b');  // Escape
    await settle();

    await expect(terminal.getByText('1: Plugin Traces')).toBeVisible();
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await navigateToPluginTraces(terminal);

    await expect(terminal.getByText('Profile:')).toBeVisible();
  });
});

test.describe('Plugin Traces Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await navigateToPluginTraces(terminal);

    terminal.write('\t');  // Tab to move focus
    await settle();

    await expect(terminal).toMatchSnapshot();
  });
});
