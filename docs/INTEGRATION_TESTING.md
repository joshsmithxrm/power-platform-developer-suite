# Integration Testing Guide

**Comprehensive guide to running and debugging integration tests in PPDS.**

---

## Quick Reference

| Filter | Tests Included | Use Case |
|--------|----------------|----------|
| `Category!=Integration` | Unit tests only | Fast local dev, CI default |
| `Category=Integration` | Live Dataverse tests | Full integration validation |
| `Category=Integration&Category!=SecureStorage` | Live tests minus DPAPI | CI-safe integration |
| `Category=Integration&Category!=SlowIntegration` | Live tests minus slow queries | Faster CI |

---

## Test Categories

### `[Trait("Category", "Integration")]`

**Applied to:** All tests in `PPDS.LiveTests/`
**Meaning:** Requires live Dataverse connection
**CI behavior:** Runs in `integration-tests.yml` workflow only
**Applied via:** `LiveTestBase` or `CliE2ETestBase` base classes (automatic)

### `[Trait("Category", "SecureStorage")]`

**Applied to:** Tests that use `SecureCredentialStore` directly
**Meaning:** Requires OS-level secure storage (DPAPI on Windows, Keychain on macOS)
**CI behavior:** Excluded - DPAPI unavailable on GitHub runners
**When to use:** Tests validating credential persistence to secure storage

### `[Trait("Category", "SlowIntegration")]`

**Applied to:** Tests with queries taking 60+ seconds
**Meaning:** Long-running operations (e.g., listing 60k stock Dataverse plugins)
**CI behavior:** Excluded to keep CI fast
**When to use:** Unfiltered queries against large tables

### `[Trait("Category", "DestructiveE2E")]`

**Applied to:** Tests that modify Dataverse data
**Meaning:** Creates/updates/deletes real records
**CI behavior:** Runs (with cleanup)
**When to use:** Tests that deploy plugins, create profiles, modify entities

---

## Skip Attributes

Use these to skip tests when prerequisites aren't available:

| Attribute | Skips When | Use For |
|-----------|------------|---------|
| `[SkipIfNoCredentials]` | No auth method available | Any authenticated test |
| `[SkipIfNoClientSecret]` | `PPDS_TEST_CLIENT_SECRET` missing | Client secret auth tests |
| `[SkipIfNoCertificate]` | `PPDS_TEST_CERT_BASE64` missing | Certificate auth tests |
| `[SkipIfNoGitHubOidc]` | Not in GitHub Actions | GitHub OIDC tests |
| `[SkipIfNoAzureDevOpsOidc]` | Not in Azure Pipelines | Azure DevOps OIDC tests |
| `[CliE2EFact]` | Not .NET 8.0 runtime | CLI E2E tests (TFM-specific) |
| `[CliE2EWithCredentials]` | Not .NET 8.0 OR no credentials | CLI E2E tests requiring auth |

---

## CI Environment Constraints

### DPAPI/SecureCredentialStore

**Problem:** Windows DPAPI requires an interactive user session. GitHub Actions runners lack this.

**Symptoms:**
- Tests hang indefinitely (no timeout, no error)
- `SecureCredentialStore` constructor blocks on `MsalCacheHelper.CreateAsync()`
- Job times out after 6 hours

**Solutions:**

1. **Set `PPDS_TEST_CLIENT_SECRET` in CI** (recommended):
   ```yaml
   # In workflow env section:
   PPDS_TEST_CLIENT_SECRET: ${{ secrets.PPDS_TEST_CLIENT_SECRET }}
   ```
   `CredentialProviderFactory` automatically checks `PPDS_SPN_SECRET` first, then falls back to `PPDS_TEST_CLIENT_SECRET`. Since CI already sets the latter, the bypass "just works" without extra configuration.

