# Test

Run tests with auto-detection and iterative fixing.

## Usage

`/test` - Auto-detect and run appropriate tests
`/test --unit` - Force unit tests only
`/test --tui` - Force TUI tests
`/test --integration` - Run integration tests (requires credentials)
`/test --cli` - Run CLI smoke tests
`/test --all` - Run all test types

## Auto-Detection

When run without flags, `/test` analyzes changed files to determine what to run:

| Changed Files | Test Type |
|---------------|-----------|
| `src/PPDS.Cli/Tui/**` | TUI tests first, then unit |
| `src/PPDS.Cli/Commands/**` | Unit tests + CLI smoke |
| Any `src/**` files | Unit tests |
| No changes | Unit tests |

## Test Types

### Unit Tests (Default)

```bash
dotnet test --configuration Release --verbosity normal --filter "Category!=Integration"
```

Fast, no external dependencies. Default choice.

### TUI Tests

TUI testing has three layers:
1. **TuiUnit** - State assertions (fast, no terminal needed)
2. **tui-e2e** - Visual snapshots using @microsoft/tui-test (needs Node.js)
3. **TuiIntegration** - Integration with FakeXrmEasy (future)

```bash
# Build first
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore

# TUI unit tests - state assertions (fast)
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --no-build

# TUI visual snapshots (if Node.js available)
# Use --prefix to run npm from the repo root
if (Get-Command node -ErrorAction SilentlyContinue) {
    npm test --prefix tests/tui-e2e
}

# TUI integration tests (if unit pass)
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiIntegration" --no-build
```

**Updating snapshots:** If visual changes are intentional, update snapshots:
```bash
npm test --prefix tests/tui-e2e -- --update-snapshots
```

#### TUI Testing Philosophy

TUI tests verify **presentation**, not business logic. CLI and TUI share Application Services (ADR-0015), so service logic is tested once at the CLI layer.

**What TUI E2E tests verify:**
- Does the screen render correctly? (snapshots)
- Do keyboard shortcuts work? (key sequences)
- Does navigation flow correctly? (screen transitions)
- Do errors display properly? (error dialogs)

**What TUI E2E tests do NOT verify:**
- Query execution correctness (CLI tests `ISqlQueryService`)
- Export format validity (CLI tests `IExportService`)
- Authentication flows (CLI tests auth)

**Trust the service layer:** If CLI tests pass, services work. Don't duplicate that coverage.

#### Interpreting Snapshot Diffs

When E2E tests fail with snapshot diffs:

1. **Read the ASCII diff** - Shows expected vs actual terminal output
2. **Identify what's wrong** - Layout shifted? Text missing? Wrong content?
3. **Fix the TUI code** - The rendering issue is in the view/screen code
4. **Re-run tests** - Verify fix with `npm test --prefix tests/tui-e2e`
5. **If change was intentional** - Update snapshots with `--update-snapshots`

Snapshots are the visual spec. They define "correct" appearance. When you see a diff:
- If the "actual" looks wrong → fix the code
- If the "actual" looks right (intentional change) → update the snapshot

### Integration Tests

Requires `.env.local` with Dataverse credentials.

```powershell
# Load credentials
Get-Content .env.local | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') {
        $name = $matches[1].Trim()
        $value = $matches[2].Trim()
        if ($name -and -not $name.StartsWith('#')) {
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
}

# Run integration tests
dotnet test --configuration Release --filter "Category=Integration" --verbosity normal
```

If `.env.local` is missing, show error and skip.

### CLI Smoke Tests

Runs actual CLI commands to verify behavior:

1. Verify CLI is installed: `ppds --version`
2. Run baseline tests (help commands)
3. Run change-specific tests based on what changed
4. Use only `--profile cli-test` for authenticated commands

## Iterative Fix Loop

When tests fail:

1. **Parse failure output** - Extract test name, assertion, stack trace
2. **Locate the test** - Find test file in `tests/`
3. **Understand assertion** - What behavior is being verified?
4. **Find code under test** - Navigate to implementation
5. **Determine root cause**:
   - Test wrong? Fix test expectation
   - Implementation wrong? Fix source code
6. **Apply minimal fix** - Change only what's necessary
7. **Re-run tests** - Repeat until green

**Limits:**
- Maximum 5 retry attempts
- If stuck on same failure 3 times, escalate (update session status to stuck)

## What To Fix

| Scenario | Action |
|----------|--------|
| Implementation bug | Fix the source code |
| Test has wrong expectation | Fix the test assertion |
| Test setup incomplete | Fix test arrangement |
| Missing mock/stub | Add appropriate test double |

## What NOT To Do

- Don't skip or delete failing tests
- Don't add `[Fact(Skip = "...")]` without discussion
- Don't weaken assertions to make tests pass
- Don't fix unrelated code while fixing tests
- Don't create stub tests (must have real assertions)

## Output

```
Test
====
Auto-detected: Unit tests (changes in src/PPDS.Auth/)

[1/5] Running unit tests...
[✗] 2 tests failed

Analyzing failures:
1. ProfileCollection_Add_FirstProfile_SetsAsActive
   - Expected: profile1, Actual: null
   - Root cause: Add() not setting ActiveProfile
   - Fix: Add ActiveProfile assignment

Applying fix...

[2/5] Running unit tests...
[✓] All 47 tests passed

Done.
```

## CLI Smoke Test Details

When `--cli` or auto-detected for CLI changes:

**Baseline tests (always):**
```bash
ppds --version
ppds --help
ppds auth --help
ppds env --help
ppds data --help
ppds plugins --help
```

**Change-specific tests:**
| Change Type | Test |
|-------------|------|
| New flag | Run with new flag, verify accepted |
| Removed flag | Run with old flag, verify error |
| New command | Run `--help`, verify options |
| Output format | Test with `-f Json` and `-f Text` |

**Auth commands:** Use `--profile cli-test` only. If profile doesn't exist, skip auth tests.

## When to Use

- After making changes (auto-detects what to test)
- Before `/ship` (validates everything works)
- When debugging failures
- After refactoring

## Related Commands

| Command | Purpose |
|---------|---------|
| `/ship` | Commit, push, create PR (runs tests internally) |
| `/pre-pr` | Full validation (absorbed into /ship) |

## Reference

- ADR-0028: TUI Testing Strategy
- ADR-0029: Testing Strategy
- `tests/tui-e2e/` - TUI visual snapshot tests using @microsoft/tui-test
