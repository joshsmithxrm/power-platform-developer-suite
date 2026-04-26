import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath, openToolsMenuItem, settle } from './test-helpers.js';

const ppdsPath = getPpdsPath();

test.use({ program: { file: ppdsPath, args: ['interactive'] } });

async function navigateToSolutions(terminal: any) {
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  await openToolsMenuItem(terminal, 'Solutions');

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
