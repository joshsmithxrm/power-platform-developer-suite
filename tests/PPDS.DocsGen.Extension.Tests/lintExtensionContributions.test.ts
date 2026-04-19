import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const REPO_ROOT = resolve(__dirname, '..', '..');
const SCRIPT = resolve(REPO_ROOT, 'scripts', 'docs-gen', 'lint-extension-contributions.js');
const FIXTURES = resolve(__dirname, 'fixtures');

function runLint(fixturePackageJson: string) {
  const result = spawnSync(
    process.execPath,
    [SCRIPT, '--package-json', fixturePackageJson],
    { encoding: 'utf8' },
  );
  return {
    exitCode: result.status,
    stdout: result.stdout ?? '',
    stderr: result.stderr ?? '',
  };
}

describe('LintExtensionContributionsTests', () => {
  it('FailsOnMissingTitle', () => {
    // AC-13: contributes.commands entry missing 'title' → exit 1, stderr names the command id.
    const fixture = resolve(FIXTURES, 'missing-title', 'package.json');
    const { exitCode, stderr } = runLint(fixture);

    expect(exitCode).toBe(1);
    expect(stderr).toContain('Extension contribution lint failed:');
    expect(stderr).toContain('ppds.fixtureCommandMissingTitle');
    expect(stderr).toContain("missing 'title'");
  });

  it('FailsOnMissingCategory', () => {
    // AC-14: contributes.commands entry missing 'category' → exit 1, stderr names the command id.
    const fixture = resolve(FIXTURES, 'missing-category', 'package.json');
    const { exitCode, stderr } = runLint(fixture);

    expect(exitCode).toBe(1);
    expect(stderr).toContain('Extension contribution lint failed:');
    expect(stderr).toContain('ppds.fixtureCommandMissingCategory');
    expect(stderr).toContain("missing 'category'");
  });
});
