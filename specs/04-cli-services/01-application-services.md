# PPDS.Cli Services: Application Services

## Overview

The Application Services subsystem implements a layered architecture that separates business logic from presentation layers (CLI, TUI, RPC/Daemon). Services encapsulate all business logic shared between interfaces, ensuring consistent behavior while allowing each interface to format output appropriately for its medium. This architecture follows ADR-0015 and enables the TUI-first, multi-interface development strategy.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `ISqlQueryService` | Parse, transpile, execute SQL queries to FetchXML |
| `IProfileService` | Manage auth profiles (list, create, delete, select) |
| `IEnvironmentService` | Discover and manage Dataverse environments |
| `IQueryHistoryService` | Persist and retrieve query history per-environment |
| `IExportService` | Export query results to CSV/TSV/JSON/clipboard |
| `IConnectionService` | Power Apps Admin API for connection management |
| `IPluginRegistrationService` | Plugin SDK integration |

### Classes

| Class | Purpose |
|-------|---------|
| `SqlQueryService` | SQL transpilation and Dataverse query execution |
| `ProfileService` | Profile CRUD operations and selection |
| `EnvironmentService` | Environment discovery via Admin API |
| `QueryHistoryService` | History persistence with file-based storage |
| `ExportService` | Multi-format data export |
| `ConnectionService` | Power Apps connection management |
| `PluginRegistrationService` | Plugin assembly registration |
| `ProfileServiceFactory` | Creates service providers from profiles |
| `ServiceRegistration` | DI registration extension methods |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `SqlQueryRequest` | SQL execution request with pagination |
| `SqlQueryResult` | Query result with FetchXML and data |
| `ProfileSummary` | Profile metadata for listing |
| `QueryHistoryEntry` | Single history record |
| `ExportOptions` | Export format configuration |
| `ProgressSnapshot` | Progress reporting state |

## Behaviors

### Architectural Pattern

```
Presentation Layer (CLI/TUI/RPC)
    ↓ (consumes)
Application Services Layer (Business Logic)
    ↓ (uses)
Infrastructure Layer (Dataverse API, File I/O, Auth)
```

**Key Principles:**
- Services encapsulate all business logic
- Services return domain objects, not UI-specific data
- Each presentation layer formats output for its medium
- Services are testable in isolation

### Service Lifecycle

| Service | Lifecycle | Reason |
|---------|-----------|--------|
| `ISqlQueryService` | Transient | Stateless per request |
| `IProfileService` | Transient | Stateless, thread-safe via ProfileStore |
| `IEnvironmentService` | Transient | Stateless |
| `IQueryHistoryService` | Singleton | Manages shared file I/O locking |
| `IExportService` | Transient | Stateless |
| `ProfileStore` | Singleton | Credential store and file access |

### Request/Response Pattern

Services use immutable request/response objects:

```csharp
// Request (immutable record)
public sealed record SqlQueryRequest
{
    public required string Sql { get; init; }
    public int? TopOverride { get; init; }
    public int? PageNumber { get; init; }
    public string? PagingCookie { get; init; }
    public bool IncludeCount { get; init; }
}

// Response (domain object)
public sealed class SqlQueryResult
{
    public required string OriginalSql { get; init; }
    public required string TranspiledFetchXml { get; init; }
    public required QueryResult Result { get; init; }
}
```

### Output Formatting Separation

| Consumer | Service Returns | Layer Formats To |
|----------|-----------------|------------------|
| CLI | Domain object | Text tables, CSV, JSON |
| TUI | Domain object | Spectre.Console tables |
| RPC | Domain object | JSON response DTO |

### Shared Local State (ADR-0024)

All persistent data stored in `~/.ppds/`:

```
~/.ppds/
├── profiles.json              # IProfileService
├── history/                   # IQueryHistoryService
│   ├── {env-hash-1}.json
│   └── {env-hash-2}.json
├── settings.json              # Future
├── msal_token_cache.bin       # Auth
└── ppds.credentials.dat       # Credentials
```

