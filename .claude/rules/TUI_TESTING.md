# TUI Testing Guide

Reference: ADR-0028 (TUI Testing Strategy)

## Test Categories

| Category | Purpose | Speed | Command |
|----------|---------|-------|---------|
| `TuiUnit` | Session lifecycle, profile switching | <5s | `dotnet test --filter "Category=TuiUnit"` |
| `TuiIntegration` | Query execution with FakeXrmEasy | <30s | `dotnet test --filter "Category=TuiIntegration"` |
| `TuiE2E` | Full process with PTY (future) | Minutes | Not implemented |

---

## Testing Pattern

### 1. Use MockServiceProviderFactory for DI

```csharp
var mockFactory = new MockServiceProviderFactory();
var session = new InteractiveSession(
    profileName: null,
    profileStore,
    mockFactory);  // Inject mock

await session.InitializeAsync();

// Verify factory was called
Assert.Single(mockFactory.CreationLog);
```

### 2. Use TempProfileStore for isolated profiles

```csharp
using var tempStore = new TempProfileStore();

var profile = TempProfileStore.CreateTestProfile(
    "TestProfile",
    environmentUrl: "https://test.crm.dynamics.com");
await tempStore.SeedProfilesAsync("TestProfile", profile);

var session = new InteractiveSession(null, tempStore.Store, mockFactory);
```

### 3. Use FakeSqlQueryService for query results

```csharp
var fakeQueryService = new FakeSqlQueryService();
fakeQueryService.NextResult = new SqlQueryResult
{
    OriginalSql = "SELECT name FROM account",
    TranspiledFetchXml = "<fetch>...</fetch>",
    Result = QueryResult.Empty("account")
};

// Configure MockServiceProviderFactory to return this service
```

---

## Running Tests

```bash
# Fast unit tests (run these frequently)
dotnet test --filter "Category=TuiUnit"

# Integration tests (run before PR)
dotnet test --filter "Category=TuiIntegration"

# All TUI tests
dotnet test --filter "Category=TuiUnit|Category=TuiIntegration"
```

---

## When Adding TUI Features

1. **Write failing test first** (TDD when possible)
2. **Implement feature**
3. **Run `dotnet test --filter "Category=TuiUnit"`**
4. **Only ask user for UX verification after tests pass**

---

## Key Files

| File | Purpose |
|------|---------|
| `src/PPDS.Cli/Infrastructure/IServiceProviderFactory.cs` | DI interface |
| `src/PPDS.Cli/Infrastructure/ProfileBasedServiceProviderFactory.cs` | Production impl |
| `tests/PPDS.Cli.Tests/Mocks/MockServiceProviderFactory.cs` | Test double |
| `tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs` | Query service fake |
| `tests/PPDS.Cli.Tests/Mocks/TempProfileStore.cs` | Isolated profile store |
| `tests/PPDS.Cli.Tests/Tui/InteractiveSessionLifecycleTests.cs` | Session tests |

---

## Debug Logging

TUI debug log: `~/.ppds/tui-debug.log`

```csharp
TuiDebugLog.Log("MyScreen", "Starting operation");
```

See `.claude/rules/TUI_TROUBLESHOOTING.md` for debugging patterns.
