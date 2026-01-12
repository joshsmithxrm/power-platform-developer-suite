import { spawn, ChildProcess } from 'child_process';
import * as path from 'path';
import * as os from 'os';

/**
 * Gets the path to the PPDS CLI executable.
 */
export function getPpdsPath(): string {
  // Use the locally built CLI
  const repoRoot = path.resolve(__dirname, '../../..');
  const targetFramework = 'net10.0';

  if (os.platform() === 'win32') {
    return path.join(repoRoot, 'src', 'PPDS.Cli', 'bin', 'Debug', targetFramework, 'ppds.exe');
  }
  return path.join(repoRoot, 'src', 'PPDS.Cli', 'bin', 'Debug', targetFramework, 'ppds');
}

/**
 * Waits for the terminal output to contain the specified text.
 */
export async function waitForText(
  output: string[],
  text: string,
  timeoutMs: number = 10000
): Promise<void> {
  const startTime = Date.now();
  while (Date.now() - startTime < timeoutMs) {
    const combined = output.join('\n');
    if (combined.includes(text)) {
      return;
    }
    await sleep(100);
  }
  throw new Error(`Timed out waiting for text: "${text}". Output: ${output.join('\n')}`);
}

/**
 * Sleep for the specified number of milliseconds.
 */
export function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}
