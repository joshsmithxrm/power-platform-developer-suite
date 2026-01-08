# ADR-0029: Testing Strategy

## Status
Accepted

## Context
PPDS uses multiple test categories across unit, integration, and E2E tests. Test behavior differs between local development, commit hooks, and CI environments. This ADR documents the testing strategy and CI behavior.

## Decision

### Test Projects

| Package | Unit Tests | Integration Tests |
|---------|------------|-------------------|
| PPDS.Plugins | PPDS.Plugins.Tests | - |
| PPDS.Dataverse | PPDS.Dataverse.Tests | PPDS.Dataverse.IntegrationTests (FakeXrmEasy) |
| PPDS.Cli | PPDS.Cli.Tests | PPDS.LiveTests/Cli (E2E) |
| PPDS.Auth | PPDS.Auth.Tests | PPDS.LiveTests/Authentication |
| PPDS.Migration | PPDS.Migration.Tests | - |

### Test Categories

| Category | Purpose | CI Behavior |
|----------|---------|-------------|
| `Integration` | Live Dataverse tests | Runs in integration-tests.yml |
| `SecureStorage` | DPAPI/credential store tests | **Excluded** - DPAPI unavailable on runners |
| `SlowIntegration` | 60+ second queries | **Excluded** - keeps CI fast |
| `DestructiveE2E` | Modifies Dataverse data | Runs (with cleanup) |
| `TuiUnit` | TUI session lifecycle | Runs on commits |
| `TuiIntegration` | TUI with FakeXrmEasy | Runs in integration-tests.yml |
| `[CliE2EFact]` | CLI tests, .NET 8.0 only | Runs |
| `[CliE2EWithCredentials]` | CLI tests + auth | Runs if credentials available |

### CI Filtering

- **Commits (pre-commit hook):** Unit tests only (`--filter Category!=Integration`)
- **PRs (CI):** All tests including integration
- **DPAPI constraint:** GitHub runners don't support DPAPI; use `PPDS_SPN_SECRET` env var

### Local Integration Test Setup

1. Copy `.env.example` to `.env.local`
2. Add Dataverse URL and SPN credentials
3. Run `. .\scripts\Load-TestEnv.ps1` then `dotnet test --filter "Category=Integration"`

See [docs/INTEGRATION_TESTING.md](../INTEGRATION_TESTING.md) for full guide.

### Live Tests (PPDS.LiveTests)

Tests against real Dataverse environment:
- `Authentication/` - Client secret, certificate, GitHub OIDC, Azure DevOps OIDC
- `Pooling/` - Connection pool, DOP detection
- `Resilience/` - Throttle detection
- `BulkOperations/` - Live bulk operation execution
- `Cli/` - CLI E2E tests

## Consequences

### Positive
- Clear categorization enables selective test execution
- Fast commits (~10s) with comprehensive PR testing
- Graceful skip when credentials unavailable

### Negative
- SecureStorage tests don't run in CI
- Need to maintain .env.local separately

## References
- ADR-0028: TUI Testing Strategy
- docs/INTEGRATION_TESTING.md
