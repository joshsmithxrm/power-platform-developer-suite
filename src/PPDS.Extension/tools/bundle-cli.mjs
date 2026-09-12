#!/usr/bin/env node

/**
 * Builds the ppds CLI binary for a specific platform and places it in src/PPDS.Extension/bin/.
 *
 * Usage:
 *   node tools/bundle-cli.mjs --rid win-x64
 *   node tools/bundle-cli.mjs --rid linux-x64 --expected-version 1.4.0
 *   node tools/bundle-cli.mjs --rid osx-arm64
 */

import { execFileSync } from 'child_process';
import { existsSync, mkdirSync, readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import { verifyBundledCliVersion } from './bundle-cli-version.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const EXTENSION_DIR = join(__dirname, '..');
const CLI_PROJECT = join(EXTENSION_DIR, '..', 'PPDS.Cli', 'PPDS.Cli.csproj');
const BIN_DIR = join(EXTENSION_DIR, 'bin');

function parseArgs() {
    const args = process.argv.slice(2);
    const ridIndex = args.indexOf('--rid');
    if (ridIndex === -1 || ridIndex + 1 >= args.length) {
        console.error(
            'Usage: node tools/bundle-cli.mjs --rid <runtime-identifier> ' +
            '[--expected-version <version>]'
        );
        process.exit(1);
    }

    const expectedVersionIndex = args.indexOf('--expected-version');
    if (expectedVersionIndex !== -1 && expectedVersionIndex + 1 >= args.length) {
        console.error('--expected-version requires a value');
        process.exit(1);
    }

    return {
        rid: args[ridIndex + 1],
        expectedVersion: expectedVersionIndex === -1 ? undefined : args[expectedVersionIndex + 1],
    };
}

function main() {
    const { rid, expectedVersion } = parseArgs();
    const isWindows = rid.startsWith('win');
    const binaryName = isWindows ? 'ppds.exe' : 'ppds';

    console.log(`Building ppds CLI for ${rid}...`);

    if (!existsSync(BIN_DIR)) {
        mkdirSync(BIN_DIR, { recursive: true });
    }

    const publishArgs = [
        'publish', CLI_PROJECT,
        '-c', 'Release',
        '-f', 'net8.0',
        '-r', rid,
        '--self-contained',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-o', BIN_DIR,
    ];

    console.log(`Running: dotnet ${publishArgs.join(' ')}`);

    try {
        execFileSync('dotnet', publishArgs, { stdio: 'inherit' });
    } catch (error) {
        console.error(`Failed to build CLI: ${error.message}`);
        process.exit(1);
    }

    const binaryPath = join(BIN_DIR, binaryName);
    if (!existsSync(binaryPath)) {
        console.error(`Expected binary not found at: ${binaryPath}`);
        process.exit(1);
    }

    if (expectedVersion) {
        const assemblyInfoPath = join(
            dirname(CLI_PROJECT),
            'obj', 'Release', 'net8.0', rid, 'PPDS.Cli.AssemblyInfo.cs'
        );
        if (!existsSync(assemblyInfoPath)) {
            console.error(`Generated CLI assembly metadata not found at: ${assemblyInfoPath}`);
            process.exit(1);
        }

        try {
            const actualVersion = verifyBundledCliVersion(
                readFileSync(assemblyInfoPath, 'utf8'),
                expectedVersion
            );
            console.log(`Verified bundled CLI version: ${actualVersion}`);
        } catch (error) {
            console.error(error.message);
            process.exit(1);
        }
    }

    console.log(`CLI binary built successfully: ${binaryPath}`);
}

main();
