# Testing

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [tests/](../tests/)

---

## Overview

PPDS uses a multi-tier testing strategy with xUnit, FakeXrmEasy, and @microsoft/tui-test to validate functionality across unit, integration, and E2E layers. Tests are organized by category to enable selective execution in different environments—fast unit tests for commits, comprehensive integration tests for PRs.

### Goals

- **Fast feedback**: Unit tests complete in <10 seconds for commit hooks
- **Live validation**: Integration tests verify real Dataverse behavior when credentials are available
- **TUI coverage**: Specialized tests validate Terminal.Gui presentation layer without duplicating service logic
- **Graceful degradation**: Tests skip cleanly when credentials unavailable

### Non-Goals

- Testing Terminal.Gui rendering internals (accepted trade-off)
- Full UI automation for TUI (future enhancement)
- Cross-browser testing (not applicable—CLI/TUI only)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                       Test Categories                            │
├─────────────────────────────────────────────────────────────────┤
│  TuiUnit      │  TuiIntegration  │  Integration  │  (no category)│
│  TUI session  │  TUI + FakeXrm   │  Live Dataverse│  Unit tests   │
│  <5s          │  <30s            │  ~minutes      │  <10s total   │
└───────┬───────┴────────┬─────────┴───────┬───────┴───────┬───────┘
        │                │                 │               │
        ▼                ▼                 ▼               ▼
┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌───────────────┐
│MockServiceProv│ │ FakeXrmEasy   │ │ LiveTestBase  │ │ Standard Moq  │
│iderFactory    │ │ + Mocks       │ │ + Credentials │ │ patterns      │
└───────────────┘ └───────────────┘ └───────────────┘ └───────────────┘
```

### Test Projects

| Project | Type | Framework | Purpose |
|---------|------|-----------|---------|
| PPDS.Plugins.Tests | Unit | xUnit | Plugin attributes and enums |
| PPDS.Dataverse.Tests | Unit | xUnit, Moq | Core SDK functionality |
| PPDS.Dataverse.IntegrationTests | Integration | xUnit, FakeXrmEasy | Mocked Dataverse operations |
| PPDS.Auth.Tests | Unit | xUnit, Moq | Profile and credential handling |
| PPDS.Auth.IntegrationTests | Integration | xUnit | Live auth methods |
| PPDS.Cli.Tests | Unit + TuiUnit | xUnit, Moq | CLI commands and TUI session |
| PPDS.Cli.DaemonTests | E2E | xUnit, StreamJsonRpc | RPC protocol tests |
| PPDS.Migration.Tests | Unit | xUnit, Moq | Migration utilities |
| PPDS.Mcp.Tests | Unit | xUnit, Moq | MCP server tools |
| PPDS.LiveTests | E2E | xUnit | Real Dataverse operations |
| PPDS.LiveTests.Fixtures | Fixture | - | Test plugin assemblies (net462) |
| tui-e2e | E2E | @microsoft/tui-test | Terminal UI snapshots |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md)
- Tests services from: [application-services.md](./application-services.md)

---

## Specification

### Core Requirements

1. Unit tests MUST run without network access or credentials
2. Integration tests MUST skip gracefully when credentials are unavailable
3. TUI tests MUST NOT duplicate service logic already tested at CLI layer
4. Live tests MUST clean up created data after execution
5. Tests MUST be categorized for selective CI execution

### Test Categories

| Category | Purpose | CI Behavior | Speed |
|----------|---------|-------------|-------|
| *(none)* | Default unit tests | Runs on commits | <10s |
| `TuiUnit` | TUI session lifecycle | Runs on commits | <5s |
| `TuiIntegration` | TUI with FakeXrmEasy | Runs in integration-tests.yml | <30s |
| `Integration` | Live Dataverse tests | Runs in integration-tests.yml | ~minutes |
| `SecureStorage` | DPAPI/credential tests | **Excluded**—DPAPI unavailable on runners | - |
| `SlowIntegration` | 60+ second queries | **Excluded**—keeps CI fast | - |
| `DestructiveE2E` | Modifies Dataverse data | Runs with cleanup | - |

### CI Filtering

**Commits (pre-commit hook):**
```bash
dotnet test --filter Category!=Integration
```

**PRs (full CI):**
```bash
dotnet test --filter "Category=Integration"
```

**TUI unit tests only:**
```bash
dotnet test --filter Category=TuiUnit
```

### Test Execution Commands

```bash
# Default: Unit tests (fast)
dotnet test --filter Category!=Integration