### Lifecycle

- **Registration**: `ServiceRegistration.AddCliApplicationServices()` registers all services
- **Creation**: `ProfileServiceFactory.CreateFromProfileAsync()` creates service provider
- **Usage**: Commands/wizards resolve services from provider
- **Disposal**: Service provider disposed after command completion

## Error Handling (ADR-0026)

### Exception Hierarchy

```
PpdsException (base)
├── PpdsAuthException      # Authentication/authorization failures
├── PpdsThrottleException  # Rate limiting (429)
├── PpdsValidationException # Input validation failures
├── PpdsNotFoundException  # Resource not found
```

### Exception Structure

```csharp
public class PpdsException : Exception
{
    public string ErrorCode { get; init; }      // Machine-readable (e.g., "Auth.Expired")
    public string UserMessage { get; init; }     // Human-readable, safe to display
    public PpdsSeverity Severity { get; init; }  // Info, Warning, Error
    public IDictionary<string, object>? Context { get; init; }  // Debug context
}
```

### Standard Error Codes

| Category | Codes |
|----------|-------|
| Profile | `Profile.NotFound`, `Profile.InvalidName` |
| Auth | `Auth.Expired`, `Auth.InsufficientPermissions` |
| Connection | `Connection.Throttled`, `Connection.EnvironmentNotFound` |
| Query | `Query.ParseError`, `Query.ExecutionFailed` |

### Exit Code Mapping

| Exception | Exit Code |
|-----------|-----------|
| `PpdsNotFoundException` | `NotFoundError` |
| `PpdsValidationException` | `InvalidArguments` |
| `PpdsAuthException` | `AuthError` |
| `PpdsThrottleException` | `ConnectionError` |
| Other `PpdsException` | `Failure` |

## Progress Reporting (ADR-0025)

### UI-Agnostic Interface

```csharp
public interface IProgressReporter
{
    void ReportProgress(ProgressSnapshot snapshot);
    void ReportPhase(string phase, string? detail = null);
    void ReportWarning(string message);
    void ReportInfo(string message);
}

public sealed record ProgressSnapshot
{
    public required int CurrentItem { get; init; }
    public required int TotalItems { get; init; }
    public string? CurrentEntity { get; init; }
    public double? RecordsPerSecond { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string? StatusMessage { get; init; }
}
```

### Adapter Pattern

| Interface | Adapter | Implementation |
|-----------|---------|----------------|
| CLI | `CliProgressReporter` | Spectre.Console progress bars |
| TUI | `TuiProgressReporter` | Terminal.Gui progress bars |
| Non-interactive | `NullProgressReporter` | No-op singleton |

### Service Integration

Services accept optional `IProgressReporter` for operations >1 second:

```csharp
public async Task ExportAsync(
    DataTable table,
    Stream stream,
    IOperationProgress? progress = null,  // Optional
    CancellationToken cancellationToken = default)
{
    progress?.ReportStatus($"Exporting {totalRows} rows...");
    // ... process ...
    progress?.ReportProgress(processedRows, totalRows);
}
```

## Thread Safety

### Locking Strategy

Services use semaphore-based locking for file I/O:

```csharp
private readonly SemaphoreSlim _lock = new(1, 1);

public async Task<T> OperationAsync(CancellationToken ct)
{
    await _lock.WaitAsync(ct);
    try
    {
        return await DoFileOperationAsync(ct);
    }
    finally
    {
        _lock.Release();
    }
}
```

### Async Patterns

- All I/O operations are async with `async/await`
- `CancellationToken` required on all async methods
- No sync-over-async (with documented exceptions)
- Connection pooling for parallel Dataverse operations

## Multi-Interface Development (ADR-0027)

### Development Order

1. **Application Service** - Business logic implementation
2. **CLI Command** - Command-line exposure
3. **TUI Panel** - Interactive reference UI
4. **RPC Method** - Remote exposure (future)
5. **MCP Tool** - AI integration (future)
6. **Extension View** - VS Code UI (future)

