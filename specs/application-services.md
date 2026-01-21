# Application Services

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Cli/Services/](../src/PPDS.Cli/Services/)

---

## Overview

Application Services encapsulate business logic shared across CLI commands, TUI screens, RPC handlers, and MCP tools. They provide a single code path for all interfaces, ensuring consistent behavior regardless of how users interact with PPDS.

### Goals

- **Single source of truth**: Business logic lives in services, not scattered across UI code
- **UI agnosticism**: Services return domain objects; presentation layers format for their medium
- **Testability**: Services can be unit tested without UI framework dependencies
- **Progress reporting**: Long operations report progress via `IProgressReporter` for all UIs

### Non-Goals

- Output formatting (handled by each presentation layer)
- UI framework concerns (Terminal.Gui, Spectre.Console)
- Connection management (delegated to PPDS.Dataverse connection pool)

---

## Architecture

```
┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐
│ CLI Command │  │ TUI Screen  │  │ RPC Handler │  │  MCP Tool   │
└──────┬──────┘  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘
       │                │                │                │
       └────────────────┴────────────────┴────────────────┘
                               │
                    ┌──────────▼──────────┐
                    │  Application Services│
                    │   - ISqlQueryService │
                    │   - IProfileService  │
                    │   - IExportService   │
                    │   - etc.             │
                    └──────────┬──────────┘
                               │
              ┌────────────────┼────────────────┐
              │                │                │
       ┌──────▼──────┐  ┌──────▼──────┐  ┌──────▼──────┐
       │ ProfileStore│  │IQueryExecutor│  │  IDataverse │
       │             │  │             │  │ConnectionPool│
       └─────────────┘  └─────────────┘  └─────────────┘
```

Services call domain libraries (PPDS.Auth, PPDS.Dataverse, PPDS.Migration) but are unaware of which presentation layer invoked them.

### Components

| Component | Responsibility |
|-----------|----------------|
| Service interfaces | Define contracts for business operations |
| Service implementations | Execute business logic, throw `PpdsException` |
| `ServiceRegistration` | Registers all services in DI container |
| `ProfileServiceFactory` | Creates configured `ServiceProvider` from auth profiles |
| Progress interfaces | Enable UI-agnostic progress reporting |

### Dependencies

- Depends on: [architecture.md](./architecture.md) - Module structure and layering
- Depends on: [connection-pool.md](./connection-pool.md) - Services requiring Dataverse access
- Depends on: [authentication.md](./authentication.md) - Profile resolution

---

## Specification

### Core Requirements

1. Services MUST NOT reference UI frameworks (Terminal.Gui, Spectre.Console)
2. Services MUST return domain objects, not formatted strings
3. Services MUST throw `PpdsException` with `ErrorCode` for all errors
4. Services accepting `IProgressReporter` MUST report progress for operations >1 second
5. Services MUST be stateless (no mutable instance fields beyond dependencies)

### Primary Flows

**Service Consumption (CLI):**

1. **Create provider**: `ProfileServiceFactory.CreateFromProfileAsync()` builds DI container
2. **Resolve service**: Command calls `sp.GetRequiredService<IMyService>()`
3. **Execute**: Service performs business logic, may throw `PpdsException`
4. **Format**: Command formats result via `IOutputWriter`
5. **Dispose**: Command disposes `ServiceProvider` (releases connection pool)

**Service Consumption (TUI):**

1. **Create provider**: TUI shell creates provider on startup
2. **Resolve service**: Screen gets service from `_serviceProvider`
3. **Execute with progress**: Screen passes `TuiOperationProgress` adapter
4. **Update UI**: Screen refreshes with service result

### Constraints

