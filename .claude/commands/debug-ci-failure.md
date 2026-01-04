# Debug CI Failure

Fetches and analyzes CI workflow logs to diagnose test failures.

## Usage

`/debug-ci-failure [run-id]`

Examples:
- `/debug-ci-failure` - Analyze most recent failed run
- `/debug-ci-failure 12345678` - Analyze specific run ID

## Process

### 1. Get Failed Run

If no run-id provided, get most recent failure:

```bash
gh run list --repo joshsmithxrm/ppds-sdk --status failure --limit 1 --json databaseId,headBranch,conclusion,name,updatedAt
```

If run-id provided, get that specific run:

```bash
gh run view [run-id] --repo joshsmithxrm/ppds-sdk --json conclusion,name,jobs
```

### 2. Download Failed Logs

```bash
gh run view [run-id] --repo joshsmithxrm/ppds-sdk --log-failed
```

### 3. Analyze Failure Patterns

Look for these common issues in the logs:

| Pattern | Likely Cause | Solution |
|---------|--------------|----------|
| Test hung / no output for 6hrs | DPAPI/SecureCredentialStore | Set `PPDS_TEST_CLIENT_SECRET` in CI (auto-bypasses) |
| `TimeoutException` | CLI command timeout | Check for infinite loops or slow queries |
| `JsonException` parsing output | Output format mismatch | Update test to parse envelope format |
| `CryptographicException` | Certificate loading | Use MachineKeySet + EphemeralKeySet flags |
| `SkipException` | Missing credentials | Check GitHub environment secrets |
| `null is not a valid value` | Missing required field | Check data being sent to Dataverse |

### 4. Check Test Categories

Verify the workflow filter is correct:

```bash
# integration-tests.yml should have:
--filter "Category=Integration&Category!=SecureStorage&Category!=SlowIntegration"
```

If a `SecureStorage` or `SlowIntegration` test is running, it's miscategorized.

### 5. Present Analysis

Present summary:

```markdown
## CI Failure Analysis - Run #[id]

**Workflow:** [workflow name]
**Branch:** [branch]
**Failed at:** [step name]

### Failed Tests

1. `[TestClass].[TestMethod]`
   - **Error:** [error message]
   - **Likely cause:** [pattern match]
   - **Suggestion:** [fix]

### Environment Issues Detected

- [any issues with secrets, variables, etc.]

### Recommendations

1. [First recommendation]
2. [Second recommendation]
```

## Behavior

| Situation | Action |
|-----------|--------|
| No failed runs | Report "No recent failures found" |
| Run still in progress | Report status and wait or suggest watching |
| Network error | Retry once, then report |
| Unrecognized pattern | Show raw logs, ask user for context |

## Common CI-Only Failures

### DPAPI Unavailable

**Pattern:** Test hangs indefinitely, job times out after 6 hours

**Symptoms in logs:**
- No test output after "Starting test execution..."
- Job cancelled due to timeout

**Solution:**
1. Ensure `PPDS_TEST_CLIENT_SECRET` is set in CI (CredentialProviderFactory auto-bypasses SecureCredentialStore)
2. If test intentionally uses SecureCredentialStore, add `[Trait("Category", "SecureStorage")]` to exclude from CI

### JSON Output Envelope

**Pattern:** `JsonException: The input does not contain any JSON tokens`

**Symptoms in logs:**
- Test expects `[...]` but CLI outputs `{"version":"1.0","data":[...]}`

**Solution:** Update test to expect envelope format per ADR-0008

### GitHub OIDC Tests

**Pattern:** `[SkipIfNoGitHubOidc]` tests pass in CI but skip locally

**This is expected.** OIDC tokens only available in GitHub Actions. These tests should pass in CI.

### TFM-Specific Failures

**Pattern:** Test passes on net8.0, fails on net9.0/net10.0

**Check:** TFM-specific behavior, API differences between versions

## When to Use

- After CI failure notification
- When local tests pass but CI fails
- Investigating flaky tests
- Understanding CI environment differences

## Related

- [docs/INTEGRATION_TESTING.md](docs/INTEGRATION_TESTING.md) - Testing guide
- `/run-integration-local` - Run tests locally with credentials
- `/fix-tests` - Iteratively fix test failures
