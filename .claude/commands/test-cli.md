# CLI Smoke Test

Tests the local CLI to verify changes work correctly.

## When to Use

| Use `/test-cli` for | Use `dotnet test` for |
|---------------------|----------------------|
| Quick verification after local install | Command structure verification |
| Exploratory testing of new features | Regression testing |
| End-to-end smoke tests | CI/CD validation |
| Verifying help text is sensible | Verifying subcommands/options exist |

**Rule of thumb**: If it can be a unit test, it should be. Use `/test-cli` for things unit tests can't verify (actual CLI behavior after install, help text quality, end-to-end flows with real auth).

## Usage

`/test-cli`

## What It Does

### 1. Analyze Changes
- Read recent commits and changelog to understand what changed
- Identify which commands/features need testing
- If unclear what to test, ask the user

### 2. Verify Version
```bash
ppds --version
```
- Confirm version includes expected commit hash from `git rev-parse --short HEAD`
- If CLI is not available, prompt user to run `/install-cli` or use terminal profile's `ppds` function

### 3. Run Tests

**CRITICAL**: Run each command INDIVIDUALLY.

DO NOT:
- Chain commands with `&&` or `||`
- Wrap commands in `echo` statements
- Redirect output to `/dev/null`
- Construct compound bash statements

DO:
- Run each command as a standalone Bash call
- Read the actual output
- Analyze the output to determine pass/fail
- Move to the next command

#### Baseline Tests (always run)

Run each command individually and verify it works:

1. `ppds --version`
2. `ppds --help`
3. `ppds auth --help`
4. `ppds env --help`
5. `ppds data --help`
6. `ppds plugins --help`

#### Change-Specific Tests

Based on what changed:

| Change Type | How to Test |
|-------------|-------------|
| New flag/option | Run command with new flag, verify it's accepted |
| Removed flag/option | Run command with old flag, verify it shows error |
| Moved command | Run new path (should work), run old path (should show "not found") |
| New command | Run `--help`, verify it shows options |
| Output format | Test with `-f Json` and `-f Text` |

#### Commands Requiring Auth

Use the `cli-test` profile:
- `ppds env list --profile cli-test -f Json`
- `ppds auth who --profile cli-test`

**Rules**:
- ONLY use `--profile cli-test` - never default or other profiles
- If `cli-test` doesn't exist, SKIP auth tests and note it
- Never run destructive commands

### 4. Report Results

After running all tests, provide a summary:

```
CLI Smoke Test Results
======================
Version: 1.0.0-beta.5.4+d750e7d ✓

Baseline Tests:
  [✓] ppds --version
  [✓] ppds --help
  [✓] ppds auth --help
  [✓] ppds env --help
  [✓] ppds data --help
  [✓] ppds plugins --help

Change-Specific Tests:
  [✓] ppds auth list -f Json - new flag accepted
  [✓] ppds data schema --help - moved command works
  [✓] ppds schema generate - correctly removed (command not found)

Summary: 9/9 passed
```

## Behavior

| Situation | Action |
|-----------|--------|
| CLI not found | Prompt to run `/install-cli` or use terminal profile |
| Test failure | Report which failed, show output, continue |
| Version mismatch | Warn but continue |
| Profile missing | Skip auth tests with note |

## Safe Commands Only

NEVER:
- Use any profile other than `cli-test`
- Run data export/import against real environments
- Delete or modify Dataverse data
- Run `ppds plugins clean` without `--dry-run`
- Run `ppds plugins deploy` without `--dry-run`