- Services requiring Dataverse access MUST be registered as **Transient** to enable connection pool parallelism (don't hold pooled clients in singletons)
- Services storing persistent data MUST use `ProfileStore` or similar abstraction (not direct file I/O)
- Services MUST accept `CancellationToken` for all async operations

### Validation Rules

| Input | Rule | Error |
|-------|------|-------|
| SQL query | Non-empty, parseable | `SqlParseException` with line/column |
| Profile name | Exists in store | `PpdsNotFoundException` with `Profile.NotFound` |
| Environment | Resolves to URL | `PpdsNotFoundException` with `Environment.NotFound` |

---

## Core Types

### IProgressReporter

UI-agnostic interface for reporting operation progress ([`IProgressReporter.cs:11-37`](../src/PPDS.Cli/Infrastructure/Progress/IProgressReporter.cs#L11-L37)). Used for migration-specific operations with rich progress data.

```csharp
public interface IProgressReporter
{
    void ReportProgress(ProgressSnapshot snapshot);
    void ReportPhase(string phase, string? detail = null);
    void ReportWarning(string message);
    void ReportInfo(string message);
}
```

**ProgressSnapshot** includes:
- `CurrentItem` / `TotalItems` - Item-based progress (0-indexed)
- `CurrentEntity` - Entity being processed (e.g., "account")
- `RecordsPerSecond` - Processing rate
- `EstimatedRemaining` - ETA calculation
- `StatusMessage` - Human-readable status

### IOperationProgress

Simpler interface for general-purpose operations ([`IOperationProgress.cs:34-69`](../src/PPDS.Cli/Infrastructure/IOperationProgress.cs#L34-L69)). Used by export, query, and other services.

```csharp
public interface IOperationProgress
{
    void ReportStatus(string message);                    // Indeterminate
    void ReportProgress(int current, int total, string? message);  // Item-based
    void ReportProgress(double fraction, string? message); // Percentage
    void ReportComplete(string message);
    void ReportError(string message);
}
```

### Usage Pattern

Services accept optional progress reporters with null-conditional invocation:

```csharp
public async Task ExportCsvAsync(
    DataTable table,
    Stream stream,
    ExportOptions? options = null,
    IOperationProgress? progress = null,  // Optional
    CancellationToken cancellationToken = default)
{
    progress?.ReportStatus($"Exporting {table.Rows.Count} rows...");

    for (int i = 0; i < table.Rows.Count; i++)
    {
        // Export row...
        progress?.ReportProgress(i + 1, table.Rows.Count);
    }

    progress?.ReportComplete($"Exported {table.Rows.Count} rows.");
}
```

---

## Service Inventory

### ISqlQueryService

SQL query transpilation and execution ([`ISqlQueryService.cs:12-33`](../src/PPDS.Cli/Services/Query/ISqlQueryService.cs#L12-L33)).

| Method | Purpose |
|--------|---------|
| `TranspileSql(sql, topOverride)` | Parse SQL, return FetchXML (no execution) |
| `ExecuteAsync(request)` | Transpile and execute against Dataverse |

**Scope:** Transient (requires `IQueryExecutor`)

### IProfileService

Profile CRUD operations ([`IProfileService.cs`](../src/PPDS.Cli/Services/Profile/IProfileService.cs)).

| Method | Purpose |
|--------|---------|
| `GetProfilesAsync()` | List all profiles |
| `GetActiveProfileAsync()` | Get currently active profile |
| `SetActiveProfileAsync(nameOrIndex)` | Activate a profile |
| `CreateProfileAsync(request, callback)` | Create with authentication |
| `DeleteProfileAsync(nameOrIndex)` | Delete profile |
| `UpdateProfileAsync(nameOrIndex, ...)` | Update name/environment |
| `ClearAllAsync()` | Clear all profiles and credentials |

**Scope:** Transient (uses singleton `ProfileStore`)

### IEnvironmentService

Environment discovery and binding ([`IEnvironmentService.cs`](../src/PPDS.Cli/Services/Environment/IEnvironmentService.cs)).

| Method | Purpose |
|--------|---------|
| `DiscoverEnvironmentsAsync(callback)` | List accessible environments via GDS |
| `GetCurrentEnvironmentAsync()` | Get environment from active profile |
| `SetEnvironmentAsync(identifier, callback)` | Resolve and bind to profile |
| `ClearEnvironmentAsync()` | Clear environment from profile |

**Scope:** Transient

### IExportService

Data export to various formats ([`IExportService.cs:14-83`](../src/PPDS.Cli/Services/Export/IExportService.cs#L14-L83)).

| Method | Purpose |
|--------|---------|
| `ExportCsvAsync(table, stream, options, progress)` | Export to CSV |
| `ExportTsvAsync(table, stream, options, progress)` | Export to TSV |
| `ExportJsonAsync(table, stream, columnTypes, options, progress)` | Export to JSON with types |
| `FormatForClipboard(table, rows, cols, headers)` | Tab-separated for paste |

**Scope:** Transient

### IQueryHistoryService

Per-environment query history ([`IQueryHistoryService.cs`](../src/PPDS.Cli/Services/History/IQueryHistoryService.cs)).

| Method | Purpose |
|--------|---------|
| `GetHistoryAsync(envUrl, count)` | Recent queries (max 200) |
| `AddQueryAsync(envUrl, sql, rowCount, execTime)` | Record executed query |
| `SearchHistoryAsync(envUrl, pattern, count)` | Find by pattern |
| `DeleteEntryAsync(envUrl, entryId)` | Delete single entry |
| `ClearHistoryAsync(envUrl)` | Clear all for environment |

**Storage:** `~/.ppds/history/{env-hash}.json`
**Scope:** Singleton (manages file I/O)

### IPluginRegistrationService

Plugin assembly/type queries ([`IPluginRegistrationService.cs`](../src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs)).

| Method | Purpose |
|--------|---------|
| `ListAssembliesAsync(filter, options)` | List plugin assemblies |
| `ListPackagesAsync(filter, options)` | List plugin packages |
| `ListTypesForAssemblyAsync(assemblyId)` | Get types in assembly |

**Scope:** Transient (requires connection pool)

### IConnectionService

Power Apps connections ([`IConnectionService.cs`](../src/PPDS.Cli/Services/IConnectionService.cs)).

| Method | Purpose |
|--------|---------|
| `ListAsync(connectorFilter)` | List connections via Admin API |
| `GetAsync(connectionId)` | Get specific connection |

**Note:** Requires user-delegated auth (not SPN) for full functionality.
**Scope:** Transient (requires token provider)

---

## Service Registration

Services are registered in [`ServiceRegistration.cs:32-114`](../src/PPDS.Cli/Services/ServiceRegistration.cs#L32-L114):

```csharp
public static IServiceCollection AddCliApplicationServices(this IServiceCollection services)
{
    // Profile management
    services.AddSingleton<ProfileStore>();
    services.AddTransient<IProfileService, ProfileService>();
    services.AddTransient<IEnvironmentService, EnvironmentService>();

    // Query services
    services.AddTransient<ISqlQueryService, SqlQueryService>();
    services.AddSingleton<IQueryHistoryService, QueryHistoryService>();

    // Export services
    services.AddTransient<IExportService, ExportService>();

    // Factory registrations for complex dependencies...
    return services;
}
```

### Lifetime Guidelines

| Lifetime | Use When |
|----------|----------|
| **Transient** | Service needs Dataverse connection (enables pool parallelism) |
| **Singleton** | Service manages persistent storage (e.g., `QueryHistoryService`) |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `SqlParseException` | Invalid SQL syntax | Show parse error with line/column |
| `PpdsNotFoundException` | Profile/environment not found | Suggest `ppds auth create` or `ppds env select` |
| `PpdsAuthException` | Token expired or invalid | Re-authenticate with `ppds auth create` |
| `PpdsValidationException` | Invalid input | Show validation errors |

### Service Exception Pattern

Services wrap low-level exceptions in domain exceptions:

```csharp
public async Task<SqlQueryResult> ExecuteAsync(SqlQueryRequest request, CancellationToken ct)
{
    try
    {
        var parser = new SqlParser(request.Sql);
        var ast = parser.Parse();  // May throw SqlParseException
        // ...
    }
    catch (FaultException ex) when (ex.Message.Contains("not found"))
    {
        throw new PpdsNotFoundException(
            "Entity.NotFound",
            $"Entity '{entityName}' not found",
            ex);
    }
}
```

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty query | Throw `ArgumentException` before parsing |
| No active profile | Throw `InvalidOperationException` with instructions |
| Environment without ID | Operations requiring Admin API fail with clear message |

---

## Design Decisions

### Why UI-Agnostic Progress Reporting?

**Context:** Long-running operations need progress feedback. Different UIs display progress differently (Spectre.Console spinners, Terminal.Gui progress bars, JSON-RPC notifications).

**Decision:** Services accept `IProgressReporter` or `IOperationProgress` for operations >1 second. Each UI provides its own adapter implementation.

**Duration guidelines:**
| Operation Duration | Recommendation |
|-------------------|----------------|
| < 100ms | No progress needed |
| 100ms - 1s | Optional, phase reporting only |
| > 1s | Required with item-level progress |
| > 10s | Required with rate/ETA |

**Adapter implementations:**
- `ConsoleProgressReporter` - CLI with elapsed time prefix
- `JsonProgressReporter` - JSON lines to stderr for CI/CD
- `TuiOperationProgress` - Terminal.Gui progress bar updates
- `NullProgressReporter` / `NullOperationProgress` - Silent operations

**Consequences:**
- Positive: Services don't know about UI frameworks; same code works everywhere
- Positive: Rich progress info (rate, ETA) available to all UIs
- Negative: Additional parameter on service methods

### Why Operation Clock for Elapsed Time?

**Context:** Progress reporter and MEL logger both displayed elapsed time with independent stopwatches, causing timestamps to jump backwards in logs.

**Decision:** Introduce static `OperationClock` that owns operation start time. All components read from same source.

```csharp
public static class OperationClock
{
    private static readonly Stopwatch Stopwatch = new();
    public static TimeSpan Elapsed => Stopwatch.Elapsed;
    public static void Start() => Stopwatch.Restart();
}
```

**Usage:**
```csharp
// CLI command starts the clock
OperationClock.Start();
var reporter = ServiceFactory.CreateProgressReporter(...);
await service.ImportAsync(request, reporter, ct);
```

**Alternatives considered:**
- Inject `IOperationClock` via DI: More testable but awkward for MEL formatters created early in pipeline
- Progress reporter owns clock: Creates coupling between logging and progress

**Consequences:**
- Positive: Consistent timestamps across all components
- Negative: Static state requires explicit `Start()` call

### Why Transient Services for Dataverse Access?

**Context:** Connection pool enables parallelism by checking out clients to concurrent operations. Singleton services would hold one client, defeating the pool.

**Decision:** Services requiring Dataverse connections are **Transient**. Each resolution gets fresh instance that requests client from pool.

**Example from ADR-0002:**
```csharp
// BAD - Singleton holds one client
services.AddSingleton<IMyService, MyService>();  // Defeats pool parallelism

// GOOD - Transient enables parallel client checkout
services.AddTransient<IMyService, MyService>();
```

**Consequences:**
- Positive: Multiple concurrent operations each get their own pooled client
- Negative: Service instances recreated per-use (minimal overhead)

---

## Extension Points

### Adding a New Application Service

1. **Define interface** in `src/PPDS.Cli/Services/{Domain}/`:
   ```csharp
   public interface IMyService
   {
       Task<MyResult> DoSomethingAsync(
           MyRequest request,
           IOperationProgress? progress = null,
           CancellationToken cancellationToken = default);
   }
   ```

2. **Implement service** in same folder:
   ```csharp
   public sealed class MyService : IMyService
   {
       private readonly IDependency _dep;
       private readonly ILogger<MyService> _logger;

       public MyService(IDependency dep, ILogger<MyService> logger)
       {
           _dep = dep ?? throw new ArgumentNullException(nameof(dep));
           _logger = logger ?? throw new ArgumentNullException(nameof(logger));
       }

       public async Task<MyResult> DoSomethingAsync(...)
       {
           progress?.ReportStatus("Starting...");
           // Implementation
       }
   }
   ```

3. **Register in `ServiceRegistration.cs`**:
   ```csharp
   services.AddTransient<IMyService, MyService>();
   ```

### Adding a Progress Adapter

1. **Implement interface** for your UI framework:
   ```csharp
   internal sealed class MyUiProgressAdapter : IOperationProgress
   {
       private readonly MyUiProgressControl _control;

       public void ReportProgress(int current, int total, string? message)
       {
           _control.SetProgress((double)current / total);
           _control.SetMessage(message);
       }
       // ...
   }
   ```

2. **Pass to service** from your presentation layer

---

## Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Query history max entries | int | 200 | Per-environment limit |
| Query history file | path | `~/.ppds/history/{hash}.json` | Per-environment storage |

---

## Testing

### Acceptance Criteria

- [ ] All services are unit testable without UI frameworks
- [ ] Progress reporting works identically across CLI, TUI, RPC
- [ ] Services throw `PpdsException` with error codes (not raw exceptions)
- [ ] Transient services don't hold connection pool clients

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| SQL with syntax error | `"SELECT * FORM account"` | `SqlParseException` with position |
| Query with null reporter | `progress: null` | No exceptions, works silently |
| Export empty table | `table.Rows.Count == 0` | Empty file, `progress.ReportComplete("Exported 0 rows")` |

### Test Examples

```csharp
[Fact]
public async Task ExecuteAsync_WithValidSql_ReturnsTranspiledResult()
{
    // Arrange
    var mockExecutor = new Mock<IQueryExecutor>();
    mockExecutor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), ...))
        .ReturnsAsync(new QueryResult { /* ... */ });
    var service = new SqlQueryService(mockExecutor.Object);

    // Act
    var result = await service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account" });

    // Assert
    Assert.Contains("<entity name='account'", result.TranspiledFetchXml);
}

[Fact]
public async Task ExportCsvAsync_ReportsProgress()
{
    // Arrange
    var service = new ExportService(NullLogger<ExportService>.Instance);
    var progress = new Mock<IOperationProgress>();
    var table = CreateTestTable(rows: 100);

    // Act
    using var stream = new MemoryStream();
    await service.ExportCsvAsync(table, stream, progress: progress.Object);

    // Assert
    progress.Verify(p => p.ReportProgress(It.IsAny<int>(), 100, It.IsAny<string>()), Times.AtLeastOnce);
    progress.Verify(p => p.ReportComplete(It.IsAny<string>()), Times.Once);
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Layering and module structure
- [connection-pool.md](./connection-pool.md) - Why services are transient
- [cli.md](./cli.md) - How CLI commands consume services
- [error-handling.md](./error-handling.md) - PpdsException hierarchy
- [tui.md](./tui.md) - How TUI screens consume services with progress

---

## Roadmap

- Unified caching layer for metadata across services
- Service telemetry/metrics collection
- Retry policies at service layer (beyond connection pool)