2. **Mark tests with `[Trait("Category", "SecureStorage")]`**:
   ```csharp
   [SkipIfNoClientSecret]
   [Trait("Category", "SecureStorage")]
   public async Task Test_WithSecureStorage()
   {
       // This test will be excluded from CI
   }
   ```

3. **Use `allowCleartextFallback: true` for test stores**:
   ```csharp
   using var store = new SecureCredentialStore(tempPath, allowCleartextFallback: true);
   ```

### Timeout Considerations

| Operation | Default | Notes |
|-----------|---------|-------|
| CLI command (`RunCliAsync`) | 2 minutes | Usually sufficient |
| ServiceClient auth | 60 seconds | Internal timeout added |
| Dataverse queries | Varies | Large result sets can timeout |
| Bulk operations | No default | Always pass `CancellationToken` |

### CI Credential Configuration

Set in GitHub environment secrets (`test-dataverse` environment):

```yaml
env:
  DATAVERSE_URL: ${{ vars.DATAVERSE_URL }}
  PPDS_TEST_APP_ID: ${{ vars.PPDS_TEST_APP_ID }}
  PPDS_TEST_TENANT_ID: ${{ vars.PPDS_TEST_TENANT_ID }}
  PPDS_TEST_CLIENT_SECRET: ${{ secrets.PPDS_TEST_CLIENT_SECRET }}
  PPDS_TEST_CERT_BASE64: ${{ secrets.PPDS_TEST_CERT_BASE64 }}
  PPDS_TEST_CERT_PASSWORD: ${{ secrets.PPDS_TEST_CERT_PASSWORD }}
```

---

## Local Setup Workflow

### 1. Copy environment template

```powershell
Copy-Item .env.example .env.local
```

### 2. Configure credentials

Edit `.env.local` with your Dataverse app registration:

```ini
DATAVERSE_URL=https://yourorg.crm.dynamics.com
PPDS_TEST_APP_ID=00000000-0000-0000-0000-000000000000
PPDS_TEST_CLIENT_SECRET=your-client-secret
PPDS_TEST_TENANT_ID=00000000-0000-0000-0000-000000000000
```

### 3. Load and run

```powershell
# Load environment variables
. .\scripts\Load-TestEnv.ps1

# Run integration tests
dotnet test --filter "Category=Integration"
```

Or use the `/run-integration-local` command.

---

## Common Failure Patterns

### DPAPI Hang (CI-Only)

**Symptoms:** Test hangs, job times out after 6 hours

**Cause:** `SecureCredentialStore` waiting for DPAPI on headless system

**Fix:**
1. Ensure `PPDS_SPN_SECRET` env var is set in CI
2. Add `[Trait("Category", "SecureStorage")]` if testing credential storage
3. CLI E2E tests: Already use isolated `PPDS_CONFIG_DIR`

### CLI JSON Output Format Mismatch

**Symptoms:** `JsonException`, test expects `[...]`, gets `{"version":"1.0","data":[...]}`

**Cause:** CLI uses envelope format for structured output (see [ADR-0008](adr/0008_CLI_OUTPUT_ARCHITECTURE.md))

**Fix:** Parse the envelope:
```csharp
// Wrong:
result.StdOut.Should().StartWith("[");

// Correct:
result.StdOut.Should().StartWith("{");
var envelope = JsonSerializer.Deserialize<JsonElement>(result.StdOut);
var data = envelope.GetProperty("data");
```

### Test Category Not Applied

**Symptoms:** Integration tests run during unit test phase

**Cause:** Missing `[Trait("Category", "Integration")]` or not using base class

**Fix:** Inherit from `LiveTestBase` or `CliE2ETestBase` (they apply the trait automatically)

### Certificate Loading Errors

**Symptoms:** `CryptographicException` when loading certificate

**Fix:** Use correct storage flags:
```csharp
var flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet;
var cert = new X509Certificate2(bytes, password, flags);
```

### Slow Query Timeout

**Symptoms:** Test times out on large queries (e.g., listing all plugins)

