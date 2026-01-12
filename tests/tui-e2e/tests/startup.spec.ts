/**
 * TUI Startup Flow Tests
 *
 * Tests the main window startup and initial layout.
 * These tests use @microsoft/tui-test for terminal rendering capture.
 */
import { test, expect } from '@microsoft/tui-test';
import { getPpdsPath } from './test-helpers';

const ppdsPath = getPpdsPath();

test.describe('Startup Flow', () => {
  test('launches and displays main menu', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for the main window to appear
    await terminal.waitForText('PPDS - Power Platform Developer Suite');
    await terminal.waitForText('Welcome to PPDS Interactive Mode');

    // Verify the main menu items are displayed
    await terminal.waitForText('SQL Query (F2)');
    await terminal.waitForText('Quit (Ctrl+Q)');

    // Take a snapshot of the initial state
    await expect(terminal).toMatchSnapshot('main-menu.txt');
  });

  test('shows status bar with profile/environment info', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for main window
    await terminal.waitForText('PPDS - Power Platform Developer Suite');

    // Verify status bar shows profile section (even if no profile is set)
    await terminal.waitForText('Profile:');
    await terminal.waitForText('Environment:');
  });

  test('F1 opens keyboard shortcuts dialog', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for main window
    await terminal.waitForText('PPDS - Power Platform Developer Suite');

    // Press F1 to open keyboard shortcuts
    await terminal.sendKey('F1');

    // Verify the shortcuts dialog appears
    await terminal.waitForText('Keyboard Shortcuts');
    await terminal.waitForText('Alt+P');
    await terminal.waitForText('Alt+E');

    // Take a snapshot
    await expect(terminal).toMatchSnapshot('keyboard-shortcuts.txt');

    // Press Escape to close
    await terminal.sendKey('Escape');

    // Verify we're back at main menu
    await terminal.waitForText('SQL Query (F2)');
  });

  test('F2 opens SQL query screen', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for main window
    await terminal.waitForText('PPDS - Power Platform Developer Suite');

    // Press F2 to open SQL query screen
    await terminal.sendKey('F2');

    // Verify SQL query screen appears
    await terminal.waitForText('SQL Query');
    await terminal.waitForText('Query (Ctrl+Enter to execute)');

    // Take a snapshot
    await expect(terminal).toMatchSnapshot('sql-query-screen.txt');

    // Press Escape to go back
    await terminal.sendKey('Escape');

    // Verify we're back at main menu
    await terminal.waitForText('Welcome to PPDS Interactive Mode');
  });

  test('Ctrl+Q quits the application', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for main window
    await terminal.waitForText('PPDS - Power Platform Developer Suite');

    // Press Ctrl+Q to quit
    await terminal.sendKey('Control+q');

    // Terminal should exit
    await terminal.waitForExit();
  });
});

test.describe('Profile Selector', () => {
  test('Alt+P opens profile selector dialog', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for main window
    await terminal.waitForText('PPDS - Power Platform Developer Suite');

    // Press Alt+P to open profile selector
    await terminal.sendKey('Alt+p');

    // Verify the profile selector appears
    await terminal.waitForText('Select Profile');
    await terminal.waitForText('Profiles');

    // Take a snapshot
    await expect(terminal).toMatchSnapshot('profile-selector.txt');

    // Press Escape to close
    await terminal.sendKey('Escape');
  });
});

test.describe('Environment Selector', () => {
  test('Alt+E opens environment selector dialog', async ({ terminal }) => {
    // Launch the PPDS TUI
    await terminal.spawn(ppdsPath, ['--no-update-check']);

    // Wait for main window
    await terminal.waitForText('PPDS - Power Platform Developer Suite');

    // Note: Environment selector may require a profile to be set
    // This test validates the hotkey works, even if it shows an error

    // Press Alt+E
    await terminal.sendKey('Alt+e');

    // Should show either the environment selector or an error about no profile
    // (depends on whether there's a profile configured)

    // Give it a moment to respond
    await terminal.sleep(500);

    // Take a snapshot of whatever appears
    await expect(terminal).toMatchSnapshot('environment-selector-or-error.txt');

    // Press Escape to close any dialog
    await terminal.sendKey('Escape');
  });
});