### Example: SQL Query Feature

```
ISqlQueryService (service)
    ↓
SqlCommand (CLI)  → "ppds sql 'SELECT name FROM account'"
    ↓
SqlQueryWizard (TUI)  → Interactive SQL editor
    ↓
RpcMethodHandler  → "execute_sql" method
```

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Service not registered | Throws from DI | Check registration order |
| Profile not found | `PpdsNotFoundException` | Error code: `Profile.NotFound` |
| Auth token expired | `PpdsAuthException` | Auto-refresh attempted first |
| Rate limited | `PpdsThrottleException` | Includes `RetryAfter` |
| File locked | Waits for semaphore | Cancellation supported |
| Progress null | No-op | Services handle gracefully |

## Dependencies

- **Internal**:
  - `PPDS.Auth` - Profile and credential storage
  - `PPDS.Dataverse` - Query execution, connection pooling
- **External**:
  - `Microsoft.Extensions.DependencyInjection` - DI framework
  - `Microsoft.Extensions.Logging` - Logging abstraction

## Configuration

Services are registered via DI extension method:

```csharp
public static IServiceCollection AddCliApplicationServices(this IServiceCollection services)
{
    services.AddSingleton<ProfileStore>();
    services.AddTransient<IProfileService, ProfileService>();
    services.AddTransient<IEnvironmentService, EnvironmentService>();
    services.AddTransient<ISqlQueryService, SqlQueryService>();
    services.AddSingleton<IQueryHistoryService, QueryHistoryService>();
    services.AddTransient<IExportService, ExportService>();
    // ...
    return services;
}
```

## Related

- [ADR-0015: Application Service Layer](../docs/adr/0015_APPLICATION_SERVICE_LAYER.md)
- [ADR-0024: Shared Local State](../docs/adr/0024_SHARED_LOCAL_STATE.md)
- [ADR-0025: UI-Agnostic Progress](../docs/adr/0025_UI_AGNOSTIC_PROGRESS.md)
- [ADR-0026: Structured Error Model](../docs/adr/0026_STRUCTURED_ERROR_MODEL.md)
- [ADR-0027: Multi-Interface Development](../docs/adr/0027_MULTI_INTERFACE_DEVELOPMENT.md)

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Cli/Services/ServiceRegistration.cs` | DI registration |
| `src/PPDS.Cli/Services/Query/ISqlQueryService.cs` | SQL service interface |
| `src/PPDS.Cli/Services/Query/SqlQueryService.cs` | SQL service implementation |
| `src/PPDS.Cli/Services/Profile/IProfileService.cs` | Profile service interface |
| `src/PPDS.Cli/Services/Profile/ProfileService.cs` | Profile service implementation |
| `src/PPDS.Cli/Services/Environment/IEnvironmentService.cs` | Environment service interface |
| `src/PPDS.Cli/Services/Environment/EnvironmentService.cs` | Environment service implementation |
| `src/PPDS.Cli/Services/History/IQueryHistoryService.cs` | History service interface |
| `src/PPDS.Cli/Services/History/QueryHistoryService.cs` | History service implementation |
| `src/PPDS.Cli/Services/Export/IExportService.cs` | Export service interface |
| `src/PPDS.Cli/Services/Export/ExportService.cs` | Export service implementation |
| `src/PPDS.Cli/Infrastructure/Errors/PpdsException.cs` | Exception hierarchy |
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` | Error codes catalog |
| `src/PPDS.Cli/Infrastructure/Errors/ExceptionMapper.cs` | Exception-to-error mapping |
| `src/PPDS.Cli/Infrastructure/Progress/IProgressReporter.cs` | Progress interface |
| `src/PPDS.Cli/Infrastructure/Progress/ProgressSnapshot.cs` | Progress state model |
| `src/PPDS.Cli/Infrastructure/ProfileServiceFactory.cs` | Service provider factory |
| `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` | SQL service tests |
| `tests/PPDS.Cli.Tests/Services/Profile/ProfileServiceTests.cs` | Profile service tests |
