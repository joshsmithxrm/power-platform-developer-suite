import * as path from 'path';
import * as os from 'os';

/**
 * Gets the path to the PPDS CLI executable.
 * Uses process.cwd() which is the tui-e2e directory, then navigates to repo root.
 */
export function getPpdsPath(): string {
  // tui-test runs from the tui-e2e directory, go up to repo root
  const repoRoot = path.resolve(process.cwd(), '../..');
  const targetFramework = 'net10.0';

  if (os.platform() === 'win32') {
    return path.join(repoRoot, 'src', 'PPDS.Cli', 'bin', 'Debug', targetFramework, 'ppds.exe');
  }
  return path.join(repoRoot, 'src', 'PPDS.Cli', 'bin', 'Debug', targetFramework, 'ppds');
}