# Integration tests (requires credentials)
dotnet test --filter Category=Integration

# TUI unit tests
dotnet test --filter Category=TuiUnit

# Specific test class
dotnet test --filter ClassName

# TUI E2E (TypeScript)
npm test --prefix tests/tui-e2e
npm test --prefix tests/tui-e2e -- --update-snapshots
```

---

## Core Types

### LiveTestConfiguration

Central configuration for live integration tests. Reads credentials from environment variables and provides availability checks.

```csharp
public sealed class LiveTestConfiguration : IDisposable
{
    public bool HasClientSecretCredentials { get; }
    public bool HasCertificateCredentials { get; }
    public bool HasGitHubOidcCredentials { get; }
    public bool HasAnyCredentials { get; }
}
```

The implementation ([`LiveTestConfiguration.cs:1-240`](../tests/PPDS.LiveTests/Infrastructure/LiveTestConfiguration.cs#L1-L240)) reads from environment variables:

| Variable | Purpose |
|----------|---------|
| `DATAVERSE_URL` | Organization URL |
| `PPDS_TEST_APP_ID` | Application (Client) ID |
| `PPDS_TEST_CLIENT_SECRET` | Client secret |
| `PPDS_TEST_TENANT_ID` | Entra tenant ID |
| `PPDS_TEST_CERT_BASE64` | Certificate (base64 encoded) |
| `PPDS_TEST_CERT_PASSWORD` | Certificate password |

### LiveTestBase

Base class for live Dataverse tests with xUnit lifecycle support.

```csharp
[Collection("LiveDataverse")]
[Trait("Category", "Integration")]
public abstract class LiveTestBase : IAsyncLifetime
{
    protected LiveTestConfiguration Configuration { get; }
    public virtual Task InitializeAsync() => Task.CompletedTask;
    public virtual Task DisposeAsync() => Task.CompletedTask;
}
```

The collection definition ([`LiveTestBase.cs:49-52`](../tests/PPDS.LiveTests/Infrastructure/LiveTestBase.cs#L49-L52)) ensures sequential execution to avoid API rate limiting.

### FakeXrmEasyTestsBase

Base class for FakeXrmEasy-based integration tests with custom message executors for bulk operations.

```csharp
public abstract class FakeXrmEasyTestsBase : IDisposable
{
    protected IXrmFakedContext Context { get; }
    protected IOrganizationService Service { get; }
    protected void InitializeWith(params Entity[] entities);
}
```

The implementation ([`FakeXrmEasyTestsBase.cs:32-48`](../tests/PPDS.Dataverse.IntegrationTests/FakeXrmEasyTestsBase.cs#L32-L48)) registers custom executors for `CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`, and `DeleteMultiple` operations.

### BulkOperationExecutorTestsBase

Pre-configured base for bulk operation tests with mocked dependencies.

```csharp
public abstract class BulkOperationExecutorTestsBase : FakeXrmEasyTestsBase
{
    protected IBulkOperationExecutor Executor { get; }
    protected FakeConnectionPool ConnectionPool { get; }
    protected FakeThrottleTracker ThrottleTracker { get; }
    protected static List<Entity> CreateTestEntities(string name, int count);
}
```

The implementation ([`BulkOperationExecutorTestsBase.cs:42-67`](../tests/PPDS.Dataverse.IntegrationTests/BulkOperations/BulkOperationExecutorTestsBase.cs#L42-L67)) configures `MaxParallelBatches = 1` for deterministic test execution.

### Skip Attributes

Custom xUnit `FactAttribute` implementations that conditionally skip tests.

```csharp
[SkipIfNoCredentials]     // Any credentials
[SkipIfNoClientSecret]    // Client secret specifically
[SkipIfNoCertificate]     // Certificate auth
[SkipIfNoGitHubOidc]      // GitHub Actions OIDC
[SkipIfNoAzureDevOpsOidc] // Azure Pipelines OIDC
[CliE2EFact]              // .NET 8.0 only
[CliE2EWithCredentials]   // .NET 8.0 + credentials
```

The implementation ([`SkipIfNoCredentialsAttribute.cs:9-163`](../tests/PPDS.LiveTests/Infrastructure/SkipIfNoCredentialsAttribute.cs#L9-L163)) checks `LiveTestConfiguration` and sets `Skip` reason when credentials are missing.

---

## Mock Implementations

### FakeConnectionPool

Mocks `IDataverseConnectionPool` for testing without real Dataverse connections.

```csharp
public class FakeConnectionPool : IDataverseConnectionPool
{
    public Task<IPooledClient> GetClientAsync(...);
    public PoolStatistics Statistics { get; }
}
```

The implementation ([`FakeConnectionPool.cs:12-119`](../tests/PPDS.Dataverse.IntegrationTests/Mocks/FakeConnectionPool.cs#L12-L119)) tracks active connections and returns `FakePooledClient` instances wrapping FakeXrmEasy.

### MockServiceProviderFactory

Mocks `IServiceProviderFactory` for TUI testing with configurable behavior.

```csharp
public sealed class MockServiceProviderFactory : IServiceProviderFactory
{
    public IReadOnlyList<ProviderCreationRecord> CreationLog { get; }
    public Exception? ExceptionToThrow { get; set; }
    public TimeSpan CreateDelay { get; set; }
}
```

The implementation ([`MockServiceProviderFactory.cs:15-105`](../tests/PPDS.Cli.Tests/Mocks/MockServiceProviderFactory.cs#L15-L105)) logs creation calls and returns a `ServiceProvider` with fake services (`FakeSqlQueryService`, `FakeQueryHistoryService`, `FakeExportService`).

### Service Fakes

| Fake | Interface | Purpose |
|------|-----------|---------|
| `FakeSqlQueryService` | `ISqlQueryService` | Returns configurable query results |
| `FakeQueryHistoryService` | `IQueryHistoryService` | In-memory history storage |
| `FakeExportService` | `IExportService` | Tracks export operations |
| `TempProfileStore` | `ProfileStore` | Isolated temp directory for profiles |
| `FakeThrottleTracker` | `IThrottleTracker` | Configurable throttle behavior |

---

## Error Handling

### Test Skip Patterns

| Condition | Skip Message |
|-----------|--------------|
| No credentials | "Missing environment variables: DATAVERSE_URL, ..." |
| Wrong TFM | "CLI E2E tests only run on .NET 8.0" |
| No DPAPI | Excluded entirely via `SecureStorage` category |

### Collection-Based Isolation

| Collection | Purpose | Parallelization |
|------------|---------|-----------------|
| `LiveDataverse` | Live API tests | Sequential (rate limiting) |
| `CliE2E` | CLI process tests | Sequential (profile conflicts) |
| *(none)* | Unit tests | Parallel |

---

## Design Decisions

### Why Three Test Tiers?

**Context:** Different test environments have different capabilities and speed requirements.

**Decision:** Implement unit, integration, and E2E test tiers with category-based filtering.

**Test results:**
| Tier | Duration | Credentials | CI Context |
|------|----------|-------------|------------|
| Unit | <10s | Not required | Every commit |
| Integration (FakeXrmEasy) | <30s | Not required | PRs |
| Live E2E | ~minutes | Required | Scheduled/manual |

**Alternatives considered:**
- Single tier with mocking: Rejected—misses real API behavior
- All tests require credentials: Rejected—blocks local development

**Consequences:**
- Positive: Fast feedback loop, graceful degradation
- Negative: Three layers of test infrastructure to maintain

### Why IServiceProviderFactory for TUI Testing?

**Context:** The TUI (`InteractiveSession`) creates dependencies inline, making mock injection impossible.

**Decision:** Introduce `IServiceProviderFactory` abstraction that TUI calls to create its `ServiceProvider`. Tests inject `MockServiceProviderFactory`.

```csharp
// Before: Inline dependency creation
_serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(...);

