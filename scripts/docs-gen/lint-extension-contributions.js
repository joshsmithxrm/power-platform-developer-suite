#!/usr/bin/env node
// Lint VS Code extension contributions for required metadata (title + category).
// Spec: specs/docs-generation.md — Core Requirement 5, AC-13, AC-14.
//
// Usage:
//   node scripts/docs-gen/lint-extension-contributions.js
//   node scripts/docs-gen/lint-extension-contributions.js --package-json <path>
//
// Exit codes:
//   0 — all contributes.commands entries have 'command', 'title', 'category'
//       (also 0 when contributes / commands are absent)
//   1 — at least one entry is missing a required field; diagnostics on stderr

'use strict';

const fs = require('fs');
const path = require('path');

const DEFAULT_PACKAGE_JSON = 'src/PPDS.Extension/package.json';
const REQUIRED_FIELDS = ['command', 'title', 'category'];

function parseArgs(argv) {
  const args = { packageJson: DEFAULT_PACKAGE_JSON };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--package-json') {
      const next = argv[i + 1];
      if (!next) {
        console.error("Extension contribution lint failed: --package-json requires a path argument");
        process.exit(2);
      }
      args.packageJson = next;
      i++;
    }
  }
  return args;
}

function main(argv) {
  const { packageJson } = parseArgs(argv);
  const absolutePath = path.resolve(packageJson);

  let raw;
  try {
    raw = fs.readFileSync(absolutePath, 'utf8');
  } catch (err) {
    console.error(`Extension contribution lint failed: cannot read '${absolutePath}': ${err.message}`);
    process.exit(2);
  }

  let pkg;
  try {
    pkg = JSON.parse(raw);
  } catch (err) {
    console.error(`Extension contribution lint failed: invalid JSON in '${absolutePath}': ${err.message}`);
    process.exit(2);
  }

  const commands = pkg && pkg.contributes && pkg.contributes.commands;
  if (!Array.isArray(commands)) {
    // No contributes.commands array: nothing to lint.
    process.exit(0);
  }

  const errors = [];
  for (const cmd of commands) {
    const id = (cmd && typeof cmd.command === 'string' && cmd.command.length > 0)
      ? cmd.command
      : '<unknown>';
    for (const field of REQUIRED_FIELDS) {
      const value = cmd ? cmd[field] : undefined;
      if (typeof value !== 'string' || value.length === 0) {
        errors.push(`${id}: missing '${field}'`);
      }
    }
  }

  if (errors.length > 0) {
    console.error('Extension contribution lint failed:');
    for (const err of errors) {
      console.error(`  ${err}`);
    }
    process.exit(1);
  }

  process.exit(0);
}

main(process.argv.slice(2));
