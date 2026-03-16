#!/usr/bin/env node

/**
 * Builds the ppds CLI binary for a specific platform and places it in src/PPDS.Extension/bin/.
 *
 * Usage:
 *   node tools/bundle-cli.mjs --rid win-x64
 *   node tools/bundle-cli.mjs --rid linux-x64
 *   node tools/bundle-cli.mjs --rid osx-arm64
 */

import { execSync } from 'child_process';
import { existsSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const EXTENSION_DIR = join(__dirname, '..');
const CLI_PROJECT = join(EXTENSION_DIR, '..', 'PPDS.Cli', 'PPDS.Cli.csproj');
const BIN_DIR = join(EXTENSION_DIR, 'bin');

function parseArgs() {
    const args = process.argv.slice(2);
    const ridIndex = args.indexOf('--rid');
    if (ridIndex === -1 || ridIndex + 1 >= args.length) {
        console.error('Usage: node tools/bundle-cli.mjs --rid <runtime-identifier>');
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

    if (!existsSync(BIN_DIR)) {
        mkdirSync(BIN_DIR, { recursive: true });
    }

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

    const binaryPath = join(BIN_DIR, binaryName);
    if (!existsSync(binaryPath)) {
        console.error(`Expected binary not found at: ${binaryPath}`);
        process.exit(1);
    }

    console.log(`CLI binary built successfully: ${binaryPath}`);
}

main();