**Cause:** Unfiltered queries return 60k+ records

**Fix:** Add filter or mark with `[Trait("Category", "SlowIntegration")]`:
```csharp
[SkipIfNoClientSecret]
[Trait("Category", "SlowIntegration")]
public async Task List_AllPlugins_ReturnsResults()
{
    // This 100+ second query won't block CI
}
```

---

## Test Base Classes

### `LiveTestBase`

For library integration tests (Dataverse operations):

```csharp
public class MyLiveTests : LiveTestBase
{
    [SkipIfNoClientSecret]
    public async Task MyTest()
    {
        // Configuration property provides all credentials
        var pool = CreatePool(Configuration);
        await using var client = await pool.GetClientAsync(CancellationToken);
        // ...
    }
}
```

**Features:**
- `[Trait("Category", "Integration")]` applied automatically
- `[Collection("LiveDataverse")]` - sequential execution (avoids throttling)
- `Configuration` property with all credentials
- `CancellationToken` with test timeout

### `CliE2ETestBase`

For CLI end-to-end tests:

```csharp
public class MyCliTests : CliE2ETestBase
{
    [CliE2EWithCredentials]
    public async Task MyTest()
    {
        var result = await RunCliAsync("auth", "list");
        result.ExitCode.Should().Be(0);
    }
}
```

**Features:**
- `RunCliAsync()` and `RunCliWithEnvAsync()` helpers
- Isolated `PPDS_CONFIG_DIR` per test instance (avoids race conditions)
- Auto-cleanup of temp files and profiles
- Only runs on .NET 8.0 (CLI is single TFM)
- `[Collection("CliE2E")]` - sequential execution

---

## Test Filtering

### Common Filters

```powershell
# Unit tests only (CI default, fast)
dotnet test --filter "Category!=Integration"

# Integration tests only
dotnet test --filter "Category=Integration"

# Integration tests minus slow/secure storage
dotnet test --filter "Category=Integration&Category!=SecureStorage&Category!=SlowIntegration"

# Specific test class
dotnet test --filter "FullyQualifiedName~DataSchemaCommandE2ETests"

# Specific test method
dotnet test --filter "FullyQualifiedName~JsonFormat_OutputsProgress"
```

### Multi-TFM Behavior

Integration tests run on all TFMs (net8.0, net9.0, net10.0).

CLI E2E tests only run on net8.0 via `[CliE2EFact]` attribute - the CLI assembly is single-TFM.

---

## Adding New Integration Tests

### Checklist

1. **Inherit from correct base class:**
   - `LiveTestBase` for library tests
   - `CliE2ETestBase` for CLI E2E tests

2. **Apply correct skip attribute:**
   - `[SkipIfNoCredentials]` - any auth
   - `[SkipIfNoClientSecret]` - client secret specifically
   - `[CliE2EFact]` - CLI tests without auth
   - `[CliE2EWithCredentials]` - CLI tests with auth

3. **Consider test categories:**
   - Slow (60+ seconds)? Add `[Trait("Category", "SlowIntegration")]`
   - Uses DPAPI directly? Add `[Trait("Category", "SecureStorage")]`
   - Destructive? Use try-finally for cleanup

4. **Handle cleanup:**
   ```csharp
   [CliE2EWithCredentials]
   public async Task DestructiveTest()
   {
       try
       {
           await RunCliAsync("plugins", "deploy", "--config", configPath);
           // assertions
       }
       finally
       {
           await RunCliAsync("plugins", "clean", "--config", configPath);
       }
   }
   ```

---

## Related Documentation

- [ADR-0008: CLI Output Architecture](adr/0008_CLI_OUTPUT_ARCHITECTURE.md) - CLI output format
- [COVERAGE_BASELINE.md](COVERAGE_BASELINE.md) - Coverage targets
- [LOGGING_STANDARDS.md](LOGGING_STANDARDS.md) - Console output conventions
