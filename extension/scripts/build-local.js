#!/usr/bin/env node

/**
 * Builds and installs a local development version of the extension.
 *
 * This script:
 * 1. Reads the current version from package.json (e.g., "0.5.0")
 * 2. Increments the dev counter from .dev-version file
 * 3. Temporarily modifies package.json to append -dev.X (e.g., "0.5.0-dev.5")
 * 4. Builds and packages the extension with the dev version
 * 5. Restores package.json to original state
 * 6. Installs the dev-versioned .vsix locally
 *
 * This ensures:
 * - package.json always shows the "real" production version
 * - Local installs are clearly marked as dev builds
 * - No risk of accidentally publishing a dev version
 */

const { execSync } = require('child_process');
const { readFileSync, writeFileSync, copyFileSync, unlinkSync } = require('fs');
const { join } = require('path');

const ROOT_DIR = join(__dirname, '..');
const PACKAGE_JSON_PATH = join(ROOT_DIR, 'package.json');
const PACKAGE_JSON_BACKUP_PATH = join(ROOT_DIR, 'package.json.backup');
const DEV_VERSION_PATH = join(ROOT_DIR, '.dev-version');

function getAndIncrementDevVersion() {
	let devVersion = 1;

	try {
		const content = readFileSync(DEV_VERSION_PATH, 'utf8').trim();
		devVersion = parseInt(content, 10) || 1;
	} catch (error) {
		if (error.code !== 'ENOENT') {
			throw error;
		}
	}

	const newDevVersion = devVersion + 1;
	writeFileSync(DEV_VERSION_PATH, newDevVersion.toString(), 'utf8');

	return newDevVersion;
}

function getCurrentVersion() {
	const packageJson = JSON.parse(readFileSync(PACKAGE_JSON_PATH, 'utf8'));
	return packageJson.version;
}

function backupPackageJson() {
	copyFileSync(PACKAGE_JSON_PATH, PACKAGE_JSON_BACKUP_PATH);
}

function restorePackageJson() {
	try {
		copyFileSync(PACKAGE_JSON_BACKUP_PATH, PACKAGE_JSON_PATH);
		unlinkSync(PACKAGE_JSON_BACKUP_PATH);
	} catch (error) {
		if (error.code !== 'ENOENT') {
			throw error;
		}
	}
}

function setDevVersion(devVersion) {
	const packageJson = JSON.parse(readFileSync(PACKAGE_JSON_PATH, 'utf8'));
	packageJson.version = devVersion;
	writeFileSync(PACKAGE_JSON_PATH, JSON.stringify(packageJson, null, 2) + '\n', 'utf8');
}

function runCommand(command, description) {
	console.log(`\n${description}...`);
	try {
		execSync(command, { stdio: 'inherit', cwd: ROOT_DIR });
	} catch (error) {
		throw new Error(`Failed to ${description.toLowerCase()}: ${error.message}`);
	}
}

async function main() {
	let restored = false;

	try {
		const currentVersion = getCurrentVersion();
		console.log(`Current version: ${currentVersion}`);

		const devCounter = getAndIncrementDevVersion();
		const devVersion = `${currentVersion}-dev.${devCounter}`;
		console.log(`Building dev version: ${devVersion}`);

		console.log('Backing up package.json...');
		backupPackageJson();

		console.log(`Temporarily setting version to ${devVersion}...`);
		setDevVersion(devVersion);

		runCommand('npm run package', 'Building production bundle');
		runCommand('npm run vsce-package', 'Creating .vsix package');

		console.log('Restoring package.json...');
		restorePackageJson();
		restored = true;
		console.log(`package.json restored to version ${currentVersion}`);

		runCommand('npm run install-local', 'Installing extension locally');

		console.log(`\nSuccess! Extension ${devVersion} installed locally.`);
		console.log(`Note: package.json remains at version ${currentVersion}`);
		console.log(`Next dev build will be ${currentVersion}-dev.${devCounter + 1}`);

	} catch (error) {
		console.error(`\nError: ${error.message}`);

		if (!restored) {
			console.log('Restoring package.json after error...');
			restorePackageJson();
			console.log('package.json restored');
		}

		process.exit(1);
	}
}

main();