// After: Injected factory
public InteractiveSession(
    IServiceProviderFactory? serviceProviderFactory = null)
{
    _serviceProviderFactory = serviceProviderFactory
        ?? new ProfileBasedServiceProviderFactory();
}
```

**Alternatives considered:**
- FakeDriver from Terminal.Gui: Rejected—internal/undocumented API
- Full process testing only: Rejected—too slow for iteration

**Consequences:**
- Positive: TUI can be tested without Terminal.Gui rendering
- Negative: Slightly more complex DI setup in InteractiveSession

### Why Test Responsibility Matrix?

**Context:** CLI and TUI share Application Services (ADR-0015). Testing the same logic twice wastes effort.

**Decision:** Service logic is tested once at CLI/service layer. TUI tests verify presentation only.

| Concern | Tested By |
|---------|-----------|
| Query execution logic | CLI/Service tests |
| Data transformation | CLI/Service tests |
| Export format validity | CLI/Service tests |
| Authentication flows | CLI/Service tests |
| Screen rendering | TUI E2E snapshots |
| Session state management | TuiUnit |

**Principle:** If CLI tests pass, the service works. TUI tests verify the presentation layer only.

**Consequences:**
- Positive: Less test duplication, faster maintenance
- Negative: TUI bugs that don't affect services need separate E2E tests

### Why Skip Attributes Instead of Conditional Facts?

**Context:** Tests requiring credentials fail loudly when credentials are missing.

**Decision:** Use custom `FactAttribute` subclasses that set `Skip` when prerequisites are missing.

```csharp
[SkipIfNoClientSecret]
public async Task Auth_WithClientSecret_Succeeds()
{
    // Only runs if PPDS_TEST_CLIENT_SECRET is set
}
```

**Alternatives considered:**
- `[Fact(Skip = "...")]`: Rejected—always skips
- `#if DEBUG`: Rejected—not credential-aware
- ConditionalFact: Rejected—no skip message

