# Fix Tests

Run tests iteratively, fixing failures until all pass.

## Usage

`/fix-tests`

## Behavior

1. Run the test suite
2. If all tests pass, report success and stop
3. If tests fail:
   - Analyze the failure output
   - Identify the root cause (test bug or implementation bug)
   - Fix the issue
   - Return to step 1
4. Maximum 5 retry attempts to prevent infinite loops
5. If stuck on same failure 3 times, ask for help

## Test Commands

### Unit Tests (Default)

```bash
dotnet test --configuration Release --verbosity normal --filter "Category!=Integration"
```

### All Tests (Including Integration)

Only if user explicitly requests or unit tests pass and integration coverage is needed:

```bash
dotnet test --configuration Release --verbosity normal
```

**Note:** Integration tests require Dataverse credentials. If credentials are missing, tests skip gracefully.

## Analysis Approach

1. **Parse failure output** - Extract test name, assertion message, stack trace
2. **Locate the test** - Find test file in `tests/` folder
3. **Understand the assertion** - What behavior is being verified?
4. **Find code under test** - Navigate to the implementation
5. **Determine root cause**:
   - Is the test wrong? (outdated expectation, incorrect setup)
   - Is the implementation wrong? (bug, missing case)
6. **Apply minimal fix** - Change only what's necessary

## What To Fix

| Scenario | Action |
|----------|--------|
| Implementation bug | Fix the source code |
| Test has wrong expectation | Fix the test assertion |
| Test setup is incomplete | Fix the test arrangement |
| Missing mock/stub | Add appropriate test double |

## What NOT To Do

- Don't skip or delete failing tests
- Don't add `[Fact(Skip = "...")]` without discussion
- Don't weaken assertions to make tests pass
- Don't fix unrelated code while fixing tests
- Don't create stub tests (see `/pre-pr` for test quality requirements)

## Output

```
Fix Tests
=========
[1/5] Running tests...
[✗] 2 tests failed

Analyzing failures:
1. ProfileCollection_Add_FirstProfile_SetsAsActive
   - Expected: profile1, Actual: null
   - Root cause: Add() not setting ActiveProfile when collection is empty
   - Fix: Add ActiveProfile assignment in Add() method

Applying fix...
[2/5] Running tests...
[✓] All 47 tests passed

Done.
```

## When to Use

- After making changes that might break tests
- When CI reports test failures
- After refactoring to verify behavior preserved
- When iterating on implementation

## Related Commands

- `/pre-pr` - Full pre-PR validation (includes test coverage check)
- `/test-cli` - Test CLI commands specifically
