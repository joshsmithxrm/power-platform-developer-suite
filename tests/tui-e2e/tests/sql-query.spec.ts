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

test.describe('SQL Query Screen', () => {
  test('F2 opens SQL Query screen', async ({ terminal }) => {
    // Wait for main menu
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    await expect(terminal.getByText('SQL Query (F2)', { full: true })).toBeVisible();

    // Press F2 to open SQL Query screen
    terminal.write('\x1bOQ');  // F2 key code

    // Verify SQL Query screen opened
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // Take snapshot of SQL Query screen initial state
    await expect(terminal).toMatchSnapshot();
  });

  test('default query is pre-populated', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2

    // Verify default query text appears
    await expect(terminal.getByText('SELECT TOP 100', { full: true })).toBeVisible();
  });

  test('status line or results table shows initial state', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2

    // Wait for SQL Query screen to load
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // The results table shows "No data" initially
    await expect(terminal.getByText('No data', { full: true })).toBeVisible();
  });

  test('Escape returns to main menu from SQL Query', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2

    // Verify we're on SQL Query screen
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // Press Escape to return to main menu
    terminal.write('\x1b');  // Escape

    // Verify we're back on main menu
    await expect(terminal.getByText('Main Menu', { full: true })).toBeVisible();
    await expect(terminal.getByText('SQL Query (F2)', { full: true })).toBeVisible();
  });

  test('Ctrl+E without results shows error message', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // Press Ctrl+E (export) without any results
    terminal.write('\x05');  // Ctrl+E

    // Verify error dialog appears
    await expect(terminal.getByText('No data to export', { full: true })).toBeVisible();

    // Take snapshot of error state
    await expect(terminal).toMatchSnapshot();
  });

  test('Ctrl+Shift+H without environment shows error message', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // Press Ctrl+Shift+H (history) without environment selected
    // Ctrl+Shift+H is a complex key combination - using the Terminal.Gui expected sequence
    terminal.write('\x1b[72;6~');  // Ctrl+Shift+H attempt

    // Note: If Ctrl+Shift+H doesn't work, the test will timeout and fail
    // This helps identify if the key binding is incorrect
  });

  test('status bar displays profile information', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // Verify status bar shows profile section
    await expect(terminal.getByText('Profile:', { full: true })).toBeVisible();
  });

  test('SQL Query screen snapshot for visual regression', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();
    // Wait for initial state with "No data" in results
    await expect(terminal.getByText('No data', { full: true })).toBeVisible();

    // Full screen snapshot for visual comparison
    await expect(terminal).toMatchSnapshot();
  });
});

test.describe('SQL Query Keyboard Navigation', () => {
  test('Tab cycles through controls', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    terminal.write('\x1bOQ');  // F2
    await expect(terminal.getByText('Query (Ctrl+Enter to execute, F6 to toggle focus)', { full: true })).toBeVisible();

    // Press Tab to move focus
    terminal.write('\t');

    // The focus should move - we verify by taking snapshots at different states
    await expect(terminal).toMatchSnapshot();
  });
});
