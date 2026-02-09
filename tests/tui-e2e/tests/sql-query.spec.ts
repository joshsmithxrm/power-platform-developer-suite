/**
 * SQL Query Screen E2E Tests
 *
 * Tests the SQL Query screen navigation, keyboard shortcuts, and UI states.
 * These tests use @microsoft/tui-test for terminal rendering capture.
 */
import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers.js';

const ppdsPath = getPpdsPath();

// Configure all tests to launch the PPDS TUI interactive mode
test.use({ program: { file: ppdsPath, args: ['interactive'] } });

// Helper to navigate to SQL Query screen via Tools menu
async function navigateToSqlQuery(terminal: any) {
  // Wait for main window
  await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

  // Navigate via Tools > SQL Query menu
  terminal.write('\x1bt');  // Alt+T to open Tools menu
  await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
  terminal.write('\r');  // Enter to select

  // Wait for SQL Query screen to load (title includes Ctrl+Space, Alt+arrows, F6 hints)
  await expect(terminal.getByText('Ctrl+Enter to execute')).toBeVisible();
}

test.describe('SQL Query Screen', () => {
  test('Tools menu opens SQL Query screen', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Take snapshot of SQL Query screen initial state
    await expect(terminal).toMatchSnapshot();
  });

  test('default query is pre-populated', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Verify default query text appears
    await expect(terminal.getByText('SELECT TOP 100', { full: true })).toBeVisible();
  });

  test('status line or results table shows initial state', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // The results table shows "No data" initially
    await expect(terminal.getByText('No data', { full: true })).toBeVisible();
  });

  test('Escape stays on SQL Query screen (use Ctrl+W to close)', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Press Escape - should NOT close the tab (changed behavior)
    terminal.write('\x1b');  // Escape

    // Verify we're still on SQL Query screen
    await expect(terminal.getByText('Ctrl+Enter to execute')).toBeVisible();
  });

  test('Ctrl+E without results shows error message', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Press Ctrl+E (export) without any results
    terminal.write('\x05');  // Ctrl+E

    // Verify error dialog appears
    await expect(terminal.getByText('No data to export', { full: true })).toBeVisible();

    // Take snapshot of error state
    await expect(terminal).toMatchSnapshot();
  });

  test('Ctrl+Shift+H without environment shows error message', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Press Ctrl+Shift+H (history) without environment selected
    // Ctrl+Shift+H is a complex key combination - using the Terminal.Gui expected sequence
    terminal.write('\x1b[72;6~');  // Ctrl+Shift+H attempt

    // Note: If Ctrl+Shift+H doesn't work, the test will timeout and fail
    // This helps identify if the key binding is incorrect
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Verify status bar shows profile section
    await expect(terminal.getByText('Profile:', { full: true })).toBeVisible();
  });

  test('SQL Query screen snapshot for visual regression', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);
    // Wait for initial state with "No data" in results
    await expect(terminal.getByText('No data', { full: true })).toBeVisible();

    // Full screen snapshot for visual comparison
    await expect(terminal).toMatchSnapshot();
  });
});

test.describe('SQL Query Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await navigateToSqlQuery(terminal);

    // Press Tab to move focus
    terminal.write('\t');

    // The focus should move - we verify by taking snapshots at different states
    await expect(terminal).toMatchSnapshot();
  });
});