**Consequences:**
- Positive: Clear skip messages, tests don't fail when credentials missing
- Negative: Custom attribute per credential type

### Why Sequential Collections for Live Tests?

**Context:** Parallel live tests can overwhelm Dataverse API quotas and cause profile store conflicts.

**Decision:** Use xUnit collections (`[Collection("LiveDataverse")]`) to enforce sequential execution.

**Alternatives considered:**
- Parallel with rate limiting: Rejected—complex coordination
- Single test class: Rejected—poor organization

**Consequences:**
- Positive: Predictable execution, no race conditions
- Negative: Slower total execution time

---

## Coverage Targets

### Package Coverage

| Package | Unit Target | Current | Status |
|---------|-------------|---------|--------|
| **PPDS.Plugins** | 95% | ~95% | ✅ Met |
| **PPDS.Dataverse** | 60% | ~65% | ✅ Met |
| **PPDS.Cli** | 60% | ~45% | ⏳ Gap |
| **PPDS.Auth** | 70% | ~65% | ✅ Near |
| **PPDS.Migration** | 50% | ~55% | ✅ Met |
| **Overall** | 60% | ~58% | ⏳ Near |

### Test Count Summary

| Test Project | Test Count | Scope |
|--------------|------------|-------|
| PPDS.Plugins.Tests | 77 | Attributes, enums |
| PPDS.Dataverse.Tests | 351 | Client, pooling, bulk operations |
| PPDS.Dataverse.IntegrationTests | ~50 | FakeXrmEasy mocked operations |
| PPDS.Cli.Tests | 210 | Command structure, parsing |
| PPDS.Auth.Tests | 282 | Profiles, credentials, discovery |
| PPDS.Migration.Tests | 200 | Models, formats, analysis |
| PPDS.LiveTests | ~40 + 27 CLI | Live Dataverse + CLI E2E |

### Measurement

Coverage collected using `coverlet.collector` via `dotnet test --collect:"XPlat Code Coverage"` and reported to Codecov. Merged from net8.0, net9.0, net10.0 test runs.

### Future Gates

- Patch coverage enforcement (80% for new code)
- Project-level coverage gates after baseline improvement

---

## Extension Points

### Adding a New Test Category

1. **Define category in tests**: Use `[Trait("Category", "NewCategory")]`
2. **Update CI filtering**: Add to workflow YAML `--filter` expressions
3. **Document in ADR-0029**: Update the category table

### Adding a New Skip Attribute

1. **Create attribute class** extending `FactAttribute`:

```csharp
public sealed class SkipIfNoNewThingAttribute : FactAttribute
{
    public SkipIfNoNewThingAttribute()
    {
        if (!NewThingAvailable())
            Skip = "NewThing not configured...";
    }
}
```

2. **Use in tests**: `[SkipIfNoNewThing]` instead of `[Fact]`

### Adding a New Mock

1. **Create fake implementation** in `tests/*/Mocks/`
2. **Implement full interface**: All methods, even if they return defaults
3. **Add tracking**: Log calls for assertion (`CreationLog`, `Exports`, etc.)
4. **Register in factory**: Add to `MockServiceProviderFactory.CreateAsync()`

