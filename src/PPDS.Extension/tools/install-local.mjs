#!/usr/bin/env node

/**
 * Installs the most recently created .vsix extension in VS Code.
 * Finds the newest .vsix file in the extension root.
 */

import { execSync } from 'child_process';
import { readdirSync, statSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT_DIR = join(__dirname, '..');

/**
 * Finds the most recently created .vsix file.
 * @returns {{ name: string, path: string, time: number } | null}
 */
function findNewestVsix() {
	const files = readdirSync(ROOT_DIR)
		.filter(file => file.endsWith('.vsix'))
		.map(file => ({
			name: file,
			path: join(ROOT_DIR, file),
			time: statSync(join(ROOT_DIR, file)).mtime.getTime()
		}))
		.sort((a, b) => b.time - a.time);

	return files.length > 0 ? files[0] : null;
}

const vsixInfo = findNewestVsix();

if (!vsixInfo) {
	console.error('Error: No .vsix file found. Run "npm run vsce:package" first.');
	process.exit(1);
}

console.log(`Installing ${vsixInfo.name}...`);
try {
	execSync(`code --install-extension "${vsixInfo.path}" --force`, { stdio: 'inherit' });
	console.log('Extension installed successfully!');
} catch (error) {
	console.error('Installation failed:', error.message);
	process.exit(1);
}
