/**
 * TUI Startup Flow Tests
 *
 * Tests the main window startup and initial layout.
 * These tests use @microsoft/tui-test for terminal rendering capture.
 */
import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers.js';

const ppdsPath = getPpdsPath();

// Configure all tests to launch the PPDS TUI interactive mode
test.use({ program: { file: ppdsPath, args: ['interactive'] } });

test.describe('Startup Flow', () => {
  test('launches and displays main menu', async ({ terminal }) => {
    // Wait for the main window to appear
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();
    await expect(terminal.getByText('Welcome to PPDS Interactive Mode', { full: true })).toBeVisible();

    // Verify the main menu items are displayed
    await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
    await expect(terminal.getByText('Quit', { full: true })).toBeVisible();

    // Take a snapshot of the initial state
    await expect(terminal).toMatchSnapshot();
  });

  test('shows status bar with profile info', async ({ terminal }) => {
    // Wait for main window
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

    // Verify status bar shows profile section (even if no profile is set)
    await expect(terminal.getByText('Profile:', { full: true })).toBeVisible();
  });

  test('Ctrl+Q quits the application', async ({ terminal }) => {
    // Wait for main window
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite', { full: true })).toBeVisible();

    // Press Ctrl+Q to quit (Ctrl+Q = \x11)
    terminal.write('\x11');

    // Wait for exit - use onExit event
    await new Promise<void>((resolve) => {
      terminal.onExit(() => resolve());
    });
  });
});