---

## Configuration

### Environment Variables for Live Tests

| Variable | Required For | Example |
|----------|--------------|---------|
| `DATAVERSE_URL` | All live tests | `https://org.crm.dynamics.com` |
| `PPDS_TEST_APP_ID` | All live tests | `00000000-0000-0000-0000-000000000000` |
| `PPDS_TEST_TENANT_ID` | All live tests | `00000000-0000-0000-0000-000000000000` |
| `PPDS_TEST_CLIENT_SECRET` | Client secret auth | `secret...` |
| `PPDS_TEST_CERT_BASE64` | Certificate auth | `MIIKvA...` (base64) |
| `PPDS_TEST_CERT_PASSWORD` | Certificate auth | `password` |
| `ACTIONS_ID_TOKEN_REQUEST_URL` | GitHub OIDC | (auto-set by GH Actions) |
| `ACTIONS_ID_TOKEN_REQUEST_TOKEN` | GitHub OIDC | (auto-set by GH Actions) |

### Local Integration Test Setup

1. Copy `.env.example` to `.env.local`
2. Add credentials
3. Run: `. .\scripts\Load-TestEnv.ps1`
4. Run: `dotnet test --filter "Category=Integration"`

---

## Testing

### Acceptance Criteria

- [ ] Unit tests run without credentials and complete <10s
- [ ] Integration tests skip gracefully without credentials
- [ ] Live tests create and clean up test data
- [ ] TUI tests use `MockServiceProviderFactory`, not real services
- [ ] Category filtering works correctly in CI

### Test Examples

**Unit test with Moq:**

```csharp
[Fact]
public async Task Export_CallsExportService()
{
    var mockExport = new Mock<IExportService>();
    var sut = new ExportCommand(mockExport.Object);

    await sut.ExecuteAsync("output.csv");

    mockExport.Verify(x => x.ExportCsvAsync(
        It.IsAny<DataTable>(),
        It.IsAny<Stream>()), Times.Once);
}
```

**FakeXrmEasy integration test:**

```csharp
public class CreateMultipleTests : BulkOperationExecutorTestsBase
{
    [Fact]
    public async Task CreateMultiple_WithValidEntities_Succeeds()
    {
        var entities = CreateTestEntities("account", 50);
        var progress = CreateProgressReporter();

        var result = await Executor.CreateMultipleAsync(
            "account", entities, progress: progress);

        Assert.True(result.IsSuccess);
        Assert.Equal(50, result.SuccessCount);
        Assert.NotEmpty(progress.Reports);
    }
}
```

**TUI unit test with MockServiceProviderFactory:**

```csharp
[Trait("Category", "TuiUnit")]
public class InteractiveSessionTests
{
    [Fact]
    public async Task Session_CreatesProvider_OnConnect()
    {
        using var store = new TempProfileStore();
        var factory = new MockServiceProviderFactory();
        var session = new InteractiveSession(
            store, serviceProviderFactory: factory);

        await session.ConnectAsync("https://test.crm.dynamics.com");

        Assert.Single(factory.CreationLog);
    }
}
```

**Live test with skip attribute:**

```csharp
[Collection("LiveDataverse")]
public class AuthenticationLiveTests : LiveTestBase
{
    [SkipIfNoClientSecret]
    public async Task ClientSecret_Authenticates()
    {
        var client = await LiveTestHelpers.CreateServiceClientAsync(
            Configuration);

        var whoAmI = await client.ExecuteAsync(new WhoAmIRequest());

        Assert.NotEqual(Guid.Empty, whoAmI.UserId);
    }
}
```

**TUI E2E snapshot test (TypeScript):**

```typescript
import { test, expect } from '@microsoft/tui-test';

test('launches and displays main menu', async ({ terminal }) => {
    await expect(terminal.getByText('PPDS - Power Platform Developer Suite'))
        .toBeVisible();
    await expect(terminal).toMatchSnapshot();
});
```

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services pattern shared by CLI and TUI
- [application-services.md](./application-services.md) - Services that tests validate
- [tui.md](./tui.md) - TUI architecture and testing patterns
- [error-handling.md](./error-handling.md) - Error types tested across tiers

---

## Roadmap

- Full PTY-based TUI E2E automation (beyond snapshot testing)
- Code coverage enforcement thresholds
- Performance regression testing
- Mutation testing integration
