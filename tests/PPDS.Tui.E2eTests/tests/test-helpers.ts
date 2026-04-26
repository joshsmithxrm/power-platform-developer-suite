import * as path from 'path';
import * as os from 'os';
import { expect } from '@microsoft/tui-test';

/**
 * Gets the path to the PPDS CLI executable.
 * Uses process.cwd() which is the PPDS.Tui.E2eTests directory, then navigates to repo root.
 */
export function getPpdsPath(): string {
  // tui-test runs from the PPDS.Tui.E2eTests directory, go up to repo root
  const repoRoot = path.resolve(process.cwd(), '../..');
  const targetFramework = 'net10.0';

  if (os.platform() === 'win32') {
    return path.join(repoRoot, 'src', 'PPDS.Cli', 'bin', 'Debug', targetFramework, 'ppds.exe');
  }
  return path.join(repoRoot, 'src', 'PPDS.Cli', 'bin', 'Debug', targetFramework, 'ppds');
}

/** Small delay so the TUI can repaint between key writes. */
export const settle = (ms = 150) => new Promise(r => setTimeout(r, ms));

/**
 * Tools menu items in display order. Update this if the TUI menu order changes —
 * `openToolsMenuItem` derives its Down-arrow count from this array, so all
 * navigation helpers stay in sync with a single edit here.
 */
export const TOOLS_MENU_ITEMS = [
  'SQL Query',
  'Solutions',
  'Import Jobs',
  'Connection References',
  'Environment Variables',
  'Plugin Traces',
  'Plugin Registration',
  'Metadata Browser',
] as const;

export type ToolsMenuItem = (typeof TOOLS_MENU_ITEMS)[number];

/**
 * Opens the Tools menu and selects the given item by name. Resolves the
 * Down-arrow count from TOOLS_MENU_ITEMS so tests don't hardcode indices.
 */
export async function openToolsMenuItem(terminal: any, item: ToolsMenuItem): Promise<void> {
  const index = TOOLS_MENU_ITEMS.indexOf(item);
  if (index < 0) {
    throw new Error(`Unknown Tools menu item: ${item}`);
  }

  terminal.write('\x1bt');  // Alt+T to open Tools menu
  await expect(terminal.getByText('SQL Query', { full: true })).toBeVisible();
  await settle();

  for (let i = 0; i < index; i++) {
    terminal.write('\x1b[B');  // Down arrow
    await settle();
  }
  terminal.write('\r');  // Enter to select
}
