#!/usr/bin/env node

/**
 * Builds the ppds CLI binary for a specific platform and places it in extension/bin/.
 *
 * Usage:
 *   node scripts/bundle-cli.js --rid win-x64
 *   node scripts/bundle-cli.js --rid linux-x64
 *   node scripts/bundle-cli.js --rid osx-arm64
 */

const { execSync } = require('child_process');
const { existsSync, mkdirSync } = require('fs');
const { join } = require('path');

const EXTENSION_DIR = join(__dirname, '..');
const CLI_PROJECT = join(EXTENSION_DIR, '..', 'src', 'PPDS.Cli', 'PPDS.Cli.csproj');
const BIN_DIR = join(EXTENSION_DIR, 'bin');

function parseArgs() {
    const args = process.argv.slice(2);
    const ridIndex = args.indexOf('--rid');
    if (ridIndex === -1 || ridIndex + 1 >= args.length) {
        console.error('Usage: node scripts/bundle-cli.js --rid <runtime-identifier>');
        console.error('  e.g.: --rid win-x64, --rid linux-x64, --rid osx-x64, --rid osx-arm64');
        process.exit(1);
    }
    return args[ridIndex + 1];
}

function main() {
    const rid = parseArgs();
    const isWindows = rid.startsWith('win');
    const binaryName = isWindows ? 'ppds.exe' : 'ppds';

    console.log(`Building ppds CLI for ${rid}...`);

    // Ensure bin directory exists
    if (!existsSync(BIN_DIR)) {
        mkdirSync(BIN_DIR, { recursive: true });
    }

    // Build self-contained single-file binary
    const publishCmd = [
        'dotnet', 'publish', `"${CLI_PROJECT}"`,
        '-c', 'Release',
        '-f', 'net8.0',
        '-r', rid,
        '--self-contained',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-o', `"${BIN_DIR}"`,
    ].join(' ');

    console.log(`Running: ${publishCmd}`);

    try {
        execSync(publishCmd, { stdio: 'inherit' });
    } catch (error) {
        console.error(`Failed to build CLI: ${error.message}`);
        process.exit(1);
    }

    // Verify the binary exists
    const binaryPath = join(BIN_DIR, binaryName);
    if (!existsSync(binaryPath)) {
        console.error(`Expected binary not found at: ${binaryPath}`);
        process.exit(1);
    }

    console.log(`CLI binary built successfully: ${binaryPath}`);
}

main();
