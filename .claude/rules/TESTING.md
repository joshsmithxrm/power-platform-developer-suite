# Testing Guide

## Test Projects

| Package | Unit Tests | Integration Tests |
|---------|------------|-------------------|
| PPDS.Plugins | PPDS.Plugins.Tests | - |
| PPDS.Dataverse | PPDS.Dataverse.Tests | PPDS.Dataverse.IntegrationTests (FakeXrmEasy) |
| PPDS.Cli | PPDS.Cli.Tests | PPDS.LiveTests/Cli (E2E) |
| PPDS.Auth | PPDS.Auth.Tests | PPDS.LiveTests/Authentication |
| PPDS.Migration | PPDS.Migration.Tests | - |

---

## Test Categories

| Category/Attribute | Purpose | CI Behavior |
|--------------------|---------|-------------|
| `Integration` | Live Dataverse tests | Runs in integration-tests.yml |
| `SecureStorage` | DPAPI/credential store tests | **Excluded** - DPAPI unavailable |
| `SlowIntegration` | 60+ second queries | **Excluded** - keeps CI fast |
| `DestructiveE2E` | Modifies Dataverse data | Runs (with cleanup) |
| `[CliE2EFact]` | CLI tests, .NET 8.0 only | Runs |
| `[CliE2EWithCredentials]` | CLI tests + auth | Runs if credentials available |

**CI constraint:** DPAPI unavailable on GitHub runners. Use `PPDS_SPN_SECRET` env var to bypass `SecureCredentialStore`.

---

## Test Filtering

- **Commits:** Unit tests only (`--filter Category!=Integration`)
- **PRs:** All tests including integration

---

## Local Integration Test Setup

1. **Copy environment template:**
   ```powershell
   Copy-Item .env.example .env.local
   ```

2. **Edit `.env.local`** with your values:
   ```
   DATAVERSE_URL=https://yourorg.crm.dynamics.com
   PPDS_TEST_APP_ID=your-app-id
   PPDS_TEST_CLIENT_SECRET=your-secret
   PPDS_TEST_TENANT_ID=your-tenant-id
   ```

3. **Load and run:**
   ```powershell
   . .\scripts\Load-TestEnv.ps1
   dotnet test --filter "Category=Integration"
   ```

**Note:** `.env.local` is gitignored. Tests skip gracefully when credentials are missing.

See [docs/INTEGRATION_TESTING.md](docs/INTEGRATION_TESTING.md) for full guide.

---

## Live Tests (PPDS.LiveTests)

Live integration tests against real Dataverse environment:
- `Authentication/` - Client secret, certificate, GitHub OIDC, Azure DevOps OIDC
- `Pooling/` - Connection pool, DOP detection
- `Resilience/` - Throttle detection
- `BulkOperations/` - Live bulk operation execution
- `Cli/` - CLI E2E tests (auth, env, data schema commands)

---

## Rules

- New public class → must have corresponding test class
- New public method → must have test coverage
- Mark integration tests with `[Trait("Category", "Integration")]`
