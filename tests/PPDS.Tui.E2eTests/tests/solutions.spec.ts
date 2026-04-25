import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

const settle = (ms = 150) => new Promise(r => setTimeout(r, ms));

async function navigateToSolutions(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  terminal.write('\x1bt');  // Alt+T to open Tools menu
  await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
  await settle();
  terminal.write('\x1b[B');  // Down to Solutions
  await settle();
  terminal.write('\r');  // Enter to select

  await expect(terminal.getByText('Include Managed')).toBeVisible();
}

test.describe('Solutions Screen', () => {
  test('navigates to Solutions via Tools menu', async ({ terminal }) => {
    await navigateToSolutions(terminal);

    await expect(terminal.getByText('Filter')).toBeVisible();
    await settle(300);
    await expect(terminal).toMatchSnapshot();
  });

  test('shows filter frame with search field', async ({ terminal }) => {
    await navigateToSolutions(terminal);

    await expect(terminal.getByText('Filter')).toBeVisible();
    await expect(terminal.getByText('Include Managed')).toBeVisible();
  });

  test('Escape stays on Solutions screen', async ({ terminal }) => {
    await navigateToSolutions(terminal);

    terminal.write('\x1b');  // Escape
    await settle();

    await expect(terminal.getByText('Include Managed')).toBeVisible();
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await navigateToSolutions(terminal);

    await expect(terminal.getByText('Profile:')).toBeVisible();
  });
});

test.describe('Solutions Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await navigateToSolutions(terminal);

    terminal.write('\t');  // Tab to move focus
    await settle();

    await expect(terminal).toMatchSnapshot();
  });
});
